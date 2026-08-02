using System.Diagnostics;
using FlowX.Observability;
using FlowX.Runtime;

namespace FlowX.Hosting;

/// <summary>
/// One sweep for instances whose declared wait has come due, and the resumption of as many of
/// them as this node has room for.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is what arms a durable timer, and it is deliberately not a timer.</strong>
/// Nothing here sleeps for a flow: a <c>.Delay(TimeSpan.FromDays(7))</c> is a row carrying an
/// instant, and this is the query that finds the rows whose instant has passed. A
/// <c>Task.Delay</c> per waiting instance would hold a process and a scheduler entry for seven
/// days to reproduce what one indexed column already says, and would lose every one of them
/// when the node restarted.
/// </para>
/// <para>
/// <strong>It resumes through <see cref="FlowHost.ResumeAsync(Guid, FlowRegistration, string, CancellationToken)"/>,
/// which is the same call <see cref="FlowRecoveryScan"/> makes and the same one
/// <c>FlowHost.SignalAsync</c> is.</strong> There is no third way into an instance. The lease
/// is acquired, its token raises the fence, the frontier is read, and the engine walks the
/// plan and decides for itself whether the wait it is standing at is over — this sweep only
/// says "look again", which is exactly what a resume carrying no signal has always meant.
/// </para>
/// <para>
/// <strong>The anti-stampede measures are <see cref="FlowRecoveryScan"/>'s, and they are the
/// same measures because it is the same shape of sweep.</strong> The candidate set is filtered
/// at the store, the page is bounded and this node's appetite is smaller still, the batch is
/// walked from a random offset so ten nodes handed one page do not all contend for its first
/// row, losing the lease is a free skip, and the caller jitters the interval. One difference:
/// a due instance stays due until something moves it, so a node that loses the race does not
/// simply forget it — it is still in the next page, behind whatever the winner has since
/// resumed and rewritten.
/// </para>
/// <para>
/// <strong>A candidate this node cannot run is left alone</strong>, for the reason a recovery
/// candidate is: an instance is pinned to the flow version it started with, and taking a lease
/// this node could not use would deny the instance to one that can for a whole TTL, every
/// sweep.
/// </para>
/// </remarks>
public sealed class FlowTimerScan
{
    private readonly FlowHost _host;
    private readonly FlowCatalog _catalog;
    private readonly FlowDurability _durability;
    private readonly FlowXOptions _options;
    private readonly IClock _clock;

    /// <summary>Builds a sweep over one host's journal, lease store and catalogue.</summary>
    /// <param name="host">Where a woken instance is resumed, so it is counted and drained.</param>
    /// <param name="catalog">Which flows, at which versions, this node can run.</param>
    /// <param name="durability">The journal and lease store, and the index to sweep.</param>
    /// <param name="options">The validated host options.</param>
    /// <param name="clock">
    /// The runtime's clock, so "due" is measured against the same instant the step loop
    /// measures it against. Asking the database for <c>now()</c> instead would put the
    /// decision on two clocks and make a suite that winds time forward untestable.
    /// </param>
    public FlowTimerScan(
        FlowHost host,
        FlowCatalog catalog,
        FlowDurability durability,
        FlowXOptions options,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(durability);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _host = host;
        _catalog = catalog;
        _durability = durability;
        _options = options;
        _clock = clock;
    }

    /// <summary>Whether this host is able to sweep at all.</summary>
    /// <remarks>
    /// False when the journal cannot answer the query. That host still runs durable flows and
    /// still parks them; what it does not do is wake them, so a <c>.Delay(...)</c> waits until
    /// something else resumes the instance and an expired <c>.OnTimeout(...)</c> never fires.
    /// A deployment that has declined <c>ITimerIndex</c> and writes flows with timers in them
    /// has made two decisions that disagree, and this is where it can find that out.
    /// </remarks>
    public bool IsEnabled => _durability.CanWake;

    /// <summary>Runs one sweep and returns what it did.</summary>
    /// <param name="ct">Cancels the sweep, and every resume it started.</param>
    /// <returns>
    /// The counts, and the store's error when the query itself failed. A sweep that found
    /// nothing is the ordinary result and not an error.
    /// </returns>
    /// <remarks>
    /// Awaits the instances it woke, for the reason <see cref="FlowRecoveryScan.RunOnceAsync"/>
    /// does: waking is executing, so a sweep can last as long as a flow does, and that is the
    /// property that bounds concurrency without a second counter to keep correct.
    /// </remarks>
    public async ValueTask<TimerScanReport> RunOnceAsync(CancellationToken ct = default)
    {
        // The sweep's own span, for the reason FlowRecoveryScan.RunOnceAsync opens one: a woken
        // instance's flow span otherwise has no parent, and "why did this instance run at 03:14"
        // is the first question asked of a flow nobody triggered.
        using var span = FlowXTelemetry.Source.StartActivity("timer scan", ActivityKind.Internal);

        if (_durability.TimerIndex is not { } index || _host.IsDraining)
        {
            return Tagged(span, TimerScanReport.Nothing);
        }

        var query = new DueInstanceQuery
        {
            // Now, with no grace period. A parked instance has no owner to wait for, so
            // there is nothing here for the lease-TTL allowance a recovery scan needs.
            DueBefore = _clock.UtcNow,
            Limit = _options.TimerScanBatchSize,
        };

        var listed = await index.ListDueAsync(query, ct).ConfigureAwait(false);

        if (listed.IsFailure)
        {
            return Tagged(span, TimerScanReport.Nothing with { Error = listed.Error });
        }

        var candidates = listed.Value;

        if (candidates.Count == 0)
        {
            return Tagged(span, TimerScanReport.Nothing);
        }

        var offset = Random.Shared.Next(candidates.Count);
        var capacity = _options.MaxConcurrentRecoveries;

        List<Task<Attempt>>? wakes = null;
        var examined = 0;
        var notRunnable = 0;

        for (var i = 0; i < candidates.Count && (wakes?.Count ?? 0) < capacity; i++)
        {
            var candidate = candidates[(i + offset) % candidates.Count];

            examined++;

            if (!_catalog.TryGet(candidate.FlowId, candidate.FlowVersion, out var registration))
            {
                notRunnable++;
                continue;
            }

            wakes ??= new List<Task<Attempt>>(capacity);
            wakes.Add(WakeAsync(candidate.InstanceId, registration, candidate.TenantId, ct));
        }

        if (wakes is null)
        {
            return Tagged(
                span, TimerScanReport.Nothing with { Examined = examined, NotRunnable = notRunnable });
        }

        var attempts = await Task.WhenAll(wakes).ConfigureAwait(false);

        var woken = 0;
        var contended = 0;
        var failed = 0;

        foreach (var attempt in attempts)
        {
            switch (attempt)
            {
                case Attempt.Woken:
                    woken++;
                    break;
                case Attempt.Contended:
                    contended++;
                    break;
                default:
                    failed++;
                    break;
            }
        }

        return Tagged(span, new TimerScanReport
        {
            Examined = examined,
            Woken = woken,
            Contended = contended,
            NotRunnable = notRunnable,
            Failed = failed,
        });
    }

    /// <summary>Puts what a sweep did onto its span, and hands the report back unchanged.</summary>
    /// <remarks>
    /// The mirror of <c>FlowRecoveryScan.Tagged</c>, and separate from it for the same reason
    /// the two sweeps are separate types: they ask opposite questions of disjoint sets of rows,
    /// and a shared helper would have to carry both vocabularies to name either.
    /// </remarks>
    private static TimerScanReport Tagged(Activity? span, in TimerScanReport report)
    {
        if (span is not null)
        {
            span.SetTag("flowx.scan.examined", report.Examined);
            span.SetTag("flowx.scan.woken", report.Woken);
            span.SetTag("flowx.scan.contended", report.Contended);
            span.SetTag("flowx.scan.not_runnable", report.NotRunnable);
            span.SetTag("flowx.scan.failed", report.Failed);
        }

        return report;
    }

    /// <summary>
    /// One wake, classified by whether this node became the writer — not by what the instance
    /// then did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An instance that woke, ran its escalation and failed has been woken: that is what the
    /// author declared should happen when the signal never came, and counting it as a sweep
    /// failure would make the one metric an operator watches say "timers are broken" every
    /// time one fired.
    /// </para>
    /// <para>
    /// <strong>So has one that parked again.</strong> The engine is the arbiter of whether a
    /// wait is over, and it can disagree with the sweep: a fork whose branches wait for
    /// different durations records the earlier of the two, so the sibling is found due,
    /// resumed, and parks again on its own instant. That is the design working, and it is
    /// answered before the switch below rather than falling into it.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <strong>The candidate's tenant is carried through to the resume</strong> for the reason
    /// <c>FlowRecoveryScan.TakeOverAsync</c> carries it: the woken instance runs against a
    /// journal bound to its own tenant, and the value is already on the row this sweep fetched.
    /// </remarks>
    private async Task<Attempt> WakeAsync(
        Guid instanceId,
        FlowRegistration registration,
        string? tenantId,
        CancellationToken ct)
    {
        var result = await _host.ResumeAsync(instanceId, registration, tenantId, ct)
            .ConfigureAwait(false);

        if (result.IsSuccess || result.IsSuspended)
        {
            return Attempt.Woken;
        }

        return result.Error!.Code switch
        {
            DurabilityErrors.LeaseHeldCode => Attempt.Contended,

            // The instance moved on between the query and the acquisition: a signal arrived
            // and finished it, or another node's sweep took it and fenced this one out.
            // Neither is this node's problem and neither is worth an alert.
            DurabilityErrors.InstanceNotFoundCode
                or DurabilityErrors.InstanceTerminalCode
                or DurabilityErrors.FencedOutCode
                or DurabilityErrors.LeaseLostCode => Attempt.Contended,

            HostDrainingCode => Attempt.Contended,

            // Including flow.signal_not_received, which is the wait expiring with nothing
            // declared to take instead. The sweep did its job; the flow ended the way its
            // author wrote it.
            _ => Attempt.Woken,
        };
    }

    /// <summary>The refusal a draining host gives, matched by code rather than by identity.</summary>
    private const string HostDrainingCode = "host.draining";

    /// <summary>What became of one candidate.</summary>
    private enum Attempt
    {
        Woken,
        Contended,
        Failed,
    }
}

/// <summary>What one timer sweep found and did.</summary>
/// <remarks>
/// Counts rather than a list of instance ids, for the reason
/// <see cref="RecoveryScanReport"/> carries counts: a report is what a metric is derived from
/// and what a test asserts on, and carrying the ids would make the type grow with the backlog.
/// </remarks>
public sealed record TimerScanReport
{
    /// <summary>A sweep that had nothing to do, and the base for one that did.</summary>
    public static TimerScanReport Nothing { get; } = new();

    /// <summary>How many due instances were considered.</summary>
    public int Examined { get; init; }

    /// <summary>How many this node took over and ran on.</summary>
    public int Woken { get; init; }

    /// <summary>How many another node had already taken, or a signal had already finished.</summary>
    public int Contended { get; init; }

    /// <summary>
    /// How many were pinned to a flow version this node does not carry.
    /// </summary>
    /// <remarks>
    /// Worth watching harder here than on a recovery sweep. An abandoned instance nobody can
    /// run is stuck; a <em>due</em> instance nobody can run is stuck <em>and</em> its
    /// escalation is not happening — the offer is not being withdrawn, the reminder is not
    /// being sent — which is a business consequence rather than an operational one.
    /// </remarks>
    public int NotRunnable { get; init; }

    /// <summary>How many resumes the stores refused for a reason worth looking at.</summary>
    public int Failed { get; init; }

    /// <summary>Why the query itself failed, when it did.</summary>
    public Error? Error { get; init; }
}
