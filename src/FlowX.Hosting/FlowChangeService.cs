using Microsoft.Extensions.Hosting;

namespace FlowX.Hosting;

/// <summary>
/// Runs <see cref="FlowChangeScan"/> on a jittered interval for as long as the node is serving.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FlowBusService"/>'s shape and every one of its reasons: one loop for the node rather
/// than a thread per subscription, sleeping first so a node does not sweep while its own
/// subscriptions are still registering, and a pass that swallows a store's exception rather than
/// taking every subscription down with it.
/// </para>
/// <para>
/// <strong>The interval is the worst-case latency from a change committing to its flow
/// starting</strong>, on top of whatever the feed's own visibility barrier adds — which for the
/// PostgreSQL feed is the lifetime of the oldest transaction still open when the change landed
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0048-a-change-feed-advances-a-cursor.md">ADR-0048</a>).
/// Lowering <see cref="FlowXOptions.ChangeScanInterval"/> does not shorten that half.
/// </para>
/// <para>
/// A no-op when nothing registered a subscription, or when no <c>IChangeFeed</c> was wired, rather
/// than a registration the composition root has to remember to omit.
/// </para>
/// </remarks>
internal sealed class FlowChangeService : BackgroundService
{
    private readonly FlowChangeScan? _scan;
    private readonly TimeSpan _interval;

    /// <summary>Whether this host was deployed to run this sweep at all.</summary>
    private readonly bool _deployed;

    public FlowChangeService(FlowChangeScan? scan, FlowXOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _deployed = options.Sweeps.HasFlag(HostSweeps.Change);

        _scan = scan;
        _interval = options.ChangeScanInterval;
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
            // Shutdown. Nothing is lost: a change whose flow ran but whose cursor was not
            // committed is offered again, and the derived instance id makes that a deduplication
            // rather than a second run (ADR-0048, ADR-0049).
        }
    }

    private async Task PassAsync(CancellationToken ct)
    {
        try
        {
            // Not thrown on and, for now, not recorded, for the reason FlowBusService gives:
            // nothing in src/ takes a logger. The pass is a public method a host or a test can
            // call and inspect directly.
            _ = await _scan!.RunOnceAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A store that is unreachable throws rather than refusing in some
        catch (Exception)      //   drivers. That is a reason to pass again in a second, not to
        {                      //   take every subscription this node serves down for the life of
            // Next pass.      //   the process.
        }
#pragma warning restore CA1031
    }

    /// <summary>The configured interval, spread over ±25 % so nodes drift apart.</summary>
    /// <remarks>
    /// Nodes that polled in lockstep would all reach for the same subscription lease at the same
    /// instant and all but one would be refused; drifting them apart means the first one usually
    /// wins uncontested.
    /// </remarks>
    private TimeSpan NextInterval() =>
        _interval * (0.75 + (Random.Shared.NextDouble() / 2));
}
