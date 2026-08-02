namespace FlowX.Runtime;

/// <summary>
/// The derivation that turns one delivery of one message into the id of the instance it starts —
/// and the one that turns one partition into the lease that keeps its order.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the whole of the at-least-once answer, and there is deliberately nothing
/// else</strong>
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0035-a-delivery-names-the-instance-it-starts.md">ADR-0035</a>).
/// The broker will deliver a message twice; that is its contract. Every delivery of it derives
/// the same <see cref="Guid"/>, so the two primitives the runtime already has settle what
/// happens next: <c>ILeaseStore.AcquireAsync</c> refuses the second while the first is running,
/// and <c>IFlowJournal.StartAsync</c> refuses it with <c>journal.instance_exists</c> for ever
/// afterwards.
/// </para>
/// <para>
/// <strong>The lease is the fast answer and the journal is the true one.</strong> A lease has a
/// TTL, so it says nothing about a redelivery six weeks later; the primary key is what makes
/// "this message has been consumed" a permanent fact. That is why the derivation has to be stable
/// across processes, deployments and machines, and why nothing in it is a clock reading, a
/// machine name or a hash-code.
/// </para>
/// <para>
/// <strong>It is <see cref="ScheduleOccurrence"/>'s derivation with a different subject</strong>,
/// sharing <see cref="DerivedIdentity"/> so that the two cannot drift apart on the separator or
/// on the UUID version — the two properties that make either of them injective and either of
/// them recomputable by an operator.
/// </para>
/// </remarks>
public static class BusDeliveryIdentity
{
    /// <summary>The id the instance for one delivery is started under.</summary>
    /// <param name="flowId">The subscribing flow's business identity.</param>
    /// <param name="flowVersion">The exact version this node would run it at.</param>
    /// <param name="topic">The topic, verbatim as declared and published.</param>
    /// <param name="group">The consumer group, verbatim as declared and published.</param>
    /// <param name="eventId">The message's own identity, assigned when it was staged.</param>
    /// <returns>The instance id every node derives for this delivery.</returns>
    /// <remarks>
    /// <strong>Every term is in the id because leaving it out would fold two subscriptions into
    /// one.</strong> The flow version in particular: an instance is pinned to the version it
    /// started with (<c>docs/11-Distributed-Runtime.md §7</c>), so two versions deployed side by
    /// side are two subscribers, and a shared id would let the older one's delivery suppress the
    /// newer one's for the whole of a canary. The group for the same reason one topic over: two
    /// flows reading one topic under different groups are two subscribers and both must run.
    /// </remarks>
    public static Guid InstanceIdFor(
        string flowId, string flowVersion, string topic, string group, Guid eventId)
    {
        ArgumentNullException.ThrowIfNull(flowId);
        ArgumentNullException.ThrowIfNull(flowVersion);
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(group);

        return DerivedIdentity.From(flowId, flowVersion, topic, group, eventId.ToString("d"));
    }

    /// <summary>
    /// The lease one node takes to be the only node reading one partition of one subscription.
    /// </summary>
    /// <param name="flowId">The subscribing flow's business identity.</param>
    /// <param name="flowVersion">The exact version this node would run it at.</param>
    /// <param name="topic">The topic, verbatim as declared and published.</param>
    /// <param name="group">The consumer group, verbatim as declared and published.</param>
    /// <param name="partitionKey">
    /// The key whose order is being protected, or null for the partition of unordered events —
    /// which still takes a lease, because "no declared order" is not the same as "safe to process
    /// the same entry twice".
    /// </param>
    /// <returns>The lease id.</returns>
    /// <remarks>
    /// <para>
    /// <strong>This is what makes per-key ordering survive horizontal scaling</strong>
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0037-the-consumer-offers-per-key-order.md">ADR-0037</a>).
    /// A consumer group distributes the entries of one partition across the consumers in it, so
    /// two nodes reading one partition would process its entries concurrently and lose the order
    /// the publisher paid for. Taking a lease on the partition means ten nodes spread across the
    /// partitions rather than across one partition's entries — which is the shape partitioning
    /// was for.
    /// </para>
    /// <para>
    /// <strong>Prefixed, so a partition lease can never collide with an instance lease.</strong>
    /// Both live in one <c>ILeaseStore</c> keyed by <see cref="Guid"/>, and a collision would
    /// mean a node holding a partition blocking the flow it just started. The prefix is a term no
    /// flow id can produce, because it contains the separator.
    /// </para>
    /// </remarks>
    public static Guid StreamLeaseIdFor(
        string flowId, string flowVersion, string topic, string group, string? partitionKey)
    {
        ArgumentNullException.ThrowIfNull(flowId);
        ArgumentNullException.ThrowIfNull(flowVersion);
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(group);

        return DerivedIdentity.From(
            PartitionLeaseScope,
            flowId,
            flowVersion,
            topic,
            group,

            // An absent key and the empty key are different partitions, and folding them would
            // put unordered events under the same lease as a key somebody deliberately named "".
            partitionKey is null ? UnkeyedPartition : KeyedPartition + partitionKey);
    }

    /// <summary>The term that keeps a partition lease out of the instance id space.</summary>
    private const string PartitionLeaseScope = "flowx\0bus\0partition";

    /// <summary>Marks the partition of events staged with no key.</summary>
    private const string UnkeyedPartition = "\0unkeyed";

    /// <summary>Marks a partition that has a key, so the empty key is not the absent one.</summary>
    private const string KeyedPartition = "\0keyed\0";
}
