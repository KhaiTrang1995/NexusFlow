using Microsoft.Extensions.Hosting;

namespace FlowX.Hosting;

/// <summary>
/// Runs <see cref="FlowStreamScan"/> on a jittered interval for as long as the node is serving.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FlowChangeService"/>'s shape and every one of its reasons: one loop for the node,
/// sleeping first, and a pass that swallows a source's exception rather than taking every
/// subscription down with it.
/// </para>
/// <para>
/// <strong>The interval is not the latency of a window.</strong> A window closes when the
/// watermark reaches its upper bound, and the watermark moves only when a record with a later
/// event time arrives. A stream that goes quiet leaves its last window open, indefinitely, and no
/// value here changes that — see
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0056-the-watermark-is-observed-never-wall-clock.md">ADR-0056</a>
/// for why that is the decision rather than an omission.
/// </para>
/// </remarks>
internal sealed class FlowStreamService : BackgroundService
{
    private readonly FlowStreamScan? _scan;
    private readonly TimeSpan _interval;

    /// <summary>Whether this host was deployed to run this sweep at all.</summary>
    private readonly bool _deployed;

    public FlowStreamService(FlowStreamScan? scan, FlowXOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _deployed = options.Sweeps.HasFlag(HostSweeps.Stream);

        _scan = scan;
        _interval = options.StreamScanInterval;
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
            // Shutdown. Every record this node read since its last checkpoint is re-read when a
            // node picks the subscription up, and a window whose flow committed is refused by the
            // journal's primary key rather than aggregated twice (ADR-0055).
        }
    }

    private async Task PassAsync(CancellationToken ct)
    {
        try
        {
            _ = await _scan!.RunOnceAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // FlowChangeService's reason: a store that is unreachable throws
        catch (Exception)      //   rather than refusing in some drivers, and that is a reason to
        {                      //   pass again in a second rather than to end the loop.
            // Next pass.
        }
#pragma warning restore CA1031
    }

    /// <summary>The configured interval, spread over ±25 % so nodes drift apart.</summary>
    private TimeSpan NextInterval() =>
        _interval * (0.75 + (Random.Shared.NextDouble() / 2));
}
