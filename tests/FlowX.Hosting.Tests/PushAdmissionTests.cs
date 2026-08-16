using System.Text.Json;
using System.Text.Json.Serialization;
using FlowX.Conformance.InMemory;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Hosting.Tests;

/// <summary>The contract a pushed HTTP request carries.</summary>
/// <param name="Sku">What was ordered.</param>
public sealed record PushedOrder(string Sku);

/// <summary>What the flow answers with.</summary>
/// <param name="Sku">What was ordered.</param>
public sealed record PushedReceipt(string Sku);

/// <summary>Source-generated metadata for both directions, as the seam demands.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PushedOrder))]
[JsonSerializable(typeof(PushedReceipt))]
internal sealed partial class PushedOrderJson : JsonSerializerContext;

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

    /// <summary>
    /// A burst past the ceiling loses nothing: the excess is requeued, and every requeued
    /// delivery completes when it is offered again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The whole of WP-144's guarantee in one method.</strong> Shedding is only worth
    /// having if what is shed comes back, so this asserts both halves against one seam: five
    /// distinct messages are offered to a node whose ceiling is two, three of them are refused
    /// while the first two are still running, and after the two finish the same three start and
    /// journal. The count that matters is the last one — five messages in, five instances
    /// recorded, none invented and none lost.
    /// </para>
    /// <para>
    /// <strong>The saturation is waited for rather than assumed.</strong> The two admitted flows
    /// park inside the dispatcher and signal that they are there, so by the time the burst is
    /// offered the gate is provably full. Racing three deliveries against two starts and hoping
    /// the ordering came out right is how this test would pass on a fast machine and fail in CI.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ABurstBeyondTheCeilingIsRequeuedAndEveryRequeuedDeliveryCompletesLater()
    {
        var dispatcher = new GatedDispatcher(holdUntilEntered: 2);
        var seam = BusSeam.Create(ceiling: 2, dispatcher: dispatcher);

        var holding = new[] { BusSeam.Delivery(), BusSeam.Delivery() };

        var running = holding
            .Select(delivery => seam.Scan.AdmitAsync(seam.Registration, delivery, Cancellation).AsTask())
            .ToArray();

        await dispatcher.Saturated.WaitAsync(Cancellation);

        seam.Host.AdmissionGate.InFlight.ShouldBe(
            2, "both admitted flows are inside the dispatcher and neither has released its slot.");

        // The burst. Offered serially, because the ceiling is already spent and the answer is
        // therefore the same whatever order they arrive in.
        var burst = new[] { BusSeam.Delivery(), BusSeam.Delivery(), BusSeam.Delivery() };
        var shed = new List<BusAdmission>();

        foreach (var delivery in burst)
        {
            shed.Add(await seam.Scan.AdmitAsync(seam.Registration, delivery, Cancellation));
        }

        shed.ShouldAllBe(
            admission => admission.Disposition == BusDisposition.Requeue,
            "over the ceiling the broker keeps the backlog. That is what a broker is for.");

        shed.ShouldAllBe(
            admission => admission.InstanceId == null,
            "a shed item started nothing, so it names no instance.");

        seam.Journal.Instances.Count.ShouldBe(
            2,
            "only the admitted pair may be recorded. A row for a shed delivery would describe an " +
            "execution that never happened, which is the loss this whole mechanism exists to avoid.");

        dispatcher.Release();

        var admitted = await Task.WhenAll(running);

        admitted.ShouldAllBe(admission => admission.Disposition == BusDisposition.Started);

        // Redelivery: the broker offers the three again, and now there are slots.
        var redelivered = new List<BusAdmission>();

        foreach (var delivery in burst)
        {
            redelivered.Add(await seam.Scan.AdmitAsync(seam.Registration, delivery, Cancellation));
        }

        redelivered.ShouldAllBe(
            admission => admission.Disposition == BusDisposition.Started,
            "a requeued delivery is not a failed one — it was never run, so it runs now.");

        seam.Journal.Instances.Count.ShouldBe(
            5, "five messages were offered and five flows are recorded. Nothing was lost.");

        seam.Host.AdmissionGate.InFlight.ShouldBe(
            0, "every slot taken was given back, including by the flows that were parked.");
    }

    /// <summary>
    /// With no ceiling set, an existing scenario behaves exactly as it does with one it never
    /// reaches — and the gate counts nothing at all.
    /// </summary>
    /// <remarks>
    /// <strong>The compatibility assertion, and it is run rather than argued.</strong> A hosting
    /// option must not change what a deployment already does by being added, so
    /// <c>ASweepOverAMessageThePushEntryAlreadyAdmittedStartsNothing</c> — an existing scenario
    /// that exercises push, sweep, dedup and settle in one pass — is run under both
    /// configurations and the two outcomes are compared field by field. The extra assertion is
    /// the one a comparison cannot make: on the unbounded host <c>InFlight</c> stays zero
    /// <em>while work is in flight</em>, because the unbounded path counts nothing, which is
    /// budget B6 holding.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData(64)]
    public async Task AnUnsetCeilingChangesNothingAboutAnExistingScenario(int? ceiling)
    {
        var seam = BusSeam.Create(ceiling: ceiling);
        var eventId = Guid.NewGuid();

        seam.Host.AdmissionGate.IsBounded.ShouldBe(ceiling is not null);
        seam.Host.AdmissionGate.Ceiling.ShouldBe(ceiling);

        seam.Broker.Stage(BusSeam.Topic, partitionKey: "instance-1", eventId: eventId);

        var pushed = await seam.Scan.AdmitAsync(
            seam.Registration, BusSeam.Delivery(eventId), Cancellation);

        pushed.Disposition.ShouldBe(BusDisposition.Started);

        var sweep = await seam.Scan.RunOnceAsync(Cancellation);

        sweep.Started.ShouldBe(0);
        sweep.Deduplicated.ShouldBe(1);
        sweep.Requeued.ShouldBe(0, "nothing may be shed by a ceiling that is unset or ample.");

        seam.Journal.Instances.Count.ShouldBe(1);
        seam.Broker.Acknowledged.Count.ShouldBe(1);

        seam.Host.AdmissionGate.InFlight.ShouldBe(0);
    }

    /// <summary>
    /// A window over the ceiling is held, which leaves its checkpoint where it was.
    /// </summary>
    /// <remarks>
    /// <strong><c>Held</c> and not <c>Refused</c>, and the difference is a lost aggregate.</strong>
    /// Refused says the window can never run, and a driver obeying that would settle past it —
    /// discarding the records of a window whose only problem was that the node was momentarily
    /// full. Held says nothing recorded it, which is exactly what happened, and it is the
    /// disposition <c>StartAsync</c> maps to <c>Settled: false</c>.
    /// </remarks>
    [Fact]
    public async Task AWindowOverTheCeilingIsHeldAndKeepsItsCheckpoint()
    {
        var seam = StreamSeam.Create(ceiling: 1);

        // The one slot, taken by the test, so the seam is provably at its ceiling without a
        // second window having to be in flight to hold it there.
        using var held = seam.Host.AdmissionGate.TryAcquire();

        held.Admitted.ShouldBeTrue("the ceiling is one and nothing else has taken it.");

        var admission = await seam.Scan.AdmitAsync(
            seam.Registration,
            new ClosedWindow(
                StreamSeam.Start, StreamSeam.Start.AddSeconds(10), [StreamSeam.Record(tenantId: null)]),
            Cancellation);

        admission.Disposition.ShouldBe(WindowDisposition.Held);
        admission.InstanceId.ShouldBeNull();

        admission.Error.ShouldBeNull(
            "a shed window is not a broken one. An error here would send a driver looking for a " +
            "defect in records that are perfectly good.");

        seam.Journal.Instances.ShouldBeEmpty();

        seam.Checkpoints.Committed.ShouldBeEmpty(
            "a window that did not run must not have its records checkpointed past. That is the " +
            "aggregate this disposition exists to keep.");
    }

    /// <summary>
    /// An HTTP request over the ceiling gets 429, a Retry-After, an RFC 7807 body — and no
    /// journal row.
    /// </summary>
    /// <remarks>
    /// <strong>The control is what makes the last clause a test.</strong> The same request under
    /// an unsaturated gate journals an instance, so "no row" here is the ceiling's doing rather
    /// than a property of a seam that never journals anything.
    /// </remarks>
    [Fact]
    public async Task AnHttpRequestOverTheCeilingIsRefusedWith429AndIsNotJournaled()
    {
        var seam = HttpSeam.Create(ceiling: 1);

        using (var held = seam.Host.AdmissionGate.TryAcquire())
        {
            held.Admitted.ShouldBeTrue();

            var shed = await seam.PostAsync(Cancellation);

            shed.Status.ShouldBe(429);
            shed.ContentType.ShouldBe("application/problem+json");

            shed.RetryAfterSeconds.ShouldBe(
                1, "the header a caller obeys, and it is what the generated entry point writes.");

            using var problem = JsonDocument.Parse(shed.Body);

            // RFC 7807's four members, plus the extension the header duplicates.
            problem.RootElement.GetProperty("type").GetString()
                .ShouldBe("https://flowx.dev/errors/host.saturated");

            problem.RootElement.GetProperty("title").GetString()
                .ShouldBe("The service is temporarily unavailable");

            problem.RootElement.GetProperty("status").GetInt32().ShouldBe(429);
            problem.RootElement.GetProperty("detail").GetString().ShouldNotBeNull()
                .ShouldContain("shed rather than queued");
            problem.RootElement.GetProperty("retryAfterSeconds").GetInt32().ShouldBe(1);

            problem.RootElement.GetProperty("code").GetString().ShouldBe("host.saturated");

            seam.Journal.Instances.ShouldBeEmpty(
                "the request was shed before its body was read, so there is nothing to record.");
        }

        // The control: the slot is back, and the identical request now runs and journals.
        var admitted = await seam.PostAsync(Cancellation);

        admitted.Status.ShouldBe(200);
        admitted.RetryAfterSeconds.ShouldBeNull("a request that succeeded has nothing to retry.");

        seam.Journal.Instances.ShouldHaveSingleItem();
    }

    /// <summary>One node, one journal, one broker nothing is expected to call, and one subscription.</summary>
    internal sealed class BusSeam
    {
        internal const string Topic = "order.placed";

        private static readonly CapabilityDescriptor Reprice =
            CapabilityDescriptor.Create("pricing.reprice", "1.0.0", isIdempotent: true);

        private BusSeam(TenantIsolation isolation, int? ceiling, IStepDispatcher? dispatcher)
        {
            Options = new FlowXOptions
            {
                ApplicationName = "Tests",
                NodeName = "node",
                ShutdownDrainTimeout = TimeSpan.FromSeconds(5),
                TenantIsolation = isolation,

                // Set here rather than on the returned seam, and that is not a style choice:
                // FlowHost reads it once to build its FlowAdmissionGate, so a test that assigned
                // it afterwards would configure a ceiling nothing consults and then assert that
                // nothing was shed.
                MaxInFlightAdmissions = ceiling,
            };

            var durability = new FlowDurability(Journal, new InMemoryLeaseStore());

            Host = new FlowHost(new FlowEngine(SystemClock.Instance), Options, durability);

            var subscriptions = new FlowBusCatalog().Add(
                new BusSubscription("pricing.reprice", "1.0.0", Topic, "pricing"),
                ExecutionPlan.Create(
                    FlowDescriptor.Create(
                        "pricing.reprice", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromMinutes(5)),
                    StepGraph.Create([StepNode.ForCapability(0, Reprice)])),
                dispatcher ?? new SucceedingDispatcher());

            Registration = subscriptions.Registrations[0];
            Scan = new FlowBusScan(Host, subscriptions, Broker, durability, Options);
        }

        public InMemoryFlowJournal Journal { get; } = new();

        /// <summary>The host, for a test that needs to reach the ceiling every seam shares.</summary>
        public FlowHost Host { get; }

        /// <summary>
        /// The broker the sweep would use, present because <see cref="FlowBusScan"/> takes one —
        /// and asserted untouched by every push test here.
        /// </summary>
        public RecordingBusConsumer Broker { get; } = new();

        public FlowXOptions Options { get; }

        public BusRegistration Registration { get; }

        public FlowBusScan Scan { get; }

        public static BusSeam Create(
            TenantIsolation isolation = TenantIsolation.None,
            int? ceiling = null,
            IStepDispatcher? dispatcher = null) =>
            new(isolation, ceiling, dispatcher);

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

        private StreamSeam(int? ceiling)
        {
            var options = new FlowXOptions
            {
                ApplicationName = "Tests",
                NodeName = "node",
                MaxInFlightAdmissions = ceiling,
            };

            var durability = new FlowDurability(Journal, new InMemoryLeaseStore());

            Host = new FlowHost(new FlowEngine(SystemClock.Instance), options, durability);

            var host = Host;

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

        /// <summary>The host, for a test that needs to reach the ceiling every seam shares.</summary>
        public FlowHost Host { get; }

        public StreamRegistration Registration { get; }

        public FlowStreamScan Scan { get; }

        public static StreamSeam Create(int? ceiling = null) => new(ceiling);

        /// <summary>One record in the first window, under a tenant a test names.</summary>
        public static StreamRecord Record(string? tenantId) =>
            new(new StreamPosition("0"), Start, TenantId: tenantId);
    }

    /// <summary>
    /// One node and the serverless HTTP door onto it, with a journal so that "nothing was
    /// recorded" is an assertion rather than a property of the fixture.
    /// </summary>
    internal sealed class HttpSeam
    {
        private static readonly CapabilityDescriptor Reprice =
            CapabilityDescriptor.Create("pricing.reprice", "1.0.0", isIdempotent: true);

        private readonly ExecutionPlan _plan;

        private HttpSeam(int? ceiling)
        {
            var options = new FlowXOptions
            {
                ApplicationName = "Tests",
                NodeName = "node",
                MaxInFlightAdmissions = ceiling,
            };

            var durability = new FlowDurability(Journal, new InMemoryLeaseStore());

            Host = new FlowHost(new FlowEngine(SystemClock.Instance), options, durability);

            // Durable, so an admitted request writes an instance row and the control arm of the
            // shed test has something to see.
            _plan = ExecutionPlan.Create(
                FlowDescriptor.Create(
                    "pricing.reprice", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromMinutes(5)),
                StepGraph.Create([StepNode.ForCapability(0, Reprice)]));

            Seams = new FlowPushSeams(Host, new FlowBusCatalog(), bus: null, schedule: null, change: null);
        }

        public InMemoryFlowJournal Journal { get; } = new();

        public FlowHost Host { get; }

        public FlowPushSeams Seams { get; }

        public static HttpSeam Create(int? ceiling = null) => new(ceiling);

        /// <summary>One request through the serverless door, body and all.</summary>
        public ValueTask<FlowFunctionResponse> PostAsync(CancellationToken cancellationToken) =>
            Seams.HttpAsync<PushedOrder, PushedReceipt>(
                _plan,
                new SucceedingDispatcher(),
                new MemoryStream("{\"sku\":\"SKU-7\"}"u8.ToArray()),
                headers: null,
                static _ => new PushedReceipt("SKU-7"),
                PushedOrderJson.Default,
                requireIdempotencyKey: false,
                cancellationToken);
    }

    /// <summary>
    /// A dispatcher that parks every step until it is released, so a test can hold the ceiling
    /// full at a known instant.
    /// </summary>
    /// <remarks>
    /// <see cref="Saturated"/> completes when <c>holdUntilEntered</c> steps are inside, which is
    /// what turns "the gate is full" from a race into a wait.
    /// </remarks>
    private sealed class GatedDispatcher(int holdUntilEntered) : IStepDispatcher
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _saturated =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _entered;

        /// <summary>Completes once enough steps are parked to have spent the ceiling.</summary>
        public Task Saturated => _saturated.Task;

        /// <summary>Lets every parked step finish.</summary>
        public void Release() => _release.TrySetResult();

        public async ValueTask<StepOutcome> ExecuteAsync(
            int stepIndex, FlowContext ctx, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _entered) == holdUntilEntered)
            {
                _saturated.TrySetResult();
            }

            await _release.Task.WaitAsync(ct).ConfigureAwait(false);

            return StepOutcome.Success;
        }

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
