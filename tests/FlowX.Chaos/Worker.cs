using System.Globalization;
using FlowX.Postgres;
using FlowX.Runtime;
using Npgsql;
using NpgsqlTypes;

namespace FlowX.Chaos;

/// <summary>
/// One node: it claims instances, runs them durably against the shared database, and — when
/// the schedule says so — dies where it stands.
/// </summary>
/// <remarks>
/// <para>
/// The lease, the journal write and the engine are the real ones. What this class does not
/// use is <c>FlowHost.RunAsync</c>, for one reason: that method mints the instance id itself,
/// and an id minted inside a process that then dies cannot be enumerated afterwards. The rig
/// pre-registers every id and claims it from the database, which is what makes "zero lost
/// instances" a query rather than a hope. Everything else — <c>DurableLease.AcquireAsync</c>,
/// <c>BeginAsync</c>, <c>FlowEngine.ExecuteAsync</c> — is exactly what the host does inside.
/// </para>
/// <para>
/// A claim is written <em>before</em> the lease is acquired, so an instance whose worker died
/// between the claim and the first journal row is still visible as one that started. The
/// alternative leaves a hole in the accounting exactly where a crash is most likely.
/// </para>
/// </remarks>
internal static class Worker
{
    private const string ClaimSql =
        """
        UPDATE chaos_instance
           SET worker_node = @node, worker_pid = @pid, started_at = clock_timestamp()
         WHERE instance_id = (
               SELECT instance_id FROM chaos_instance
                WHERE started_at IS NULL
                ORDER BY ordinal
                  FOR UPDATE SKIP LOCKED
                LIMIT 1)
        RETURNING instance_id
        """;

    private const string FinishSql =
        """
        UPDATE chaos_instance
           SET finished_at = clock_timestamp(), outcome = @outcome
         WHERE instance_id = @instance
        """;

    /// <summary>Runs until there is nothing left to claim, or until the kill schedule fires.</summary>
    /// <param name="options">The run's parameters.</param>
    /// <param name="cancellationToken">Cancels the worker.</param>
    /// <returns>The process exit code. A killed worker never reaches it.</returns>
    public static async Task<int> RunAsync(ChaosOptions options, CancellationToken cancellationToken)
    {
        var journalOptions = new PostgresJournalOptions { Schema = options.Schema };

        await using var dataSource = ServiceCollectionExtensions.BuildDataSource(
            options.ConnectionString, journalOptions);

        var journal = new PostgresFlowJournal(dataSource);
        var leases = new PostgresLeaseStore(dataSource);
        var engine = new FlowEngine(SystemClock.Instance);
        var dispatcher = new LedgerDispatcher(dataSource, options, resuming: false);
        var plan = LedgerDispatcher.Plan();

        var policy = new LeasePolicy
        {
            Ttl = options.LeaseTtl,
            RenewalInterval = TimeSpan.FromSeconds(Math.Max(1, options.LeaseTtl.TotalSeconds / 3)),
        };

        var lanes = new Task[options.Concurrency];

        for (var lane = 0; lane < lanes.Length; lane++)
        {
            lanes[lane] = LaneAsync(
                new Lane(dataSource, journal, leases, engine, dispatcher, plan, policy, options),
                cancellationToken);
        }

        await Task.WhenAll(lanes).ConfigureAwait(false);

        return 0;
    }

    private static async Task LaneAsync(Lane lane, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var instance = await ClaimAsync(lane, cancellationToken).ConfigureAwait(false);

            if (instance is null)
            {
                return;
            }

            var outcome = await RunOneAsync(lane, instance.Value, cancellationToken)
                .ConfigureAwait(false);

            await FinishAsync(lane, instance.Value, outcome, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<Guid?> ClaimAsync(Lane lane, CancellationToken cancellationToken)
    {
        await using var command = lane.DataSource.CreateCommand(ClaimSql);

        _ = command.Parameters.AddWithValue("node", lane.Options.NodeName);
        _ = command.Parameters.AddWithValue("pid", NpgsqlDbType.Integer, Environment.ProcessId);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is Guid claimed ? claimed : null;
    }

    private static async Task<string> RunOneAsync(
        Lane lane,
        Guid instance,
        CancellationToken cancellationToken)
    {
        var acquired = await DurableLease
            .AcquireAsync(lane.Leases, instance, lane.Options.NodeName, lane.Policy, cancellationToken)
            .ConfigureAwait(false);

        if (acquired.IsFailure)
        {
            return "lease:" + acquired.Error.Code;
        }

        var lease = acquired.Value;

        try
        {
            var invocation = new FlowInvocation(
                "chaos-" + instance.ToString("n", CultureInfo.InvariantCulture),
                instance.ToString("d", CultureInfo.InvariantCulture),
                TenantId: null,
                Deadline: DateTimeOffset.UtcNow + lane.Plan.Flow.Deadline);

            var begun = await lease
                .BeginAsync(lane.Journal, lane.Plan, invocation, input: null, cancellationToken)
                .ConfigureAwait(false);

            if (begun.IsFailure)
            {
                return "begin:" + begun.Error.Code;
            }

            var result = await lane.Engine
                .ExecuteAsync(lane.Plan, lane.Dispatcher, invocation, begun.Value, cancellationToken)
                .ConfigureAwait(false);

            return result.IsSuccess ? "completed" : "failed:" + result.Error!.Code;
        }
        finally
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task FinishAsync(
        Lane lane,
        Guid instance,
        string outcome,
        CancellationToken cancellationToken)
    {
        await using var command = lane.DataSource.CreateCommand(FinishSql);

        _ = command.Parameters.AddWithValue("instance", NpgsqlDbType.Uuid, instance);
        _ = command.Parameters.AddWithValue("outcome", outcome);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Everything one concurrent lane of a worker needs, gathered once.</summary>
    private sealed record Lane(
        NpgsqlDataSource DataSource,
        PostgresFlowJournal Journal,
        PostgresLeaseStore Leases,
        FlowEngine Engine,
        LedgerDispatcher Dispatcher,
        ExecutionPlan Plan,
        LeasePolicy Policy,
        ChaosOptions Options);
}
