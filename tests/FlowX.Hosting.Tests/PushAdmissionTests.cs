using FlowX.Conformance.InMemory;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Hosting.Tests;

/// <summary>
/// The per-item entry a pushed delivery arrives through: what it decides, and what it deliberately
/// does not do about it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A push host has no broker to answer.</strong> Its platform hands it one message, holds
/// the lock itself, and settles from the value the function returns — so a seam that acknowledged
/// through <see cref="IBusConsumer"/> would either acknowledge a message twice or fight the
/// platform for a lock it does not hold. Every test here therefore asserts on the
/// <see cref="BusAdmission"/> and on the broker having been left alone, which is the pair of facts
/// <see cref="BusScanTests"/> cannot state because the sweep settles by design.
/// </para>
/// <para>
/// <strong>The decisions themselves are not re-tested here.</strong> Redelivery, acknowledgement,
/// ordering and poison are asserted in <see cref="BusScanTests"/> against the pull path, and the
/// point of this seam is that there is one copy of them; a second suite asserting the same four
/// would pass while the two routes drifted. What is new is the disposition being <em>returned</em>
/// rather than acted on.
/// </para>
/// </remarks>
public sealed class PushAdmissionTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// One pushed message starts the flow, journals it, and leaves the broker holding it.
    /// </summary>
    /// <remarks>
    /// The whole of what WP-140 adds, in one assertion pair: the flow ran, and nothing was
    /// acknowledged. A seam that settled would pass the first half.
    /// </remarks>
    [Fact]
    public async Task APushedMessageStartsItsFlowAndSettlesNothing()
    {
        var seam = BusSeam.Create();

        var admission = await seam.Scan.AdmitAsync(seam.Registration, BusSeam.Delivery(), Cancellation);

        admission.Disposition.ShouldBe(BusDisposition.Started);
        admission.InstanceId.ShouldNotBeNull();

        seam.Journal.Instances.ShouldHaveSingleItem().InstanceId.ShouldBe(admission.InstanceId!.Value);

        seam.Broker.Acknowledged.ShouldBeEmpty(
            "the platform that pushed the message settles it. Acknowledging here would " +
            "acknowledge a delivery this process never took from a broker.");

        seam.Broker.DeadLettered.ShouldBeEmpty();
    }

    /// <summary>
    /// An entry that is not a message comes back as a dead-letter decision, with the reason, and
    /// journals nothing.
    /// </summary>
    /// <remarks>
    /// ADR-0038's first condition. The reason is returned rather than sent, because the platform
    /// owns the dead-letter destination on a push host — Service Bus takes a reason string on
    /// <c>DeadLetterMessageAsync</c> — and it is the same sentence the sweep hands
    /// <c>IBusConsumer.DeadLetterAsync</c>.
    /// </remarks>
    [Fact]
    public async Task AnUnreadableEntryIsDeadLetteredWithItsReasonAndJournalsNothing()
    {
        var seam = BusSeam.Create();

        var admission = await seam.Scan.AdmitAsync(
            seam.Registration, BusSeam.Unreadable(), Cancellation);

        admission.Disposition.ShouldBe(BusDisposition.DeadLetter);
        admission.InstanceId.ShouldBeNull();

        admission.Reason.ShouldNotBeNull().ShouldContain(
            "not a message",
            customMessage: "ADR-0038 dead-letters an unreadable entry on its first delivery, and " +
            "the reason is what an operator reads off the dead-letter queue.");

        seam.Journal.Instances.ShouldBeEmpty(
            "nothing ran, so nothing may be recorded as having run. A journal row here would " +
            "describe an execution that never happened.");

        seam.Broker.DeadLettered.ShouldBeEmpty("the seam decides; the caller diverts.");
    }

    /// <summary>
    /// A message past <c>BusMaxDeliveries</c> is dead-lettered with the count in the reason, and
    /// journals nothing.
    /// </summary>
    /// <remarks>
    /// ADR-0038's second condition, and the one a push host cannot work out for itself: the
    /// platform knows the delivery count and the framework knows the bound.
    /// </remarks>
    [Fact]
    public async Task AMessageDeliveredTooManyTimesIsDeadLetteredAndJournalsNothing()
    {
        var seam = BusSeam.Create();

        seam.Options.BusMaxDeliveries = 2;

        var admission = await seam.Scan.AdmitAsync(
            seam.Registration, BusSeam.Delivery(deliveryCount: 3), Cancellation);

        admission.Disposition.ShouldBe(BusDisposition.DeadLetter);
        admission.Reason.ShouldNotBeNull().ShouldContain("BusMaxDeliveries");

        seam.Journal.Instances.ShouldBeEmpty();
    }

    /// <summary>
    /// A message admission refuses comes back as a requeue, so the platform abandons it.
    /// </summary>
    /// <remarks>
    /// The disposition a push host must not confuse with a failure: nothing journalled the
    /// message, so completing it would discard work with nothing anywhere describing it — the
    /// broker's version of a cursor committed past a change that never ran.
    /// </remarks>
    [Fact]
    public async Task AMessageThatNamedNoTenantComesBackAsARequeue()
    {
        var seam = BusSeam.Create(isolation: TenantIsolation.Row);

        var admission = await seam.Scan.AdmitAsync(seam.Registration, BusSeam.Delivery(), Cancellation);

        admission.Disposition.ShouldBe(BusDisposition.Requeue);
        admission.InstanceId.ShouldBeNull();

        seam.Journal.Instances.ShouldBeEmpty();
    }

    /// <summary>
    /// The pull path reaches the same seam, so one message decided twice is decided once.
    /// </summary>
    /// <remarks>
    /// <strong>This is the anti-drift assertion.</strong> The sweep is the seam plus a settle, and
    /// the way that stays true is that a message admitted through the push entry is then
    /// deduplicated by the sweep rather than started a second time — which can only happen if both
    /// derived the same instance id from the same delivery.
    /// </remarks>
    [Fact]
    public async Task ASweepOverAMessageThePushEntryAlreadyAdmittedStartsNothing()
    {
        var seam = BusSeam.Create();
        var eventId = Guid.NewGuid();

        seam.Broker.Stage(BusSeam.Topic, partitionKey: "instance-1", eventId: eventId);

        var pushed = await seam.Scan.AdmitAsync(
            seam.Registration, BusSeam.Delivery(eventId), Cancellation);

        pushed.Disposition.ShouldBe(BusDisposition.Started);

        var sweep = await seam.Scan.RunOnceAsync(Cancellation);

        sweep.Started.ShouldBe(0);
        sweep.Deduplicated.ShouldBe(1, "one message names one instance, whichever route it came in by");

        seam.Journal.Instances.Count.ShouldBe(1);
        seam.Broker.Acknowledged.Count.ShouldBe(1, "and the sweep, which does settle, settled");
    }

    /// <summary>
    /// A window whose records disagree about their tenant is refused rather than held.
    /// </summary>
    /// <remarks>
    /// The stream seam's own answer, and the one that is not a runtime refusal: a held window is
    /// offered again unchanged and this one would be refused identically for ever, so a push host
    /// that retried it would loop.
    /// </remarks>
    [Fact]
    public async Task AWindowOverTwoTenantsIsRefusedByTheStreamSeam()
    {
        var seam = StreamSeam.Create();

        var admission = await seam.Scan.AdmitAsync(
            seam.Registration,
            new ClosedWindow(
                StreamSeam.Start,
                StreamSeam.Start.AddSeconds(10),
                [StreamSeam.Record("acme"), StreamSeam.Record("globex")]),
            Cancellation);

        admission.Disposition.ShouldBe(WindowDisposition.Refused);
        admission.Error.ShouldNotBeNull().Code.ShouldBe("stream.mixed_tenants");

        seam.Journal.Instances.ShouldBeEmpty();
    }

    /// <summary>A pushed window starts its flow and moves no checkpoint.</summary>
    [Fact]
    public async Task APushedWindowStartsItsFlowAndCheckpointsNothing()
    {
        var seam = StreamSeam.Create();

        var admission = await seam.Scan.AdmitAsync(
            seam.Registration,
            new ClosedWindow(
                StreamSeam.Start, StreamSeam.Start.AddSeconds(10), [StreamSeam.Record(tenantId: null)]),
            Cancellation);

        admission.Disposition.ShouldBe(WindowDisposition.Started);

        seam.Journal.Instances.ShouldHaveSingleItem().InstanceId.ShouldBe(admission.InstanceId!.Value);

        seam.Checkpoints.Committed.ShouldBeEmpty(
            "progress belongs to whatever read the records. A seam that checkpointed would " +
            "commit past records a push host had not finished with.");
    }

    /// <summary>One node, one journal, one broker nothing is expected to call, and one subscription.</summary>
    internal sealed class BusSeam
    {
        internal const string Topic = "order.placed";

        private static readonly CapabilityDescriptor Reprice =
            CapabilityDescriptor.Create("pricing.reprice", "1.0.0", isIdempotent: true);

        private BusSeam(TenantIsolation isolation)
        {
            Options = new FlowXOptions
            {
                ApplicationName = "Tests",
                NodeName = "node",
                ShutdownDrainTimeout = TimeSpan.FromSeconds(5),
                TenantIsolation = isolation,
            };

            var durability = new FlowDurability(Journal, new InMemoryLeaseStore());
            var host = new FlowHost(new FlowEngine(SystemClock.Instance), Options, durability);

            var subscriptions = new FlowBusCatalog().Add(
                new BusSubscription("pricing.reprice", "1.0.0", Topic, "pricing"),
                ExecutionPlan.Create(
                    FlowDescriptor.Create(
                        "pricing.reprice", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromMinutes(5)),
                    StepGraph.Create([StepNode.ForCapability(0, Reprice)])),
                new SucceedingDispatcher());

            Registration = subscriptions.Registrations[0];
            Scan = new FlowBusScan(host, subscriptions, Broker, durability, Options);
        }

        public InMemoryFlowJournal Journal { get; } = new();

        /// <summary>
        /// The broker the sweep would use, present because <see cref="FlowBusScan"/> takes one —
        /// and asserted untouched by every push test here.
        /// </summary>
        public RecordingBusConsumer Broker { get; } = new();

        public FlowXOptions Options { get; }

        public BusRegistration Registration { get; }

        public FlowBusScan Scan { get; }

        public static BusSeam Create(TenantIsolation isolation = TenantIsolation.None) =>
            new(isolation);

        /// <summary>Admits one message through the push entry, for a test that needs only that.</summary>
        public static async ValueTask<BusAdmission> AdmitOneAsync(CancellationToken cancellationToken)
        {
            var seam = Create();

            return await seam.Scan.AdmitAsync(seam.Registration, Delivery(), cancellationToken);
        }

        /// <summary>
        /// A delivery as a platform would hand one over: a message, a lock token and a count.
        /// </summary>
        public static BusDelivery Delivery(Guid? eventId = null, int deliveryCount = 1) => BusDelivery.Of(
            new BusMessage(
                eventId ?? Guid.NewGuid(), Topic, Topic, "1.0.0", "instance-1", null, null),
            "lock-token",
            deliveryCount);

        /// <summary>An entry the platform could not read.</summary>
        public static BusDelivery Unreadable() =>
            BusDelivery.Unreadable("lock-token", 1, "instance-1", "its event-id field is absent");
    }

    /// <summary>
    /// One node, one journal, and one stream subscription whose source nothing reads: the windows
    /// are handed to the seam directly, which is what a push host does.
    /// </summary>
    internal sealed class StreamSeam
    {
        internal static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private const string Source = "device.telemetry";

        private StreamSeam()
        {
            var options = new FlowXOptions { ApplicationName = "Tests", NodeName = "node" };
            var durability = new FlowDurability(Journal, new InMemoryLeaseStore());
            var host = new FlowHost(new FlowEngine(SystemClock.Instance), options, durability);

            var subscriptions = new FlowStreamCatalog().Add(
                new StreamSubscription("telemetry.aggregate", "1.0.0", Source, string.Empty),
                "tumbling:10s",
                "PT0S",
                "PT0S",
                1,
                StreamScanTests.Plan(ExecutionProfile.Streaming),
                new RecordingStreamDispatcher());

            Registration = subscriptions.Registrations[0];

            Scan = new FlowStreamScan(
                host,
                subscriptions,
                new RecordingStreamSource(),
                Checkpoints,
                new RecordingSideOutput(),
                durability,
                options);
        }

        public InMemoryFlowJournal Journal { get; } = new();

        public RecordingCheckpointStore Checkpoints { get; } = new();

        public StreamRegistration Registration { get; }

        public FlowStreamScan Scan { get; }

        public static StreamSeam Create() => new();

        /// <summary>One record in the first window, under a tenant a test names.</summary>
        public static StreamRecord Record(string? tenantId) =>
            new(new StreamPosition("0"), Start, TenantId: tenantId);
    }

    /// <summary>A dispatcher whose one step succeeds, so an admitted item reaches a terminal row.</summary>
    private sealed class SucceedingDispatcher : IStepDispatcher
    {
        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct) =>
            ValueTask.FromResult(StepOutcome.Success);

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct) =>
            ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This double runs plans with no branch step.");

        public int Select(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This double runs plans with no switch step.");

        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to begin.");

        public FlowContext EnterIteration(
            int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to enter.");
    }
}
