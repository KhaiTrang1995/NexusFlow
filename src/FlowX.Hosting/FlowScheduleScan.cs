using System.Diagnostics;
using FlowX.Observability;
using FlowX.Runtime;

namespace FlowX.Hosting;

/// <summary>
/// One sweep for schedule occurrences that have fallen due, and the firing of as many of them
/// as this node wins.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The same shape as <see cref="FlowTimerScan"/>, and it is the same shape on
/// purpose.</strong> Something becomes due, every node sweeps, one node picks it up, nothing
/// sleeps per item. What differs is where "due" is written down: a durable timer reads an
/// instant off a row, and a schedule has no row to read — the occurrence is <em>computed</em>,
/// by every node, from an expression and a clock. So this sweep queries no index. It walks the
/// registered schedules, asks each what has fallen due since it was last accounted for, and
/// tries to start it.
/// </para>
/// <para>
/// <strong>There is no leader and no election.</strong> Ten nodes computing one occurrence
/// derive one instance id from it
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0031-an-occurrence-names-the-instance-it-starts.md">ADR-0026</a>),
/// so the race is settled by the two primitives the runtime already had: the lease store
/// refuses nine of them while the winner is running, and the journal's primary key refuses
/// them for ever afterwards. A leader would have to be elected, be detected as dead, and hand
/// over — three mechanisms, each with a window in which a schedule fires twice or not at all.
/// </para>
/// <para>
/// <strong>It fires through <see cref="FlowHost.RunAsync{TIn}(ExecutionPlan, IStepDispatcher, FlowInvocation, TIn, Guid, CancellationToken)"/>,
/// which is <c>RunAsync</c> with the minted id replaced by the derived one.</strong> There is
/// no schedule-shaped entry into a flow: the lease is taken, the instance row is written with
/// the lease's token as its opening fence, and the same <c>FlowEngine.ExecuteAsync</c> an HTTP
/// request reaches is reached.
/// </para>
/// <para>
/// <strong>The in-process memory of what was fired is an optimisation and never a
/// correctness mechanism.</strong> Without it a ten-second sweep would attempt an hourly
/// schedule's last occurrence three hundred and sixty times an hour, taking a lease and being
/// refused each time. With it the node attempts each occurrence once. Correctness is entirely
/// the stores': <c>ARestartedNodeDoesNotRefireAnOccurrenceTheJournalAlreadyHolds</c> is the
/// test that says so, and it runs a fresh sweep with an empty memory against a journal that
/// already holds the row.
/// </para>
/// </remarks>
public sealed class FlowScheduleScan
{
    private readonly FlowHost _host;
    private readonly FlowScheduleCatalog _schedules;
    private readonly FlowDurability _durability;
    private readonly FlowXOptions _options;
    private readonly IClock _clock;
    private readonly DateTimeOffset _startedAt;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _accounted = new(StringComparer.Ordinal);

    /// <summary>Builds a sweep over one node's registered schedules.</summary>
    /// <param name="host">Where a firing is started, so it is counted and drained.</param>
    /// <param name="schedules">Which schedules this node fires.</param>
    /// <param name="durability">
    /// The journal, which is asked one question and only on the first sweep of a schedule:
    /// has this occurrence already been fired. See <see cref="DueForAsync"/>.
    /// </param>
    /// <param name="options">The validated host options.</param>
    /// <param name="clock">
    /// The runtime's clock, so "due" is measured against the same instant every other sweep
    /// measures it against — and so a suite can wind a day forward without waiting for one.
    /// </param>
    public FlowScheduleScan(
        FlowHost host,
        FlowScheduleCatalog schedules,
        FlowDurability durability,
        FlowXOptions options,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(durability);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _host = host;
        _schedules = schedules;
        _durability = durability;
        _options = options;
        _clock = clock;
        _startedAt = clock.UtcNow;
    }

    /// <summary>Whether this host has anything to fire.</summary>
    /// <remarks>
    /// False when nothing registered a schedule, and false when no journal was registered. The
    /// second is not a configuration to work around: without a journal there is no primary key
    /// to refuse a second node's firing, so a schedule on such a host would fire once per node
    /// per occurrence and nothing would record that it had. A host with no schedules is not
    /// misconfigured and simply does not run the loop.
    /// </remarks>
    public bool IsEnabled => _schedules.Count > 0 && _host.IsDurabilityConfigured;

    /// <summary>Runs one sweep and returns what it did.</summary>
    /// <param name="ct">Cancels the sweep, and every firing it started.</param>
    /// <returns>The counts. A sweep that found nothing due is the ordinary result.</returns>
    /// <remarks>
    /// Awaits the flows it started, for the reason <see cref="FlowTimerScan.RunOnceAsync"/>
    /// does: firing is executing, so a sweep can last as long as a flow does, and that is what
    /// bounds concurrency without a second counter to keep correct.
    /// </remarks>
    public async ValueTask<ScheduleScanReport> RunOnceAsync(CancellationToken ct = default)
    {
        // The sweep's own span, for the reason FlowTimerScan opens one: a fired instance's flow
        // span otherwise has no parent, and "why did this run at 03:14" is the first question
        // asked of a flow nobody triggered.
        using var span = FlowXTelemetry.Source.StartActivity("schedule scan", ActivityKind.Internal);

        if (!IsEnabled || _host.IsDraining)
        {
            return Tagged(span, ScheduleScanReport.Nothing);
        }

        var now = _clock.UtcNow;
        var due = new List<(ScheduleRegistration Registration, DateTimeOffset Occurrence)>();

        foreach (var registration in _schedules.Registrations)
        {
            var occurrences = await DueForAsync(registration, now, ct).ConfigureAwait(false);

            due.AddRange(occurrences.Select(occurrence => (registration, occurrence)));
        }

        if (due.Count == 0)
        {
            return Tagged(span, ScheduleScanReport.Nothing);
        }

        var capacity = _options.MaxConcurrentRecoveries;
        var fires = new List<Task<Attempt>>(Math.Min(due.Count, capacity));

        foreach (var (registration, occurrence) in due.Take(capacity))
        {
            fires.Add(FireAsync(registration, occurrence, ct));
        }

        var attempts = await Task.WhenAll(fires).ConfigureAwait(false);

        return Tagged(span, new ScheduleScanReport
        {
            Due = due.Count,
            Fired = attempts.Count(static a => a == Attempt.Fired),
            Contended = attempts.Count(static a => a == Attempt.Contended),
            Failed = attempts.Count(static a => a == Attempt.Failed),
        });
    }

    /// <summary>
    /// The occurrences of one schedule this node should fire now, ascending.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The floor is the whole of the missed-fire decision, and it comes from one of
    /// three places</strong>
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0032-a-missed-schedule-fires-late.md">ADR-0027</a>).
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <strong>This process already accounted for an occurrence.</strong> The steady state, and
    /// it asks the stores nothing: the floor is the newest occurrence this sweep has handled, so
    /// a ten-second sweep over an hourly schedule finds nothing due three hundred and fifty-nine
    /// times an hour and takes no lease doing it.
    /// </description></item>
    /// <item><description>
    /// <strong>First sweep of this schedule, and the journal holds an earlier occurrence.</strong>
    /// The fleet was running and stopped. The floor is that occurrence, so everything after it
    /// is genuinely missed and is fired late — which is what at-least-once commits a schedule
    /// to. The probe walks backwards from the newest occurrence and stops at the first the
    /// journal has, so the ordinary restart costs exactly one read.
    /// </description></item>
    /// <item><description>
    /// <strong>First sweep, and the journal holds nothing inside the horizon.</strong> There is
    /// no evidence this schedule has ever run here, so there is nothing to catch up on: the
    /// floor is when <em>this node</em> started. A first deployment therefore fires from its
    /// next occurrence rather than replaying the horizon — which is the failure the horizon
    /// would otherwise produce on every new application, and it is the reason this probe exists
    /// rather than a bare <c>now - ScheduleCatchUp</c>.
    /// </description></item>
    /// </list>
    /// <para>
    /// <strong>Two bounds, and both are visible.</strong> The floor never reaches further back
    /// than <see cref="FlowXOptions.ScheduleCatchUp"/>, so a fleet down for longer than that
    /// loses the firings outside it. And the probe reads at most
    /// <see cref="FlowXOptions.ScheduleFireBatchSize"/> occurrences, so a dense schedule after a
    /// long outage runs out of evidence before it runs out of horizon and is treated as case 3.
    /// An unbounded probe is what a fresh database makes unbounded.
    /// </para>
    /// <para>
    /// <strong>The declared <see cref="MissedFirePolicy"/> then narrows what is fired, and each
    /// value means something different about work that is late.</strong>
    /// <see cref="MissedFirePolicy.Skip"/> takes only an occurrence fresh enough that no sweep
    /// could have missed it — the author asked for the work not to be done late;
    /// <see cref="MissedFirePolicy.RunOnce"/>, the default, takes the most recent one, so a
    /// two-hour outage of a minutely schedule produces one late firing rather than a hundred
    /// and twenty; and <see cref="MissedFirePolicy.RunAll"/> takes every one, which is what an
    /// author asks for when each firing does a different piece of work.
    /// </para>
    /// </remarks>
    private async ValueTask<List<DateTimeOffset>> DueForAsync(
        ScheduleRegistration registration, DateTimeOffset now, CancellationToken ct)
    {
        var schedule = registration.Schedule;
        var key = schedule.FlowId + "\0" + schedule.FlowVersion + "\0" + schedule.Cron.Expression;
        var horizon = now - _options.ScheduleCatchUp;

        DateTimeOffset? known;

        lock (_gate)
        {
            known = _accounted.TryGetValue(key, out var last) ? last : null;
        }

        var floor = known is { } accounted && accounted > horizon
            ? accounted
            : await FirstFloorAsync(schedule, now, horizon, ct).ConfigureAwait(false);

        var occurrences = schedule.Cron.Between(floor, now).ToList();

        if (occurrences.Count == 0)
        {
            Account(key, floor);

            return [];
        }

        List<DateTimeOffset> firing;

        switch (schedule.MissedFire)
        {
            // Fresh enough that no sweep could have missed it. Two intervals, because a sweep is
            // jittered by a quarter and the one before it may have run early.
            case MissedFirePolicy.Skip:
                firing = occurrences[^1] >= now - (_options.ScheduleScanInterval * 2)
                    ? [occurrences[^1]]
                    : [];
                Account(key, occurrences[^1]);
                break;

            case MissedFirePolicy.RunAll:
                firing = [.. occurrences.Take(_options.ScheduleFireBatchSize)];

                // What the batch did not reach is not forgotten: the floor advances only as far
                // as this sweep actually fired, so the next one continues from there.
                Account(key, firing.Count == 0 ? floor : firing[^1]);
                break;

            // RunOnce, and anything a later release adds: one firing, however many were missed.
            default:
                firing = [occurrences[^1]];
                Account(key, occurrences[^1]);
                break;
        }

        return firing;
    }

    /// <summary>
    /// The floor for a schedule this process has not yet accounted for: the newest occurrence
    /// the journal already holds, or this node's own start.
    /// </summary>
    private async ValueTask<DateTimeOffset> FirstFloorAsync(
        FlowSchedule schedule, DateTimeOffset now, DateTimeOffset horizon, CancellationToken ct)
    {
        var candidates = schedule.Cron.Between(horizon, now).ToList();
        var budget = _options.ScheduleFireBatchSize;

        for (var i = candidates.Count - 1; i >= 0 && budget > 0; i--, budget--)
        {
            var read = await _durability.Journal
                .ReadInstanceAsync(schedule.InstanceIdFor(candidates[i]), ct)
                .ConfigureAwait(false);

            if (read.IsSuccess)
            {
                return candidates[i];
            }
        }

        // No evidence, so nothing to catch up on. Clamped to the horizon, so that a node which
        // has been up for a month does not reach back a month on the day a schedule is added.
        return _startedAt > horizon ? _startedAt : horizon;
    }

    private void Account(string key, DateTimeOffset floor)
    {
        lock (_gate)
        {
            _accounted[key] = floor;
        }
    }

    /// <summary>Puts what a sweep did onto its span, and hands the report back unchanged.</summary>
    private static ScheduleScanReport Tagged(Activity? span, in ScheduleScanReport report)
    {
        if (span is not null)
        {
            span.SetTag("flowx.scan.due", report.Due);
            span.SetTag("flowx.scan.fired", report.Fired);
            span.SetTag("flowx.scan.contended", report.Contended);
            span.SetTag("flowx.scan.failed", report.Failed);
        }

        return report;
    }

    /// <summary>
    /// One firing, classified by whether this node became the instance's writer — not by what
    /// the flow then did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A refusal is the mechanism working, so it is <see cref="Attempt.Contended"/>
    /// rather than a failure.</strong> <c>lease.held</c> means another node is running this
    /// occurrence right now; <c>journal.instance_exists</c> means one already has, possibly
    /// weeks ago. Both are the answer this sweep asked for. Counting either as a failure would
    /// make the one number an operator watches read "the scheduler is broken" on every node
    /// but one, on every occurrence.
    /// </para>
    /// <para>
    /// <strong>A flow that ran and failed has been fired.</strong> The schedule's job was to
    /// start it; what it then did is the flow's business and its own errors are on its own
    /// instance.
    /// </para>
    /// </remarks>
    private async Task<Attempt> FireAsync(
        ScheduleRegistration registration,
        DateTimeOffset occurrence,
        CancellationToken ct)
    {
        var schedule = registration.Schedule;
        var instanceId = schedule.InstanceIdFor(occurrence);

        var result = await _host
            .RunAsync(
                registration.Flow.Plan,
                registration.Flow.Dispatcher,

                // The correlation id is the instance id, so every node's log line about this
                // occurrence carries the same one — including the nine that were refused. There
                // is no inbound request to inherit one from, and inventing a fresh one per node
                // would make one firing look like ten.
                new FlowInvocation(instanceId.ToString(), instanceId.ToString()),
                schedule.FireFor(occurrence),
                instanceId,
                ct)
            .ConfigureAwait(false);

        if (result.IsSuccess || result.IsSuspended)
        {
            return Attempt.Fired;
        }

        return result.Error!.Code switch
        {
            DurabilityErrors.LeaseHeldCode
                or DurabilityErrors.InstanceExistsCode
                or DurabilityErrors.FencedOutCode
                or DurabilityErrors.LeaseLostCode => Attempt.Contended,

            HostDrainingCode => Attempt.Contended,

            // The flow ran and ended badly, which is the flow's outcome and not the sweep's.
            _ => Attempt.Fired,
        };
    }

    /// <summary>The refusal a draining host gives, matched by code rather than by identity.</summary>
    private const string HostDrainingCode = "host.draining";

    /// <summary>What became of one occurrence.</summary>
    private enum Attempt
    {
        Fired,
        Contended,
        Failed,
    }
}

/// <summary>What one schedule sweep found and did.</summary>
/// <remarks>
/// Counts rather than a list of occurrences, for the reason <see cref="TimerScanReport"/>
/// carries counts: a report is what a metric is derived from and what a test asserts on.
/// </remarks>
public sealed record ScheduleScanReport
{
    /// <summary>A sweep that had nothing to do, and the base for one that did.</summary>
    public static ScheduleScanReport Nothing { get; } = new();

    /// <summary>How many occurrences this node considered firing.</summary>
    public int Due { get; init; }

    /// <summary>How many this node started.</summary>
    public int Fired { get; init; }

    /// <summary>
    /// How many another node had already taken, or had already run.
    /// </summary>
    /// <remarks>
    /// <strong>The expected number on every node but one</strong>, on a fleet of <em>n</em>
    /// nodes firing one occurrence: one <see cref="Fired"/> and <em>n</em>−1 of these. An
    /// operator watching this expecting zero is watching the wrong number; what is worth an
    /// alert is <see cref="Fired"/> summing to zero across the fleet for an occurrence that
    /// was due.
    /// </remarks>
    public int Contended { get; init; }

    /// <summary>How many the stores refused for a reason worth looking at.</summary>
    public int Failed { get; init; }

    /// <summary>Why the sweep itself failed, when it did.</summary>
    public Error? Error { get; init; }
}
