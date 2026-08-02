using FlowX.Conformance.InMemory;
using FlowX.Hosting;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Hosting.Tests;

/// <summary>
/// What starts a flow from a broker message, what stops one message starting two flows, and
/// what becomes of a message nobody can process.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two silent failures, and both are tested here</strong> — the framing
/// <see cref="ScheduleScanTests"/> is written under, and it is sharper for a bus. A consumer that
/// never fires and a consumer that starts two flows for one message look identical from the
/// outside: no exception, no status code, no metric. The second is the one an at-least-once
/// broker produces <em>by default</em>, because redelivery is its contract rather than its
/// failure mode. So these tests assert a <em>count</em> of journalled instances rather than that
/// something ran.
/// </para>
/// <para>
/// The stores are the conformance suite's reference implementations, for
/// <see cref="ScheduleScanTests"/>'s reason: it is <c>StartAsync</c>'s refusal of a duplicate id
/// that this whole design rests on, and a store written for a test is a store nothing holds to
/// <c>JournalConformance</c>. The broker is a recording double here and a real Redis in
/// <c>FlowX.Redis.Tests</c>; both derive <c>BusConsumerConformance</c>.
/// </para>
/// </remarks>
public sealed class BusScanTests
{
    private static readonly CapabilityDescriptor Reprice =
        CapabilityDescriptor.Create("pricing.reprice", "1.0.0", isIdempotent: true);

    private const string Topic = "order.placed";

    private const string Group = "pricing";

    /// <summary>A pass with nothing on the broker starts nothing.</summary>
    [Fact]
    public async Task APassOverAnEmptyBrokerStartsNothing()
    {
        var fixture = Fixture.Create();

        var report = await fixture.PassAsync();

        report.Received.ShouldBe(0);
        report.Started.ShouldBe(0);
        fixture.Journal.Instances.ShouldBeEmpty();
    }

    /// <summary>One message on the broker starts the flow, and hands it the message.</summary>
    [Fact]
    public async Task OneMessageStartsTheFlow()
    {
        var fixture = Fixture.Create();

        fixture.Broker.Stage(Topic, partitionKey: "instance-1");

        var report = await fixture.PassAsync();

        report.Received.ShouldBe(1);
        report.Started.ShouldBe(1);

        fixture.Journal.Instances.Count.ShouldBe(1);
        fixture.Journal.Instances[0].FlowId.ShouldBe("pricing.reprice");

        var message = fixture.Dispatcher.Inputs[0].ShouldBeOfType<BusMessage>();

        message.Topic.ShouldBe(Topic);
        message.PartitionKey.ShouldBe("instance-1");
    }

    /// <summary>
    /// The same message delivered twice starts one flow.
    /// </summary>
    /// <remarks>
    /// <strong>This is the test the whole design exists for.</strong> The broker will redeliver;
    /// that is its contract, not its defect. The delivery derives the instance id it starts
    /// (ADR-0035), so the second one is refused by the journal's primary key rather than executed
    /// — and it is <em>acknowledged</em> on that refusal (ADR-0036), because
    /// <c>journal.instance_exists</c> means this delivery has nothing left to do.
    /// </remarks>
    [Fact]
    public async Task ARedeliveredMessageStartsNoSecondFlow()
    {
        var fixture = Fixture.Create();
        var eventId = Guid.NewGuid();

        fixture.Broker.Stage(Topic, partitionKey: "instance-1", eventId: eventId);
        await fixture.PassAsync();

        fixture.Broker.Stage(Topic, partitionKey: "instance-1", eventId: eventId);
        var second = await fixture.PassAsync();

        second.Received.ShouldBe(1, "the broker offered it again, which is what at-least-once means");
        second.Started.ShouldBe(0);
        second.Deduplicated.ShouldBe(1);

        fixture.Journal.Instances.Count.ShouldBe(
            1, "one message names one instance, however many times it arrives");

        fixture.Broker.Acknowledged.Count.ShouldBe(
            2, "a redelivery that found the instance already journalled is finished with");
    }

    /// <summary>Two different messages start two flows.</summary>
    /// <remarks>
    /// The other half of the previous test, and it is not redundant: a consumer that deduplicated
    /// on the topic, on the partition key, or on nothing at all would pass one of the two.
    /// </remarks>
    [Fact]
    public async Task TwoDifferentMessagesStartTwoFlows()
    {
        var fixture = Fixture.Create();

        fixture.Broker.Stage(Topic, partitionKey: "instance-1");
        fixture.Broker.Stage(Topic, partitionKey: "instance-1");

        var report = await fixture.PassAsync();

        report.Started.ShouldBe(2);
        fixture.Journal.Instances.Count.ShouldBe(2);
    }

    /// <summary>Ten nodes offered one message produce one instance.</summary>
    [Fact]
    public async Task TenNodesOfferedOneMessageProduceOneInstance()
    {
        var fixture = Fixture.Create();
        var nodes = fixture.Nodes(10);

        fixture.Broker.Stage(Topic, partitionKey: "instance-1", copies: 10);

        var reports = await Task.WhenAll(
            nodes.Select(node => node.RunOnceAsync(TestContext.Current.CancellationToken).AsTask()));

        fixture.Journal.Instances.Count.ShouldBe(1);
        reports.Sum(static r => r.Started).ShouldBe(1);
    }

    /// <summary>A flow that fails as a value is acknowledged, not redelivered.</summary>
    /// <remarks>
    /// ADR-0007 says a business failure is a <c>Result</c> and not an exception, so "the handler
    /// threw" is not the only failure mode — and a flow that ran, decided against and wrote a
    /// terminal row has <em>happened</em>. Redelivering it would be an infinite loop over a
    /// message whose instance the primary key now refuses.
    /// </remarks>
    [Fact]
    public async Task AFlowThatFailsAsAValueIsAcknowledged()
    {
        var fixture = Fixture.Create(failing: true);

        fixture.Broker.Stage(Topic, partitionKey: "instance-1");

        var report = await fixture.PassAsync();

        report.Started.ShouldBe(1, "the flow ran; what it decided is the flow's business");
        fixture.Broker.Acknowledged.Count.ShouldBe(1);
        fixture.Broker.Pending.ShouldBeEmpty();
    }

    /// <summary>
    /// A message carrying its publisher's tenant field starts the subscribing flow in that
    /// tenant.
    /// </summary>
    /// <remarks>
    /// <strong>The field, never the payload.</strong> <c>docs/16 §3</c> allows a bus tenant from
    /// "a message header set by a FlowX producer" and forbids one read out of the body, and the
    /// difference is where the value travels: this one is written by the publisher from
    /// <c>OutboxRecord.TenantId</c> and read back by the consumer plugin, so nothing the event's
    /// author wrote is consulted.
    /// </remarks>
    [Fact]
    public async Task AMessageStartsItsFlowInTheTenantItsPublisherNamed()
    {
        var fixture = Fixture.Create(isolation: TenantIsolation.Row);

        fixture.Broker.Stage(Topic, partitionKey: "instance-1", tenantId: "tenant-a");

        var report = await fixture.PassAsync();

        report.Started.ShouldBe(1);

        fixture.Journal.Instances.ShouldHaveSingleItem().TenantId.ShouldBe("tenant-a");

        fixture.Broker.Acknowledged.Count.ShouldBe(
            1, "the flow reached a recorded outcome, which is what an acknowledgement means.");
    }

    /// <summary>
    /// A message naming no tenant on an isolating deployment is left with the broker rather than
    /// acknowledged.
    /// </summary>
    /// <remarks>
    /// <strong>Acknowledging it would discard it with nothing anywhere describing it</strong> —
    /// the broker's version of a cursor committed past a change that never ran. Requeued, it is
    /// redelivered, and a deployment that genuinely cannot name the tenant spends
    /// <c>BusMaxDeliveries</c> and is dead-lettered, which puts the message somewhere a human can
    /// find rather than nowhere.
    /// </remarks>
    [Fact]
    public async Task AnUntenantedMessageIsNotAcknowledgedWhereTheDeploymentIsolates()
    {
        var fixture = Fixture.Create(isolation: TenantIsolation.Row);

        fixture.Broker.Stage(Topic, partitionKey: "instance-1");

        var report = await fixture.PassAsync();

        report.Started.ShouldBe(0);
        report.Requeued.ShouldBe(1);

        fixture.Journal.Instances.ShouldBeEmpty();

        fixture.Broker.Acknowledged.ShouldBeEmpty(
            "this is the assertion: an acknowledged message admission never let start is work " +
            "lost with no record of it anywhere.");

        fixture.Broker.Pending.ShouldHaveSingleItem(
            "and the broker is still holding it, so a repaired deployment runs it.");
    }

    /// <summary>An entry that is not a message is dead-lettered on its first delivery.</summary>
    [Fact]
    public async Task AnUnreadableEntryIsDeadLetteredImmediately()
    {
        var fixture = Fixture.Create();

        fixture.Broker.StageUnreadable(Topic, partitionKey: "instance-1");

        var report = await fixture.PassAsync();

        report.DeadLettered.ShouldBe(1);
        report.Started.ShouldBe(0);
        fixture.Broker.DeadLettered.Count.ShouldBe(1);
        fixture.Broker.Pending.ShouldBeEmpty("a poison message must not block its partition");
    }

    /// <summary>The entries of one partition start their flows in the order they were staged.</summary>
    /// <remarks>
    /// ADR-0018 offers per-<c>partition_key</c> order on publication; ADR-0037 is the promise that
    /// consumption does not throw it away on the last hop. Three events under one key, three flows
    /// in that order.
    /// </remarks>
    [Fact]
    public async Task EntriesOfOnePartitionStartTheirFlowsInOrder()
    {
        var fixture = Fixture.Create();

        fixture.Broker.Stage(Topic, partitionKey: "instance-1", payload: "first");
        fixture.Broker.Stage(Topic, partitionKey: "instance-1", payload: "second");
        fixture.Broker.Stage(Topic, partitionKey: "instance-1", payload: "third");

        await fixture.PassAsync();

        fixture.Dispatcher.Inputs
            .Cast<BusMessage>()
            .Select(static message => message.Payload)
            .ShouldBe(["first", "second", "third"]);
    }

    /// <summary>A partition another node is serving is left alone, and counted as contended.</summary>
    /// <remarks>
    /// The lease is what keeps ADR-0037's guarantee true on a fleet: a consumer group would
    /// otherwise hand the entries of one partition to two nodes, which would run them
    /// concurrently. Held here by the test rather than by a second thread, so the assertion is not
    /// a race.
    /// </remarks>
    [Fact]
    public async Task APartitionAnotherNodeIsServingIsLeftAlone()
    {
        var fixture = Fixture.Create();
        var ct = TestContext.Current.CancellationToken;

        fixture.Broker.Stage(Topic, partitionKey: "instance-1");

        var held = await DurableLease.AcquireAsync(
            fixture.Leases,
            fixture.Subscriptions.Registrations[0].PartitionLeaseFor("instance-1"),
            "another-node",
            fixture.LeasePolicy,
            ct);

        held.IsSuccess.ShouldBeTrue();

        try
        {
            var report = await fixture.PassAsync();

            report.Contended.ShouldBe(1);
            report.Started.ShouldBe(0);
            fixture.Journal.Instances.ShouldBeEmpty();
            fixture.Broker.Pending.Count.ShouldBe(1, "nothing was acknowledged, so nothing is lost");
        }
        finally
        {
            await held.Value.DisposeAsync();
        }
    }

    /// <summary>
    /// A message the broker has delivered too many times is dead-lettered rather than retried.
    /// </summary>
    /// <remarks>
    /// The flow here can never commit, because something else holds the lease on the instance
    /// this delivery names — a node that took it and hung is exactly the shape of it. Every
    /// delivery is refused with <c>lease.held</c>, journalled nowhere and therefore requeued.
    /// Without a bound this message would be offered for ever and every later event of its key
    /// would queue behind it, which is the head-of-line blocking ADR-0038 exists to stop.
    /// </remarks>
    [Fact]
    public async Task AMessageDeliveredTooManyTimesIsDeadLettered()
    {
        var fixture = Fixture.Create();
        var ct = TestContext.Current.CancellationToken;
        var eventId = Guid.NewGuid();

        fixture.Options.BusMaxDeliveries = 2;
        fixture.Broker.Stage(Topic, partitionKey: "instance-1", eventId: eventId);

        var stuck = await DurableLease.AcquireAsync(
            fixture.Leases,
            fixture.Subscriptions.Registrations[0].InstanceIdFor(eventId),
            "a-node-that-hung",
            fixture.LeasePolicy,
            ct);

        stuck.IsSuccess.ShouldBeTrue();

        try
        {
            var passes = new List<BusScanReport>();

            for (var pass = 0; pass < 3; pass++)
            {
                passes.Add(await fixture.PassAsync());
            }

            passes[0].Requeued.ShouldBe(1);
            passes[1].Requeued.ShouldBe(1);
            passes[2].DeadLettered.ShouldBe(1, "the third delivery is past a limit of two");

            fixture.Broker.Pending.ShouldBeEmpty("a poison message must not block its partition");
            fixture.Broker.DeadLettered.Count.ShouldBe(1);
            fixture.Broker.DeadLettered[0].Reason.ShouldContain("BusMaxDeliveries");
        }
        finally
        {
            await stuck.Value.DisposeAsync();
        }
    }

    /// <summary>An ephemeral flow cannot carry a subscription, and the refusal is at registration.</summary>
    [Fact]
    public void AnEphemeralFlowCannotCarryASubscription()
    {
        var plan = ExecutionPlan.Create(
            FlowDescriptor.Create("pricing.reprice", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromMinutes(5)),
            StepGraph.Create([StepNode.ForCapability(0, Reprice)]));

        Should.Throw<ArgumentException>(() => new FlowBusCatalog().Add(
            new BusSubscription("pricing.reprice", "1.0.0", Topic, Group),
            plan,
            new RecordingDispatcher()));
    }

    /// <summary>One node, one journal, one broker and one subscription registered on it.</summary>
    private sealed class Fixture
    {
        private FlowBusScan? _resident;

        private Fixture(bool failing, TenantIsolation isolation)
        {
            Options = new FlowXOptions
            {
                ApplicationName = "Tests",
                NodeName = "node",
                ShutdownDrainTimeout = TimeSpan.FromSeconds(5),
                TenantIsolation = isolation,
            };

            Durability = new FlowDurability(Journal, Leases);
            Host = new FlowHost(new FlowEngine(SystemClock.Instance), Options, Durability);
            Dispatcher = new RecordingDispatcher(failing);
        }

        public InMemoryFlowJournal Journal { get; } = new();

        public InMemoryLeaseStore Leases { get; } = new();

        public RecordingBusConsumer Broker { get; } = new();

        public FlowDurability Durability { get; }

        public FlowHost Host { get; }

        public FlowXOptions Options { get; }

        /// <summary>
        /// The policy the host itself would use, restated because <c>FlowDurability.PolicyFor</c>
        /// is internal.
        /// </summary>
        /// <remarks>
        /// A test that took a lease under a different TTL would be taking a different lease from
        /// the one the scan competes for, and would pass while proving nothing about contention.
        /// </remarks>
        public LeasePolicy LeasePolicy => new()
        {
            Ttl = Options.LeaseTtl,
            RenewalInterval = Options.LeaseRenewalInterval,
        };

        public FlowBusCatalog Subscriptions { get; } = new();

        public RecordingDispatcher Dispatcher { get; }

        public static Fixture Create(
            bool failing = false, TenantIsolation isolation = TenantIsolation.None)
        {
            var fixture = new Fixture(failing, isolation);

            fixture.Subscriptions.Add(
                new BusSubscription("pricing.reprice", "1.0.0", Topic, Group),
                ExecutionPlan.Create(
                    FlowDescriptor.Create(
                        "pricing.reprice", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromMinutes(5)),
                    StepGraph.Create([StepNode.ForCapability(0, Reprice)])),
                fixture.Dispatcher);

            fixture._resident = fixture.Scan();

            return fixture;
        }

        public FlowBusScan Scan() => new(Host, Subscriptions, Broker, Durability, Options);

        public ValueTask<BusScanReport> PassAsync() =>
            _resident!.RunOnceAsync(TestContext.Current.CancellationToken);

        public IReadOnlyList<FlowBusScan> Nodes(int count) =>
        [
            .. Enumerable.Range(0, count).Select(node =>
            {
                var options = new FlowXOptions
                {
                    ApplicationName = Options.ApplicationName,
                    NodeName = "node-" + node.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };

                return new FlowBusScan(
                    new FlowHost(new FlowEngine(SystemClock.Instance), options, Durability),
                    Subscriptions,
                    Broker,
                    Durability,
                    options);
            }),
        ];
    }

    /// <summary>
    /// A dispatcher that remembers what it was started with and, on request, fails as a value.
    /// </summary>
    /// <remarks>
    /// The failing mode is not decoration. ADR-0007 makes a business failure a <c>Result</c>
    /// rather than an exception, so a consumer that acknowledged only on <c>IsSuccess</c> would
    /// pass every test in this file except the one that uses it — and would redeliver every
    /// declined order for ever in production.
    /// </remarks>
    private sealed class RecordingDispatcher(bool failing = false) : IStepDispatcher
    {
        private static readonly Error Declined =
            new("pricing.declined", "The order was not repriced.", ErrorCategory.Validation);

        private readonly Lock _gate = new();
        private readonly List<object?> _inputs = [];

        public IReadOnlyList<object?> Inputs
        {
            get
            {
                lock (_gate)
                {
                    return [.. _inputs];
                }
            }
        }

        public JournalPayload DescribeInput(object? input)
        {
            lock (_gate)
            {
                _inputs.Add(input);
            }

            return JournalPayload.Empty;
        }

        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct) =>
            ValueTask.FromResult(failing ? StepOutcome.Failed(Declined) : StepOutcome.Success);

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
