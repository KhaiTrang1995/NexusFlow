using System.Text;
using Azure.Messaging.ServiceBus;
using FlowX.Conformance;

namespace FlowX.AzureServiceBus.Tests;

/// <summary>
/// <see cref="PublisherConformance"/>'s harness over a real Service Bus, on the topology
/// <c>emulator-config.json</c> declares.
/// </summary>
/// <remarks>
/// <para>
/// The suite needs three things and this supplies exactly those: the publisher, a way to read one
/// key's events back in broker order, and a publisher pointed at a broker that is not there.
/// <strong>Nothing in <c>tests/FlowX.Conformance.Tests</c> was changed to accommodate Service
/// Bus</strong> — the third transport, and the strongest form of the finding
/// <c>RabbitMqBrokerUnderTest</c> was commissioned to produce: Redis reaches ADR-0018's per-key
/// order with one stream per key, RabbitMQ with a confirmed serial channel, and this with AMQP 1.0
/// settlement down one sender. The suite could not tell.
/// </para>
/// <para>
/// <strong>The read is a subscription the deployment declared, not one this harness made.</strong>
/// ADR-0074 is the whole point of the package: entities exist because a deployment created them.
/// A harness that created its own would be exercising a code path this plugin deliberately does
/// not have, and would prove the opposite of what the suite is here to show.
/// </para>
/// <para>
/// <strong>Everything is drained on construction, because the topology is shared.</strong>
/// RabbitMQ tests get a private exchange per fixture; here there is one namespace and one set of
/// subscriptions, so isolation is emptying what is about to be used. Messages are read
/// <c>ReceiveAndDelete</c> as they are asked for and accumulated, because the suite asks about two
/// keys in one test and a subscription answers a question once.
/// </para>
/// </remarks>
internal sealed class ServiceBusBrokerUnderTest : BrokerUnderTest
{
    /// <summary>The subscription the emulator declares for this suite.</summary>
    /// <remarks>
    /// A catch-all rather than a correlation filter on one type: the suite publishes
    /// <c>order.placed</c>, <c>order.paid</c> and <c>order.shipped</c>, and a harness that could
    /// only see the first would report the other two as never having arrived.
    /// </remarks>
    public const string Subscription = "conformance--all";

    private readonly AzureServiceBusConnection _connection;
    private readonly AzureServiceBusOptions _options;
    private readonly AzureServiceBusEventPublisher _publisher;
    private readonly List<AzureServiceBusConnection> _unreachable = [];
    private readonly List<DeliveredEvent> _drained = [];
    private readonly ServiceBusReceiver _receiver;

    private ServiceBusBrokerUnderTest(
        AzureServiceBusConnection connection, AzureServiceBusOptions options)
    {
        _connection = connection;
        _options = options;
        _publisher = new AzureServiceBusEventPublisher(connection, options);

        _receiver = connection.Client.CreateReceiver(
            options.Topic,
            Subscription,
            new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete });
    }

    /// <inheritdoc />
    public override IEventPublisher Publisher => _publisher;

    /// <summary>Creates a harness on the declared topology, or refuses to pretend it did.</summary>
    /// <param name="cancellationToken">Cancels the setup.</param>
    /// <returns>The harness.</returns>
    /// <exception cref="InvalidOperationException">
    /// A namespace was promised by the environment and is not reachable.
    /// </exception>
    public static async ValueTask<ServiceBusBrokerUnderTest> CreateAsync(
        CancellationToken cancellationToken)
    {
        var connection = await ServiceBusTestNamespace.ConnectAsync(cancellationToken);

        await ServiceBusTestNamespace.DrainAsync(connection, Subscription, cancellationToken);

        return new ServiceBusBrokerUnderTest(connection, new AzureServiceBusOptions());
    }

    /// <inheritdoc />
    public override async ValueTask<IReadOnlyList<DeliveredEvent>> ReadAsync(
        string? partitionKey,
        CancellationToken cancellationToken)
    {
        await DrainAsync(cancellationToken);

        return
        [
            .. _drained.Where(delivered =>
                string.Equals(delivered.PartitionKey, partitionKey, StringComparison.Ordinal)),
        ];
    }

    /// <inheritdoc />
    public override ValueTask<IEventPublisher> UnreachableAsync(CancellationToken cancellationToken)
    {
        // Port 1 is reserved and nothing listens on it. The client connects lazily, so this
        // constructor succeeds and the first send is what meets the closed port — the shape a
        // namespace that went away mid-deployment has.
        var dead = new AzureServiceBusConnection(
            "Endpoint=sb://127.0.0.1:1;SharedAccessKeyName=RootManageSharedAccessKey;" +
            "SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;");

        _unreachable.Add(dead);

        return ValueTask.FromResult<IEventPublisher>(
            new AzureServiceBusEventPublisher(dead, _options));
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        foreach (var dead in _unreachable)
        {
            await dead.DisposeAsync();
        }

        _unreachable.Clear();

        await _receiver.DisposeAsync();
        await _publisher.DisposeAsync();
        await _connection.DisposeAsync();

        await base.DisposeAsync();
    }

    /// <summary>Takes everything the subscription holds and appends it to what was taken before.</summary>
    /// <remarks>
    /// <para>
    /// <c>ReceiveAndDelete</c>, because the harness is the only consumer and a suite that left
    /// messages locked would answer the same question twice on the second call. The order is the
    /// subscription's, which is the order the topic filled it in, which is the order the publisher
    /// sent — the chain the suite is actually asserting on.
    /// </para>
    /// <para>
    /// <strong>Two empty polls, not one, and the difference is a false green.</strong> A send is
    /// acknowledged when the topic accepts it; a receiver already polling can still answer empty
    /// once before the message is fetchable. Stopping at the first empty batch therefore reports
    /// "what reached the broker" before all of it has — which failed
    /// <c>AnEventOfferedAgainIsPublishedAgainRatherThanSuppressed</c> about one run in thirty, and
    /// would just as happily have passed a publisher that genuinely dropped a message.
    /// </para>
    /// </remarks>
    private async ValueTask DrainAsync(CancellationToken cancellationToken)
    {
        var quiet = 0;

        while (quiet < 2)
        {
            var batch = await _receiver
                .ReceiveMessagesAsync(64, TimeSpan.FromMilliseconds(500), cancellationToken)
                .ConfigureAwait(false);

            if (batch.Count == 0)
            {
                quiet++;

                continue;
            }

            quiet = 0;

            foreach (var message in batch)
            {
                _drained.Add(new DeliveredEvent(
                    Guid.TryParse(message.MessageId, out var id) ? id : Guid.Empty,
                    message.Subject ?? string.Empty,
                    AzureServiceBusHeaders.Text(message, AzureServiceBusHeaders.SchemaVersion)
                        ?? string.Empty,
                    AzureServiceBusHeaders.Text(message, AzureServiceBusHeaders.PartitionKey),
                    message.ContentType is null ? null : Encoding.UTF8.GetString(message.Body)));
            }
        }
    }
}
