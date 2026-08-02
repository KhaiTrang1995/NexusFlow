using FlowX.Conformance.InMemory;
using FlowX.Hosting;
using FlowX.Runtime;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace FlowX.Redis.Tests;

/// <summary>
/// The bus trigger end to end, against a running Redis: the real publisher writes, the real
/// consumer reads, and <see cref="FlowBusScan"/> starts the flows.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing in the path is a double except the journal and the lease store</strong>, and
/// those are the conformance suite's reference implementations rather than something written
/// here. The point of the file is the two silent failures: a consumer that never fires and a
/// consumer that starts two flows for one message. <c>BusScanTests</c> asserts both against a
/// recording broker; this asserts that a real Redis behaves the way that double claims a broker
/// does — which is the only way to find out that <c>XREADGROUP</c> does not redeliver an
/// unacknowledged entry to the same consumer, or that <c>XAUTOCLAIM</c> is what does.
/// </para>
/// <para>
/// <strong>It skips when no Redis is configured and fails when one was promised and did not
/// answer.</strong> That decision is <see cref="RedisTestServer"/>'s and is inherited rather than
/// re-implemented, for the reason <c>RedisAvailabilityTests</c> states: a skip in the second case
/// reports an integration that never ran as a green job.
/// </para>
/// </remarks>
public sealed class RedisStreamBusConsumerTests : IAsyncLifetime
{
    private const string Topic = "order.placed";

    private const string Group = "pricing";

    private static readonly CapabilityDescriptor Reprice =
        CapabilityDescriptor.Create("pricing.reprice", "1.0.0", isIdempotent: true);

    private readonly List<IConnectionMultiplexer> _connections = [];
    private readonly List<RedisStreamOptions> _spaces = [];

    /// <summary>A message published by the real publisher starts the flow, exactly once.</summary>
    /// <remarks>
    /// The whole chain in one test: <c>XADD</c> through <see cref="RedisStreamEventPublisher"/>,
    /// <c>XREADGROUP</c> through <see cref="RedisStreamBusConsumer"/>, a derived instance id, a
    /// journalled row.
    /// </remarks>
    [Fact]
    public async Task AnEventPublishedToRedisStartsTheSubscribingFlow()
    {
        var fixture = await CreateAsync();

        await fixture.PublishAsync(Guid.NewGuid(), "instance-1");

        var report = await fixture.PassAsync();

        report.Received.ShouldBe(1);
        report.Started.ShouldBe(1);

        fixture.Journal.Instances.Count.ShouldBe(1);
        fixture.Journal.Instances[0].FlowId.ShouldBe("pricing.reprice");

        fixture.Dispatcher.Inputs[0].ShouldBeOfType<BusMessage>().PartitionKey.ShouldBe("instance-1");
    }

    /// <summary>
    /// The same event published twice — which is what the outbox's at-least-once produces —
    /// starts one flow.
    /// </summary>
    /// <remarks>
    /// <strong>The test the design exists for, against a real broker.</strong> Two distinct
    /// stream entries carrying one <c>event-id</c>, which is exactly what a crash between the
    /// broker acknowledging and the outbox committing leaves behind
    /// (ADR-0018 decision 4). Both are delivered; one instance is journalled; both are
    /// acknowledged, so neither comes back.
    /// </remarks>
    [Fact]
    public async Task AnEventPublishedTwiceStartsOneFlow()
    {
        var fixture = await CreateAsync();
        var eventId = Guid.NewGuid();

        await fixture.PublishAsync(eventId, "instance-1");
        await fixture.PublishAsync(eventId, "instance-1");

        var report = await fixture.PassAsync();

        report.Received.ShouldBe(2, "the broker delivered both entries, which is at-least-once");
        report.Started.ShouldBe(1);
        report.Deduplicated.ShouldBe(1);

        fixture.Journal.Instances.Count.ShouldBe(
            1, "one event names one instance, however many entries carry it");

        (await fixture.PendingAsync("instance-1")).ShouldBe(
            0, "both were acknowledged, so neither is redelivered");
    }

    /// <summary>Three events under one key start their flows in publication order.</summary>
    /// <remarks>
    /// ADR-0018 decision 3 offers per-<c>partition_key</c> order on publication and ADR-0037 is
    /// the promise consumption keeps it. A Redis stream is totally ordered and the stream is the
    /// partition, so this is the assertion that the two halves meet.
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

    /// <summary>An entry that is not a message is dead-lettered, and the partition advances.</summary>
    /// <remarks>
    /// <strong>The head-of-line block, and the proof it is bounded.</strong> The malformed entry
    /// is written directly with <c>XADD</c> — the publisher could not produce one — and the event
    /// behind it is a legitimate message. Without ADR-0038's divert the second would never run.
    /// </remarks>
    [Fact]
    public async Task AMalformedEntryIsDeadLetteredAndTheKeyAdvances()
    {
        var fixture = await CreateAsync();

        await fixture.PublishRawAsync("instance-1", ("type", Topic), ("event-id", "not-a-guid"));
        await fixture.PublishAsync(Guid.NewGuid(), "instance-1", "behind the poison");

        var report = await fixture.PassAsync();

        report.DeadLettered.ShouldBe(1);
        report.Started.ShouldBe(1, "the event behind the poison message ran");

        var dead = await fixture.DeadLetteredAsync("instance-1");

        dead.Count.ShouldBe(1);
        dead[0].ShouldContain("not a GUID");

        (await fixture.PendingAsync("instance-1")).ShouldBe(0);
    }

    /// <summary>A subscription is not offered another topic's events.</summary>
    /// <remarks>
    /// A Redis stream holds every type of event one partition key produced, because the key is
    /// the emitting instance and a flow may emit several. So the topic is a filter the consumer
    /// applies, and getting it wrong would start the wrong flow with the wrong body — which no
    /// amount of downstream validation would present as anything but a corrupt message.
    /// </remarks>
    [Fact]
    public async Task AnEventOfAnotherTypeIsNotOffered()
    {
        var fixture = await CreateAsync();

        await fixture.PublishAsync(Guid.NewGuid(), "instance-1", type: "order.cancelled");

        var report = await fixture.PassAsync();

        report.Received.ShouldBe(0);
        fixture.Journal.Instances.ShouldBeEmpty();

        (await fixture.PendingAsync("instance-1")).ShouldBe(
            0, "it was acknowledged unread, so it does not block this key for ever");
    }

    /// <summary>Two subscriptions on one stream each get their own topic's events.</summary>
    [Fact]
    public async Task TwoSubscriptionsOnOneStreamDoNotStealFromEachOther()
    {
        var fixture = await CreateAsync();
        var ct = TestContext.Current.CancellationToken;

        var other = new BusSubscription("pricing.reprice", "1.0.0", "order.cancelled", Group);

        await fixture.PublishAsync(Guid.NewGuid(), "instance-1");
        await fixture.PublishAsync(Guid.NewGuid(), "instance-1", type: "order.cancelled");

        (await fixture.Consumer.SubscribeAsync(other, ct)).IsSuccess.ShouldBeTrue();

        var mine = await fixture.PassAsync();
        var theirs = await fixture.Consumer.ReceiveAsync(other, 8, 16, ct);

        mine.Started.ShouldBe(1);

        theirs.Value
            .SelectMany(static batch => batch.Deliveries)
            .Select(static delivery => delivery.Message!.Type)
            .ShouldBe(["order.cancelled"]);
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        for (var i = 0; i < _connections.Count; i++)
        {
            await FlushAsync(_connections[i], _spaces[i]);

            _connections[i].Dispose();
        }

        _connections.Clear();
        _spaces.Clear();
    }

    private async ValueTask<Fixture> CreateAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await RedisTestServer.ConnectAsync(ct);
        var options = new RedisStreamOptions { KeyPrefix = "flowx_b_" + Guid.NewGuid().ToString("n") };

        _connections.Add(connection);
        _spaces.Add(options);

        return new Fixture(connection, options);
    }

    private static async ValueTask FlushAsync(IConnectionMultiplexer connection, RedisStreamOptions options)
    {
        var database = connection.GetDatabase(options.Database);

        foreach (var endpoint in connection.GetEndPoints())
        {
            var server = connection.GetServer(endpoint);

            if (server.IsReplica)
            {
                continue;
            }

            await foreach (var key in server.KeysAsync(database.Database, options.KeyPrefix + "*"))
            {
                await database.KeyDeleteAsync(key);
            }
        }
    }

    /// <summary>One node, one journal, one real Redis and one subscription registered on it.</summary>
    private sealed class Fixture
    {
        private readonly IDatabase _database;
        private readonly RedisStreamOptions _options;
        private readonly RedisStreamEventPublisher _publisher;
        private readonly FlowBusScan _scan;

        public Fixture(IConnectionMultiplexer connection, RedisStreamOptions options)
        {
            _options = options;
            _database = connection.GetDatabase(options.Database);
            _publisher = new RedisStreamEventPublisher(connection, options);

            Consumer = new RedisStreamBusConsumer(connection, options, "test-node");
            Subscription = new BusSubscription("pricing.reprice", "1.0.0", Topic, Group);

            var hostOptions = new FlowXOptions { ApplicationName = "Tests", NodeName = "node" };
            var durability = new FlowDurability(Journal, Leases);

            var subscriptions = new FlowBusCatalog().Add(
                Subscription,
                ExecutionPlan.Create(
                    FlowDescriptor.Create(
                        "pricing.reprice", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromMinutes(5)),
                    StepGraph.Create([StepNode.ForCapability(0, Reprice)])),
                Dispatcher);

            _scan = new FlowBusScan(
                new FlowHost(new FlowEngine(SystemClock.Instance), hostOptions, durability),
                subscriptions,
                Consumer,
                durability,
                hostOptions);
        }

        public InMemoryFlowJournal Journal { get; } = new();

        public InMemoryLeaseStore Leases { get; } = new();

        public RecordingDispatcher Dispatcher { get; } = new();

        public RedisStreamBusConsumer Consumer { get; }

        public BusSubscription Subscription { get; }

        /// <summary>Stages one event through the real publisher.</summary>
        /// <remarks>
        /// Through <see cref="RedisStreamEventPublisher"/> rather than a hand-written
        /// <c>XADD</c>, because the entry layout is the contract between the two halves and a
        /// test that wrote its own would be asserting that this file and the consumer agree
        /// rather than that the publisher and the consumer do.
        /// </remarks>
        public async ValueTask PublishAsync(
            Guid eventId, string? partitionKey, string? payload = null, string type = Topic)
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
                    },
                ],
                TestContext.Current.CancellationToken);

            published.IsSuccess.ShouldBeTrue();
        }

        /// <summary>Writes an entry the publisher could not produce.</summary>
        public async ValueTask PublishRawAsync(string? partitionKey, params (string Name, string Value)[] fields) =>
            await _database.StreamAddAsync(
                RedisKeys.EventStream(_options.KeyPrefix, partitionKey),
                [.. fields.Select(static field => new NameValueEntry(field.Name, field.Value))]);

        public ValueTask<BusScanReport> PassAsync() =>
            _scan.RunOnceAsync(TestContext.Current.CancellationToken);

        /// <summary>How many entries this subscription's group still holds unacknowledged.</summary>
        public async ValueTask<long> PendingAsync(string? partitionKey)
        {
            var pending = await _database.StreamPendingAsync(
                RedisKeys.EventStream(_options.KeyPrefix, partitionKey),
                RedisStreamBusConsumer.GroupNameFor(Subscription));

            return pending.PendingMessageCount;
        }

        /// <summary>The reasons in one key's dead-letter stream.</summary>
        public async ValueTask<IReadOnlyList<string>> DeadLetteredAsync(string? partitionKey)
        {
            var key = RedisKeys.EventStream(_options.KeyPrefix, partitionKey) +
                      RedisStreamBusConsumer.DeadLetterSuffix;

            var entries = await _database.StreamRangeAsync(key);

            return
            [
                .. entries.Select(static entry => (string?)entry.Values
                    .First(static value => value.Name == RedisStreamBusConsumer.DeadLetterReasonField)
                    .Value ?? string.Empty),
            ];
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
