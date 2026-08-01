using Microsoft.Extensions.Hosting;

namespace FlowX.Hosting;

/// <summary>
/// Runs <see cref="FlowScheduleScan"/> on a jittered interval for as long as the node is
/// serving.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One loop for every schedule on the node, and no timer per schedule.</strong> The
/// alternative — a scheduled callback per <c>[CronTrigger]</c> — is a process-resident object
/// whose next firing is a decision one node has already made, so a node that dies between
/// arming and firing loses the occurrence, and a node that comes back has to work out what it
/// missed anyway. A sweep that recomputes from the expression has no state to lose.
/// </para>
/// <para>
/// <strong>It sleeps first</strong>, for the reason <see cref="FlowTimerService"/> does: a
/// node that swept at startup would sweep while its own schedules are still being registered.
/// </para>
/// <para>
/// <strong>The interval is the resolution of every schedule on the node, and the lower bound
/// on how late a firing is.</strong> A schedule at <c>0 2 * * *</c> under a ten-second sweep
/// fires between 02:00:00 and 02:00:10 — which is the same promise a durable timer makes and
/// the only one a sweep can keep.
/// </para>
/// <para>
/// A no-op when nothing registered a schedule, rather than a registration the composition root
/// has to remember to omit: whether this node fires anything is a property of what was
/// registered, and asking an application to keep a service list in step with that is asking it
/// to get it wrong.
/// </para>
/// </remarks>
internal sealed class FlowScheduleService : BackgroundService
{
    private readonly FlowScheduleScan? _scan;
    private readonly TimeSpan _interval;

    public FlowScheduleService(FlowScheduleScan? scan, FlowXOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _scan = scan;
        _interval = options.ScheduleScanInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Read at the first tick rather than in the constructor: schedules are registered from
        // the composition root after the container is built, so a node that decided at
        // construction time would decide before anything had been registered and never sweep.
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(NextInterval(), stoppingToken).ConfigureAwait(false);

                if (_scan?.IsEnabled == true)
                {
                    await SweepAsync(stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown. An occurrence that was due and did not get fired here is still due, and
            // the next node to sweep computes it from the same expression — which is the whole
            // reason nothing is armed.
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            // Not thrown on and, for now, not recorded, for the reason FlowTimerService gives:
            // nothing in src/ takes a logger. The sweep is a public method a host or a test can
            // call and inspect directly.
            _ = await _scan!.RunOnceAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A store that is unreachable throws rather than refusing. That
        catch (Exception)      //   is a reason to sweep again in ten seconds, not to take the
        {                      //   background service — and with it every schedule this node
            // Next sweep.     //   would ever have fired — down for the life of the process.
        }
#pragma warning restore CA1031
    }

    /// <summary>The configured interval, spread over ±25 % so nodes drift apart.</summary>
    /// <remarks>
    /// Cheaper here than anywhere else it is applied. Nodes that swept in lockstep would all
    /// compute the same occurrence at the same instant and all race for one lease; drifting
    /// them apart means the first one usually wins uncontested and the rest are refused by the
    /// journal, which is a read rather than a write.
    /// </remarks>
    private TimeSpan NextInterval() =>
        _interval * (0.75 + (Random.Shared.NextDouble() / 2));
}
