using Microsoft.Extensions.Hosting;

namespace FlowX.Hosting;

/// <summary>
/// Runs <see cref="FlowTimerScan"/> on a jittered interval for as long as the node is serving.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One loop for every timer on the node, and that is the design rather than a
/// simplification.</strong> The alternative is a scheduled callback per waiting instance,
/// which is a process-resident object per row: a million parked onboardings would be a million
/// timers, lost on the next restart, and the row would still have to be the truth afterwards.
/// A sweep reads the rows that are already the truth.
/// </para>
/// <para>
/// <strong>It sleeps first</strong>, for the reason <see cref="FlowRecoveryService"/> does: a
/// node that swept at startup would sweep while its own flows are still being registered and
/// while every other node in a rolling update is doing the same.
/// </para>
/// <para>
/// A no-op when the journal cannot be swept, rather than a registration the composition root
/// has to remember to omit: whether timers can fire is a property of the store that was
/// registered, and asking an application to keep a service list in step with that is asking it
/// to get it wrong.
/// </para>
/// </remarks>
internal sealed class FlowTimerService : BackgroundService
{
    private readonly FlowTimerScan? _scan;
    private readonly TimeSpan _interval;

    public FlowTimerService(FlowTimerScan? scan, FlowXOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _scan = scan?.IsEnabled == true ? scan : null;
        _interval = options.TimerScanInterval;
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
            // Shutdown. The sweep in flight, if any, was cancelled with it, and a lease it had
            // taken is released by the host's drain rather than left to expire. An instance
            // whose wait was due and did not get woken is still due, and the next node to
            // sweep finds it — which is the whole reason the instant is on the row.
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            // Not thrown on and, for now, not recorded, for the reason FlowRecoveryService
            // gives: nothing in src/ takes a logger, and inventing a sink here would be the
            // first. The store's refusal is a value on the report, and the sweep is a public
            // method a host or a test can call and inspect directly.
            _ = await _scan!.RunOnceAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A store that is unreachable throws rather than refusing. That
        catch (Exception)      //   is a reason to sweep again in ten seconds, not to take the
        {                      //   background service — and with it every timer this node
            // Next sweep.     //   would ever have fired — down for the life of the process.
        }
#pragma warning restore CA1031
    }

    /// <summary>The configured interval, spread over ±25 % so nodes drift apart.</summary>
    private TimeSpan NextInterval() =>
        _interval * (0.75 + (Random.Shared.NextDouble() / 2));
}
