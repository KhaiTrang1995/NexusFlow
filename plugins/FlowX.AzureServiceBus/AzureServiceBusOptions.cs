using System.Globalization;

namespace FlowX.AzureServiceBus;

/// <summary>
/// The topology this package publishes into and consumes from.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One record for both adapters, for <c>RabbitMqOptions</c>'s reason.</strong> The topic
/// the publisher sends to is the topic the consumer's subscriptions hang off; a deployment that
/// configured them independently would have two ways to spell one name and no way to discover it
/// had used both.
/// </para>
/// <para>
/// <strong>Everything here is an address, and nothing is a policy.</strong> How many deliveries a
/// message gets before the host gives up is <c>FlowXOptions.BusMaxDeliveries</c>; how often the
/// consumer sweeps is <c>FlowXOptions.BusScanInterval</c>. Azure Service Bus has its own
/// <c>MaxDeliveryCount</c> per subscription, and this package deliberately does not set it —
/// see <see cref="AzureServiceBusConsumer"/>, and
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0074-service-bus-topology-is-created-by-a-deployment-not-by-a-consumer.md">ADR-0074</a>.
/// </para>
/// </remarks>
public sealed record AzureServiceBusOptions
{
    /// <summary>The topic every published event goes to. Defaults to <c>flowx-events</c>.</summary>
    /// <remarks>
    /// <strong>One topic, and the event type is the message's <c>Subject</c>.</strong> A
    /// subscription selects the types it wants with a correlation filter on that subject, which is
    /// what makes three groups on one event type three independent backlogs — the property this
    /// transport shares with a RabbitMQ topic exchange and a Redis stream has no equivalent of.
    /// A topic per event type would instead make every new contract a management-plane change.
    /// </remarks>
    public string Topic { get; init; } = "flowx-events";

    /// <summary>
    /// What separates a group from an event type in a subscription's name. Defaults to <c>--</c>.
    /// </summary>
    /// <remarks>
    /// Not a dot: an entity name may contain one, so a dot would make
    /// <c>scoring--lead.created</c> and a hypothetical group <c>scoring.lead</c> on type
    /// <c>created</c> the same string. Two characters that cannot appear in an event type keep
    /// the mapping injective, which matters because the name is the only thing that carries it.
    /// </remarks>
    public string Separator { get; init; } = "--";

    /// <summary>How long a receive waits for a message before answering empty.</summary>
    /// <remarks>
    /// <strong>Short, because the host polls.</strong> <c>FlowBusScan</c> calls
    /// <c>ReceiveAsync</c> on its own interval, so a long wait here would be a second scheduler
    /// disagreeing with the first. One second is long enough that an idle deployment makes one
    /// round trip per pass rather than one per message, and short enough that shutdown does not
    /// wait on it.
    /// </remarks>
    public TimeSpan ReceiveWait { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The subscription a group reads a given event type from.</summary>
    /// <param name="group">The subscription group, from <c>[BusTrigger(Group = …)]</c>.</param>
    /// <param name="topic">The event type, e.g. <c>lead.created</c>.</param>
    /// <returns>The Service Bus subscription name.</returns>
    /// <remarks>
    /// <strong>Group first, so a deployment's subscriptions sort by consumer.</strong> An operator
    /// looking at a namespace wants "what does scoring read", and the portal sorts by name.
    /// </remarks>
    /// <exception cref="ArgumentException">Either argument is null or blank.</exception>
    public string SubscriptionFor(string group, string topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        return string.Create(CultureInfo.InvariantCulture, $"{group}{Separator}{topic}");
    }
}
