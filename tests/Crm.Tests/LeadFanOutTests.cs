using System.Text.Json;
using FlowX.Generated;
using Crm;
using FlowX;
using FlowX.RabbitMq;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// The fan-out over <c>lead.created</c>, of <c>docs/26-CRM-Sample.md</c> §8.5 and §10 package 4.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What is this sample's rather than the plugin's is the three groups.</strong>
/// <c>RabbitMqBusConsumerTests</c> proves a topic exchange copies a message to every bound queue;
/// what it cannot know is whether <em>this application</em> declared three subscriptions or
/// accidentally declared one group three times — which turns a fan-out into competing consumers
/// and loses two thirds of the work with nothing failing.
/// </para>
/// <para>
/// <strong>The three-way skip discipline is <see cref="CrmDatabase"/>'s.</strong> No broker
/// configured is a skip carrying its reason; a broker configured and unreachable is a failure,
/// because that is the case a skip would report as green.
/// </para>
/// </remarks>
public sealed class LeadFanOutTests
{
    /// <summary>The variable that supplies an AMQP URI.</summary>
    public const string ConnectionVariable = "FLOWX_RABBITMQ_CONNECTION";

    private const string Topic = "lead.created";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Every subscription this application declares on <c>lead.created</c>.</summary>
    /// <remarks>
    /// <strong>Read out of the manifest, not written down here.</strong> A list spelled in a test
    /// is a second statement of what the flows declare, and the failure this file exists to catch
    /// — two flows sharing a group — is one a hand-written list would hide by disagreeing with
    /// the build.
    /// </remarks>
    private static readonly BusSubscription[] Subscriptions = Declared();

    /// <summary>
    /// §10 package 4's "done when": one publish reaches three flows, and a redelivery to one
    /// does not re-run the others.
    /// </summary>
    [Fact]
    public async Task OnePublishReachesThreeSubscriptionsAndARedeliveryToOneIsTheOthersBusiness()
    {
        await using var broker = await ConnectAsync();

        foreach (var subscription in Subscriptions)
        {
            (await broker.Consumer.SubscribeAsync(subscription, Cancellation)).IsSuccess
                .ShouldBeTrue("the queue has to exist before the publish, or the exchange drops it.");
        }

        var lead = Guid.NewGuid();

        await broker.PublishAsync(lead);

        // Every group gets its own copy.
        var held = new Dictionary<string, BusDelivery>(StringComparer.Ordinal);

        foreach (var subscription in Subscriptions)
        {
            held[subscription.Group] = (await broker.ReceiveAsync(subscription)).ShouldHaveSingleItem(
                $"'{subscription.Group}' is a group of its own, so it has a copy of its own.");

            held[subscription.Group].Message!.Type.ShouldBe(Topic);
        }

        // Two of them finish with theirs. The third's is left unacknowledged, which is what a
        // node dying mid-flow leaves behind.
        foreach (var subscription in Subscriptions[..2])
        {
            (await broker.Consumer.AcknowledgeAsync(
                subscription, held[subscription.Group], Cancellation)).IsSuccess.ShouldBeTrue();
        }

        var enrichment = Subscriptions[2];

        // Releasing the channel is what makes RabbitMQ requeue what was never acknowledged.
        await broker.ReopenAsync();

        var again = await broker.ReceiveAsync(enrichment);

        again.ShouldHaveSingleItem("the unacknowledged copy came back to the group that held it.")
            .Message!.Type.ShouldBe(Topic);

        foreach (var subscription in Subscriptions[..2])
        {
            (await broker.ReceiveAsync(subscription)).ShouldBeEmpty(
                $"'{subscription.Group}' had acknowledged nothing of the redelivered copy — " +
                "it is not in that queue at all.");
        }
    }

    /// <summary>Three flows, three groups, and no two of them the same.</summary>
    /// <remarks>
    /// The failure this guards is silent: two flows sharing a group become competing consumers,
    /// each delivery goes to exactly one of them, and half the work simply never happens.
    /// </remarks>
    [Fact]
    public void TheThreeSubscriptionsAreThreeGroups()
    {
        Subscriptions.Length.ShouldBe(3, "scoring, assignment and enrichment all read lead.created.");

        Subscriptions.Select(static s => s.Group).Distinct(StringComparer.Ordinal).Count()
            .ShouldBe(Subscriptions.Length, "two flows sharing a group are competing consumers.");
    }

    // ------------------------------------------------------------------------------- fixtures

    /// <summary>The bus triggers on <c>lead.created</c>, as the build published them.</summary>
    private static BusSubscription[] Declared()
    {
        using var manifest = JsonDocument.Parse(FlowXManifest.Json);

        return
        [
            .. from flow in manifest.RootElement.GetProperty("flows").EnumerateArray()
               from trigger in flow.GetProperty("triggers").EnumerateArray()
               where trigger.TryGetProperty("topic", out var topic) &&
                     topic.GetString() == Topic
               select new BusSubscription(
                   flow.GetProperty("id").GetString()!,
                   flow.GetProperty("version").GetString()!,
                   Topic,
                   trigger.GetProperty("group").GetString()!),
        ];
    }

    private static async ValueTask<Fan> ConnectAsync()
    {
        if (Environment.GetEnvironmentVariable(ConnectionVariable) is not { Length: > 0 } uri)
        {
            Assert.Skip(
                $"Set {ConnectionVariable} to an AMQP URI — for example " +
                "\"amqp://guest:guest@localhost:5672/\" — to run the fan-out against a real broker.");

            throw new InvalidOperationException("unreachable");
        }

        var prefix = "crm-test-" + Guid.NewGuid().ToString("N")[..8];

        var options = new RabbitMqOptions
        {
            Exchange = prefix + ".events",
            DeadLetterExchange = prefix + ".dead",
            QueuePrefix = prefix,
        };

        var connection = new RabbitMqConnection(uri);

        try
        {
            return await Fan.CreateAsync(connection, options, Cancellation);
        }
        catch (Exception failure)
        {
            await connection.DisposeAsync();

            throw new InvalidOperationException(
                $"{ConnectionVariable} is set, so this run promised a RabbitMQ broker, and none " +
                "answered. This is a failure rather than a skip on purpose: a skip here would " +
                "report the CRM's fan-out as green against a broker that was never reached. " +
                failure.Message,
                failure);
        }
    }

    /// <summary>The publisher, the consumer and the topology they share, in one lifetime.</summary>
    private sealed class Fan : IAsyncDisposable
    {
        private readonly RabbitMqConnection _connection;
        private readonly RabbitMqOptions _options;
        private readonly RabbitMqEventPublisher _publisher;

        private RabbitMqBusConsumer _consumer;

        private Fan(RabbitMqConnection connection, RabbitMqOptions options)
        {
            _connection = connection;
            _options = options;
            _publisher = new RabbitMqEventPublisher(connection, options);
            _consumer = new RabbitMqBusConsumer(connection, options);
        }

        public RabbitMqBusConsumer Consumer => _consumer;

        public static async ValueTask<Fan> CreateAsync(
            RabbitMqConnection connection, RabbitMqOptions options, CancellationToken ct)
        {
            var fan = new Fan(connection, options);

            // Reaches the broker, so an unreachable one fails here rather than in an assertion.
            (await fan.Consumer.SubscribeAsync(Subscriptions[0], ct)).IsSuccess.ShouldBeTrue();

            return fan;
        }

        public async ValueTask PublishAsync(Guid lead)
        {
            var published = await _publisher.PublishAsync(
                [
                    new OutboxRecord
                    {
                        EventId = Guid.NewGuid(),
                        InstanceId = Guid.NewGuid(),
                        Type = Topic,
                        SchemaVersion = "1.0.0",
                        PartitionKey = lead.ToString(),
                        PayloadJson = JsonSerializer.Serialize(
                            new LeadCreated(lead, "Northwind Traders", LeadSource.Web),
                            CrmJsonContext.Default.LeadCreated),
                        TenantId = CrmTokens.NorthwindTenant,
                    },
                ],
                Cancellation);

            published.IsSuccess.ShouldBeTrue(
                published.IsSuccess ? null : published.Error!.Message);

            published.Value.ShouldBe(1);
        }

        public async ValueTask<IReadOnlyList<BusDelivery>> ReceiveAsync(BusSubscription subscription)
        {
            var received = await _consumer.ReceiveAsync(subscription, 8, 16, Cancellation);

            received.IsSuccess.ShouldBeTrue(received.IsSuccess ? null : received.Error!.Message);

            return received.Value.SelectMany(static batch => batch.Deliveries).ToList();
        }

        /// <summary>Drops the consumer, which is what returns an unacknowledged delivery.</summary>
        public async ValueTask ReopenAsync()
        {
            await _consumer.DisposeAsync();

            _consumer = new RabbitMqBusConsumer(_connection, _options);

            foreach (var subscription in Subscriptions)
            {
                (await _consumer.SubscribeAsync(subscription, Cancellation)).IsSuccess.ShouldBeTrue();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _consumer.DisposeAsync();
            await _publisher.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
