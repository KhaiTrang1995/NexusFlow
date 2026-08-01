using System.Globalization;
using Npgsql;

namespace FlowX.Chaos;

/// <summary>
/// The rig's own tables — the side-effect ledger above all — and the gate that decides
/// whether this run may happen at all.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The ledger is the measurement.</strong> Counting completions cannot see a
/// duplicate effect: a flow that ran its charge step twice and then completed once counts as
/// one completion. So every application of every step writes a row, unconditionally, with no
/// unique constraint and no <c>ON CONFLICT</c> — the effect is <em>observably</em>
/// non-idempotent, and a duplicate is a row in a table rather than an inference from a
/// counter.
/// </para>
/// <para>
/// <strong>The gate is the Postgres suite's, deliberately.</strong>
/// <c>tests/FlowX.Postgres.Tests/PostgresTestDatabase.cs</c> makes the distinction this rig
/// needs: "nobody asked for a database" is a reason to skip, and "somebody asked for a
/// database and there isn't one" is a defect in the run. A chaos rig that skipped quietly
/// when its database was missing would report a green CI job for a run that killed nothing,
/// which is the single worst outcome available to this package.
/// </para>
/// </remarks>
internal static class ChaosDatabase
{
    /// <summary>The variable that supplies a connection string.</summary>
    public const string ConnectionVariable = "FLOWX_POSTGRES_CONNECTION";

    /// <summary>The variable that opts a run in. Without it, nothing is killed.</summary>
    public const string OptInVariable = "FLOWX_CHAOS";

    /// <summary>The exit code a skipped run uses, so it is not mistaken for a pass.</summary>
    public const int SkippedExitCode = 0;

    /// <summary>The tables the rig owns, in the arm's own schema.</summary>
    /// <remarks>
    /// <c>chaos_effect</c> has no primary key on <c>(instance_id, step_index)</c> on purpose;
    /// adding one would make the database silently prevent the very thing being measured.
    /// </remarks>
    private const string Ddl =
        """
        CREATE TABLE chaos_instance (
            instance_id uuid        NOT NULL PRIMARY KEY,
            ordinal     int         NOT NULL,
            worker_node text,
            worker_pid  int,
            started_at  timestamptz,
            finished_at timestamptz,
            outcome     text
        );

        CREATE INDEX chaos_instance_unclaimed_idx ON chaos_instance (ordinal)
            WHERE started_at IS NULL;

        CREATE TABLE chaos_effect (
            seq         bigserial   NOT NULL PRIMARY KEY,
            instance_id uuid        NOT NULL,
            step_index  int         NOT NULL,
            phase       text        NOT NULL,
            node        text        NOT NULL,
            pid         int         NOT NULL,
            applied_at  timestamptz NOT NULL DEFAULT clock_timestamp()
        );

        CREATE INDEX chaos_effect_instance_idx ON chaos_effect (instance_id, step_index);

        CREATE TABLE chaos_kill (
            node        text        NOT NULL PRIMARY KEY,
            pid         int         NOT NULL,
            instance_id uuid        NOT NULL,
            step_index  int         NOT NULL,
            position    text        NOT NULL,
            killed_at   timestamptz NOT NULL DEFAULT clock_timestamp()
        );

        CREATE TABLE chaos_resume (
            seq         bigserial   NOT NULL PRIMARY KEY,
            instance_id uuid        NOT NULL,
            node        text        NOT NULL,
            pid         int         NOT NULL,
            frontier    int         NOT NULL,
            first_step  int         NOT NULL,
            resumed_at  timestamptz NOT NULL DEFAULT clock_timestamp()
        );

        CREATE INDEX chaos_resume_instance_idx ON chaos_resume (instance_id);

        CREATE TABLE chaos_control (
            id   int     NOT NULL PRIMARY KEY,
            stop boolean NOT NULL DEFAULT false
        );

        INSERT INTO chaos_control (id, stop) VALUES (1, false);
        """;

    /// <summary>The connection string, or null when none was supplied.</summary>
    public static string? ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionVariable) is { Length: > 0 } value
            ? value
            : null;

    /// <summary>Whether the run was opted in to.</summary>
    public static bool IsOptedIn =>
        Environment.GetEnvironmentVariable(OptInVariable) is { Length: > 0 } value
        && !string.Equals(value, "0", StringComparison.Ordinal)
        && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>Why a run was skipped, in a sentence a reader can act on.</summary>
    public static string SkipReason =>
        $"{OptInVariable} is not set, so no process was killed and nothing about QR2 has " +
        $"been verified. This rig SIGKILLs operating-system processes and takes minutes, so " +
        $"it is opt-in rather than part of the ordinary suite. Set {OptInVariable}=1 and " +
        $"{ConnectionVariable} to a PostgreSQL connection string to run it.";

    /// <summary>
    /// Decides whether this run may proceed, and refuses loudly rather than quietly.
    /// </summary>
    /// <returns>
    /// Null when the run may proceed; otherwise the reason, with <c>skipped</c> telling the
    /// caller whether that reason is a skip or a failure.
    /// </returns>
    public static (bool Skipped, string Reason)? Gate()
    {
        if (!IsOptedIn)
        {
            return (true, SkipReason);
        }

        if (ConnectionString is null)
        {
            return (false,
                $"{OptInVariable} is set, so this run promised to kill processes against a " +
                $"PostgreSQL server, and {ConnectionVariable} names none. This is a failure " +
                $"rather than a skip on purpose: a skip here would report a chaos run that " +
                $"never happened as a green job.");
        }

        try
        {
            using var dataSource = NpgsqlDataSource.Create(ConnectionString);
            using var connection = dataSource.OpenConnection();
            using var command = connection.CreateCommand();

            command.CommandText = "SELECT version()";
            _ = command.ExecuteScalar();

            return null;
        }
        catch (Exception failure) when (failure is NpgsqlException or ArgumentException)
        {
            return (false,
                $"{ConnectionVariable} is set and the server did not answer, so this run " +
                $"killed nothing: {failure.Message}");
        }
    }

    /// <summary>Creates the rig's own tables inside an already-migrated schema.</summary>
    /// <param name="dataSource">A data source whose search path is the arm's schema.</param>
    /// <param name="cancellationToken">Cancels the setup.</param>
    /// <returns>A task that completes when the tables exist.</returns>
    public static async Task CreateAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        await ExecuteAsync(dataSource, Ddl, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Registers every instance the arm intends to run, before any of them starts.</summary>
    /// <param name="dataSource">The arm's data source.</param>
    /// <param name="flows">How many instances to register.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the rows exist.</returns>
    /// <remarks>
    /// Pre-registration is what makes "zero lost instances" answerable. An instance whose id
    /// was minted inside a process that then died would leave no trace anywhere, and a rig
    /// that cannot enumerate what it asked for cannot notice that something went missing.
    /// </remarks>
    public static async Task RegisterInstancesAsync(
        NpgsqlDataSource dataSource,
        int flows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        await using var connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var writer = await connection
            .BeginBinaryImportAsync(
                "COPY chaos_instance (instance_id, ordinal) FROM STDIN (FORMAT BINARY)",
                cancellationToken)
            .ConfigureAwait(false);

        for (var ordinal = 0; ordinal < flows; ordinal++)
        {
            await writer.StartRowAsync(cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(Guid.CreateVersion7(), cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(ordinal, cancellationToken).ConfigureAwait(false);
        }

        _ = await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a statement against a schema.</summary>
    /// <param name="dataSource">The data source.</param>
    /// <param name="sql">The statement.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many rows it affected.</returns>
    public static async Task<int> ExecuteAsync(
        NpgsqlDataSource dataSource,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one value from a schema.</summary>
    /// <param name="dataSource">The data source.</param>
    /// <param name="sql">The query.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The first column of the first row, or null.</returns>
    public static async Task<object?> ScalarAsync(
        NpgsqlDataSource dataSource,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is DBNull ? null : value;
    }

    /// <summary>Reads a count.</summary>
    /// <param name="dataSource">The data source.</param>
    /// <param name="sql">A query whose first column is a count.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The count.</returns>
    public static async Task<long> CountAsync(
        NpgsqlDataSource dataSource,
        string sql,
        CancellationToken cancellationToken)
    {
        var value = await ScalarAsync(dataSource, sql, cancellationToken).ConfigureAwait(false);

        return value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>Drops a schema and everything in it.</summary>
    /// <param name="connectionString">How to reach the server.</param>
    /// <param name="schema">The schema to drop.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the schema is gone.</returns>
    public static async Task DropSchemaAsync(
        string connectionString,
        string schema,
        CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var command = dataSource.CreateCommand(
            """
            SELECT set_config('flowx.drop_schema', @schema, false);
            DO $$
            BEGIN
                EXECUTE format('DROP SCHEMA IF EXISTS %I CASCADE', current_setting('flowx.drop_schema'));
            END
            $$;
            """);

        _ = command.Parameters.AddWithValue("schema", schema);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
