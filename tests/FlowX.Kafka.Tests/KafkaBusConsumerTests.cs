using System.Text;
using Confluent.Kafka;
using Shouldly;
using Xunit;

namespace FlowX.Kafka.Tests;

/// <summary>
/// The consumer against a real Kafka cluster, and the rule that makes an offset watermark usable
/// as a per-message acknowledgement.
/// </summary>
/// <remarks>
/// <strong>The test this file exists for is <see cref="AnOffsetIsNotCommittedPastAnUnsettledRecord"/>.</strong>
/// Everything else checks a piece of the mapping; that one checks the claim
/// <a href="../../docs/adr/ADR-0075-a-kafka-offset-is-committed-behind-a-contiguous-run-of-settled-records.md">ADR-0075</a>
/// makes — that a record acknowledged out of order does not carry its unfinished predecessors
/// over the commit line, which on this transport is silent loss rather than a visible failure.
/// </remarks>
public sealed class KafkaBusConsumerTests
{
    private const string Group = "pricing";
    private const string Type = "order.placed";

    private static readonly BusSubscription Subscription =
        new("order.pricing", "1.0.0", Type, Group);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void TheTransportNamesItself()
    {
        KafkaBusConsumer.TransportName.ShouldBe("kafka");
    }

    // ----------------------------------------------------------------------------- subscribing

    /// <summary>Joining a group is not creating a topic, and the boolean says so.</summary>
    [Fact]
    public async Task SubscribingJoinsAGroupAndClaimsNoCreditForTheTopic()
    {
        await using var fixture = await CreateAsync();

        var subscribed = await fixture.Consumer.SubscribeAsync(Subscription, Cancellation);

        subscribed.IsSuccess.ShouldBeTrue(Because(subscribed));
        subscribed.Value.ShouldBeFalse("a deployment made the topic; joining a group makes nothing.");
    }

    // ------------------------------------------------------------------------------- receiving

    [Fact]
    public async Task AReceivedRecordCarriesEveryFieldTheOutboxStaged()
    {
        await using var fixture = await CreateAsync();

        var eventId = Guid.NewGuid();

        await fixture.PublishAsync(eventId, "customer-1", """{"total":42}""");

        var delivery = (await fixture.ReceiveUntilAsync()).ShouldHaveSingleItem();

        delivery.IsReadable.ShouldBeTrue(delivery.UnreadableReason);
        delivery.DeliveryCount.ShouldBe(1, "including now, so a first delivery is one.");
        delivery.PartitionKey.ShouldBe("customer-1");

        var message = delivery.Message.ShouldNotBeNull();

        message.EventId.ShouldBe(eventId);
        message.Type.ShouldBe(Type);
        message.SchemaVersion.ShouldBe("1.0.0");
        message.Payload.ShouldBe("""{"total":42}""");
        message.TenantId.ShouldBe("tenant-a");
    }

    [Fact]
    public async Task AnEventWithNoBodyIsAMessageWithNoPayload()
    {
        await using var fixture = await CreateAsync();

        await fixture.PublishAsync(Guid.NewGuid(), "customer-1", payload: null);

        (await fixture.ReceiveUntilAsync()).ShouldHaveSingleItem()
            .Message!.Payload.ShouldBeNull("a null value is not the empty string.");
    }

    /// <summary>
    /// One topic carries every type, and a group reads only the type it subscribed to.
    /// </summary>
    /// <remarks>
    /// <strong>And the ones it does not want must not stop it.</strong> Kafka has no server-side
    /// filter, so leaving another type's record unsettled would hold the watermark behind it for
    /// ever — the second half of this test is the half that matters.
    /// </remarks>
    [Fact]
    public async Task ARecordOfAnotherTypeIsNotDeliveredAndDoesNotBlockTheGroup()
    {
        await using var fixture = await CreateAsync();

        await fixture.PublishAsync(Guid.NewGuid(), "customer-1", "{}", type: "order.shipped");
        await fixture.PublishAsync(Guid.NewGuid(), "customer-1", "{}");

        var delivered = await fixture.ReceiveUntilAsync();

        delivered.ShouldHaveSingleItem("only this group's type is offered.")
            .Message!.Type.ShouldBe(Type);

        // Settling the one it wanted commits past the one it did not: the other type was settled
        // when it was read, so there is no gap below.
        (await fixture.Consumer.AcknowledgeAsync(Subscription, delivered[0], Cancellation))
            .Value.ShouldBeTrue();

        await using var restarted = await fixture.RestartAsync();

        (await restarted.ReceiveUntilAsync(expecting: 0)).ShouldBeEmpty(
            "the watermark moved past both records, so a restart replays neither.");
    }

    [Fact]
    public async Task DeliveriesAreGroupedByTheKeyTheyWerePublishedUnder()
    {
        await using var fixture = await CreateAsync();

        await fixture.PublishAsync(Guid.NewGuid(), "customer-1", "{}");
        await fixture.PublishAsync(Guid.NewGuid(), "customer-2", "{}");
        await fixture.PublishAsync(Guid.NewGuid(), "customer-1", "{}");

        var received = await fixture.ReceiveBatchesUntilAsync(expecting: 3);

        var batches = received.ToDictionary(static batch => batch.PartitionKey!, StringComparer.Ordinal);

        batches["customer-1"].Deliveries.Count.ShouldBe(2, "two events of one key, in order.");
        batches["customer-2"].Deliveries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AnUnkeyedEventIsItsOwnPartition()
    {
        await using var fixture = await CreateAsync();

        await fixture.PublishAsync(Guid.NewGuid(), partitionKey: null, "{}");

        var received = await fixture.ReceiveBatchesUntilAsync(expecting: 1);

        received.ShouldHaveSingleItem().PartitionKey.ShouldBeNull();
    }

    /// <summary>A record no FlowX publisher wrote is still a delivery, so it can be diverted.</summary>
    [Fact]
    public async Task ARecordWithNoEventIdIsAnUnreadableDelivery()
    {
        await using var fixture = await CreateAsync();

        await fixture.SendRawAsync("customer-1", type: Type, eventId: "not-a-guid");

        var delivery = (await fixture.ReceiveUntilAsync()).ShouldHaveSingleItem();

        delivery.IsReadable.ShouldBeFalse();
        delivery.UnreadableReason.ShouldNotBeNull().ShouldContain("flowx-event-id");
        delivery.Message.ShouldBeNull("a caller must have nothing to run.");
    }

    // -------------------------------------------------------------------------------- the rule

    /// <summary>
    /// ADR-0075's claim, and the only test that can falsify it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three records of one key, so all three are in one partition and their offsets are
    /// consecutive. The second and third are acknowledged and the first is not — which is what two
    /// flows finishing before a third looks like.
    /// </para>
    /// <para>
    /// <strong>A restart is the only honest way to observe a commit.</strong> The committed offset
    /// is not readable from the consumer that holds the assignment; what it means is where the
    /// group resumes. So the group is closed and rejoined, and what it is offered says exactly how
    /// far the watermark moved.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnOffsetIsNotCommittedPastAnUnsettledRecord()
    {
        await using var fixture = await CreateAsync();

        await fixture.PublishAsync(Guid.NewGuid(), "customer-1", """{"n":1}""");
        await fixture.PublishAsync(Guid.NewGuid(), "customer-1", """{"n":2}""");
        await fixture.PublishAsync(Guid.NewGuid(), "customer-1", """{"n":3}""");

        var delivered = await fixture.ReceiveUntilAsync(expecting: 3);

        delivered.Count.ShouldBe(3, "one key, one partition, three records in order.");

        // The second and third finish; the first does not.
        (await fixture.Consumer.AcknowledgeAsync(Subscription, delivered[1], Cancellation))
            .Value.ShouldBeTrue();

        (await fixture.Consumer.AcknowledgeAsync(Subscription, delivered[2], Cancellation))
            .Value.ShouldBeTrue();

        await using var restarted = await fixture.RestartAsync();

        var again = await restarted.ReceiveUntilAsync(expecting: 3);

        again.Count.ShouldBe(
            3,
            "the watermark cannot pass the unsettled first record, so all three come back. " +
            "Committing on each acknowledgement would have returned none of them and lost the first.");

        again[0].Message!.Payload.ShouldBe("""{"n":1}""", "and the replay starts at the gap.");

        // Now close the gap. The watermark jumps over all three at once.
        (await restarted.Consumer.AcknowledgeAsync(Subscription, again[0], Cancellation))
            .Value.ShouldBeTrue();

        (await restarted.Consumer.AcknowledgeAsync(Subscription, again[1], Cancellation))
            .Value.ShouldBeTrue();

        (await restarted.Consumer.AcknowledgeAsync(Subscription, again[2], Cancellation))
            .Value.ShouldBeTrue();

        await using var third = await restarted.RestartAsync();

        (await third.ReceiveUntilAsync(expecting: 0)).ShouldBeEmpty(
            "every offset below the watermark is settled, so nothing is replayed.");
    }

    [Fact]
    public async Task AcknowledgingATokenNobodyHoldsIsSuccessAndDoesNothing()
    {
        await using var fixture = await CreateAsync();

        var acknowledged = await fixture.Consumer.AcknowledgeAsync(
            Subscription,
            new BusDelivery(null, "a-token-from-a-previous-life", 1, null, "unreadable"),
            Cancellation);

        acknowledged.IsSuccess.ShouldBeTrue(
            "ADR-0036 acknowledges after a commit, so the same token can arrive twice.");

        acknowledged.Value.ShouldBeFalse();
    }

    // ------------------------------------------------------------------------- dead-lettering

    /// <summary>
    /// A diverted record is copied to the derived topic and then settled, in that order.
    /// </summary>
    [Fact]
    public async Task DeadLetteringCopiesToTheDerivedTopicAndSettles()
    {
        await using var fixture = await CreateAsync();

        await fixture.PublishAsync(Guid.NewGuid(), "customer-1", """{"poison":true}""");

        var delivery = (await fixture.ReceiveUntilAsync()).ShouldHaveSingleItem();

        var diverted = await fixture.Consumer.DeadLetterAsync(
            Subscription, delivery, "the flow refused it three times", Cancellation);

        diverted.IsSuccess.ShouldBeTrue(Because(diverted));
        diverted.Value.ShouldBeTrue();

        var dead = fixture.DeadLettered();

        dead.ShouldHaveSingleItem()
            .Reason.ShouldBe("the flow refused it three times", "the reason travels with the copy.");

        dead[0].Payload.ShouldBe("""{"poison":true}""", "and so does the body.");

        await using var restarted = await fixture.RestartAsync();

        (await restarted.ReceiveUntilAsync(expecting: 0)).ShouldBeEmpty(
            "diverting settles it, so the group does not see it again.");
    }

    // ------------------------------------------------------------------------------- fan-out

    /// <summary>One publish reaches two groups, and each keeps its own offsets.</summary>
    [Fact]
    public async Task OnePublishReachesTwoGroupsAndNeitherStealsFromTheOther()
    {
        await using var fixture = await CreateAsync();

        var other = new BusSubscription("order.audit", "1.0.0", Type, "audit");

        (await fixture.Consumer.SubscribeAsync(other, Cancellation)).IsSuccess.ShouldBeTrue();

        await fixture.PublishAsync(Guid.NewGuid(), "customer-1", "{}");

        (await fixture.ReceiveUntilAsync()).ShouldHaveSingleItem();

        var theirs = await fixture.ReceiveUntilAsync(subscription: other);

        theirs.ShouldHaveSingleItem("a consumer group is a private cursor over the same records.")
            .Message!.Type.ShouldBe(Type);
    }

    // ------------------------------------------------------------------------------- fixtures

    private static string Because<T>(Result<T> result) =>
        result.IsSuccess ? "it succeeded" : result.Error!.Code + ": " + result.Error.Message;

    private static async ValueTask<Fixture> CreateAsync()
    {
        // One partition, so three records of one key have consecutive offsets and the ledger's
        // behaviour is observable rather than inferred.
        var options = await KafkaTestCluster.IsolatedAsync(partitions: 1, Cancellation);

        return new Fixture(options, owns: true);
    }

    private sealed class Fixture(KafkaOptions options, bool owns) : IAsyncDisposable
    {
        private readonly KafkaEventPublisher _publisher = new(options);

        private KafkaBusConsumer _consumer = new(options);

        public KafkaBusConsumer Consumer => _consumer;

        /// <summary>Closes this group and rejoins it, which is what a redeploy does.</summary>
        /// <returns>A fixture over the same topic whose consumer resumes at the committed offset.</returns>
        public async ValueTask<Fixture> RestartAsync()
        {
            await _consumer.DisposeAsync();

            _consumer = null!;

            return new Fixture(options, owns: false);
        }

        public async ValueTask PublishAsync(
            Guid eventId, string? partitionKey, string? payload, string type = Type)
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
                        TenantId = "tenant-a",
                    },
                ],
                Cancellation);

            published.IsSuccess.ShouldBeTrue(Because(published));
        }

        /// <summary>Produces a record this package did not build, for the cases it must survive.</summary>
        public async ValueTask SendRawAsync(string key, string type, string eventId)
        {
            using var producer = new ProducerBuilder<string?, byte[]?>(
                new ProducerConfig { BootstrapServers = options.BootstrapServers })
                .SetLogHandler(static (_, _) => { })
                .Build();

            var headers = new Headers();

            KafkaHeaders.Write(headers, KafkaHeaders.EventId, eventId);
            KafkaHeaders.Write(headers, KafkaHeaders.Type, type);

            await producer.ProduceAsync(
                options.Topic,
                new Message<string?, byte[]?> { Key = key, Value = null, Headers = headers },
                Cancellation);

            producer.Flush(TimeSpan.FromSeconds(5));
        }

        /// <summary>Receives until the expected count arrives, or gives up.</summary>
        /// <remarks>
        /// <para>
        /// A produce is acknowledged before the record is fetchable by a consumer that is already
        /// polling, so a single pass can legitimately return nothing. The bound is what makes a
        /// broker that never delivers a failed test rather than a hung one.
        /// </para>
        /// <para>
        /// <strong>The bound covers a consumer group's first join, not only a delivery, and that
        /// is why it is not thirty seconds.</strong> The first group in a cold cluster waits for
        /// <c>__consumer_offsets</c> to be created — fifty partitions by default — and for a
        /// coordinator to be elected for its own. That took thirty-eight seconds on the run that
        /// found this, so the first test of a freshly started broker failed while every later one
        /// passed: a flake whose message said "the broker delivered nothing" and whose cause was
        /// that nobody had asked it yet.
        /// </para>
        /// </remarks>
        public async ValueTask<IReadOnlyList<BusDelivery>> ReceiveUntilAsync(
            int expecting = 1, BusSubscription? subscription = null)
        {
            var batches = await ReceiveBatchesUntilAsync(expecting, subscription);

            return [.. batches.SelectMany(static batch => batch.Deliveries)];
        }

        public async ValueTask<IReadOnlyList<BusPartitionBatch>> ReceiveBatchesUntilAsync(
            int expecting = 1, BusSubscription? subscription = null)
        {
            var target = subscription ?? Subscription;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
            var collected = new List<BusPartitionBatch>();
            var count = 0;

            while (DateTimeOffset.UtcNow < deadline)
            {
                var received = await _consumer.ReceiveAsync(target, 8, 16, Cancellation);

                received.IsSuccess.ShouldBeTrue(Because(received));

                foreach (var batch in received.Value)
                {
                    collected.Add(batch);
                    count += batch.Deliveries.Count;
                }

                if (count >= expecting)
                {
                    break;
                }

                if (expecting == 0 && received.Value.Count == 0)
                {
                    // Two empty passes rather than one: the first may simply have raced the fetch.
                    var again = await _consumer.ReceiveAsync(target, 8, 16, Cancellation);

                    if (again.Value.Count == 0)
                    {
                        break;
                    }

                    collected.AddRange(again.Value);
                }
            }

            return Merge(collected);
        }

        /// <summary>What the derived dead-letter topic is holding.</summary>
        public List<(string? Reason, string? Payload)> DeadLettered()
        {
            using var consumer = new ConsumerBuilder<string?, byte[]?>(new ConsumerConfig
            {
                BootstrapServers = options.BootstrapServers,
                GroupId = "dead-" + Guid.NewGuid().ToString("N")[..8],
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false,
                EnablePartitionEof = true,
            })
            .SetLogHandler(static (_, _) => { })
            .Build();

            consumer.Subscribe(options.DeadLetterTopic);

            var found = new List<(string? Reason, string? Payload)>();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);

            while (DateTimeOffset.UtcNow < deadline)
            {
                var record = consumer.Consume(TimeSpan.FromMilliseconds(500));

                if (record is null)
                {
                    continue;
                }

                if (record.IsPartitionEOF)
                {
                    break;
                }

                found.Add((
                    KafkaHeaders.Text(record.Message.Headers, KafkaHeaders.DeadLetterReason),
                    record.Message.Value is { } value ? Encoding.UTF8.GetString(value) : null));
            }

            consumer.Close();

            return found;
        }

        public async ValueTask DisposeAsync()
        {
            if (_consumer is not null)
            {
                await _consumer.DisposeAsync();
            }

            await _publisher.DisposeAsync();

            if (owns)
            {
                await KafkaTestCluster.DropAsync(options);
            }
        }

        /// <summary>Joins batches of the same key that arrived on different passes.</summary>
        private static IReadOnlyList<BusPartitionBatch> Merge(List<BusPartitionBatch> batches)
        {
            var order = new List<string?>();
            var merged = new Dictionary<string, List<BusDelivery>>(StringComparer.Ordinal);
            var unkeyed = new List<BusDelivery>();

            foreach (var batch in batches)
            {
                if (batch.PartitionKey is not { } key)
                {
                    if (!order.Contains(null))
                    {
                        order.Add(null);
                    }

                    unkeyed.AddRange(batch.Deliveries);

                    continue;
                }

                if (!merged.TryGetValue(key, out var into))
                {
                    order.Add(key);
                    merged[key] = into = [];
                }

                into.AddRange(batch.Deliveries);
            }

            return [.. order.Select(key => new BusPartitionBatch(key, key is null ? unkeyed : merged[key]))];
        }
    }
}
