using System.Diagnostics;
using FlowX.Observability;
using FlowX.Runtime;

namespace FlowX.Hosting;

/// <summary>
/// One pass over the change feed for each subscription this node serves, and the starting of a
/// flow for every change that has not started one already.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="FlowBusScan"/>'s shape, with the broker replaced by a log and the
/// acknowledgement replaced by a cursor.</strong> Something becomes available, every node sweeps,
/// one node takes it, nothing sleeps per item. What differs is what "finished with it" means: a
/// broker is told, and a log is not — the subscription simply records how far it got
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0048-a-change-feed-advances-a-cursor.md">ADR-0048</a>).
/// </para>
/// <para>
/// <strong>It starts flows through
/// <see cref="FlowHost.RunAsync{TIn}(ExecutionPlan, IStepDispatcher, FlowInvocation, TIn, Guid, CancellationToken)"/>,
/// which is <c>RunAsync</c> with the minted id replaced by the derived one.</strong> There is no
/// feed-shaped entry into a flow: the same <c>FlowEngine.ExecuteAsync</c> an HTTP request reaches
/// is reached. A second invocation path would be a defect, and there is not one.
/// </para>
/// <para>
/// <strong>Three decisions live here, one record each.</strong>
/// </para>
/// <list type="number">
/// <item><description>
/// <strong>Identity</strong> — the instance id is derived from the change
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0049-a-change-names-the-instance-it-starts.md">ADR-0049</a>),
/// so the journal's primary key refuses the second reading of one change. One change starts one
/// flow.
/// </description></item>
/// <item><description>
/// <strong>Progress</strong> — <see cref="DispositionFor"/>, which is
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0036-a-message-is-acknowledged-when-its-flow-is-journalled.md">ADR-0036</a>'s
/// classification read for a different purpose: the cursor advances past the longest prefix that
/// reached a <em>recorded outcome</em>, and stops at the first change that did not.
/// </description></item>
/// <item><description>
/// <strong>One reader</strong> — a lease over the subscription, because a cursor is a
/// single-reader structure and two nodes would commit two positions over one row
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0048-a-change-feed-advances-a-cursor.md">ADR-0048</a>
/// decision 4).
/// </description></item>
/// </list>
/// <para>
/// <strong>There is no dead-letter path, and that is not an omission.</strong> A change whose flow
/// <em>ran and failed</em> reached a recorded outcome, so it does not block the subscription; the
/// only dispositions that hold the cursor are host and store refusals, every one of which is
/// transient by construction. A change that can never be processed — the broker's poison message
/// — does not arise, because the row was written by this system's own serialiser rather than by a
/// stranger.
/// </para>
/// </remarks>
public sealed class FlowChangeScan
{
    private readonly FlowHost _host;
    private readonly FlowChangeCatalog _subscriptions;
    private readonly IChangeFeed _feed;
    private readonly FlowDurability _durability;
    private readonly FlowXOptions _options;
    private readonly LeasePolicy _policy;

    /// <summary>Builds a pass over one node's registered change subscriptions.</summary>
    /// <param name="host">Where a change is started, so it is counted and drained.</param>
    /// <param name="subscriptions">Which subscriptions this node serves.</param>
    /// <param name="feed">The change feed.</param>
    /// <param name="durability">
    /// The journal, whose primary key refuses a re-read, and the lease store, which keeps one
    /// subscription to one node.
    /// </param>
    /// <param name="options">The validated host options.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public FlowChangeScan(
        FlowHost host,
        FlowChangeCatalog subscriptions,
        IChangeFeed feed,
        FlowDurability durability,
        FlowXOptions options)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(durability);
        ArgumentNullException.ThrowIfNull(options);

        _host = host;
        _subscriptions = subscriptions;
        _feed = feed;
        _durability = durability;
        _options = options;
        _policy = FlowDurability.PolicyFor(options);
    }

    /// <summary>Whether this host has anything to observe.</summary>
    /// <remarks>
    /// False when nothing registered a subscription, and false when no journal was registered —
    /// <see cref="FlowBusScan.IsEnabled"/>'s two reasons, and the second is the same silent
    /// failure: without a primary key to refuse a re-read, a subscription would start a flow per
    /// pass until the cursor happened to commit.
    /// </remarks>
    public bool IsEnabled => _subscriptions.Count > 0 && _host.IsDurabilityConfigured;

    /// <summary>Runs one pass and returns what it did.</summary>
    /// <param name="ct">Cancels the pass, and every flow it started.</param>
    /// <returns>The counts. A pass that found nothing is the ordinary result.</returns>
    public async ValueTask<ChangeScanReport> RunOnceAsync(CancellationToken ct = default)
    {
        using var span = FlowXTelemetry.Source.StartActivity("change scan", ActivityKind.Internal);

        if (!IsEnabled || _host.IsDraining)
        {
            return Tagged(span, ChangeScanReport.Nothing);
        }

        var report = ChangeScanReport.Nothing;

        foreach (var registration in _subscriptions.Registrations)
        {
            report = report.Add(await ObserveAsync(registration, ct).ConfigureAwait(false));
        }

        return Tagged(span, report);
    }

    /// <summary>One subscription's share of one pass, under a lease nobody else holds.</summary>
    private async Task<ChangeScanReport> ObserveAsync(
        ChangeRegistration registration, CancellationToken ct)
    {
        var acquired = await DurableLease
            .AcquireAsync(
                _durability.Leases,
                registration.SubscriptionLease,
                _options.NodeName,
                _policy,
                ct)
            .ConfigureAwait(false);

        if (acquired.IsFailure)
        {
            // Another node is reading this subscription right now. The ordinary answer on a
            // fleet, and not a failure: the cursor is that node's to advance.
            return ChangeScanReport.Nothing with { Contended = 1 };
        }

        var lease = acquired.Value;

        try
        {
            return await ReadAsync(registration, ct).ConfigureAwait(false);
        }
        finally
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Reads one batch, runs it in order, and commits how far it got.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The loop stops at the first change that did not reach a recorded outcome.</strong>
    /// Continuing would run change <em>n+1</em> of a key before <em>n</em>, and would leave a
    /// cursor that cannot be committed past either — so the pass keeps the position of the last
    /// change it finished with and offers the rest again next pass, behind the sibling they must
    /// follow.
    /// </para>
    /// <para>
    /// <strong>The commit is last, after the flows.</strong> The other order is at-most-once and
    /// loses every change a crash lands in the middle of; this order costs a re-read, which the
    /// derived instance id turns into <c>journal.instance_exists</c> and this method counts as
    /// deduplicated.
    /// </para>
    /// </remarks>
    private async Task<ChangeScanReport> ReadAsync(
        ChangeRegistration registration, CancellationToken ct)
    {
        var read = await _feed
            .ReadAsync(registration.Subscription, _options.ChangeReadBatchSize, ct)
            .ConfigureAwait(false);

        if (read.IsFailure)
        {
            return ChangeScanReport.Nothing with { Error = read.Error };
        }

        var changes = read.Value;

        if (changes.Count == 0)
        {
            return ChangeScanReport.Nothing;
        }

        var report = ChangeScanReport.Nothing;
        ChangePosition? reached = null;

        foreach (var change in changes)
        {
            var one = await StartAsync(registration, change, ct).ConfigureAwait(false);

            report = report.Add(one);

            if (one.Held > 0)
            {
                break;
            }

            reached = change.Position;
        }

        if (reached is not { } position)
        {
            return report;
        }

        var committed = await _feed
            .CommitAsync(registration.Subscription, position, ct)
            .ConfigureAwait(false);

        return committed.IsFailure ? report with { Error = committed.Error } : report;
    }

    /// <summary>One change, from the feed's offer to a disposition.</summary>
    private async Task<ChangeScanReport> StartAsync(
        ChangeRegistration registration, ObservedChange change, CancellationToken ct)
    {
        var one = ChangeScanReport.Nothing with { Observed = 1 };
        var message = change.Message;
        var instanceId = registration.InstanceIdFor(message.EventId);

        var result = await _host
            .RunAsync(
                registration.Flow.Plan,
                registration.Flow.Dispatcher,

                // The correlation id is the event's, so the emitting instance's log lines and the
                // observing instance's carry one id between them; the causation id is the
                // instance this change started. FlowBusScan's reasoning, unchanged — there is no
                // inbound request to inherit either from.
                new FlowInvocation(message.EventId.ToString("d"), instanceId.ToString()),
                message,
                instanceId,
                ct)
            .ConfigureAwait(false);

        return DispositionFor(result) switch
        {
            Disposition.Held => one with { Held = 1 },
            Disposition.Deduplicated => one with { Deduplicated = 1 },
            _ => one with { Started = 1 },
        };
    }

    /// <summary>
    /// Whether the cursor may move past this change, given what the runtime did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0036-a-message-is-acknowledged-when-its-flow-is-journalled.md">ADR-0036</a>'s
    /// classification, read for progress rather than for acknowledgement — and the same switch on
    /// <c>Error.Code</c> rather than a second classification the two could disagree about.
    /// </para>
    /// <para>
    /// <strong>A flow that failed as a value is progress.</strong> ADR-0007 says a business
    /// failure is a <c>Result</c>, so a flow that ran, compensated and wrote a terminal row has
    /// happened; holding the cursor would stop the subscription for ever over an instance the
    /// primary key now refuses. A suspension is progress too: the journal holds it and a signal
    /// resumes it.
    /// </para>
    /// <para>
    /// <strong>Only a refusal that journalled nothing holds the cursor.</strong> The lease was
    /// lost, the fence was raised under us, the host is draining, or no journal is configured. In
    /// each of those no instance row describes this change, so the cursor is the only thing that
    /// will bring it back.
    /// </para>
    /// </remarks>
    private static Disposition DispositionFor(FlowExecutionResult result)
    {
        if (result.IsSuccess || result.IsSuspended)
        {
            return Disposition.Started;
        }

        return result.Error!.Code switch
        {
            // An earlier pass already ran this change and died before committing the cursor.
            // This pass has nothing left to do, which is the answer it asked for.
            DurabilityErrors.InstanceExistsCode => Disposition.Deduplicated,

            DurabilityErrors.LeaseHeldCode
                or DurabilityErrors.LeaseLostCode
                or DurabilityErrors.FencedOutCode
                or HostDrainingCode
                or DurabilityNotConfiguredCode => Disposition.Held,

            // The flow ran and ended badly, which is the flow's outcome and not the change's.
            _ => Disposition.Started,
        };
    }

    /// <summary>The refusal a draining host gives, matched by code rather than by identity.</summary>
    private const string HostDrainingCode = "host.draining";

    /// <summary>The refusal a host with no journal gives a <c>Durable</c> flow.</summary>
    private const string DurabilityNotConfiguredCode = "flow.durability_not_configured";

    /// <summary>Puts what a pass did onto its span, and hands the report back unchanged.</summary>
    private static ChangeScanReport Tagged(Activity? span, in ChangeScanReport report)
    {
        if (span is not null)
        {
            span.SetTag("flowx.scan.observed", report.Observed);
            span.SetTag("flowx.scan.started", report.Started);
            span.SetTag("flowx.scan.deduplicated", report.Deduplicated);
            span.SetTag("flowx.scan.held", report.Held);
        }

        return report;
    }

    /// <summary>What became of one change.</summary>
    private enum Disposition
    {
        Started,
        Deduplicated,
        Held,
    }
}

/// <summary>What one change pass found and did.</summary>
/// <remarks>
/// Counts rather than a list of changes, for <see cref="BusScanReport"/>'s reason: a report is
/// what a metric is derived from and what a test asserts on.
/// </remarks>
public sealed record ChangeScanReport
{
    /// <summary>A pass that had nothing to do, and the base for one that did.</summary>
    public static ChangeScanReport Nothing { get; } = new();

    /// <summary>How many changes the feed offered.</summary>
    public int Observed { get; init; }

    /// <summary>How many started a flow.</summary>
    public int Started { get; init; }

    /// <summary>
    /// How many were a re-reading of a change an instance already exists for.
    /// </summary>
    /// <remarks>
    /// <strong>The expected number after any restart, and zero on a healthy steady state</strong>
    /// — unlike <see cref="BusScanReport.Deduplicated"/>, which is ordinary on any fleet. Here it
    /// counts exactly the window between a flow committing and the cursor committing, so a
    /// figure that stays above zero pass after pass is a cursor that is not being written.
    /// </remarks>
    public int Deduplicated { get; init; }

    /// <summary>
    /// How many were left for the next pass because nothing recorded them — and, with them,
    /// everything behind them in the batch.
    /// </summary>
    public int Held { get; init; }

    /// <summary>How many subscriptions another node was already reading.</summary>
    public int Contended { get; init; }

    /// <summary>Why the pass itself failed, when it did.</summary>
    public Error? Error { get; init; }

    /// <summary>Sums two reports, keeping the first error seen.</summary>
    /// <param name="other">The report to add.</param>
    /// <returns>The sum.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    public ChangeScanReport Add(ChangeScanReport other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return new ChangeScanReport
        {
            Observed = Observed + other.Observed,
            Started = Started + other.Started,
            Deduplicated = Deduplicated + other.Deduplicated,
            Held = Held + other.Held,
            Contended = Contended + other.Contended,
            Error = Error ?? other.Error,
        };
    }
}
