using FlowX.Postgres;
using Npgsql;
using Xunit;

namespace FlowX.Cli.Tests;

/// <summary>
/// One empty schema with the journal's real adapter over it, so a <c>replay</c> test reads
/// rows the engine could actually have written.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every row goes in through <see cref="PostgresFlowJournal"/>, never through raw
/// <c>INSERT</c>.</strong> The whole point of these tests is that
/// <c>src/FlowX.Cli/Replay</c> reads the shape the adapter writes — the accepted cost in
/// [ADR-0020](../../docs/adr/ADR-0020-cli-reads-the-journal-as-rows.md)) — and a test that
/// hand-wrote its own rows could pass against a shape the adapter never produces. That is
/// the same argument <c>PostgresTestSchema</c> makes for the same reason.
/// </para>
/// <para>
/// <strong>A schema per test.</strong> The CLI is told which one with <c>--schema</c>, so
/// no case can see another's instances and none of them can see a developer's real journal.
/// </para>
/// </remarks>
internal sealed class ReplayFixture : IAsyncDisposable
{
    private ReplayFixture(NpgsqlDataSource dataSource, string schema)
    {
        DataSource = dataSource;
        Schema = schema;
        Journal = new PostgresFlowJournal(dataSource);
    }

    /// <summary>The data source, with <c>search_path</c> already pointing at the schema.</summary>
    public NpgsqlDataSource DataSource { get; }

    /// <summary>The schema this fixture's tables live in.</summary>
    public string Schema { get; }

    /// <summary>The adapter every row is written through.</summary>
    public PostgresFlowJournal Journal { get; }

    /// <summary>The token every write in these tests carries.</summary>
    public static FencingToken Token => new(1);

    /// <summary>Creates a migrated schema, or refuses to pretend it did.</summary>
    /// <param name="cancellationToken">Cancels the setup.</param>
    /// <returns>The prepared fixture.</returns>
    /// <exception cref="InvalidOperationException">
    /// A database was promised by the environment and is not reachable.
    /// </exception>
    public static async ValueTask<ReplayFixture> CreateAsync(CancellationToken cancellationToken)
    {
        if (!CliPostgresDatabase.IsAvailable)
        {
            if (CliPostgresDatabase.IsPromised)
            {
                throw CliPostgresDatabase.Unreachable();
            }

            Assert.Skip(CliPostgresDatabase.Reason);
        }

        var options = new PostgresJournalOptions
        {
            Schema = "flowx_cli_" + Guid.NewGuid().ToString("n"),
        };

        var dataSource = ServiceCollectionExtensions.BuildDataSource(
            CliPostgresDatabase.ConnectionString!, options);

        var fixture = new ReplayFixture(dataSource, options.Schema);

        await new PostgresMigrator(dataSource, options)
            .MigrateAsync(cancellationToken)
            .ConfigureAwait(false);

        return fixture;
    }

    /// <summary>Opens an instance exactly as <c>FlowHost</c> does.</summary>
    /// <param name="flowId">The flow's business identity.</param>
    /// <param name="tenantId">The partition key, or null.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The instance that was opened.</returns>
    /// <remarks>
    /// <strong><see cref="FlowInstanceStart.Input"/> is left at
    /// <see cref="JournalPayload.Empty"/>, which is what the host passes.</strong>
    /// <c>FlowHost</c> calls <c>BeginAsync(…, input: null, …)</c>, so
    /// <c>flow_instance.input</c> is NULL on every row that has ever been written. That is a
    /// defect being fixed elsewhere, and these tests deliberately do not depend on the fix
    /// landing: they assert what the verb does with the NULL, which is a question that
    /// survives the column being populated.
    /// </remarks>
    public async ValueTask<Guid> StartAsync(
        string flowId,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var instanceId = Guid.CreateVersion7();

        var started = await Journal.StartAsync(
            new FlowInstanceStart
            {
                InstanceId = instanceId,
                FlowId = flowId,
                FlowVersion = "1.0.0",
                Token = Token,
                TenantId = tenantId,
                CorrelationId = "corr-" + instanceId.ToString("n")[..8],
            },
            cancellationToken).ConfigureAwait(false);

        started.IsSuccess.ShouldBeTrueOrThrow(started);

        return instanceId;
    }

    /// <summary>Appends one step row.</summary>
    /// <param name="instanceId">The instance the step belongs to.</param>
    /// <param name="scope">The iteration scope, <c>default</c> for the flow body.</param>
    /// <param name="stepId">The step's index in the plan.</param>
    /// <param name="capability">The capability invoked, without its version.</param>
    /// <param name="outcome">How the attempt ended.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <param name="attempt">Which attempt this row records.</param>
    /// <param name="duration">How long the attempt took.</param>
    /// <param name="capture">What the step read that it could not have computed.</param>
    /// <param name="state">The instance's new state, when this step changed it.</param>
    /// <returns>Nothing; the row is the effect.</returns>
    public async ValueTask CommitAsync(
        Guid instanceId,
        StepScope scope,
        int stepId,
        string capability,
        JournalOutcome outcome,
        CancellationToken cancellationToken,
        int attempt = 1,
        TimeSpan duration = default,
        NondeterminismCapture? capture = null,
        FlowInstanceState? state = null)
    {
        var committed = await Journal.CommitAsync(
            new StepCommit
            {
                Key = new StepKey(instanceId, scope, stepId, attempt),
                Token = Token,
                CapabilityId = capability,
                CapabilityVersion = "1.0.0",
                Outcome = outcome,
                Duration = duration,
                Nondeterminism = capture ?? NondeterminismCapture.None,
                State = state,
            },
            cancellationToken).ConfigureAwait(false);

        committed.IsSuccess.ShouldBeTrueOrThrow(committed);
    }

    /// <summary>Moves the instance to a terminal state.</summary>
    /// <param name="instanceId">The instance to finish.</param>
    /// <param name="state">The state to leave it in.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Nothing; the row is the effect.</returns>
    public async ValueTask CompleteAsync(Guid instanceId,
        FlowInstanceState state,
        CancellationToken cancellationToken)
    {
        var completed = await Journal
            .CompleteAsync(instanceId, Token, state, JournalPayload.Empty, wake: null, cancellationToken)
            .ConfigureAwait(false);

        completed.IsSuccess.ShouldBeTrueOrThrow(completed);
    }

    /// <summary>Drops the schema and its data source.</summary>
    /// <returns>A task that completes when the schema is gone.</returns>
    public async ValueTask DisposeAsync()
    {
        try
        {
            var connection = await DataSource.OpenConnectionAsync().ConfigureAwait(false);
            await using var closing = connection.ConfigureAwait(false);

            using var command = connection.CreateCommand();

            command.CommandText = $"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE";

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            // The server went away mid-test. The schema is named after a fresh Guid, so
            // leaving it behind costs a row in pg_namespace and nothing else — and throwing
            // here would replace the test's own failure with this one.
        }

        await DataSource.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Turns a failed journal write into a readable arrangement failure.</summary>
internal static class ResultAssertions
{
    /// <summary>Throws with the error when a journal call used for arrangement failed.</summary>
    /// <typeparam name="T">The result's value type.</typeparam>
    /// <param name="success">Whether the call succeeded.</param>
    /// <param name="result">The result, read for its error when it did not.</param>
    /// <remarks>
    /// An arrangement that fails silently produces a test failure about the assertion rather
    /// than about the setup, and the error a journal returns says exactly what went wrong.
    /// </remarks>
    public static void ShouldBeTrueOrThrow<T>(this bool success, Result<T> result)
    {
        if (!success)
        {
            throw new InvalidOperationException(
                $"Arranging the journal failed: {result.Error.Code} — {result.Error.Message}");
        }
    }
}
