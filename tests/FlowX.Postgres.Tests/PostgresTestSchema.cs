using Npgsql;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// One empty schema, its own data source, and the three adapters over it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A schema per test, not a truncate per test.</strong> The conformance suite
/// requires "a fresh, empty journal … no state may survive between them", and a schema is
/// the cheapest thing in PostgreSQL that genuinely satisfies it — no leftover sequence
/// values, no leftover lease tokens, and no ordering dependency between tests that would
/// let one of them pass because of what another left behind.
/// </para>
/// <para>
/// It also means the migrator runs once per test rather than once per run, which is a
/// useful accident: by the end of a conformance pass the schema has been created from
/// nothing several dozen times.
/// </para>
/// </remarks>
internal sealed class PostgresTestSchema : IAsyncDisposable
{
    private PostgresTestSchema(NpgsqlDataSource dataSource, PostgresJournalOptions options)
    {
        DataSource = dataSource;
        Options = options;
        Journal = new PostgresFlowJournal(dataSource);
        Leases = new PostgresLeaseStore(dataSource);
        RecoveryIndex = new PostgresRecoveryIndex(dataSource);
        Retention = new PostgresRetention(dataSource);
        Migrator = new PostgresMigrator(dataSource, options);
    }

    /// <summary>The data source, with <c>search_path</c> already pointing at the schema.</summary>
    public NpgsqlDataSource DataSource { get; }

    /// <summary>Where the tables live.</summary>
    public PostgresJournalOptions Options { get; }

    /// <summary>The journal under test.</summary>
    public PostgresFlowJournal Journal { get; }

    /// <summary>The lease store under test.</summary>
    public PostgresLeaseStore Leases { get; }

    /// <summary>The recovery scan's query, under test.</summary>
    public PostgresRecoveryIndex RecoveryIndex { get; }

    /// <summary>The retention sweeper under test.</summary>
    public PostgresRetention Retention { get; }

    /// <summary>The migrator for this schema.</summary>
    public PostgresMigrator Migrator { get; }

    /// <summary>
    /// Creates a schema and brings it to a version, or refuses to pretend it did.
    /// </summary>
    /// <param name="cancellationToken">Cancels the setup.</param>
    /// <param name="throughVersion">The schema version to migrate to. Defaults to the latest.</param>
    /// <returns>The prepared schema.</returns>
    /// <exception cref="InvalidOperationException">
    /// A database was promised by the environment and is not reachable.
    /// </exception>
    public static async ValueTask<PostgresTestSchema> CreateAsync(
        CancellationToken cancellationToken,
        int? throughVersion = null)
    {
        if (!PostgresTestDatabase.IsAvailable)
        {
            if (PostgresTestDatabase.IsPromised)
            {
                throw PostgresTestDatabase.Unreachable();
            }

            Assert.Skip(PostgresTestDatabase.Reason);
        }

        var options = new PostgresJournalOptions
        {
            Schema = "flowx_t_" + Guid.NewGuid().ToString("n"),
        };

        var dataSource = ServiceCollectionExtensions.BuildDataSource(
            PostgresTestDatabase.ConnectionString!, options);

        var schema = new PostgresTestSchema(dataSource, options);

        await schema.Migrator
            .MigrateAsync(throughVersion ?? PostgresMigrator.TargetVersion, cancellationToken)
            .ConfigureAwait(false);

        return schema;
    }

    /// <summary>Runs a statement against this schema.</summary>
    /// <param name="sql">The statement.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many rows it affected.</returns>
    public async Task<int> ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = await DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Reads a single value from this schema.</summary>
    /// <param name="sql">The query.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The first column of the first row, or null.</returns>
    public async Task<object?> ScalarAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = await DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = sql;

        var value = await command.ExecuteScalarAsync(cancellationToken);

        return value is DBNull ? null : value;
    }

    /// <summary>Drops the schema and everything in it.</summary>
    public async ValueTask DisposeAsync()
    {
        await using (var connection = await DataSource.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            // The same parameterised-identifier route the migrator uses to create it.
            command.CommandText =
                """
                SELECT set_config('flowx.drop_schema', @schema, false);
                DO $$
                BEGIN
                    EXECUTE format('DROP SCHEMA IF EXISTS %I CASCADE', current_setting('flowx.drop_schema'));
                END
                $$;
                """;

            command.Parameters.AddWithValue("schema", Options.Schema);

            await command.ExecuteNonQueryAsync();
        }

        await DataSource.DisposeAsync();
    }
}
