namespace FlowX;

/// <summary>
/// Where a staged event goes when it leaves the outbox.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the seam between the outbox and a broker, and nothing else.</strong> The
/// transactional outbox makes "the state changed and the event was emitted" one fact
/// (<c>docs/11-Distributed-Runtime.md §5</c>); it does not make the event arrive anywhere. A
/// publisher is the half that does, and it is an extension point rather than a built-in
/// because the set of brokers is open and none of them belongs in the runtime's dependency
/// tree — <c>docs/17-Plugin-System.md §2</c> has listed this contract as an extension point
/// since before it existed.
/// </para>
/// <para>
/// <strong>The unit is a batch, in order, and the answer is a prefix.</strong> The
/// implementation publishes <c>batch[0]</c>, then <c>batch[1]</c>, and so on, and reports how
/// many of them reached the broker before it stopped. Anything it did not report stays
/// pending and is offered again. That is the whole ordering contract: a caller can rely on
/// "the first <c>n</c> arrived, in this order, and nothing after them did", which is what
/// makes per-<c>partition_key</c> ordering expressible without a per-key protocol here.
/// </para>
/// <para>
/// <strong>At-least-once, never exactly-once.</strong> A crash between the broker
/// acknowledging and the outbox row being marked republishes the event
/// (<c>docs/11-Distributed-Runtime.md §4</c>). Consumers must be idempotent on
/// <see cref="OutboxRecord.EventId"/>, which is stable across every redelivery of the same
/// staged event, and every event contract says so.
/// </para>
/// <para>
/// <strong>Errors are values; a broken publisher is an exception.</strong> A broker that
/// refuses, is unreachable, or times out is an <see cref="Error"/> — the outbox is durable,
/// so a refusal costs a retry and nothing else. An exception means the publisher itself is
/// defective, and the caller lets it propagate rather than marking anything published
/// (ADR-0007).
/// </para>
/// </remarks>
public interface IEventPublisher
{
    /// <summary>
    /// Publishes a batch of staged events, in the order given.
    /// </summary>
    /// <param name="batch">
    /// The events to publish, oldest first. Every one is pending: its
    /// <see cref="OutboxRecord.PublishedAt"/> is null.
    /// </param>
    /// <param name="cancellationToken">Cancels the publish.</param>
    /// <returns>
    /// <para>
    /// On success, how many events from the front of <paramref name="batch"/> reached the
    /// broker. <c>0</c> is a legitimate answer, and so is any count below
    /// <c>batch.Count</c> — the caller marks exactly that many published and offers the rest
    /// again. An implementation must never report a count that includes an event it did not
    /// send, and must never send an event it did not include in the count *unless* it is
    /// prepared for that event to be sent again, which at-least-once always permits.
    /// </para>
    /// <para>
    /// On failure, an <see cref="Error"/> and no count: the caller marks nothing and the
    /// whole batch is offered again. Use this for a broker that is unreachable rather than
    /// for one that rejected a single message; a per-message rejection is better reported as
    /// a shorter prefix, because it leaves the events that did arrive marked.
    /// </para>
    /// </returns>
    ValueTask<Result<int>> PublishAsync(
        IReadOnlyList<OutboxRecord> batch,
        CancellationToken cancellationToken);
}
