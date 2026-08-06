using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// Brings a schema's <em>CRM</em> tables up to the version this build writes against.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists next to <c>PostgresMigrator</c> rather than inside it.</strong>
/// <c>plugins/FlowX.Postgres/Migrations</c> is the journal's schema, and
/// <c>PostgresMigrator.Migrations</c> is the list every FlowX deployment applies at start-up.
/// The eleven tables in <c>0001_crm_schema.sql</c> belong to one sample. Adding them there
/// would give <c>samples/banking</c> an opportunity pipeline, would move
/// <c>PostgresMigrator.TargetVersion</c> for a change no platform consumer made, and would put
/// a sample's schema on the platform's expand/contract schedule. So the tables are the
/// sample's, the ledger is the sample's — <c>crm_schema_migration</c>, beside
/// <c>schema_migration</c> and independent of it — and the two migrators can run in either
/// order.
/// </para>
/// <para>
/// <strong>What is kept from <c>PostgresMigrator</c>, because it was right there.</strong> One
/// advisory lock so two replicas starting together do not both migrate; one transaction so a
/// failure leaves the schema at the version it was already at rather than half-way into the
/// next one; a listed rather than globbed migration set, because ordering a schema's history
/// by whatever order the runtime hands back resources is how two deployments of one package end
/// up with two different schemas.
/// </para>
/// <para>
/// <strong>What is deliberately dropped.</strong> There is no <c>CreateSchemaIfMissing</c>: the
/// schema is selected by the data source's <c>search_path</c> and created by whoever created
/// it. A sample's migrator that could conjure a schema would be a second answer to a question
/// <c>PostgresJournalOptions</c> already answers.
/// </para>
/// </remarks>
public sealed class CrmMigrator
{
    /// <summary>
    /// Keeps this lock out of the platform migrator's key space, and out of anybody else's.
    /// </summary>
    /// <remarks>
    /// <c>PostgresMigrator</c> uses <c>0x464C5758</c> — "FLWX". This is "FLWC", so the two
    /// migrators cannot block each other while both are keyed on the same schema name.
    /// </remarks>
    private const int AdvisoryLockNamespace = 0x464C5743;

    private const string Lock = "SELECT pg_advisory_xact_lock(@namespace, hashtext(current_schema()))";

    private const string Ledger =
        """
        CREATE TABLE IF NOT EXISTS crm_schema_migration (
            version    int         NOT NULL PRIMARY KEY,
            name       text        NOT NULL,
            applied_at timestamptz NOT NULL DEFAULT now()
        )
        """;

    private const string Applied = "SELECT version FROM crm_schema_migration";

    private const string Record =
        "INSERT INTO crm_schema_migration (version, name) VALUES (@version, @name)";

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates a migrator over one data source, whose schema it takes as given.</summary>
    /// <param name="dataSource">
    /// The data source. Its connection string must already select the schema the CRM tables
    /// are to live in.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is null.</exception>
    public CrmMigrator(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <summary>The migrations this build carries, in the order they must be applied.</summary>
    public static IReadOnlyList<CrmMigration> Migrations { get; } =
    [
        new(1, "crm_schema", "0001_crm_schema.sql"),
        new(2, "crm_row_level_security", "0002_crm_row_level_security.sql"),
        new(3, "crm_activity_integrity", "0003_crm_activity_integrity.sql"),
        new(4, "crm_lead_enrichment", "0004_crm_lead_enrichment.sql"),
        new(5, "crm_dynamic_schema", "0005_crm_dynamic_schema.sql"),
        new(6, "crm_picklists_and_connectors", "0006_crm_picklists_and_connectors.sql"),
        new(7, "crm_validation_and_field_policy", "0007_crm_validation_and_field_policy.sql"),
        new(8, "crm_rollups", "0008_crm_rollups.sql"),
        new(9, "crm_queries_and_read_policy", "0009_crm_queries_and_read_policy.sql"),
        new(10, "crm_search_and_compound_filters", "0010_crm_search_and_compound_filters.sql"),
        new(11, "crm_formulas_and_ordering", "0011_crm_formulas_and_ordering.sql"),
        new(12, "crm_change_feed", "0012_crm_change_feed.sql"),
        new(13, "crm_bulk_jobs", "0013_crm_bulk_jobs.sql"),
        new(14, "crm_reports_and_dashboards", "0014_crm_reports_and_dashboards.sql"),
        new(15, "crm_tenant_ui_settings", "0015_crm_tenant_ui_settings.sql"),
        new(16, "crm_planning", "0016_crm_planning.sql"),
        new(17, "crm_management", "0017_crm_management.sql"),
    ];

    /// <summary>The schema version this build of the sample reads and writes.</summary>
    public static int TargetVersion => Migrations[^1].Version;

    /// <summary>Reads a migration's SQL out of the assembly.</summary>
    /// <param name="migration">The migration to read.</param>
    /// <returns>The script body.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="migration"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The script was not embedded.</exception>
    /// <remarks>
    /// Named <c>ReadScript</c> to match <c>PostgresMigrator</c>, and the name is load-bearing:
    /// <c>SqlSurvey.BuildTimeScriptReaders</c> allows a method by that name to hand SQL
    /// straight to a command, and <c>TheEmbeddedScriptAllowanceStillReadsAnEmbeddedScript</c>
    /// requires every declaration of it to read an embedded resource. This one does, which is
    /// why it may.
    /// </remarks>
    public static string ReadScript(CrmMigration migration)
    {
        ArgumentNullException.ThrowIfNull(migration);

        var assembly = typeof(CrmMigrator).Assembly;
        var name = $"{typeof(CrmMigrator).Namespace}.Migrations.{migration.Resource}";

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Migration script '{name}' is not embedded in {assembly.GetName().Name}. " +
                "The .sql files are included by an EmbeddedResource item in Crm.csproj; a " +
                "migration added to the list above without its script is an application that " +
                "cannot create its own schema.");

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    /// <summary>Applies every migration the schema has not seen.</summary>
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
    /// The overload exists for the reason <c>PostgresMigrator</c>'s does — expand/contract is a
    /// sequence of deployments — and it is what lets a test stand a schema at version 1, where
    /// the tables exist and no policy does.
    /// </remarks>
    public async ValueTask<int> MigrateAsync(
        int throughVersion,
        CancellationToken cancellationToken = default)
    {
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

    private static async ValueTask LockAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = Lock;
        command.Parameters.Add(new NpgsqlParameter("namespace", NpgsqlDbType.Integer)
        {
            Value = AdvisoryLockNamespace,
        });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask EnsureLedgerAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = Ledger;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<HashSet<int>> AppliedVersionsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = Applied;

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
        CrmMigration migration,
        CancellationToken cancellationToken)
    {
        using (var script = connection.CreateCommand())
        {
            script.CommandText = ReadScript(migration);

            await script.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using var record = connection.CreateCommand();

        record.CommandText = Record;
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

/// <summary>One migration in the CRM schema's history.</summary>
/// <param name="Version">Its ordinal. Applied in ascending order and never renumbered.</param>
/// <param name="Name">What it does, for the ledger and for a failure message.</param>
/// <param name="Resource">The embedded <c>.sql</c> file that carries it.</param>
public sealed record CrmMigration(int Version, string Name, string Resource)
{
    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Version:0000} {Name}");
}
