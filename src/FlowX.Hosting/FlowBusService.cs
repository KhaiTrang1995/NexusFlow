using Microsoft.Extensions.Hosting;

namespace FlowX.Hosting;

/// <summary>
/// Runs <see cref="FlowBusScan"/> on a jittered interval for as long as the node is serving.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One loop for every subscription on the node, and no thread per subscription.</strong>
/// The alternative — a long-lived blocking read per topic — is the shape most broker clients
/// suggest, and it makes shutdown, backpressure and "how many flows is this node running" three
/// separate problems per subscription instead of one for the node. A pass that asks each
/// subscription in turn has no state to lose and drains with the host.
/// </para>
/// <para>
/// <strong>It sleeps first</strong>, for the reason <see cref="FlowScheduleService"/> does: a node
/// that swept at startup would sweep while its own subscriptions are still being registered.
/// </para>
/// <para>
/// <strong>The interval is the worst-case latency from a message being published to its flow
/// starting</strong>, and it is a poll rather than a push on purpose. A push consumer is a
/// long-lived read whose cancellation, reconnection and lease renewal are all bespoke; a poll at
/// one second costs one round trip per subscription per second and is the same mechanism the
/// timer and schedule sweeps already use. A deployment that needs lower latency lowers
/// <see cref="FlowXOptions.BusScanInterval"/> and pays for it in round trips.
/// </para>
/// <para>
/// A no-op when nothing registered a subscription, or when no <c>IBusConsumer</c> was wired,
/// rather than a registration the composition root has to remember to omit.
/// </para>
/// </remarks>
internal sealed class FlowBusService : BackgroundService
{
    private readonly FlowBusScan? _scan;
    private readonly TimeSpan _interval;

    /// <summary>Whether this host was deployed to run this sweep at all.</summary>
    private readonly bool _deployed;

    public FlowBusService(FlowBusScan? scan, FlowXOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _deployed = options.Sweeps.HasFlag(HostSweeps.Bus);

        _scan = scan;
        _interval = options.BusScanInterval;
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

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(NextInterval(), stoppingToken).ConfigureAwait(false);

                if (_scan?.IsEnabled == true)
                {
                    await PassAsync(stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown. Nothing is lost: a delivery this node did not acknowledge is still
            // pending at the broker, and the next node to pass is offered it — which is the whole
            // reason acknowledgement follows the commit rather than preceding it (ADR-0036).
        }
    }

    private async Task PassAsync(CancellationToken ct)
    {
        try
        {
            // Not thrown on and, for now, not recorded, for the reason FlowScheduleService gives:
            // nothing in src/ takes a logger. The pass is a public method a host or a test can
            // call and inspect directly.
            _ = await _scan!.RunOnceAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A broker that is unreachable throws rather than refusing in some
        catch (Exception)      //   clients. That is a reason to pass again in a second, not to
        {                      //   take the background service — and with it every subscription
            // Next pass.      //   this node serves — down for the life of the process.
        }
#pragma warning restore CA1031
    }

    /// <summary>The configured interval, spread over ±25 % so nodes drift apart.</summary>
    /// <remarks>
    /// Nodes that polled in lockstep would all reach for the same partition lease at the same
    /// instant and all but one would be refused; drifting them apart means the first one usually
    /// wins uncontested.
    /// </remarks>
    private TimeSpan NextInterval() =>
        _interval * (0.75 + (Random.Shared.NextDouble() / 2));
}
