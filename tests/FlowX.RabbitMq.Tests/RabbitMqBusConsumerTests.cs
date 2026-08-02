using FlowX.Conformance.InMemory;
using FlowX.Hosting;
using FlowX.Runtime;
using RabbitMQ.Client;
using Shouldly;
using Xunit;

namespace FlowX.RabbitMq.Tests;

/// <summary>
/// The bus trigger end to end, against a running RabbitMQ: the real publisher writes, the real
/// consumer reads, and <see cref="FlowBusScan"/> starts the flows.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing in the path is a double except the journal and the lease store</strong>, and
/// those are the conformance suite's reference implementations rather than something written
/// here. The point of the file is the two silent failures: a consumer that never fires and a
/// consumer that starts two flows for one message. <c>BusScanTests</c> asserts both against a
/// recording broker; this asserts that a real RabbitMQ behaves the way that double claims a
/// broker does — which is the only way to find out that a quorum queue puts a requeued message
/// at the back, or that a second <c>basic.ack</c> closes the channel.
/// </para>
/// <para>
/// <strong>It skips when no broker is configured and fails when one was promised and did not
/// answer.</strong> That decision is <see cref="RabbitMqTestBroker"/>'s and is inherited rather
/// than re-implemented, for the reason <see cref="RabbitMqAvailabilityTests"/> states.
/// </para>
/// </remarks>
public sealed class RabbitMqBusConsumerTests : IAsyncLifetime
{
    private const string Topic = "order.placed";

    private const string Group = "pricing";

    private static readonly CapabilityDescriptor Reprice =
        CapabilityDescriptor.Create("pricing.reprice", "1.0.0", isIdempotent: true);

    private readonly List<Fixture> _fixtures = [];

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A message published by the real publisher starts the flow, exactly once.</summary>
    /// <remarks>
    /// The whole chain in one test: a confirmed publish into a topic exchange, a
    /// <c>basic.get</c> off the queue the broker routed it into, a derived instance id, a
    /// journalled row.
    /// </remarks>
    [Fact]
    public async Task AnEventPublishedToRabbitMqStartsTheSubscribingFlow()
    {
        var fixture = await CreateAsync();

        await fixture.PublishAsync(Guid.NewGuid(), "instance-1");

        var report = await fixture.PassAsync();

        report.Received.ShouldBe(1);
        report.Started.ShouldBe(1);

        fixture.Journal.Instances.Count.ShouldBe(1);
        fixture.Journal.Instances[0].FlowId.ShouldBe("pricing.reprice");

        fixture.Dispatcher.Inputs[0].ShouldBeOfType<BusMessage>().PartitionKey.ShouldBe("instance-1");

        (await fixture.PendingAsync()).ShouldBe(0, "it was acknowledged, so it is gone from the queue");
    }

    /// <summary>The tenant a publisher wrote onto a message is the tenant the consumer reads back.</summary>
    /// <remarks>
    /// <strong>The wire is the contract, and this is the only test that can prove it.</strong>
    /// <see cref="FlowBusScan"/> starts the flow in <c>BusMessage.TenantId</c>, so a publisher
    /// that wrote the header under one name and a consumer that read another would leave every
    /// tenanted deployment refusing every message with <c>tenant.required</c> — and every
    /// in-memory double would still pass. Both halves are the real plugin here.
    /// </remarks>
    [Fact]
    public async Task ATenantWrittenByThePublisherIsReadBackByTheConsumer()
    {
        var fixture = await CreateAsync();

        await fixture.PublishAsync(Guid.NewGuid(), "instance-1", tenantId: "tenant-a");

        (await fixture.ReceiveAsync())
            .SelectMany(static batch => batch.Deliveries)
            .ShouldHaveSingleItem()
            .Message!.TenantId
            .ShouldBe("tenant-a");
    }

    /// <summary>A message published without a tenant carries none, rather than an empty one.</summary>
    /// <remarks>
    /// The distinction admission turns on: null is "the producing deployment did not isolate" and
    /// is refused where this one does, while an empty string would be a tenant no row can carry
    /// and would fail somewhere further in. A header written as <c>""</c> rather than omitted is
    /// what would produce the second.
    /// </remarks>
    [Fact]
    public async Task AMessagePublishedWithNoTenantCarriesNone()
    {
        var fixture = await CreateAsync();

        await fixture.PublishAsync(Guid.NewGuid(), "instance-1");

        (await fixture.ReceiveAsync())
            .SelectMany(static batch => batch.Deliveries)
            .ShouldHaveSingleItem()
            .Message!.TenantId
            .ShouldBeNull();
    }

    /// <summary>An event with no body reads back as no body, not as the empty string.</summary>
    /// <remarks>
    /// AMQP has no null body — a message with nothing in it has a zero-length one — so the
    /// distinction is carried by the content type instead. A consumer that read the body length
    /// would hand a flow <c>""</c> where its contract says there is nothing to deserialise.
    /// </remarks>
    [Fact]
    public async Task AnEventWithNoBodyReadsBackWithNone()
    {
        var fixture = await CreateAsync();

        await fixture.PublishAsync(Guid.NewGuid(), "instance-1", payload: null);

        (await fixture.ReceiveAsync())
            .SelectMany(static batch => batch.Deliveries)
            .ShouldHaveSingleItem()
            .Message!.Payload
            .ShouldBeNull();
    }

    /// <summary>
    /// The same event published twice — which is what the outbox's at-least-once produces —
    /// starts one flow.
    /// </summary>
    /// <remarks>
    /// <strong>The test the design exists for, against a real broker.</strong> Two distinct
    /// messages carrying one <c>message-id</c>, which is exactly what a crash between the broker
    /// confirming and the outbox committing leaves behind (ADR-0018 decision 4). Both are
    /// delivered; one instance is journalled; both are acknowledged, so neither comes back.
    /// </remarks>
    [Fact]
    public async Task AnEventPublishedTwiceStartsOneFlow()
    {
        var fixture = await CreateAsync();
        var eventId = Guid.NewGuid();

        await fixture.PublishAsync(eventId, "instance-1");
        await fixture.PublishAsync(eventId, "instance-1");

        var report = await fixture.PassAsync();

        report.Received.ShouldBe(2, "the broker delivered both, which is at-least-once");
        report.Started.ShouldBe(1);
        report.Deduplicated.ShouldBe(1);

        fixture.Journal.Instances.Count.ShouldBe(
            1, "one event names one instance, however many messages carry it");

        (await fixture.PendingAsync()).ShouldBe(0, "both were acknowledged, so neither comes back");
    }

    /// <summary>Three events under one key start their flows in publication order.</summary>
    /// <remarks>
    /// ADR-0018 decision 3 offers per-<c>partition_key</c> order on publication and ADR-0037 is
    /// the promise consumption keeps it. A queue preserves the order the exchange filled it in
    /// and the publisher fills the exchange serially, so this is the assertion that the two
    /// halves meet — the same sentence as the Redis suite's, reached by a different mechanism.
    /// </remarks>
    [Fact]
    public async Task EventsUnderOneKeyStartTheirFlowsInPublicationOrder()
    {
        var fixture = await CreateAsync();

        await fixture.PublishAsync(Guid.NewGuid(), "instance-1", "first");
        await fixture.PublishAsync(Guid.NewGuid(), "instance-1", "second");
        await fixture.PublishAsync(Guid.NewGuid(), "instance-1", "third");

        await fixture.PassAsync();

        fixture.Dispatcher.Inputs
            .Cast<BusMessage>()
            .Select(static message => message.Payload)
            .ShouldBe(["first", "second", "third"]);
    }

    /// <summary>A queue carrying two keys is offered back as two partitions, each in order.</summary>
    /// <remarks>
    /// <strong>This is the whole of <c>ADR-0072</c>'s decision, asserted.</strong> A Redis stream
    /// <em>is</em> a partition; a queue is not, so the consumer makes the partitions out of a
    /// header. If it did not, <see cref="FlowBusScan"/> would take one lease over a batch spanning
    /// two keys and serialise work that ADR-0037 says may run concurrently — or, worse, run two
    /// events of one key at once under two partition leases that were never contended.
    /// </remarks>
    [Fact]
    public async Task OneQueueIsOfferedBackAsOnePartitionPerKey()
    {
        var fixture = await CreateAsync();

        await fixture.PublishAsync(Guid.NewGuid(), "customer-1", "a1");
        await fixture.PublishAsync(Guid.NewGuid(), "customer-2", "b1");
        await fixture.PublishAsync(Guid.NewGuid(), "customer-1", "a2");

        var batches = await fixture.ReceiveAsync();

        batches.Count.ShouldBe(2, "two keys, two partitions");

        batches.Single(static batch => batch.PartitionKey == "customer-1")
            .Deliveries.Select(static delivery => delivery.Message!.Payload)
            .ShouldBe(["a1", "a2"]);

        batches.Single(static batch => batch.PartitionKey == "customer-2")
            .Deliveries.Select(static delivery => delivery.Message!.Payload)
            .ShouldBe(["b1"]);
    }

    /// <summary>
    /// A delivery whose flow reached no recorded outcome is offered again, ahead of a newer event
    /// of the same key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the load-bearing test of this package and the one worth breaking to check
    /// it works.</strong> Something else holds the lease on the instance the first delivery names
    /// — a node that took it and hung is the shape of it — so the flow is refused with
    /// <c>lease.held</c>, journalled nowhere, and <see cref="FlowBusScan"/> leaves it with the
    /// broker. Two things must then be true on the next pass, and they are two different claims:
    /// the message must come back <em>at all</em>, and it must come back <em>before</em> the event
    /// staged behind it.
    /// </para>
    /// <para>
    /// The second is where RabbitMQ differs from Redis and why this consumer holds rather than
    /// requeues. A <c>basic.nack</c> with requeue on a quorum queue puts the message at the back
    /// of the queue — measured on 3.12, not assumed — so a consumer that handed it back would let
    /// <c>second</c> run before <c>first</c>. Held on the channel, it stays the oldest thing this
    /// subscription has.
    /// </para>
    /// <para>
    /// <strong>The third claim is the one a consumer that acknowledged on receipt would
    /// break.</strong> Holding a delivery in this object's own memory is not the guarantee — the
    /// guarantee is that the broker is still holding it too, so a node that dies loses nothing.
    /// The last phase kills the channel and asserts both events come back off the queue, which is
    /// false the moment <c>basic.get</c> is asked with <c>autoAck</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ADeliveryThatReachedNoOutcomeIsOfferedAgainAheadOfItsKeysNextEvent()
    {
        var fixture = await CreateAsync();
        var eventId = Guid.NewGuid();

        await fixture.PublishAsync(eventId, "instance-1", "first");

        var stuck = await DurableLease.AcquireAsync(
            fixture.Leases,
            fixture.Registration.InstanceIdFor(eventId),
            "a-node-that-hung",
            fixture.LeasePolicy,
            Cancellation);

        stuck.IsSuccess.ShouldBeTrue();

        try
        {
            var refused = await fixture.PassAsync();

            refused.Received.ShouldBe(1);
            refused.Requeued.ShouldBe(1, "nothing was journalled, so the broker still owns it");
            refused.Started.ShouldBe(0);

            (await fixture.PendingAsync()).ShouldBe(
                0,
                "and it is not back on the queue either: it is held un-acknowledged on this " +
                "consumer's channel, which is what stops the broker offering it to another node " +
                "and what stops a quorum queue putting it behind the event published next.");

            await fixture.PublishAsync(Guid.NewGuid(), "instance-1", "second");
        }
        finally
        {
            await stuck.Value.DisposeAsync();
        }

        var batches = await fixture.ReceiveAsync();

        var partition = batches.ShouldHaveSingleItem();

        partition.Deliveries.Select(static delivery => delivery.Message!.Payload).ShouldBe(
            ["first", "second"],
            "the delivery nobody disposed of comes back, and it comes back in front. A consumer " +
            "that acknowledged on receipt would have lost 'first' entirely; one that nacked it " +
            "back onto a quorum queue would offer it after 'second'.");

        partition.Deliveries[0].DeliveryCount.ShouldBe(
            2, "'first' has now been handed out twice, which is what ADR-0038's bound counts.");

        partition.Deliveries[1].DeliveryCount.ShouldBe(1, "and 'second' once.");

        // A node death: the channel goes, and with it every delivery tag it issued. Nothing was
        // acknowledged, so the broker still owns both events and hands them to whoever asks next.
        await fixture.ReopenAsync();

        (await fixture.ReceiveAsync())
            .SelectMany(static batch => batch.Deliveries)
            .Select(static delivery => delivery.Message!.Payload)
            .ShouldBe(
                ["first", "second"],
                "both are still the broker's. Holding a delivery in this consumer's memory is " +
                "not the durability guarantee — leaving it un-acknowledged at the broker is, and " +
                "a consumer that acknowledged on receipt would have lost both here while passing " +
                "every assertion above.");
    }

    /// <summary>
    /// A message the consumer has offered too many times is dead-lettered with a sentence.
    /// </summary>
    /// <remarks>
    /// ADR-0038's undeliverable condition over a real broker. The delivery count that bounds it
    /// is the quorum queue's <c>x-delivery-count</c> plus this consumer's own re-offers, and this
    /// is the test that says the second half is counted: a held message never goes back to the
    /// broker, so without it the count would sit at one for ever and the queue would never
    /// advance past its first poison message.
    /// </remarks>
    [Fact]
    public async Task AMessageOfferedTooManyTimesIsDeadLetteredWithAReason()
    {
        var fixture = await CreateAsync();
        var eventId = Guid.NewGuid();

        fixture.Options.BusMaxDeliveries = 2;

        await fixture.PublishAsync(eventId, "instance-1");

        var stuck = await DurableLease.AcquireAsync(
            fixture.Leases,
            fixture.Registration.InstanceIdFor(eventId),
            "a-node-that-hung",
            fixture.LeasePolicy,
            Cancellation);

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
        }
        finally
        {
            await stuck.Value.DisposeAsync();
        }

        var dead = await fixture.DeadLetteredAsync();

        dead.Count.ShouldBe(1);
        dead[0].ShouldContain("BusMaxDeliveries");

        (await fixture.PendingAsync()).ShouldBe(0, "a poison message must not block its queue");
    }

    /// <summary>A message that is not an event is dead-lettered, and the queue advances.</summary>
    /// <remarks>
    /// <strong>The head-of-line block, and the proof it is bounded.</strong> The malformed message
    /// is published directly — the publisher could not produce one — and the event behind it is
    /// legitimate. Without ADR-0038's divert the second would never run.
    /// </remarks>
    [Fact]
    public async Task AMalformedMessageIsDeadLetteredAndTheQueueAdvances()
    {
        var fixture = await CreateAsync();

        await fixture.PublishRawAsync("not-a-guid", "instance-1");
        await fixture.PublishAsync(Guid.NewGuid(), "instance-1", "behind the poison");

        var report = await fixture.PassAsync();

        report.DeadLettered.ShouldBe(1);
        report.Started.ShouldBe(1, "the event behind the poison message ran");

        var dead = await fixture.DeadLetteredAsync();

        dead.Count.ShouldBe(1);
        dead[0].ShouldContain("not a GUID");

        (await fixture.PendingAsync()).ShouldBe(0);
    }

    /// <summary>An event of another type never reaches this subscription's queue at all.</summary>
    /// <remarks>
    /// <strong>The difference from Redis, stated as a test.</strong>
    /// <c>RedisStreamBusConsumer</c> reads every type a partition key produced and acknowledges
    /// the ones it did not want, because a stream has no topic. Here the routing key is the event
    /// type and the queue is bound to the subscription's topic, so the broker never puts it in
    /// front of this consumer — no filter, no wasted acknowledgement, and nothing for a filtering
    /// bug to get wrong.
    /// </remarks>
    [Fact]
    public async Task AnEventOfAnotherTypeNeverReachesTheQueue()
    {
        var fixture = await CreateAsync();

        await fixture.PublishAsync(Guid.NewGuid(), "instance-1", type: "order.cancelled");

        var report = await fixture.PassAsync();

        report.Received.ShouldBe(0);
        fixture.Journal.Instances.ShouldBeEmpty();

        (await fixture.PendingAsync()).ShouldBe(0, "it was never routed here");
    }

    /// <summary>One publish reaches two subscriptions, and each acknowledges its own copy.</summary>
    /// <remarks>
    /// <strong>This is what the transport is for</strong> (docs/26-CRM-Sample.md §8.5). A topic
    /// exchange copies a message to every bound queue, so two groups on one event type are two
    /// independent backlogs: the second group's copy is still there after the first has
    /// acknowledged its own, and a redelivery to one is invisible to the other. Redis Streams
    /// reaches the same outcome with one consumer group per subscriber over one stream, which is
    /// the arrangement this replaces rather than the property it adds.
    /// </remarks>
    [Fact]
    public async Task OnePublishReachesTwoSubscriptionsAndNeitherStealsFromTheOther()
    {
        var fixture = await CreateAsync();

        var other = new BusSubscription("pricing.audit", "1.0.0", Topic, "audit");

        (await fixture.Consumer.SubscribeAsync(other, Cancellation)).IsSuccess.ShouldBeTrue();

        await fixture.PublishAsync(Guid.NewGuid(), "instance-1");

        var mine = await fixture.PassAsync();

        mine.Started.ShouldBe(1);

        var theirs = await fixture.Consumer.ReceiveAsync(other, 8, 16, Cancellation);

        theirs.IsSuccess.ShouldBeTrue();

        theirs.Value
            .SelectMany(static batch => batch.Deliveries)
            .ShouldHaveSingleItem()
            .Message!.Type
            .ShouldBe(Topic, "the second group's copy is untouched by the first group's flow.");
    }

    /// <summary>Subscribing says whether it created the queue, and it is idempotent.</summary>
    /// <remarks>
    /// Both booleans are success, and which one came back is the difference between a first
    /// deployment and every pass after it. Called on every pass rather than once at startup,
    /// because a broker restored from an empty state has forgotten the queue — so the second call
    /// has to be safe, and the first has to be able to say it was the first.
    /// </remarks>
    [Fact]
    public async Task SubscribingCreatesTheQueueOnceAndIsSafeToRepeat()
    {
        // Not the declared fixture: the queue's existence is what this test is about, and a
        // harness that had already made it would answer the first question with the second's.
        var fixture = await CreateAsync(declare: false);

        var first = await fixture.Consumer.SubscribeAsync(fixture.Subscription, Cancellation);

        first.IsSuccess.ShouldBeTrue();
        first.Value.ShouldBeTrue("nothing had declared this queue before.");

        var second = await fixture.Consumer.SubscribeAsync(fixture.Subscription, Cancellation);

        second.IsSuccess.ShouldBeTrue();
        second.Value.ShouldBeFalse("and the second pass found it rather than making it.");
    }

    /// <summary>Acknowledging twice is success both times, and the second changes nothing.</summary>
    /// <remarks>
    /// <strong>The requirement <see cref="IBusConsumer"/> states and AMQP refuses.</strong>
    /// ADR-0036 acknowledges after a commit, so a node that died between the two acknowledges the
    /// same token again on the redelivery that follows — and a second <c>basic.ack</c> for one
    /// delivery tag is a channel-level <c>PRECONDITION_FAILED</c> that closes the channel and
    /// voids every other outstanding delivery on it. The second call must therefore be answered
    /// without reaching the broker, and the delivery held beside it must survive.
    /// </remarks>
    [Fact]
    public async Task AcknowledgingTwiceIsSuccessAndDoesNotCloseTheChannel()
    {
        var fixture = await CreateAsync();

        await fixture.PublishAsync(Guid.NewGuid(), "customer-1");
        await fixture.PublishAsync(Guid.NewGuid(), "customer-2");

        var batches = await fixture.ReceiveAsync();
        var first = batches[0].Deliveries[0];

        var once = await fixture.Consumer.AcknowledgeAsync(fixture.Subscription, first, Cancellation);

        once.IsSuccess.ShouldBeTrue();
        once.Value.ShouldBeTrue("the broker was still holding it.");

        var twice = await fixture.Consumer.AcknowledgeAsync(fixture.Subscription, first, Cancellation);

        twice.IsSuccess.ShouldBeTrue(
            $"a second acknowledgement is expected rather than exceptional. It said: " +
            $"{(twice.IsFailure ? twice.Error.ToString() : "nothing")}");

        twice.Value.ShouldBeFalse("and it says the broker was no longer holding it.");

        var still = await fixture.ReceiveAsync();

        still.SelectMany(static batch => batch.Deliveries)
            .Select(static delivery => delivery.Message!.PartitionKey)
            .ShouldBe(
                [batches[1].PartitionKey],
                "the other delivery is still held, so the second acknowledgement did not take " +
                "the channel down with it.");
    }

    /// <summary>
    /// A message the broker itself has delivered past the queue's limit reaches the dead-letter
    /// exchange with no help from this consumer.
    /// </summary>
    /// <remarks>
    /// <strong>The backstop, and the reason <c>RabbitMqOptions.DeliveryLimit</c> defaults well
    /// above <c>FlowXOptions.BusMaxDeliveries</c>.</strong> Every dead letter in the tests above
    /// carries a sentence because <see cref="FlowBusScan"/> asked for it. This one carries
    /// <c>x-first-death-reason: delivery_limit</c> instead, because nothing asked: the consumer is
    /// disposed each round, which requeues what it was holding, and the broker eventually stops
    /// offering it. That is the path a node crashing mid-flow every time would take, and without
    /// it the queue would never drain.
    /// </remarks>
    [Fact]
    public async Task TheBrokerDeadLettersAMessagePastTheQueuesOwnDeliveryLimit()
    {
        var fixture = await CreateAsync(deliveryLimit: 1);

        await fixture.PublishAsync(Guid.NewGuid(), "instance-1");

        // Each round takes the message and then drops the channel without acknowledging, which is
        // a node dying mid-flow. RabbitMQ requeues it and raises its delivery count; past the
        // limit it goes to the dead-letter exchange instead of coming back.
        for (var round = 0; round < 3; round++)
        {
            (await fixture.ReceiveAsync()).SelectMany(static batch => batch.Deliveries).ShouldNotBeNull();

            await fixture.ReopenAsync();
        }

        (await fixture.PendingAsync()).ShouldBe(
            0, "past the queue's own x-delivery-limit the broker stops offering it.");

        (await fixture.DeadLetterCountAsync()).ShouldBe(
            1,
            "and it is on the dead-letter exchange rather than dropped. This is the path a " +
            "message takes when nothing ever calls DeadLetterAsync for it.");
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var fixture in _fixtures)
        {
            await fixture.DisposeAsync();
        }

        _fixtures.Clear();
    }

    private async ValueTask<Fixture> CreateAsync(int? deliveryLimit = null, bool declare = true)
    {
        var connection = await RabbitMqTestBroker.ConnectAsync(Cancellation);
        var fixture = new Fixture(connection, RabbitMqTestBroker.IsolatedTopology(deliveryLimit));

        _fixtures.Add(fixture);

        if (declare)
        {
            await fixture.DeclareAsync();
        }

        return fixture;
    }

    /// <summary>One node, one journal, one real broker and one subscription registered on it.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly RabbitMqConnection _connection;
        private readonly RabbitMqOptions _options;
        private readonly RabbitMqEventPublisher _publisher;
        private readonly FlowBusScan _scan;

        public Fixture(RabbitMqConnection connection, RabbitMqOptions options)
        {
            _connection = connection;
            _options = options;
            _publisher = new RabbitMqEventPublisher(connection, options);

            Consumer = new RabbitMqBusConsumer(connection, options);
            Subscription = new BusSubscription("pricing.reprice", "1.0.0", Topic, Group);

            Options = new FlowXOptions { ApplicationName = "Tests", NodeName = "node" };
            Durability = new FlowDurability(Journal, Leases);

            var catalog = new FlowBusCatalog().Add(
                Subscription,
                ExecutionPlan.Create(
                    FlowDescriptor.Create(
                        "pricing.reprice", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromMinutes(5)),
                    StepGraph.Create([StepNode.ForCapability(0, Reprice)])),
                Dispatcher);

            Registration = catalog.Registrations[0];

            _scan = new FlowBusScan(
                new FlowHost(new FlowEngine(SystemClock.Instance), Options, Durability),
                catalog,
                Consumer,
                Durability,
                Options);
        }

        public InMemoryFlowJournal Journal { get; } = new();

        public InMemoryLeaseStore Leases { get; } = new();

        public RecordingDispatcher Dispatcher { get; } = new();

        public RabbitMqBusConsumer Consumer { get; private set; }

        public BusSubscription Subscription { get; }

        public BusRegistration Registration { get; }

        public FlowXOptions Options { get; }

        public FlowDurability Durability { get; }

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

        private string Queue => _options.QueueFor(Group, Topic);

        private string DeadLetterQueue => _options.DeadLetterQueueFor(Group, Topic);

        /// <summary>Makes the topology exist before anything publishes into it.</summary>
        /// <remarks>
        /// An exchange holds nothing, so a message published before the queue was bound is
        /// confirmed and then dropped. A host does the same thing for the same reason — the bus
        /// service calls <c>SubscribeAsync</c> on every pass, including the first — but here it
        /// has to happen before the test's own publish rather than merely before its first read.
        /// </remarks>
        public async ValueTask DeclareAsync() =>
            (await Consumer.SubscribeAsync(Subscription, Cancellation)).IsSuccess.ShouldBeTrue();

        /// <summary>Stages one event through the real publisher.</summary>
        /// <remarks>
        /// Through <see cref="RabbitMqEventPublisher"/> rather than a hand-written publish,
        /// because the wire layout is the contract between the two halves and a test that wrote
        /// its own would be asserting that this file and the consumer agree rather than that the
        /// publisher and the consumer do.
        /// </remarks>
        public async ValueTask PublishAsync(
            Guid eventId,
            string? partitionKey,
            string? payload = "{}",
            string type = Topic,
            string? tenantId = null)
        {
            var published = await _publisher.PublishAsync(
                [
                    new OutboxRecord
                    {
                        EventId = eventId,
                        InstanceId = Guid.NewGuid(),
                        Type = type,
                        SchemaVersion = "1.0.0",
                        PartitionKey = partitionKey,
                        PayloadJson = payload,
                        TenantId = tenantId,
                    },
                ],
                Cancellation);

            published.IsSuccess.ShouldBeTrue();
        }

        /// <summary>Publishes a message the publisher could not produce.</summary>
        public async ValueTask PublishRawAsync(string messageId, string partitionKey)
        {
            var open = await _connection.OpenAsync(Cancellation);

            await using var channel = await open.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true),
                Cancellation);

            await channel.BasicPublishAsync(
                _options.Exchange,
                Topic,
                mandatory: false,
                new BasicProperties
                {
                    MessageId = messageId,
                    Type = Topic,
                    Persistent = true,
                    Headers = new Dictionary<string, object?>
                    {
                        [RabbitMqHeaders.PartitionKey] = partitionKey,
                    },
                },
                ReadOnlyMemory<byte>.Empty,
                Cancellation);
        }

        public ValueTask<BusScanReport> PassAsync() => _scan.RunOnceAsync(Cancellation);

        public async ValueTask<IReadOnlyList<BusPartitionBatch>> ReceiveAsync()
        {
            var received = await Consumer.ReceiveAsync(Subscription, 8, 16, Cancellation);

            received.IsSuccess.ShouldBeTrue(
                $"the broker is reachable. It said: " +
                $"{(received.IsFailure ? received.Error.ToString() : "nothing")}");

            return received.Value;
        }

        /// <summary>Drops the consumer's channel, as a node dying would.</summary>
        /// <remarks>
        /// Everything held on it is requeued by the broker, which is the only way this suite can
        /// reach the requeue path at all — the consumer never takes it deliberately.
        /// </remarks>
        public async ValueTask ReopenAsync()
        {
            await Consumer.DisposeAsync();

            Consumer = new RabbitMqBusConsumer(_connection, _options);

            (await Consumer.SubscribeAsync(Subscription, Cancellation)).IsSuccess.ShouldBeTrue();
        }

        /// <summary>How many messages this subscription's queue is still holding.</summary>
        public ValueTask<int> PendingAsync() => CountAsync(Queue);

        /// <summary>How many messages reached the dead-letter queue.</summary>
        public ValueTask<int> DeadLetterCountAsync() => CountAsync(DeadLetterQueue);

        /// <summary>The reasons in this subscription's dead-letter queue.</summary>
        public async ValueTask<IReadOnlyList<string>> DeadLetteredAsync()
        {
            var open = await _connection.OpenAsync(Cancellation);

            await using var channel = await open.CreateChannelAsync(cancellationToken: Cancellation);

            var reasons = new List<string>();

            while (await channel.BasicGetAsync(DeadLetterQueue, autoAck: true, Cancellation) is { } got)
            {
                reasons.Add(
                    RabbitMqHeaders.Text(got.BasicProperties, RabbitMqHeaders.DeadLetterReason) ??
                    RabbitMqHeaders.Text(got.BasicProperties, "x-first-death-reason") ??
                    string.Empty);
            }

            return reasons;
        }

        public async ValueTask DisposeAsync()
        {
            await Consumer.DisposeAsync();
            await _publisher.DisposeAsync();
            await _connection.DisposeAsync();

            await RabbitMqTestBroker.DropAsync(
                _options, [Queue, DeadLetterQueue], CancellationToken.None);
        }

        private async ValueTask<int> CountAsync(string queue)
        {
            var open = await _connection.OpenAsync(Cancellation);

            await using var channel = await open.CreateChannelAsync(cancellationToken: Cancellation);

            return (int)await channel.MessageCountAsync(queue, Cancellation);
        }
    }

    /// <summary>A dispatcher that answers success and remembers what it was started with.</summary>
    private sealed class RecordingDispatcher : IStepDispatcher
    {
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
