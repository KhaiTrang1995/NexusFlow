using System.Text;
using Azure.Messaging.ServiceBus;
using Shouldly;
using Xunit;

namespace FlowX.AzureServiceBus.Tests;

/// <summary>
/// The consumer against a real Service Bus, on a topology a deployment declared.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every subscription these tests read is one <c>emulator-config.json</c> made</strong> —
/// which is the package's central decision under test rather than a fixture detail. A suite that
/// created its own entities would exercise a code path this plugin does not have and would prove
/// the opposite of
/// <a href="../../docs/adr/ADR-0074-service-bus-topology-is-created-by-a-deployment-not-by-a-consumer.md">ADR-0074</a>.
/// </para>
/// <para>
/// The skip discipline is <see cref="ServiceBusTestNamespace"/>'s: no namespace configured is a
/// skip carrying its reason, and a namespace configured but unreachable is a failure.
/// </para>
/// </remarks>
public sealed class ServiceBusConsumerTests
{
    private const string Group = "consumer";
    private const string Type = ServiceBusTestNamespace.OrderPlaced;

    private static readonly BusSubscription Subscription =
        new("order.pricing", "1.0.0", Type, Group);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void TheTransportNamesItself()
    {
        AzureServiceBusConsumer.TransportName.ShouldBe("azure-servicebus");
    }

    // ----------------------------------------------------------------------------- subscribing

    /// <summary>
    /// Subscribing finds what a deployment made, and says it did not make it.
    /// </summary>
    /// <remarks>
    /// <c>false</c> is success, and it is the honest answer: <c>IBusConsumer</c> reads the boolean
    /// as "did this call create the group", and on this transport no call ever does.
    /// </remarks>
    [Fact]
    public async Task SubscribingFindsWhatADeploymentMadeAndClaimsNoCreditForIt()
    {
        await using var fixture = await CreateAsync();

        var subscribed = await fixture.Consumer.SubscribeAsync(Subscription, Cancellation);

        subscribed.IsSuccess.ShouldBeTrue(Because(subscribed));
        subscribed.Value.ShouldBeFalse("this package creates no entities.");
    }

    /// <summary>
    /// A group nobody declared is refused with the thing an operator has to do about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The failure this replaces is the quiet one: a consumer that created the subscription itself
    /// would succeed here and then read an empty backlog for ever, because a subscription created
    /// after the events were published has none of them.
    /// </para>
    /// <para>
    /// <strong>The code asserted is the general one, deliberately.</strong> A namespace answers a
    /// missing entity with <c>MessagingEntityNotFound</c> and the emulator answers the same
    /// condition with a timeout, so a distinct code would be a branch only one of the two could
    /// reach. What both carry, and what this asserts, is the sentence naming the entity.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AGroupNobodyDeclaredIsRefusedAndTheErrorSaysWhatToMake()
    {
        await using var fixture = await CreateAsync();

        var subscribed = await fixture.Consumer.SubscribeAsync(
            new BusSubscription("order.pricing", "1.0.0", Type, "nobody-declared-this"), Cancellation);

        subscribed.IsSuccess.ShouldBeFalse("the entity is not there and nothing here makes one.");
        subscribed.Error!.Code.ShouldBe(AzureServiceBusConsumer.ReceiveFailedCode);

        subscribed.Error.Message.ShouldContain(
            "nobody-declared-this--" + Type,
            customMessage: "an operator has to be told which entity is missing.");

        subscribed.Error.Message.ShouldContain(
            "Subject = 'order.placed'", customMessage: "and what filter it has to carry.");
    }

    // ------------------------------------------------------------------------------- receiving

    [Fact]
    public async Task AReceivedMessageCarriesEveryFieldTheOutboxStaged()
    {
        await using var fixture = await CreateAsync();

        var eventId = Guid.NewGuid();

        await fixture.PublishAsync(eventId, "customer-1", """{"total":42}""");

        var delivery = (await fixture.ReceiveAsync()).ShouldHaveSingleItem();

        delivery.IsReadable.ShouldBeTrue(delivery.UnreadableReason);
        delivery.DeliveryCount.ShouldBe(1, "including now, so a first delivery is one.");
        delivery.PartitionKey.ShouldBe("customer-1");

        var message = delivery.Message.ShouldNotBeNull();

        message.EventId.ShouldBe(eventId);
        message.Type.ShouldBe(Type);
        message.SchemaVersion.ShouldBe("1.0.0");
        message.PartitionKey.ShouldBe("customer-1");
        message.Payload.ShouldBe("""{"total":42}""");
        message.TenantId.ShouldBe("tenant-a");
    }

    [Fact]
    public async Task AnEventWithNoBodyIsAMessageWithNoPayload()
    {
        await using var fixture = await CreateAsync();

        await fixture.PublishAsync(Guid.NewGuid(), "customer-1", payload: null);

        (await fixture.ReceiveAsync()).ShouldHaveSingleItem()
            .Message!.Payload.ShouldBeNull("an absent body is not the empty string.");
    }

    [Fact]
    public async Task DeliveriesAreGroupedByTheKeyTheyWerePublishedUnder()
    {
        await using var fixture = await CreateAsync();

        await fixture.PublishAsync(Guid.NewGuid(), "customer-1", "{}");
        await fixture.PublishAsync(Guid.NewGuid(), "customer-2", "{}");
        await fixture.PublishAsync(Guid.NewGuid(), "customer-1", "{}");

        // THREE PUBLISHES ARE NOT THREE MESSAGES YET, WHICH IS WHAT A SINGLE RECEIVE ASSUMED.
        // The broker makes a message visible on its own clock, so one call can hand back two of
        // the three and be perfectly correct in doing so — this read `customer-1` with one
        // delivery on CI and asserted two. Keep asking until all three have arrived or the bound
        // passes, which turns a broker that never delivers them into a failed test rather than a
        // wrong one. Grouping is still what is asserted: the key on each batch is the consumer's
        // answer, and counting per key across the calls says the same thing about it.
        var batches = await fixture.GroupedUntilAsync(3, TimeSpan.FromSeconds(30));

        batches["customer-1"].Count.ShouldBe(2, "two events of one key, in order.");
        batches["customer-2"].Count.ShouldBe(1);
    }

    [Fact]
    public async Task AnUnkeyedEventIsItsOwnPartition()
    {
        await using var fixture = await CreateAsync();

        await fixture.PublishAsync(Guid.NewGuid(), partitionKey: null, "{}");

        var received = await fixture.Consumer.ReceiveAsync(Subscription, 8, 16, Cancellation);

        received.Value.ShouldHaveSingleItem().PartitionKey.ShouldBeNull();
    }

    /// <summary>
    /// A message no FlowX publisher wrote is still a delivery, so it can be diverted.
    /// </summary>
    /// <remarks>
    /// The alternative — dropping what cannot be parsed — leaves the subscription redelivering it
    /// until its delivery count runs out, with nothing anywhere recording why.
    /// </remarks>
    [Fact]
    public async Task AMessageWhoseIdIsNotAGuidIsAnUnreadableDelivery()
    {
        await using var fixture = await CreateAsync();

        await fixture.SendRawAsync(new ServiceBusMessage
        {
            MessageId = "not-a-guid",
            Subject = Type,
        });

        var delivery = (await fixture.ReceiveAsync()).ShouldHaveSingleItem();

        delivery.IsReadable.ShouldBeFalse();
        delivery.UnreadableReason.ShouldNotBeNull().ShouldContain("not-a-guid");
        delivery.Message.ShouldBeNull("a caller must have nothing to run.");
    }

    // ------------------------------------------------------------------------------- settling

    [Fact]
    public async Task AcknowledgingSettlesTheDeliveryAndASecondCallIsStillSuccess()
    {
        await using var fixture = await CreateAsync();

        await fixture.PublishAsync(Guid.NewGuid(), "customer-1", "{}");

        var delivery = (await fixture.ReceiveAsync()).ShouldHaveSingleItem();

        var once = await fixture.Consumer.AcknowledgeAsync(Subscription, delivery, Cancellation);

        once.IsSuccess.ShouldBeTrue(Because(once));
        once.Value.ShouldBeTrue("the broker was still holding it.");

        var twice = await fixture.Consumer.AcknowledgeAsync(Subscription, delivery, Cancellation);

        twice.IsSuccess.ShouldBeTrue(
            "ADR-0036 acknowledges after a commit, so a node that died between the two " +
            "acknowledges the same token again on the redelivery that follows.");

        twice.Value.ShouldBeFalse("and the second call changed nothing.");

        (await fixture.ReceiveAsync()).ShouldBeEmpty();
    }

    /// <summary>
    /// Work a node never settled comes back, with the broker's own count one higher.
    /// </summary>
    /// <remarks>
    /// <strong>Dropping the consumer is what a node dying does.</strong> The locks lapse, the
    /// subscription takes the messages back, and <c>DeliveryCount</c> — which is what bounds
    /// ADR-0038's dead-letter rule — is the broker's rather than this package's bookkeeping.
    /// </remarks>
    [Fact]
    public async Task WorkNobodySettledComesBackWithAHigherDeliveryCount()
    {
        // Its own subscription, whose lock is the five seconds Service Bus allows as a minimum.
        // Dropping the receiver does not hand the message back — the broker holds the lock for its
        // full duration however the holder went away — so this is the one test that waits, and it
        // waits five seconds rather than the thirty every other subscription is declared with.
        await using var fixture = await CreateAsync(group: "redelivery");

        await fixture.PublishAsync(Guid.NewGuid(), "customer-1", "{}");

        (await fixture.ReceiveAsync()).ShouldHaveSingleItem().DeliveryCount.ShouldBe(1);

        await fixture.ReopenAsync();

        var again = await fixture.ReceiveUntilAsync(TimeSpan.FromSeconds(30));

        again.ShouldHaveSingleItem("the lock lapsed and the subscription took it back.")
            .DeliveryCount.ShouldBe(2, "the broker counted the delivery the dead node spent.");
    }

    /// <summary>
    /// Dead-lettering is one call, because the destination is the broker's own.
    /// </summary>
    /// <remarks>
    /// ADR-0073 held that a dead-letter destination is deployment configuration, and derived one
    /// on the two transports that have none. Service Bus has one per subscription that needs no
    /// address at all — the same conclusion reached from the other side, and the reason this test
    /// reads the sub-queue rather than a configured second entity.
    /// </remarks>
    [Fact]
    public async Task DeadLetteringMovesItToTheSubscriptionsOwnDeadLetterQueue()
    {
        await using var fixture = await CreateAsync();

        await fixture.PublishAsync(Guid.NewGuid(), "customer-1", "{}");

        var delivery = (await fixture.ReceiveAsync()).ShouldHaveSingleItem();

        var diverted = await fixture.Consumer.DeadLetterAsync(
            Subscription, delivery, "the flow refused it three times", Cancellation);

        diverted.IsSuccess.ShouldBeTrue(Because(diverted));
        diverted.Value.ShouldBeTrue();

        (await fixture.ReceiveAsync()).ShouldBeEmpty("it is not on the subscription any more.");

        var dead = await fixture.DeadLetteredAsync();

        dead.ShouldHaveSingleItem().DeadLetterReason
            .ShouldBe("the flow refused it three times", "the reason is where the portal shows it.");
    }

    [Fact]
    public async Task DeadLetteringATokenNobodyHoldsIsSuccessAndDoesNothing()
    {
        await using var fixture = await CreateAsync();

        var diverted = await fixture.Consumer.DeadLetterAsync(
            Subscription,
            new BusDelivery(null, "a-token-from-a-previous-life", 1, null, "unreadable"),
            "whatever",
            Cancellation);

        diverted.IsSuccess.ShouldBeTrue();
        diverted.Value.ShouldBeFalse();
    }

    // ------------------------------------------------------------------------------- fan-out

    /// <summary>
    /// One publish reaches three groups, and each holds a copy of its own.
    /// </summary>
    /// <remarks>
    /// <strong>This is what the transport is for.</strong> A topic copies a message to every
    /// subscription whose filter matches, so three groups on one event type are three independent
    /// backlogs — the same property a RabbitMQ topic exchange has, reached by a correlation filter
    /// on <c>Subject</c> rather than by a routing key.
    /// </remarks>
    [Fact]
    public async Task OnePublishReachesThreeGroupsAndNeitherStealsFromTheOthers()
    {
        await using var fixture = await CreateAsync(drain: false);

        string[] groups = ["scoring", "assignment", "enrichment"];

        foreach (var group in groups)
        {
            await ServiceBusTestNamespace.DrainAsync(
                fixture.Connection, group + "--lead.created", Cancellation);
        }

        await fixture.SendRawAsync(new ServiceBusMessage
        {
            MessageId = Guid.NewGuid().ToString("d"),
            Subject = "lead.created",
        });

        foreach (var group in groups)
        {
            var subscription = new BusSubscription("crm.lead." + group, "1.0.0", "lead.created", group);

            var received = await fixture.Consumer.ReceiveAsync(subscription, 8, 16, Cancellation);

            received.IsSuccess.ShouldBeTrue(Because(received));

            received.Value.SelectMany(static batch => batch.Deliveries)
                .ShouldHaveSingleItem($"'{group}' has a copy of its own.")
                .Message!.Type.ShouldBe("lead.created");
        }
    }

    // ------------------------------------------------------------------------------- fixtures

    private static string Because<T>(Result<T> result) =>
        result.IsSuccess ? "it succeeded" : result.Error!.Code + ": " + result.Error.Message;

    private static async ValueTask<Fixture> CreateAsync(bool drain = true, string group = Group)
    {
        var connection = await ServiceBusTestNamespace.ConnectAsync(Cancellation);
        var options = new AzureServiceBusOptions();

        if (drain)
        {
            await ServiceBusTestNamespace.DrainAsync(
                connection, options.SubscriptionFor(group, Type), Cancellation);
        }

        return new Fixture(connection, options, group);
    }

    private sealed class Fixture(
        AzureServiceBusConnection connection, AzureServiceBusOptions options, string group)
        : IAsyncDisposable
    {
        private readonly BusSubscription _subscription =
            new("order.pricing", "1.0.0", Type, group);

        private readonly AzureServiceBusEventPublisher _publisher = new(connection, options);

        private AzureServiceBusConsumer _consumer = new(connection, options);

        public AzureServiceBusConnection Connection => connection;

        public AzureServiceBusConsumer Consumer => _consumer;

        public async ValueTask PublishAsync(Guid eventId, string? partitionKey, string? payload)
        {
            var published = await _publisher.PublishAsync(
                [
                    new OutboxRecord
                    {
                        EventId = eventId,
                        InstanceId = Guid.NewGuid(),
                        Type = Type,
                        SchemaVersion = "1.0.0",
                        PartitionKey = partitionKey,
                        PayloadJson = payload,
                        TenantId = "tenant-a",
                    },
                ],
                Cancellation);

            published.IsSuccess.ShouldBeTrue(Because(published));
        }

        /// <summary>Sends a message this package did not build, for the cases it must survive.</summary>
        public async ValueTask SendRawAsync(ServiceBusMessage message)
        {
            var sender = connection.Client.CreateSender(options.Topic);

            await using var closing = sender.ConfigureAwait(false);

            await sender.SendMessageAsync(message, Cancellation);
        }

        public async ValueTask<IReadOnlyList<BusDelivery>> ReceiveAsync()
        {
            var received = await _consumer.ReceiveAsync(_subscription, 8, 16, Cancellation);

            received.IsSuccess.ShouldBeTrue(Because(received));

            return [.. received.Value.SelectMany(static batch => batch.Deliveries)];
        }

        /// <summary>Receives until a number of deliveries has arrived, grouped by their key.</summary>
        /// <remarks>
        /// <strong>A count, not merely "something".</strong> <see cref="ReceiveUntilAsync"/>
        /// returns on the first delivery, which is right for a redelivery and wrong for a test
        /// about how several messages are grouped: the first call can legitimately answer with a
        /// subset. This accumulates by the key the consumer put on each batch until the total is
        /// there, so a partial first answer is a slower test rather than a failing one.
        /// </remarks>
        public async ValueTask<IReadOnlyDictionary<string, List<BusDelivery>>> GroupedUntilAsync(
            int deliveries, TimeSpan bound)
        {
            var grouped = new Dictionary<string, List<BusDelivery>>(StringComparer.Ordinal);
            var seen = 0;
            var deadline = DateTimeOffset.UtcNow + bound;

            while (seen < deliveries && DateTimeOffset.UtcNow < deadline)
            {
                var received = await _consumer.ReceiveAsync(_subscription, 8, 16, Cancellation);

                received.IsSuccess.ShouldBeTrue(Because(received));

                foreach (var batch in received.Value)
                {
                    if (!grouped.TryGetValue(batch.PartitionKey!, out var into))
                    {
                        into = [];
                        grouped[batch.PartitionKey!] = into;
                    }

                    into.AddRange(batch.Deliveries);
                    seen += batch.Deliveries.Count;
                }
            }

            seen.ShouldBe(
                deliveries,
                $"the broker handed back {seen} of {deliveries} within {bound}. Grouping cannot " +
                "be judged on a subset, so this is the broker failing to deliver rather than " +
                "the consumer failing to group.");

            return grouped;
        }

        /// <summary>Receives until something arrives, or gives up.</summary>
        /// <remarks>
        /// A lock lapses on the broker's clock, not on this process's, so the only honest way to
        /// see the redelivery is to keep asking. The bound is what makes a broker that never
        /// hands it back a failed test rather than a hung one.
        /// </remarks>
        public async ValueTask<IReadOnlyList<BusDelivery>> ReceiveUntilAsync(TimeSpan bound)
        {
            var deadline = DateTimeOffset.UtcNow + bound;

            while (DateTimeOffset.UtcNow < deadline)
            {
                if (await ReceiveAsync() is { Count: > 0 } delivered)
                {
                    return delivered;
                }
            }

            return [];
        }

        /// <summary>What the subscription's own dead-letter queue is holding.</summary>
        public async ValueTask<IReadOnlyList<ServiceBusReceivedMessage>> DeadLetteredAsync()
        {
            var receiver = connection.Client.CreateReceiver(
                options.Topic,
                options.SubscriptionFor(group, Type),
                new ServiceBusReceiverOptions
                {
                    SubQueue = SubQueue.DeadLetter,
                    ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete,
                });

            await using var closing = receiver.ConfigureAwait(false);

            return await receiver.ReceiveMessagesAsync(8, TimeSpan.FromSeconds(1), Cancellation);
        }

        /// <summary>Drops the consumer, which is what a node dying does to its locks.</summary>
        public async ValueTask ReopenAsync()
        {
            await _consumer.DisposeAsync();

            _consumer = new AzureServiceBusConsumer(connection, options);
        }

        public async ValueTask DisposeAsync()
        {
            await _consumer.DisposeAsync();
            await _publisher.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
