using Npgsql;

namespace FlowX.Durability.Bench;

/// <summary>
/// The gate that decides whether this run may happen, and the schema it happens in.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two outcomes, and the middle one is a failure.</strong> Nobody asking for a
/// database is a reason to skip; somebody asking for one that is not there is a defect in the
/// run. <c>tests/FlowX.Postgres.Tests/PostgresTestDatabase.cs</c> settled that distinction for
/// this repository and this rig keeps it, because a rig that skipped quietly would report a
/// green job for a run that measured nothing — which is precisely the state B7 and B8 were in
/// before this project existed.
/// </para>
/// <para>
/// <strong>Its own schema, dropped at the end.</strong> The rig writes several thousand rows
/// per run and the numbers depend on how big the tables are, so it cannot share a schema with
/// anything and cannot leave one behind for the next run to inherit.
/// </para>
/// </remarks>
internal static class LatencyDatabase
{
    /// <summary>The variable that supplies a connection string.</summary>
    public const string ConnectionVariable = "FLOWX_POSTGRES_CONNECTION";

    /// <summary>The connection string, or null when none was supplied.</summary>
    public static string? ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionVariable) is { Length: > 0 } value
            ? value
            : null;

    /// <summary>Why a run was skipped, in a sentence a reader can act on.</summary>
    public static string SkipReason =>
        $"{ConnectionVariable} is not set, so nothing was measured and B7 and B8 remain " +
        $"unverified by this run. Set it to a PostgreSQL connection string to measure them " +
        $"— for example 'Host=localhost;Port=5432;Username=postgres;Database=postgres', with " +
        $"a password added when the server asks for one. The account needs CREATE on the " +
        $"database, because the rig is given a schema of its own.";

    /// <summary>
    /// Decides whether this run may proceed, and refuses loudly rather than quietly.
    /// </summary>
    /// <returns>
    /// Null when the run may proceed; otherwise the reason, with <c>Skipped</c> telling the
    /// caller whether that reason is a skip or a failure.
    /// </returns>
    public static (bool Skipped, string Reason)? Gate()
    {
        var connectionString = ConnectionString;

        if (connectionString is null)
        {
            return (true, SkipReason);
        }

        try
        {
            using var dataSource = NpgsqlDataSource.Create(connectionString);
            using var connection = dataSource.OpenConnection();
            using var command = connection.CreateCommand();

            command.CommandText = "SELECT version()";

            return command.ExecuteScalar() is string
                ? null
                : (false, $"{ConnectionVariable} names a server that answered nothing.");
        }
        catch (Exception failure) when (failure is NpgsqlException or ArgumentException)
        {
            return (false,
                $"{ConnectionVariable} is set, so this run promised a PostgreSQL server, and " +
                $"none answered: {failure.Message}. This is a failure rather than a skip on " +
                $"purpose — a skip here would report an unmeasured B7 and B8 as a green job.");
        }
    }

    /// <summary>The server's version string, for the results document.</summary>
    /// <param name="connectionString">How to reach the server.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What <c>version()</c> answered.</returns>
    /// <remarks>
    /// Recorded because both budgets are properties of a database as much as of this code: a
    /// p99 read against a different major version, or a server with a different
    /// <c>synchronous_commit</c>, is a different measurement wearing the same name.
    /// </remarks>
    public static async Task<string> ServerVersionAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var command = dataSource.CreateCommand("SELECT version()");

        var version = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return version as string ?? "unknown";
    }

    /// <summary>
    /// How the server is set to acknowledge a commit, which is the single setting B7 means.
    /// </summary>
    /// <param name="connectionString">How to reach the server.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The value of <c>synchronous_commit</c>.</returns>
    /// <remarks>
    /// <strong>Without this the number cannot be read.</strong> B7 is a durable commit — a
    /// step boundary that survives the loss of the process that wrote it. A server running
    /// with <c>synchronous_commit = off</c> answers before the write reaches disk and will
    /// produce a p99 several times better than the same hardware honestly can, for a commit
    /// that is not durable. The results document carries the setting so that a fast number
    /// obtained that way is visibly not a B7 number.
    /// </remarks>
    public static async Task<string> SynchronousCommitAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var command = dataSource.CreateCommand("SHOW synchronous_commit");

        var setting = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return setting as string ?? "unknown";
    }

    /// <summary>
    /// Refuses a run that would ask for more connections than the server will give.
    /// </summary>
    /// <param name="connectionString">How to reach the server.</param>
    /// <param name="writers">Concurrent writers the B7 arm will open.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The reason to refuse, or null when the run may proceed.</returns>
    /// <remarks>
    /// Without this, asking for more writers than <c>max_connections</c> allows ends in a
    /// FATAL 53300 from PostgreSQL partway through the arm — a stack trace that names
    /// <c>InitProcess</c> and does not name the knob. The rig is meant to be turned up until
    /// it saturates something, so hitting this limit is an ordinary thing to do and deserves
    /// a sentence rather than a crash.
    /// </remarks>
    public static async Task<string?> TooManyWritersAsync(
        string connectionString,
        int writers,
        CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var command = dataSource.CreateCommand(
            "SELECT current_setting('max_connections')::int - "
            + "(SELECT count(*) FROM pg_stat_activity)::int");

        var headroom = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as int?;

        return headroom is { } free && writers >= free
            ? $"The run asks for {writers} concurrent writers and the server has room for "
              + $"{free} more connections. Lower --writers, or raise max_connections. "
              + "Refusing before the arm starts rather than failing partway through it."
            : null;
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
