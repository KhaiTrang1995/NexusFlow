using System.Diagnostics;
using FlowX.Observability;
using FlowX.Runtime;

namespace FlowX.Hosting;

/// <summary>
/// One pass over the broker for messages this node's subscriptions are waiting for, and the
/// starting of a flow for each one that has not been started already.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The same shape as <see cref="FlowScheduleScan"/> and <see cref="FlowTimerScan"/>, and
/// it is the same shape on purpose.</strong> Something becomes available, every node sweeps, one
/// node takes it, nothing sleeps per item. What differs is where "available" is written down: a
/// durable timer reads an instant off a row, a schedule computes an occurrence from an
/// expression, and this asks a broker. So this pass queries no index of ours at all — it calls
/// <see cref="IBusConsumer.ReceiveAsync"/>, and everything after that is the runtime's.
/// </para>
/// <para>
/// <strong>It starts flows through
/// <see cref="FlowHost.RunAsync{TIn}(ExecutionPlan, IStepDispatcher, FlowInvocation, TIn, Guid, CancellationToken)"/>,
/// which is <c>RunAsync</c> with the minted id replaced by the derived one.</strong> There is no
/// broker-shaped entry into a flow: the lease is taken, the instance row is written with the
/// lease's token as its opening fence, and the same <c>FlowEngine.ExecuteAsync</c> an HTTP request
/// reaches is reached. A second invocation path would be a defect, and there is not one.
/// </para>
/// <para>
/// <strong>Four decisions live here, one ADR each, and they are separated in the code the way
/// they are separated in the records.</strong>
/// </para>
/// <list type="number">
/// <item><description>
/// <strong>Redelivery</strong> — the instance id is derived from the delivery
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0035-a-delivery-names-the-instance-it-starts.md">ADR-0035</a>),
/// so the journal's primary key refuses the second delivery of one message. One message starts
/// one flow.
/// </description></item>
/// <item><description>
/// <strong>Acknowledgement</strong> — <see cref="DispositionFor"/>, and it acknowledges a flow
/// that <em>failed as a value</em>
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0036-a-message-is-acknowledged-when-its-flow-is-journalled.md">ADR-0036</a>),
/// because ADR-0007 says a business failure is a <c>Result</c> and a flow that ran and decided
/// against has happened.
/// </description></item>
/// <item><description>
/// <strong>Ordering</strong> — one partition is read under a lease and its entries are run one
/// at a time in order, so per-<c>partition_key</c> order survives a fleet
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0037-the-consumer-offers-per-key-order.md">ADR-0037</a>).
/// Partitions are served concurrently, and nothing is offered across them.
/// </description></item>
/// <item><description>
/// <strong>Poison</strong> — an unreadable entry is dead-lettered on its first delivery and an
/// undeliverable one past <see cref="FlowXOptions.BusMaxDeliveries"/>
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0038-a-poison-message-is-dead-lettered.md">ADR-0038</a>),
/// so a message nobody can process does not stop its partition for ever.
/// </description></item>
/// </list>
/// </remarks>
public sealed class FlowBusScan
{
    private readonly FlowHost _host;
    private readonly FlowBusCatalog _subscriptions;
    private readonly IBusConsumer? _consumer;
    private readonly FlowDurability _durability;
    private readonly FlowXOptions _options;
    private readonly LeasePolicy _policy;

    /// <summary>Builds a pass over one node's registered subscriptions.</summary>
    /// <param name="host">Where a delivery is started, so it is counted and drained.</param>
    /// <param name="subscriptions">Which subscriptions this node serves.</param>
    /// <param name="consumer">
    /// The broker, or null on a host that is <em>pushed</em> its messages and has none.
    /// </param>
    /// <param name="durability">
    /// The journal, whose primary key refuses a redelivery, and the lease store, which keeps one
    /// partition to one node.
    /// </param>
    /// <param name="options">The validated host options.</param>
    /// <remarks>
    /// <strong>The broker is optional and the journal is not, which is the asymmetry the push
    /// path introduced.</strong> <see cref="RunOnceAsync"/> asks a broker for work and cannot
    /// run without one; <see cref="AdmitAsync"/> is handed the work and never touches it. A
    /// serverless host has a platform that pulls on its behalf, so it wires no
    /// <see cref="IBusConsumer"/> and still needs every decision this class makes — and the way
    /// it gets them is that <see cref="IsEnabled"/> reads false and the pass does nothing, while
    /// the seam serves. Passing null to get a scan that pulls would be a silent failure, and
    /// <see cref="IsEnabled"/> is what makes it a stated one.
    /// </remarks>
    public FlowBusScan(
        FlowHost host,
        FlowBusCatalog subscriptions,
        IBusConsumer? consumer,
        FlowDurability durability,
        FlowXOptions options)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(durability);
        ArgumentNullException.ThrowIfNull(options);

        _host = host;
        _subscriptions = subscriptions;
        _consumer = consumer;
        _durability = durability;
        _options = options;
        _policy = FlowDurability.PolicyFor(options);
    }

    /// <summary>Whether this host has anything to consume.</summary>
    /// <remarks>
    /// False when nothing registered a subscription, and false when no journal was registered. The
    /// second is not a configuration to work around: without a primary key to refuse a redelivery,
    /// a subscription would start a flow per delivery of one message and nothing would record that
    /// it had — the silent failure <see cref="FlowBusCatalog.Add"/> refuses at registration. A host
    /// with no subscriptions is not misconfigured and simply does not run the loop.
    /// False also when no broker was wired, which is the ordinary state of a push host: its
    /// platform pulls, and <see cref="AdmitAsync"/> is the entry it uses.
    /// </remarks>
    public bool IsEnabled =>
        _consumer is not null && _subscriptions.Count > 0 && _host.IsDurabilityConfigured;

    /// <summary>Runs one pass and returns what it did.</summary>
    /// <param name="ct">Cancels the pass, and every flow it started.</param>
    /// <returns>The counts. A pass that found nothing is the ordinary result.</returns>
    public async ValueTask<BusScanReport> RunOnceAsync(CancellationToken ct = default)
    {
        // The pass's own span, for the reason FlowScheduleScan opens one: a started instance's
        // flow span otherwise has no parent, and "what started this" is the first question asked
        // of a flow nobody called.
        using var span = FlowXTelemetry.Source.StartActivity("bus scan", ActivityKind.Internal);

        if (!IsEnabled || _host.IsDraining)
        {
            return Tagged(span, BusScanReport.Nothing);
        }

        var report = BusScanReport.Nothing;

        foreach (var registration in _subscriptions.Registrations)
        {
            report = report.Add(await ConsumeAsync(registration, ct).ConfigureAwait(false));
        }

        return Tagged(span, report);
    }

    /// <summary>One subscription's share of one pass.</summary>
    /// <remarks>
    /// <c>SubscribeAsync</c> is called every pass rather than once at startup, because a broker
    /// restored from an empty state has forgotten the group, and a consumer that created it only
    /// once would then read nothing for ever — silently, which is the failure mode this whole
    /// class is written against.
    /// </remarks>
    private async Task<BusScanReport> ConsumeAsync(BusRegistration registration, CancellationToken ct)
    {
        var subscription = registration.Subscription;

        var subscribed = await _consumer!.SubscribeAsync(subscription, ct).ConfigureAwait(false);

        if (subscribed.IsFailure)
        {
            return BusScanReport.Nothing with { Error = subscribed.Error };
        }

        var received = await _consumer!
            .ReceiveAsync(
                subscription, _options.BusMaxConcurrentPartitions, _options.BusReceiveBatchSize, ct)
            .ConfigureAwait(false);

        if (received.IsFailure)
        {
            return BusScanReport.Nothing with { Error = received.Error };
        }

        var batches = received.Value;

        if (batches.Count == 0)
        {
            return BusScanReport.Nothing;
        }

        // Partitions concurrently, entries within a partition serially. That is decision 3 of
        // ADR-0037 expressed as control flow: the concurrency is exactly the concurrency
        // partitioning exists to provide, and no more.
        var partitions = await Task
            .WhenAll(batches.Select(batch => PartitionAsync(registration, batch, ct)))
            .ConfigureAwait(false);

        return partitions.Aggregate(BusScanReport.Nothing, static (total, one) => total.Add(one));
    }

    /// <summary>One partition's entries, in order, under a lease nobody else holds.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The lease is what makes the ordering guarantee survive a second node.</strong> A
    /// consumer group hands the entries of one partition to whichever consumer asks, so two nodes
    /// reading one partition would run its entries concurrently and lose the order the publisher
    /// paid a per-row probe for. A node that does not win the lease leaves the batch pending and
    /// takes another partition; nothing is lost, because nothing was acknowledged.
    /// </para>
    /// <para>
    /// <strong>The loop stops at the first entry that did not reach a disposition.</strong>
    /// Continuing would run entry <em>n+1</em> of this key before <em>n</em>, which is the whole
    /// of what this method exists to prevent. The remaining entries stay pending and are offered
    /// again next pass, behind the sibling they must follow.
    /// </para>
    /// </remarks>
    private async Task<BusScanReport> PartitionAsync(
        BusRegistration registration, BusPartitionBatch batch, CancellationToken ct)
    {
        if (batch.Deliveries.Count == 0)
        {
            return BusScanReport.Nothing;
        }

        var acquired = await DurableLease
            .AcquireAsync(
                _durability.Leases,
                registration.PartitionLeaseFor(batch.PartitionKey),
                _options.NodeName,
                _policy,
                ct)
            .ConfigureAwait(false);

        if (acquired.IsFailure)
        {
            // Another node is serving this partition right now. The ordinary answer on a fleet,
            // and not a failure: the entries stay pending and that node is running them in order.
            return BusScanReport.Nothing with { Received = batch.Deliveries.Count, Contended = 1 };
        }

        var lease = acquired.Value;

        try
        {
            var report = BusScanReport.Nothing;

            foreach (var delivery in batch.Deliveries)
            {
                report = report.Add(await DeliverAsync(registration, delivery, ct).ConfigureAwait(false));

                if (report.Requeued > 0)
                {
                    break;
                }
            }

            return report;
        }
        finally
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What becomes of one delivery, decided and not settled.
    /// </summary>
    /// <param name="registration">The subscription the delivery belongs to.</param>
    /// <param name="delivery">The delivery, as the broker or the platform offered it.</param>
    /// <param name="ct">Cancels the flow this admission starts.</param>
    /// <returns>The disposition, and the dead-letter reason when there is one.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// <para>
    /// <strong>The public per-item entry, and the reason it decides rather than settles.</strong>
    /// <see cref="RunOnceAsync"/> pulls, so it has an <see cref="IBusConsumer"/> to answer; a
    /// serverless host is <em>pushed</em> one message and its platform settles that message
    /// itself, so a seam that acknowledged would acknowledge twice or fight the platform for the
    /// lock. Both routes need the same four decisions — redelivery, acknowledgement, ordering,
    /// poison — and this is where all four are made, once. What each caller then does with the
    /// answer is the caller's.
    /// </para>
    /// <para>
    /// <strong>No lease is taken here.</strong> The partition lease belongs to
    /// <see cref="PartitionAsync"/>, which holds it across a batch so ADR-0037's order survives a
    /// fleet; a push host's platform is what orders its own delivery. Exactly-once execution does
    /// not rest on that lease in either case — it rests on the derived instance id and the
    /// journal's primary key (ADR-0035), which this call goes through unchanged.
    /// </para>
    /// <para>
    /// <strong>Over <c>FlowXOptions.MaxInFlightAdmissions</c> this requeues, and the broker is
    /// what the backlog is left with.</strong> That is what a broker is for: nothing was
    /// journalled, so the message is still pending, and it is offered again when a slot frees.
    /// Dead-lettering a shed message would discard work the node was merely busy for, and running
    /// it anyway is the saturation the ceiling exists to prevent. A shed does not count against
    /// <c>BusMaxDeliveries</c> any differently from any other requeue — a delivery that keeps
    /// arriving while the node is full will eventually be dead-lettered by ADR-0038, which is the
    /// correct outcome for a backlog no amount of shedding is draining.
    /// </para>
    /// </remarks>
    public async ValueTask<BusAdmission> AdmitAsync(
        BusRegistration registration, BusDelivery delivery, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(delivery);

        if (PoisonReasonFor(delivery) is { } poison)
        {
            return BusAdmission.DeadLetter(poison);
        }

        var message = delivery.Message!;
        var instanceId = registration.InstanceIdFor(message.EventId);

        // Taken before the run and released after it, which is what "in flight" means: a slot is
        // held for exactly as long as the flow it admitted is unfinished. A poison message never
        // reaches here, because a dead-letter runs nothing and holding a slot for it would let a
        // broken partition consume the ceiling.
        using var slot = _host.AdmissionGate.TryAcquire();

        if (!slot.Admitted)
        {
            TriggerAdmissionCounter.Rejected(
                TriggerKind.Bus, TriggerAdmissionCounter.ShedReason, message.TenantId);

            return BusAdmission.Requeue;
        }

        var result = await _host
            .RunAsync(
                registration.Flow.Plan,
                registration.Flow.Dispatcher,

                // The correlation id is the event's, so the emitting instance's log lines and the
                // consuming instance's carry one id between them; the causation id is the
                // instance this delivery started. There is no inbound request to inherit either
                // from, and minting a fresh correlation per node would make one message look like
                // several.
                //
                // The tenant is the message's own field, written beside the body by the
                // publishing side and never read out of the payload (docs/16 §3). Attested and
                // not IsContinuation: this is a start, so every step's stance is still decided.
                new FlowInvocation(
                    message.EventId.ToString("d"),
                    instanceId.ToString(),
                    message.TenantId,
                    TenantAttested: true),
                message,
                instanceId,
                ct)
            .ConfigureAwait(false);

        var disposition = DispositionFor(result);

        if (disposition == BusDisposition.Requeue)
        {
            return BusAdmission.Requeue;
        }

        TriggerAdmissionCounter.Admitted(
            TriggerKind.Bus,
            disposition == BusDisposition.Deduplicated ? "deduplicated" : "started",
            message.TenantId);

        return new BusAdmission(disposition, instanceId);
    }

    /// <summary>One delivery, from the broker's offer to the broker's answer.</summary>
    private async Task<BusScanReport> DeliverAsync(
        BusRegistration registration, BusDelivery delivery, CancellationToken ct)
    {
        var one = BusScanReport.Nothing with { Received = 1 };
        var admission = await AdmitAsync(registration, delivery, ct).ConfigureAwait(false);

        switch (admission.Disposition)
        {
            case BusDisposition.DeadLetter:
                await _consumer!
                    .DeadLetterAsync(registration.Subscription, delivery, admission.Reason!, ct)
                    .ConfigureAwait(false);

                return one with { DeadLettered = 1 };

            case BusDisposition.Requeue:
                return one with { Requeued = 1 };

            default:
                await _consumer!
                    .AcknowledgeAsync(registration.Subscription, delivery, ct)
                    .ConfigureAwait(false);

                return admission.Disposition == BusDisposition.Deduplicated
                    ? one with { Deduplicated = 1 }
                    : one with { Started = 1 };
        }
    }

    /// <summary>
    /// Why this delivery can never be processed, or <c>null</c> when it still can.
    /// </summary>
    /// <remarks>
    /// The two conditions of
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0038-a-poison-message-is-dead-lettered.md">ADR-0038</a>,
    /// and they are different failures. An entry that is not a message will never become one, so
    /// it goes on its first delivery; an entry whose flow keeps not committing gets
    /// <see cref="FlowXOptions.BusMaxDeliveries"/> chances first, because the reason may well be
    /// a store that is down.
    /// </remarks>
    private string? PoisonReasonFor(BusDelivery delivery)
    {
        if (!delivery.IsReadable)
        {
            return "The entry is not a message: " + delivery.UnreadableReason +
                   ". Dead-lettered on its first delivery, because no number of retries turns an " +
                   "entry that cannot be read into one that can.";
        }

        if (delivery.DeliveryCount > _options.BusMaxDeliveries)
        {
            return
                $"Delivered {delivery.DeliveryCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                $"times without the flow reaching a recorded outcome, which is past " +
                $"FlowXOptions.BusMaxDeliveries " +
                $"({_options.BusMaxDeliveries.ToString(System.Globalization.CultureInfo.InvariantCulture)}). " +
                "Dead-lettered so it stops blocking its partition.";
        }

        return null;
    }

    /// <summary>
    /// What to tell the broker, given what the runtime did — the whole of ADR-0036.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A flow that failed as a value is acknowledged.</strong> ADR-0007 says a business
    /// failure is a <c>Result</c> and not an exception, so "the handler threw" is not the only
    /// failure mode — and a flow that ran, compensated and wrote a terminal row has
    /// <em>happened</em>. Redelivering it would be an infinite loop over a message whose instance
    /// the primary key now refuses.
    /// </para>
    /// <para>
    /// <strong>A suspension is acknowledged too.</strong> The flow parked at a wait, the journal
    /// holds it, and a signal will resume it. Holding the message until the flow finally completes
    /// would mean a message pending for the human-scale duration of an approval, which every
    /// broker reclaims long before.
    /// </para>
    /// <para>
    /// <strong>Only a refusal that journalled nothing requeues.</strong> The lease was held
    /// elsewhere, the fence was raised under us, the host is draining, no journal is configured,
    /// or admission refused the message on its tenant's account. In each of those no instance row
    /// describes this message, so the broker is the only thing still holding it.
    /// </para>
    /// <para>
    /// <strong>A tenant refusal requeues, and <see cref="PoisonReasonFor"/> is what stops that
    /// becoming a loop.</strong> Acknowledging a message admission never let start would discard
    /// it at the broker with nothing anywhere describing it — the same silent loss the change
    /// cursor used to commit. Requeued, it is redelivered; a deployment that genuinely cannot
    /// name the tenant spends <see cref="FlowXOptions.BusMaxDeliveries"/> and is dead-lettered,
    /// which puts it somewhere a human can find rather than nowhere.
    /// </para>
    /// </remarks>
    private static BusDisposition DispositionFor(FlowExecutionResult result)
    {
        if (result.IsSuccess || result.IsSuspended)
        {
            return BusDisposition.Started;
        }

        return result.Error!.Code switch
        {
            // An earlier delivery of this same message already ran it. This delivery has nothing
            // left to do, which is the answer it asked for and not a failure.
            DurabilityErrors.InstanceExistsCode => BusDisposition.Deduplicated,

            DurabilityErrors.LeaseHeldCode
                or DurabilityErrors.LeaseLostCode
                or DurabilityErrors.FencedOutCode
                or HostDrainingCode
                or DurabilityNotConfiguredCode
                or TenantErrors.TenantRequiredCode
                or TenantErrors.CrossTenantDeniedCode
                or TenantErrors.RateLimitedCode
                or TenantErrors.QuotaExhaustedCode
                or TenantErrors.SaturatedCode
                or TenantErrors.FairnessUnavailableCode => BusDisposition.Requeue,

            // The flow ran and ended badly, which is the flow's outcome and not the delivery's
            // (ADR-0007, ADR-0036).
            _ => BusDisposition.Started,
        };
    }

    /// <summary>The refusal a draining host gives, matched by code rather than by identity.</summary>
    private const string HostDrainingCode = "host.draining";

    /// <summary>The refusal a host with no journal gives a <c>Durable</c> flow.</summary>
    private const string DurabilityNotConfiguredCode = "flow.durability_not_configured";

    /// <summary>Puts what a pass did onto its span, and hands the report back unchanged.</summary>
    private static BusScanReport Tagged(Activity? span, in BusScanReport report)
    {
        if (span is not null)
        {
            span.SetTag("flowx.scan.received", report.Received);
            span.SetTag("flowx.scan.started", report.Started);
            span.SetTag("flowx.scan.deduplicated", report.Deduplicated);
            span.SetTag("flowx.scan.requeued", report.Requeued);
            span.SetTag("flowx.scan.dead_lettered", report.DeadLettered);
        }

        return report;
    }
}

/// <summary>What became of one delivery.</summary>
/// <remarks>
/// <strong>A decision, not an acknowledgement.</strong> Each member says what the delivery is,
/// and every caller of <see cref="FlowBusScan.AdmitAsync"/> settles it in its own way: the sweep
/// answers the broker through <see cref="IBusConsumer"/>, and a push host returns the decision to
/// a platform that holds the lock itself.
/// </remarks>
public enum BusDisposition
{
    /// <summary>The flow ran, and reached an outcome the journal holds.</summary>
    Started,

    /// <summary>An earlier delivery of this message already started it (ADR-0035).</summary>
    Deduplicated,

    /// <summary>
    /// Nothing recorded this delivery, so the message must be offered again.
    /// </summary>
    /// <remarks>
    /// The lease was held elsewhere, the fence was raised, the host is draining, or admission
    /// refused the message on its tenant's account. Acknowledging one of these would discard a
    /// message with nothing anywhere describing it.
    /// </remarks>
    Requeue,

    /// <summary>
    /// The message can never be processed and belongs somewhere a human can find it.
    /// </summary>
    /// <remarks>ADR-0038's two conditions, and <see cref="BusAdmission.Reason"/> says which.</remarks>
    DeadLetter,
}

/// <summary>What <see cref="FlowBusScan.AdmitAsync"/> decided about one delivery.</summary>
/// <param name="Disposition">What the delivery is.</param>
/// <param name="InstanceId">
/// The instance the delivery names (ADR-0035), for a <see cref="BusDisposition.Started"/> or a
/// <see cref="BusDisposition.Deduplicated"/>, and null otherwise. A push host logs it; nothing
/// derives anything from it.
/// </param>
/// <param name="Reason">
/// Why the message is undeliverable, on <see cref="BusDisposition.DeadLetter"/> alone. It is the
/// sentence that reaches the dead-letter destination, so it is written for the operator who
/// finds it there.
/// </param>
public sealed record BusAdmission(
    BusDisposition Disposition, Guid? InstanceId = null, string? Reason = null)
{
    /// <summary>The decision for a delivery nothing recorded.</summary>
    public static BusAdmission Requeue { get; } = new(BusDisposition.Requeue);

    /// <summary>The decision for a message ADR-0038 says is poison.</summary>
    /// <param name="reason">Why, in a sentence an operator can act on.</param>
    /// <returns>The decision.</returns>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is null or blank.</exception>
    public static BusAdmission DeadLetter(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new BusAdmission(BusDisposition.DeadLetter, Reason: reason);
    }
}

/// <summary>What one bus pass found and did.</summary>
/// <remarks>
/// Counts rather than a list of messages, for the reason <see cref="ScheduleScanReport"/> carries
/// counts: a report is what a metric is derived from and what a test asserts on.
/// </remarks>
public sealed record BusScanReport
{
    /// <summary>A pass that had nothing to do, and the base for one that did.</summary>
    public static BusScanReport Nothing { get; } = new();

    /// <summary>How many messages the broker offered.</summary>
    public int Received { get; init; }

    /// <summary>How many started a flow.</summary>
    public int Started { get; init; }

    /// <summary>
    /// How many were a redelivery of a message an instance already exists for.
    /// </summary>
    /// <remarks>
    /// <strong>The expected number on a fleet, and on any deployment where a node has ever
    /// restarted mid-flow.</strong> An operator watching this expecting zero is watching the wrong
    /// number: it is at-least-once delivery meeting exactly-once execution, which is the whole
    /// design working. What is worth an alert is <see cref="Requeued"/> climbing while
    /// <see cref="Started"/> does not.
    /// </remarks>
    public int Deduplicated { get; init; }

    /// <summary>How many were left pending because nothing recorded them.</summary>
    public int Requeued { get; init; }

    /// <summary>How many were diverted to the dead-letter path.</summary>
    public int DeadLettered { get; init; }

    /// <summary>How many partitions another node was already serving.</summary>
    public int Contended { get; init; }

    /// <summary>Why the pass itself failed, when it did.</summary>
    public Error? Error { get; init; }

    /// <summary>Sums two reports, keeping the first error seen.</summary>
    /// <param name="other">The report to add.</param>
    /// <returns>The sum.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    public BusScanReport Add(BusScanReport other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return new BusScanReport
        {
            Received = Received + other.Received,
            Started = Started + other.Started,
            Deduplicated = Deduplicated + other.Deduplicated,
            Requeued = Requeued + other.Requeued,
            DeadLettered = DeadLettered + other.DeadLettered,
            Contended = Contended + other.Contended,
            Error = Error ?? other.Error,
        };
    }
}
