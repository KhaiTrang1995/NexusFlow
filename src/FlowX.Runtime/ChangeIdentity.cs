namespace FlowX.Runtime;

/// <summary>
/// The derivation that turns one observed change into the id of the instance it starts — and the
/// one that makes a subscription readable by one node at a time.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="BusDeliveryIdentity"/>'s derivation with a different subject and a scope
/// term</strong>
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0049-a-change-names-the-instance-it-starts.md">ADR-0049</a>).
/// A change feed is at-least-once for a reason of its own — the cursor is committed after the
/// flows have run, so a crash in between re-offers everything since the last commit — and the
/// answer is the one ADR-0031 and ADR-0035 already reached: derive the id, and let
/// <c>ILeaseStore.AcquireAsync</c> refuse the second while the first is running and
/// <c>IFlowJournal.StartAsync</c> refuse it for ever afterwards.
/// </para>
/// <para>
/// <strong>The scope term is load-bearing.</strong> A flow carrying both
/// <c>[BusTrigger("order.placed", Group = "g")]</c> and
/// <c>[ChangeTrigger("order.placed", Group = "g")]</c> is two subscribers over two transports,
/// and <see cref="BusDeliveryIdentity.InstanceIdFor"/>'s five terms are identical for both — so
/// without it the change subscription would be permanently suppressed by the bus one's journal
/// rows, silently and only on the events both saw.
/// </para>
/// </remarks>
public static class ChangeIdentity
{
    /// <summary>The id the instance for one observed change is started under.</summary>
    /// <param name="flowId">The observing flow's business identity.</param>
    /// <param name="flowVersion">The exact version this node would run it at.</param>
    /// <param name="source">The event type observed, verbatim as declared and published.</param>
    /// <param name="group">The subscription group, verbatim as declared and published.</param>
    /// <param name="changeId">
    /// The change's own identity — for the outbox, the <c>event_id</c> the store assigned when the
    /// step that emitted it committed. Stable across every re-read of the row, which is what makes
    /// the whole mechanism work.
    /// </param>
    /// <returns>The instance id every node derives for this change.</returns>
    /// <exception cref="ArgumentNullException">A term is null.</exception>
    public static Guid InstanceIdFor(
        string flowId, string flowVersion, string source, string group, Guid changeId)
    {
        ArgumentNullException.ThrowIfNull(flowId);
        ArgumentNullException.ThrowIfNull(flowVersion);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(group);

        return DerivedIdentity.From(
            ChangeScope, flowId, flowVersion, source, group, changeId.ToString("d"));
    }

    /// <summary>
    /// The lease one node takes to be the only node reading one subscription's cursor.
    /// </summary>
    /// <param name="flowId">The observing flow's business identity.</param>
    /// <param name="flowVersion">The exact version this node would run it at.</param>
    /// <param name="source">The event type observed, verbatim as declared and published.</param>
    /// <param name="group">The subscription group, verbatim as declared and published.</param>
    /// <returns>The lease id.</returns>
    /// <exception cref="ArgumentNullException">A term is null.</exception>
    /// <remarks>
    /// <para>
    /// <strong>One subscription, one reader</strong>
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0048-a-change-feed-advances-a-cursor.md">ADR-0048</a>
    /// decision 4). A cursor is a single-reader structure: two nodes reading one subscription
    /// would run its changes concurrently and commit two positions over one row, and the later
    /// commit would decide. That is weaker than the per-partition concurrency a bus subscription
    /// gets, and it is the honest cost of a log with a cursor — a deployment scales by adding
    /// subscriptions rather than nodes.
    /// </para>
    /// <para>
    /// Carries no change id, because the subject is the subscription. Scoped separately from
    /// <see cref="InstanceIdFor"/> so that a node holding a subscription can never be holding the
    /// lease of an instance it is about to start.
    /// </para>
    /// </remarks>
    public static Guid SubscriptionLeaseIdFor(
        string flowId, string flowVersion, string source, string group)
    {
        ArgumentNullException.ThrowIfNull(flowId);
        ArgumentNullException.ThrowIfNull(flowVersion);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(group);

        return DerivedIdentity.From(SubscriptionLeaseScope, flowId, flowVersion, source, group);
    }

    /// <summary>The term that keeps a change's instance id out of a delivery's id space.</summary>
    private const string ChangeScope = "flowx\0change";

    /// <summary>The term that keeps a subscription lease out of the instance id space.</summary>
    private const string SubscriptionLeaseScope = "flowx\0change\0subscription";
}
