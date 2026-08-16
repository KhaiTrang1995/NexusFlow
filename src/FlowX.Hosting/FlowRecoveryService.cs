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
/// <strong>A registered <see cref="ISweepSignal"/> shortens the wait and changes nothing
/// else.</strong> A store that can say when a lease lapses ends the wait at that instant, so
/// the interval stops being how long a dead node's instances wait <em>on top of</em>
/// <see cref="FlowXOptions.LeaseTtl"/> and becomes the ceiling on it. The sweep is the same
/// sweep over the same rows — a wake this node never hears is a takeover that happens on the
/// interval, which is what every release before this one did.
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
    private readonly ISweepSignal? _wake;
    private readonly TimeSpan _interval;

    /// <summary>Whether this host was deployed to run this sweep at all.</summary>
    private readonly bool _deployed;

    public FlowRecoveryService(
        FlowRecoveryScan? scan, FlowXOptions options, ISweepSignal? wake = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _deployed = options.Sweeps.HasFlag(HostSweeps.Recovery);

        _scan = scan?.IsEnabled == true ? scan : null;
        _wake = wake;
        _interval = options.RecoveryScanInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // What this host was deployed to do, which is a different question from what it
        // is capable of doing -- HostSweeps says why the two are kept apart. Checked
        // before anything else so an opted-out host starts no loop and takes no lock.
        if (!_deployed)
        {
            return;
        }

        if (_scan is null)
        {
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await WaitAsync(stoppingToken).ConfigureAwait(false);

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

    /// <summary>The pause before the next sweep: the interval, or a wake that arrives first.</summary>
    /// <remarks>
    /// <see cref="FlowTimerService"/>'s shape and its reason — the
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> this replaces is still what runs when
    /// no signal was registered.
    /// </remarks>
    private Task WaitAsync(CancellationToken stoppingToken) =>
        _wake is null
            ? Task.Delay(NextInterval(), stoppingToken)
            : _wake.WaitAsync(SweepKind.Recovery, NextInterval(), stoppingToken);

    /// <summary>The configured interval, spread over ±25 % so nodes drift apart.</summary>
    private TimeSpan NextInterval() =>
        _interval * (0.75 + (Random.Shared.NextDouble() / 2));
}
