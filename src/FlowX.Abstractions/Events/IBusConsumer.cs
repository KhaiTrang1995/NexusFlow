namespace FlowX;

/// <summary>
/// One declared subscription, as the broker plugin that has to serve it holds it.
/// </summary>
/// <param name="FlowId">The subscribing flow's business identity, from <c>[Flow]</c>.</param>
/// <param name="FlowVersion">The exact version this node would run it at.</param>
/// <param name="Topic">The event type consumed, as the manifest published it.</param>
/// <param name="Group">The consumer group, as the manifest published it.</param>
/// <param name="Transport">
/// The broker family the declaration named, or null when it named none. <c>[BusTrigger]</c>
/// leaves it null — the flow says what it consumes, not on what — while <c>[KafkaTrigger]</c>
/// sets it, and a consumer serving a different family refuses the subscription at registration
/// rather than serving it silently.
/// </param>
/// <remarks>
/// <para>
/// <strong>Four of these five values are in every instance id this subscription starts</strong>
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0035-a-delivery-names-the-instance-it-starts.md">ADR-0035</a>),
/// which is why they are carried as a unit rather than reassembled per delivery: a subscription
/// that lost its version between registration and derivation would fold a canary onto the version
/// it was replacing.
/// </para>
/// <para>
/// <strong>Address and admission only</strong>, exactly like <c>TriggerModel</c> and for the same
/// reason. How many deliveries a message gets, how often the consumer sweeps and how many
/// partitions it serves at once are <c>FlowXOptions</c> values and are deliberately not here.
/// </para>
/// </remarks>
public sealed record BusSubscription(
    string FlowId,
    string FlowVersion,
    string Topic,
    string Group,
    string? Transport = null);

/// <summary>
/// One message the broker is currently offering, and what the broker knows about it.
/// </summary>
/// <param name="Message">The message, as the flow it starts will receive it.</param>
/// <param name="Token">
/// Whatever the broker needs to be told in order to acknowledge or divert this exact delivery —
/// a Redis stream entry id, a Service Bus lock token, a delivery tag. Opaque to everything above
/// the plugin, and never derived from or compared with anything.
/// </param>
/// <param name="DeliveryCount">
/// How many times the broker has handed this message out, including now. One on a first
/// delivery. This is what bounds
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0038-a-poison-message-is-dead-lettered.md">ADR-0038</a>'s
/// dead-letter rule, and it counts <em>deliveries</em> rather than attempts: a node that claimed
/// the message and died before running anything has still spent one.
/// </param>
/// <param name="PartitionKey">
/// The key whose order this delivery belongs to, or null for an unordered one. Carried separately
/// from <see cref="BusMessage.PartitionKey"/> because a broker may know it — Redis Streams knows
/// it as the stream the entry came from — for an entry whose own field is missing, which is
/// exactly the entry that is about to be dead-lettered as unreadable and still has to be
/// attributed to a partition to be diverted.
/// </param>
/// <param name="UnreadableReason">
/// Why this entry is not a message, or null when it is one. Non-null and
/// <see cref="Message"/> null are the same fact stated twice, and both are needed: the reason is
/// what reaches the dead-letter stream, and the null message is what stops the caller running
/// anything.
/// </param>
/// <remarks>
/// <strong>An entry that is not a message is still a delivery.</strong> The alternative — a
/// plugin that dropped what it could not parse — would lose the entry silently and leave the
/// partition's pending list holding it for ever. So a malformed entry comes back like any other
/// and is dead-lettered on its first delivery
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0038-a-poison-message-is-dead-lettered.md">ADR-0038</a>),
/// because no number of retries turns an <c>event-id</c> that is not a GUID into one. Use
/// <see cref="Of"/> and <see cref="Unreadable"/> rather than the constructor, so the two states
/// cannot be built inconsistently.
/// </remarks>
public sealed record BusDelivery(
    BusMessage? Message,
    string Token,
    int DeliveryCount,
    string? PartitionKey = null,
    string? UnreadableReason = null)
{
    /// <summary>A delivery of a message the broker could read.</summary>
    /// <param name="message">The message.</param>
    /// <param name="token">What acknowledges this exact delivery.</param>
    /// <param name="deliveryCount">How many times it has been handed out, including now.</param>
    /// <returns>The delivery.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    public static BusDelivery Of(BusMessage message, string token, int deliveryCount)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new BusDelivery(message, token, deliveryCount, message.PartitionKey);
    }

    /// <summary>A delivery of an entry that is not a message.</summary>
    /// <param name="token">What acknowledges this exact delivery.</param>
    /// <param name="deliveryCount">How many times it has been handed out, including now.</param>
    /// <param name="partitionKey">The partition it blocks, so the divert can name it.</param>
    /// <param name="reason">Why it could not be read, in a sentence an operator can act on.</param>
    /// <returns>The delivery.</returns>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is null or blank.</exception>
    public static BusDelivery Unreadable(
        string token, int deliveryCount, string? partitionKey, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new BusDelivery(null, token, deliveryCount, partitionKey, reason);
    }

    /// <summary>Whether this entry became a message.</summary>
    public bool IsReadable => Message is not null;
}

/// <summary>
/// A batch of deliveries taken from one <em>partition</em>, in the order the broker holds them.
/// </summary>
/// <param name="PartitionKey">The key, or null for the partition of unordered events.</param>
/// <param name="Deliveries">The messages, oldest first.</param>
/// <remarks>
/// <strong>The unit is a partition and not a subscription, because the ordering guarantee is per
/// partition</strong>
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0037-the-consumer-offers-per-key-order.md">ADR-0037</a>).
/// A flat list across partitions would put the caller in the position of having to reconstruct
/// which entries had to be run serially and which could go at once — a fact the broker already
/// knows and the caller would have to infer from a field.
/// </remarks>
public sealed record BusPartitionBatch(string? PartitionKey, IReadOnlyList<BusDelivery> Deliveries);

/// <summary>
/// The seam between a broker and the runtime, in the direction the outbox does not go.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The mirror of <see cref="IEventPublisher"/>, and deliberately the same shape:
/// pull, act, report.</strong> A callback-shaped consumer — "give me a handler and I will call
/// it" — would put the flow-starting decision inside a plugin, where the journal, the lease
/// store and <c>FlowHost</c> are not; and every question this contract has to answer
/// (redelivery, acknowledgement, ordering, poison) would then be answered once per plugin
/// instead of once in <c>FlowBusScan</c>. Pulling keeps all four in the host and leaves the
/// plugin with the broker.
/// </para>
/// <para>
/// <strong>Every method returns a <see cref="Result"/> rather than throwing for a broker that is
/// unreachable</strong> (ADR-0007). A broker that is down is a reason to try again on the next
/// pass, not a defect; an exception means the consumer itself is broken — a null subscription, a
/// disposed connection — and propagates.
/// </para>
/// <para>
/// <strong>What an implementation must guarantee.</strong> A delivery returned by
/// <see cref="ReceiveAsync"/> is not returned again to any consumer until it is either
/// acknowledged, dead-lettered, or reclaimed after a visibility timeout; deliveries within one
/// <see cref="BusPartitionBatch"/> are in the order the broker holds them; and
/// <see cref="AcknowledgeAsync"/> is idempotent, because
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0036-a-message-is-acknowledged-when-its-flow-is-journalled.md">ADR-0036</a>
/// acknowledges after a commit and a node may die between the two.
/// </para>
/// </remarks>
public interface IBusConsumer
{
    /// <summary>
    /// The broker family this consumer serves, e.g. <c>redis-streams</c>.
    /// </summary>
    /// <remarks>
    /// Compared against <see cref="BusSubscription.Transport"/> at registration. A subscription
    /// that named a different family is refused loudly, because the alternative is a
    /// <c>[KafkaTrigger]</c> silently consumed from somewhere else — and the manifest would still
    /// publish <c>"transport": "kafka"</c> to everyone reading it.
    /// </remarks>
    string Transport { get; }

    /// <summary>
    /// Makes the subscription exist at the broker, idempotently.
    /// </summary>
    /// <param name="subscription">What to subscribe to.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// <c>true</c> when this call created the subscription and <c>false</c> when it already
    /// existed, or an <see cref="Error"/> when the broker could not be reached. Both booleans are
    /// success: the operation is idempotent, and which of the two happened is the difference
    /// between a first deployment and every pass after it.
    /// </returns>
    /// <remarks>
    /// Called on every pass rather than once at startup, because a broker restored from an empty
    /// state has forgotten the group and a consumer that only ever created it once would then
    /// read nothing for ever, silently.
    /// </remarks>
    ValueTask<Result<bool>> SubscribeAsync(
        BusSubscription subscription, CancellationToken cancellationToken);

    /// <summary>
    /// Takes up to <paramref name="maxPartitions"/> partitions' worth of pending messages.
    /// </summary>
    /// <param name="subscription">The subscription to read.</param>
    /// <param name="maxPartitions">How many partitions this pass will serve.</param>
    /// <param name="maxPerPartition">How many messages to take from each.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The batches, one per partition, in no promised order relative to each other — which is the
    /// whole of what ADR-0037 offers across keys. An empty list is the ordinary result.
    /// </returns>
    ValueTask<Result<IReadOnlyList<BusPartitionBatch>>> ReceiveAsync(
        BusSubscription subscription,
        int maxPartitions,
        int maxPerPartition,
        CancellationToken cancellationToken);

    /// <summary>Tells the broker this delivery is finished with.</summary>
    /// <param name="subscription">The subscription it came from.</param>
    /// <param name="delivery">The delivery.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// <c>true</c> when the broker was still holding this delivery and <c>false</c> when it was
    /// not, or an <see cref="Error"/>. <c>false</c> is success and is expected: ADR-0036
    /// acknowledges after a commit, so a node that died between the two acknowledges the same
    /// token again on the redelivery that follows.
    /// </returns>
    ValueTask<Result<bool>> AcknowledgeAsync(
        BusSubscription subscription, BusDelivery delivery, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a delivery somewhere a human can find it, and finishes with it.
    /// </summary>
    /// <param name="subscription">The subscription it came from.</param>
    /// <param name="delivery">The delivery.</param>
    /// <param name="reason">Why, in a sentence an operator can act on.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// <c>true</c> when the delivery was diverted and <c>false</c> when the broker no longer held
    /// it, or an <see cref="Error"/>.
    /// </returns>
    /// <remarks>
    /// <strong>Copy before acknowledge, never the reverse.</strong> A crash between the two
    /// redelivers the original, which is at-least-once behaving as it always does; the other
    /// order loses the message, which is the failure ADR-0038 exists to prevent.
    /// </remarks>
    ValueTask<Result<bool>> DeadLetterAsync(
        BusSubscription subscription,
        BusDelivery delivery,
        string reason,
        CancellationToken cancellationToken);
}
