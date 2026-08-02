namespace FlowX;

/// <summary>
/// One declared change subscription, as the store that has to serve it holds it.
/// </summary>
/// <param name="FlowId">The observing flow's business identity, from <c>[Flow]</c>.</param>
/// <param name="FlowVersion">The exact version this node would run it at.</param>
/// <param name="Source">The event type observed, as the manifest published it as <c>topic</c>.</param>
/// <param name="Group">The subscription group, as the manifest published it.</param>
/// <remarks>
/// <para>
/// <strong>All four values are in every instance id this subscription starts</strong>
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0049-a-change-names-the-instance-it-starts.md">ADR-0049</a>),
/// and all four are the cursor's key, which is why they are carried as a unit rather than
/// reassembled per pass: a subscription that lost its version between registration and derivation
/// would fold a canary onto the version it was replacing and read that one's cursor.
/// </para>
/// <para>
/// <strong>Address and admission only</strong>, exactly like <see cref="BusSubscription"/>. How
/// often a subscription is swept and how many changes a pass takes are <c>FlowXOptions</c>
/// values.
/// </para>
/// </remarks>
public sealed record ChangeSubscription(
    string FlowId,
    string FlowVersion,
    string Source,
    string Group);

/// <summary>
/// How far through a feed a subscription has read, in whatever terms the feed keeps.
/// </summary>
/// <param name="Value">
/// The position, rendered by the feed that produced it. Opaque above the plugin: never parsed,
/// compared or ordered by anything outside the implementation that made it.
/// </param>
/// <remarks>
/// <para>
/// <strong>Opaque on purpose, because the safe cursor is store-specific.</strong> The PostgreSQL
/// feed's position is a transaction id and a staging position, and reading it as anything else —
/// a timestamp, a row count, a sequence number — is the mistake
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0048-a-change-feed-advances-a-cursor.md">ADR-0048</a>
/// exists to prevent. A host that could compare two positions would eventually arithmetic on
/// them.
/// </para>
/// <para>
/// The host's only operation on a position is to hand back the one that came with the last change
/// it finished with, which is why <see cref="ObservedChange"/> carries one per change rather than
/// one per batch.
/// </para>
/// </remarks>
public readonly record struct ChangePosition(string Value);

/// <summary>One change the feed is offering, and where it sits in the feed.</summary>
/// <param name="Message">The change, as the flow it starts will receive it.</param>
/// <param name="Position">
/// The position a subscription reaches by finishing with this change. Committed only once every
/// change before it has been finished with too, so the cursor never moves past something that did
/// not run.
/// </param>
/// <remarks>
/// <strong>There is no delivery count and no acknowledgement token, and neither is an
/// omission.</strong> A feed does not hold a change on a consumer's behalf — it is a log, and the
/// only thing a consumer owns is its own position. The consequences are recorded in
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0048-a-change-feed-advances-a-cursor.md">ADR-0048</a>:
/// no per-change acknowledgement, and no dead-letter path, because a change that cannot be
/// processed is a flow that failed and a flow that failed is a recorded outcome.
/// </remarks>
public sealed record ObservedChange(BusMessage Message, ChangePosition Position);

/// <summary>
/// The seam between a change feed and the runtime — the outbox read forwards rather than drained.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The shape <see cref="IBusConsumer"/> has, minus the half a log does not have.</strong>
/// Pull, act, report: <see cref="ReadAsync"/> offers changes and <see cref="CommitAsync"/> records
/// how far the subscription got. There is no subscribe (a feed exists because the table does), no
/// acknowledge (nothing is held on the consumer's behalf) and no dead-letter (see
/// <see cref="ObservedChange"/>).
/// </para>
/// <para>
/// <strong>The implementation holds the cursor, and the host never sees where it is.</strong>
/// <see cref="ReadAsync"/> takes no position: it reads from wherever this subscription's cursor
/// is. That keeps the durable cursor in the same transaction boundary as the store that answers
/// the query, and it means a host cannot start a subscription from a position it invented.
/// </para>
/// <para>
/// <strong>What an implementation must guarantee</strong>, and the whole of what
/// <c>FlowChangeScan</c> relies on:
/// </para>
/// <list type="number">
/// <item><description>
/// <strong>No gaps.</strong> A change committed to the underlying store is offered exactly once
/// to a subscription whose cursor is behind it, and is never skipped by a later change becoming
/// readable first. The PostgreSQL feed meets this with a snapshot barrier; a feed over a store
/// with no such primitive has to meet it by its own means, and a feed that cannot must not be
/// written.
/// </description></item>
/// <item><description>
/// <strong>Order.</strong> Changes are offered in the order the store committed them, and for a
/// given <c>PartitionKey</c> that order is the order they were staged in — which is exactly what
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0018-outbox-publication-and-ordering.md">ADR-0018</a>
/// offers and no more. Nothing is promised across keys.
/// </description></item>
/// <item><description>
/// <strong>A committed position is durable and monotonic.</strong> A position committed by one
/// node is what the next <see cref="ReadAsync"/> on any node reads from, and committing an older
/// position than the one already stored moves nothing.
/// </description></item>
/// </list>
/// <para>
/// <strong>Every method returns a <see cref="Result"/> rather than throwing for a store that is
/// unreachable</strong> (ADR-0007), for <see cref="IBusConsumer"/>'s reason.
/// </para>
/// </remarks>
public interface IChangeFeed
{
    /// <summary>
    /// The feed family this implementation serves, e.g. <c>postgres-outbox</c>.
    /// </summary>
    /// <remarks>
    /// Reported rather than matched, unlike <see cref="IBusConsumer.Transport"/>. A change trigger
    /// names no family — there is nothing on the attribute to disagree with — so this exists to
    /// make a diagnostic, a log line or a health check able to say which feed a subscription is
    /// being served by.
    /// </remarks>
    string Feed { get; }

    /// <summary>
    /// Takes up to <paramref name="max"/> changes from wherever this subscription's cursor is.
    /// </summary>
    /// <param name="subscription">The subscription to read.</param>
    /// <param name="max">How many changes this pass will take.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The changes, oldest first, or an <see cref="Error"/> when the store could not be reached.
    /// An empty list is the ordinary result: a subscription that is caught up has nothing to do.
    /// </returns>
    ValueTask<Result<IReadOnlyList<ObservedChange>>> ReadAsync(
        ChangeSubscription subscription, int max, CancellationToken cancellationToken);

    /// <summary>
    /// Records that this subscription has finished with everything up to and including a change.
    /// </summary>
    /// <param name="subscription">The subscription.</param>
    /// <param name="position">The position of the last change finished with.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// <c>true</c> when the cursor moved and <c>false</c> when it was already at or beyond this
    /// position, or an <see cref="Error"/>. <c>false</c> is success: a node that committed and
    /// then died repeats the commit on its next pass.
    /// </returns>
    /// <remarks>
    /// <strong>Called after the flows have run, never before</strong>
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0048-a-change-feed-advances-a-cursor.md">ADR-0048</a>).
    /// The other order is at-most-once, and loses every change a crash lands in the middle of.
    /// </remarks>
    ValueTask<Result<bool>> CommitAsync(
        ChangeSubscription subscription,
        ChangePosition position,
        CancellationToken cancellationToken);
}
