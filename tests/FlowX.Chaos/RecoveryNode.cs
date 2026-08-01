using FlowX.Hosting;
using FlowX.Postgres;
using FlowX.Runtime;
using Npgsql;

namespace FlowX.Chaos;

/// <summary>
/// The other side of the diagram in <c>docs/11-Distributed-Runtime.md §3</c>, as a process
/// rather than as a second object in one: it sweeps for instances a killed worker left
/// running, takes the lease, and finishes them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing here is a rig-specific recovery routine.</strong> It builds a
/// <c>FlowHost</c>, a <c>FlowCatalog</c> and a <c>FlowRecoveryScan</c> over the same
/// PostgreSQL adapters a deployment would, and calls <c>RunOnceAsync</c> on a jittered timer.
/// If this loop recovers an instance, the shipped code recovered it.
/// </para>
/// <para>
/// It stops when the coordinator sets <c>chaos_control.stop</c>, which is a row rather than a
/// signal because a signal is exactly what a <c>SIGKILL</c>ed worker cannot be relied on to
/// have left the database in a state consistent with.
/// </para>
/// </remarks>
internal static class RecoveryNode
{
    /// <summary>Sweeps until the coordinator says to stop.</summary>
    /// <param name="options">The run's parameters.</param>
    /// <param name="cancellationToken">Cancels the node.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(ChaosOptions options, CancellationToken cancellationToken)
    {
        var journalOptions = new PostgresJournalOptions { Schema = options.Schema };

        await using var dataSource = ServiceCollectionExtensions.BuildDataSource(
            options.ConnectionString, journalOptions);

        var durability = new FlowDurability(
            new PostgresFlowJournal(dataSource),
            new PostgresLeaseStore(dataSource),
            new PostgresRecoveryIndex(dataSource));

        var hostOptions = new FlowXOptions
        {
            ApplicationName = "FlowX.Chaos",
            NodeName = options.NodeName,
            LeaseTtl = options.LeaseTtl,
            LeaseRenewalInterval = TimeSpan.FromSeconds(
                Math.Max(1, options.LeaseTtl.TotalSeconds / 3)),
            RecoveryScanInterval = options.ScanInterval,
            RecoveryScanBatchSize = Math.Max(64, options.MaxConcurrentRecoveries * 2),
            MaxConcurrentRecoveries = options.MaxConcurrentRecoveries,
            ShutdownDrainTimeout = TimeSpan.FromSeconds(30),
        };

        var host = new FlowHost(new FlowEngine(SystemClock.Instance), hostOptions, durability);
        var dispatcher = new LedgerDispatcher(dataSource, options, resuming: true);
        var catalog = new FlowCatalog().Add(LedgerDispatcher.Plan(), dispatcher);
        var scan = new FlowRecoveryScan(host, catalog, durability, hostOptions, SystemClock.Instance);

        if (!scan.IsEnabled)
        {
            await Console.Error
                .WriteLineAsync("The recovery scan is disabled, so this node would sweep for nothing.")
                .ConfigureAwait(false);

            return 2;
        }

        var resumed = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (await IsStoppedAsync(dataSource, cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            var report = await scan.RunOnceAsync(cancellationToken).ConfigureAwait(false);

            resumed += report.Resumed;

            if (report.Error is not null)
            {
                await Console.Error
                    .WriteLineAsync($"{options.NodeName}: scan failed — {report.Error}")
                    .ConfigureAwait(false);
            }

            // The jitter FlowRecoveryService applies, applied here for the same reason: a
            // fleet started by one command must not sweep in lockstep for ever.
            var jitter = 0.75 + (Random.Shared.NextDouble() / 2);

            await Task.Delay(options.ScanInterval * jitter, cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine($"{options.NodeName}: resumed {resumed} instance(s).");

        return 0;
    }

    private static async Task<bool> IsStoppedAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT stop FROM chaos_control WHERE id = 1");

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is true;
    }
}
