using Microsoft.Extensions.Hosting;

namespace FlowX.Hosting;

/// <summary>
/// Runs <see cref="FlowRecoveryScan"/> on a jittered interval for as long as the node is
/// serving.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The jitter is the point of having a loop at all.</strong> The sweep itself is
/// written to be safe when every node runs it; what a fixed interval adds is
/// <em>synchronisation</em> — a fleet started by one rollout shares a start time, so an
/// unjittered timer has all of them query the journal in the same millisecond, for ever.
/// Spreading each node's next sweep over ±25 % of the interval breaks that up within a few
/// cycles and costs nothing.
/// </para>
/// <para>
/// <strong>It sleeps first.</strong> A node that scanned at startup would scan while its own
/// instances are still being registered and while every other node in a rolling update is
/// doing the same, which is the worst moment available.
/// </para>
/// <para>
/// A no-op when the journal cannot be scanned, rather than a registration the composition
/// root has to remember to omit: whether recovery is possible is a property of the store
/// that was registered, and asking an application to keep a service list in step with that
/// is asking it to get it wrong.
/// </para>
/// </remarks>
internal sealed class FlowRecoveryService : BackgroundService
{
    private readonly FlowRecoveryScan? _scan;
    private readonly TimeSpan _interval;

    public FlowRecoveryService(FlowRecoveryScan? scan, FlowXOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _scan = scan?.IsEnabled == true ? scan : null;
        _interval = options.RecoveryScanInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_scan is null)
        {
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(NextInterval(), stoppingToken).ConfigureAwait(false);

                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown. The sweep in flight, if any, was cancelled with it, and a lease it
            // had taken is released by the host's drain rather than left to expire.
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            // The report is deliberately not thrown on and, for now, not recorded: nothing in
            // src/ takes a logger, and inventing a sink here would be the first. What a store
            // refuses is already a value on the report, and the sweep is a public method a
            // host or a test can call and inspect directly. Observability for this loop
            // belongs with the rest of the platform's, not ahead of it.
            _ = await _scan!.RunOnceAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A store that is unreachable throws rather than refusing.
        catch (Exception)      //   That is a reason to sweep again in ten seconds, not to
        {                      //   take the background service — and with it every later
            // Next sweep.     //   recovery on this node — down for the life of the process.
        }
#pragma warning restore CA1031
    }

    /// <summary>The configured interval, spread over ±25 % so nodes drift apart.</summary>
    private TimeSpan NextInterval() =>
        _interval * (0.75 + (Random.Shared.NextDouble() / 2));
}
