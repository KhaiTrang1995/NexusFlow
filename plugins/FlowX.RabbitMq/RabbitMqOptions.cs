namespace FlowX.RabbitMq;

/// <summary>
/// The topology this package publishes into and consumes from, and the one broker-side bound it
/// asks RabbitMQ to enforce.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One record for both adapters, unlike <c>RedisStreamOptions</c> and
/// <c>RedisLeaseOptions</c>.</strong> Those are separate because a deployment may take its leases
/// from Redis and its broker from somewhere else. Here the publisher and the consumer are two
/// halves of <em>one</em> topology: the exchange the publisher writes to is the exchange the
/// consumer's queue is bound to, and a deployment that configured them independently would have
/// two ways to spell one name and no way to find out it had used both.
/// </para>
/// <para>
/// <strong>Everything here is an address, and nothing is a policy.</strong> How many deliveries a
/// message gets before the host gives up on it is <c>FlowXOptions.BusMaxDeliveries</c>; how often
/// the consumer sweeps is <c>FlowXOptions.BusScanInterval</c>. <see cref="DeliveryLimit"/> is the
/// one apparent exception and is not one — see its own remarks.
/// </para>
/// </remarks>
public sealed record RabbitMqOptions
{
    /// <summary>The topic exchange every published event goes to. Defaults to <c>flowx.events</c>.</summary>
    /// <remarks>
    /// <strong>A topic exchange, and the routing key is the event type.</strong> That is the whole
    /// of what this transport buys over a Redis stream: the broker does the fan-out, so three
    /// subscriptions to one event type are three queues the broker fills, and a redelivery to one
    /// of them does not re-run the other two.
    /// </remarks>
    public string Exchange { get; init; } = "flowx.events";

    /// <summary>
    /// The topic exchange a dead-lettered message goes to. Defaults to <c>flowx.events.dead</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Derived from nothing the manifest publishes</strong>, which is the decision
    /// <see href="../../docs/adr/ADR-0073-a-dead-letter-is-a-broker-object-and-its-address-is-still-deployment-configuration.md">ADR-0073</see>
    /// records: <c>KafkaTriggerAttribute.DeadLetter</c> stays unread here exactly as it stays
    /// unread in <c>FlowX.Redis</c>, because a destination is deployment configuration and
    /// <see href="../../docs/adr/ADR-0039-a-bus-subscription-publishes-no-new-manifest-field.md">ADR-0039</see>
    /// declines to put deployment configuration in a contract document.
    /// </para>
    /// <para>
    /// It is a second exchange rather than the same one with another routing key, so that a
    /// deployment can bind a single queue to <c>#</c> on it and have every subscription's dead
    /// letters in one place — and so that nothing bound to <see cref="Exchange"/> can accidentally
    /// receive them back.
    /// </para>
    /// </remarks>
    public string DeadLetterExchange { get; init; } = "flowx.events.dead";

    /// <summary>The prefix every queue this package declares begins with. Defaults to <c>flowx</c>.</summary>
    /// <remarks>
    /// Namespacing, for the reason <c>RedisStreamOptions.KeyPrefix</c> gives: two deployments
    /// sharing one broker share one queue name space unless somebody says otherwise, and two
    /// deployments consuming one queue is a silent split of the messages rather than an error.
    /// </remarks>
    public string QueuePrefix { get; init; } = "flowx";

    /// <summary>
    /// How many times RabbitMQ itself will deliver a message before dead-lettering it. Defaults
    /// to 20.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is a backstop and not the poison rule.</strong> The poison rule is
    /// <see href="../../docs/adr/ADR-0038-a-poison-message-is-dead-lettered.md">ADR-0038</see>'s,
    /// it lives in <c>FlowBusScan</c>, it is bounded by <c>FlowXOptions.BusMaxDeliveries</c>
    /// (default 5), and what it diverts carries a sentence an operator can read. The default here
    /// is deliberately <em>four times</em> that number so the host's rule fires first in every
    /// ordinary deployment and the broker's <c>x-death</c> never has to stand in for it.
    /// </para>
    /// <para>
    /// What it covers is the case the host's rule cannot reach: a queue whose consumer has stopped
    /// calling <c>DeadLetterAsync</c> at all — a node that crashes mid-flow every time, a
    /// deployment that wired the queue and then removed the subscription, a consumer that is not
    /// FlowX's. Without it such a message is redelivered for ever and the queue never drains.
    /// </para>
    /// <para>
    /// <strong>It is why the queues are quorum queues.</strong> RabbitMQ 3.12 supports
    /// <c>x-delivery-limit</c> on a quorum queue and not on a classic one, and it is the same
    /// choice that gives <c>x-delivery-count</c> — the number ADR-0038's bound is computed from.
    /// The price is stated in
    /// <see href="../../docs/adr/ADR-0072-a-rabbitmq-queue-is-a-partition-only-while-one-node-holds-it.md">ADR-0072</see>.
    /// </para>
    /// </remarks>
    public int DeliveryLimit { get; init; } = 20;

    /// <summary>The queue one subscription is served by.</summary>
    /// <param name="group">The subscription's consumer group.</param>
    /// <param name="topic">The subscription's topic — the event type.</param>
    /// <returns>The queue name.</returns>
    /// <exception cref="ArgumentException"><paramref name="group"/> or <paramref name="topic"/> is blank.</exception>
    /// <remarks>
    /// <strong>The group <em>and</em> the topic, for <c>RedisStreamBusConsumer.GroupNameFor</c>'s
    /// reason.</strong> Two subscriptions on different topics sharing one queue would steal each
    /// other's messages, silently, because a queue distributes among its consumers rather than
    /// copying to them. Naming the queue for the pair makes each subscription's backlog its own —
    /// and it is the pair, not the flow id, because two flows subscribing as one group are
    /// deliberately one subscriber.
    /// </remarks>
    public string QueueFor(string group, string topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        return $"{QueuePrefix}.{group}.{topic}";
    }

    /// <summary>The queue holding what one subscription gave up on.</summary>
    /// <param name="group">The subscription's consumer group.</param>
    /// <param name="topic">The subscription's topic — the event type.</param>
    /// <returns>The dead-letter queue name.</returns>
    /// <exception cref="ArgumentException"><paramref name="group"/> or <paramref name="topic"/> is blank.</exception>
    /// <remarks>
    /// One per subscription rather than one per deployment, so "what did this subscription give
    /// up on" is answerable without reading every other subscription's failures — the same reason
    /// <c>FlowX.Redis</c> derives a dead-letter stream per source stream.
    /// </remarks>
    public string DeadLetterQueueFor(string group, string topic) =>
        QueueFor(group, topic) + ".dead";
}
