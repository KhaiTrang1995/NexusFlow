using System.Globalization;
using System.Reflection;
using Npgsql;
using NpgsqlTypes;

namespace FlowX.Postgres;

/// <summary>
/// Brings a schema up to the version this build of the adapter writes against, applying
/// the migrations that are missing and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Expand/contract, enforced rather than intended.</strong>
/// <c>docs/11-Distributed-Runtime.md §7.4</c> requires journal schema changes to be
/// additive within a release so that old and new pods coexist during a rollout. That is a
/// property of the scripts, not of this class, so it is asserted against the script text
/// by <c>MigrationTests</c> rather than described here.
/// </para>
/// <para>
/// <strong>Every migration runs in one transaction, behind one advisory lock.</strong> Two
/// pods starting at once is the ordinary case during a rolling update, and both will try
/// to migrate. The lock makes the second wait; the transaction makes a failure leave the
/// schema at the version it was already at, rather than half-way into the next one.
/// </para>
/// </remarks>
public sealed class PostgresMigrator
{
    /// <summary>An arbitrary constant that keeps this lock out of other applications' key space.</summary>
    private const int AdvisoryLockNamespace = 0x464C5758;

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresJournalOptions _options;

    /// <summary>Creates a migrator for one data source and schema.</summary>
    /// <param name="dataSource">The data source. Its connection string must select the same schema.</param>
    /// <param name="options">Where the tables live.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public PostgresMigrator(NpgsqlDataSource dataSource, PostgresJournalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
        _options = options ?? new PostgresJournalOptions();
    }

    /// <summary>
    /// The migrations this build carries, in the order they must be applied.
    /// </summary>
    /// <remarks>
    /// Listed rather than discovered by globbing the resource names. Ordering a schema's
    /// history by whatever order the runtime hands back resources is how two deployments
    /// of the same package end up with two different schemas.
    /// </remarks>
    public static IReadOnlyList<PostgresMigration> Migrations { get; } =
    [
        new(1, "initial_schema", "0001_initial_schema.sql"),
        new(2, "expand_state_bag_sequence", "0002_expand_state_bag_sequence.sql"),
    ];

    /// <summary>The schema version this build of the adapter reads and writes.</summary>
    public static int TargetVersion => Migrations[^1].Version;

    /// <summary>Reads a migration's SQL out of the assembly.</summary>
    /// <param name="migration">The migration to read.</param>
    /// <returns>The script body.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="migration"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The script was not embedded.</exception>
    public static string ReadScript(PostgresMigration migration)
    {
        ArgumentNullException.ThrowIfNull(migration);

        var assembly = typeof(PostgresMigrator).Assembly;
        var name = $"{typeof(PostgresMigrator).Namespace}.Migrations.{migration.Resource}";

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Migration script '{name}' is not embedded in {assembly.GetName().Name}. " +
                "The .sql files are included by an EmbeddedResource item in the project file; " +
                "a migration added to the list above without its script is a package that " +
                "cannot create its own schema.");

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    /// <summary>
    /// Applies every migration the schema has not seen, and returns the version it now
    /// stands at.
    /// </summary>
    /// <param name="cancellationToken">Cancels the migration.</param>
    /// <returns>The schema version after the call.</returns>
    public ValueTask<int> MigrateAsync(CancellationToken cancellationToken = default) =>
        MigrateAsync(TargetVersion, cancellationToken);

    /// <summary>
    /// Applies every migration up to and including <paramref name="throughVersion"/>.
    /// </summary>
    /// <param name="throughVersion">The version to stop at.</param>
    /// <param name="cancellationToken">Cancels the migration.</param>
    /// <returns>The schema version after the call.</returns>
    /// <remarks>
    /// Expand/contract is a sequence of deployments, not a single step: the schema moves
    /// first and the code that uses the new column follows in a later release. That is only
    /// expressible if a deployment can ask for a specific version rather than for "all of
    /// them", so this overload is the one a rollout actually uses — and it is what lets
    /// <c>MigrationTests</c> stand a schema at the previous release and check that the
    /// release before this one still works against it.
    /// </remarks>
    public async ValueTask<int> MigrateAsync(
        int throughVersion,
        CancellationToken cancellationToken = default)
    {
        if (_options.CreateSchemaIfMissing)
        {
            await CreateSchemaAsync(cancellationToken).ConfigureAwait(false);
        }

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var closing = connection.ConfigureAwait(false);

        var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var closingTransaction = transaction.ConfigureAwait(false);

        await LockAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsureLedgerAsync(connection, cancellationToken).ConfigureAwait(false);

        var applied = await AppliedVersionsAsync(connection, cancellationToken).ConfigureAwait(false);

        foreach (var migration in Migrations)
        {
            if (migration.Version > throughVersion || applied.Contains(migration.Version))
            {
                continue;
            }

            await ApplyAsync(connection, migration, cancellationToken).ConfigureAwait(false);
            applied.Add(migration.Version);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return applied.Count == 0 ? 0 : applied.Max();
    }

    /// <summary>The versions the schema has already recorded.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The applied versions, ascending.</returns>
    public async ValueTask<IReadOnlyList<int>> AppliedVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var closing = connection.ConfigureAwait(false);

        await EnsureLedgerAsync(connection, cancellationToken).ConfigureAwait(false);

        var applied = await AppliedVersionsAsync(connection, cancellationToken).ConfigureAwait(false);

        return [.. applied.Order()];
    }

    /// <summary>
    /// Creates the schema, passing its name to the server as a value and letting
    /// <c>format('%I', …)</c> quote it.
    /// </summary>
    /// <remarks>
    /// An identifier cannot be a bind parameter, which is why schema creation is usually
    /// written as string concatenation. It does not have to be: <c>set_config</c> is an
    /// ordinary function that takes a parameter, and <c>format</c> with <c>%I</c> applies
    /// the server's own quoting rules to whatever it finds there. The SQL below is two
    /// constant strings, so there is no concatenated statement for anything to be injected
    /// into — and no suppression to justify.
    /// </remarks>
    private async ValueTask CreateSchemaAsync(CancellationToken cancellationToken)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var closing = connection.ConfigureAwait(false);

        using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT set_config('flowx.migrate_schema', @schema, false);
            DO $$
            BEGIN
                EXECUTE format('CREATE SCHEMA IF NOT EXISTS %I', current_setting('flowx.migrate_schema'));
            END
            $$;
            """;

        command.Parameters.Add(new NpgsqlParameter("schema", NpgsqlDbType.Text)
        {
            Value = _options.Schema,
        });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask LockAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = "SELECT pg_advisory_xact_lock(@namespace, hashtext(@schema))";
        command.Parameters.Add(new NpgsqlParameter("namespace", NpgsqlDbType.Integer)
        {
            Value = AdvisoryLockNamespace,
        });
        command.Parameters.Add(new NpgsqlParameter("schema", NpgsqlDbType.Text)
        {
            Value = _options.Schema,
        });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask EnsureLedgerAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS schema_migration (
                version    int         NOT NULL PRIMARY KEY,
                name       text        NOT NULL,
                applied_at timestamptz NOT NULL DEFAULT now()
            )
            """;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<HashSet<int>> AppliedVersionsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = "SELECT version FROM schema_migration";

        var applied = new HashSet<int>();

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            applied.Add(reader.GetInt32(0));
        }

        return applied;
    }

    private static async ValueTask ApplyAsync(
        NpgsqlConnection connection,
        PostgresMigration migration,
        CancellationToken cancellationToken)
    {
        using (var script = connection.CreateCommand())
        {
            script.CommandText = ReadScript(migration);

            await script.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using var record = connection.CreateCommand();

        record.CommandText = "INSERT INTO schema_migration (version, name) VALUES (@version, @name)";
        record.Parameters.Add(new NpgsqlParameter("version", NpgsqlDbType.Integer)
        {
            Value = migration.Version,
        });
        record.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text)
        {
            Value = migration.Name,
        });

        await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>One migration in the schema's history.</summary>
/// <param name="Version">Its ordinal. Applied in ascending order and never renumbered.</param>
/// <param name="Name">What it does, for the ledger and for a failure message.</param>
/// <param name="Resource">The embedded <c>.sql</c> file that carries it.</param>
public sealed record PostgresMigration(int Version, string Name, string Resource)
{
    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Version:0000} {Name}");
}
