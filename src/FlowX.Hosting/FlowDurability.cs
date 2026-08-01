using FlowX.Runtime;

namespace FlowX.Hosting;

/// <summary>
/// The stores a durable flow needs, resolved once, so that "is this host wired for
/// durability" is one null check rather than three.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Both halves or neither.</strong> A journal with no lease store would journal
/// under a token nothing issued, and a lease store with no journal would fence nothing.
/// Requiring the pair here means the failure is a missing registration at startup rather
/// than a half-durable instance discovered in production.
/// </para>
/// <para>
/// <strong>This is what retires <c>flow.durability_not_configured</c> as the normal path,
/// and it is deliberately not what deletes it.</strong> A host that registers these runs
/// durable flows; a host that does not still refuses them, which is the right answer for a
/// genuinely unconfigured deployment and a different thing from the unwired state WP-52
/// left behind. The error's job changed from "nothing is built yet" to "you have not
/// registered a journal", and that is a message an operator can act on.
/// </para>
/// </remarks>
public sealed class FlowDurability
{
    /// <summary>Bundles the stores a durable flow is executed against.</summary>
    /// <param name="journal">Where step boundaries are committed.</param>
    /// <param name="leases">Where exclusive ownership and fencing tokens come from.</param>
    /// <param name="recoveryIndex">
    /// How abandoned instances are found, when the journal can answer that cheaply. Null
    /// means this host runs no recovery scan — durable flows still run, and an instance whose
    /// node dies waits for a node that does scan.
    /// </param>
    /// <param name="timerIndex">
    /// How parked instances that are due are found, when the journal can answer that cheaply.
    /// Null means this host runs no timer sweep — durable flows still run and still park, and
    /// a <c>.Delay(...)</c> or an expired <c>.OnTimeout(...)</c> waits for a node that does
    /// sweep, or for the flow's own <c>[FlowDeadline]</c>.
    /// </param>
    public FlowDurability(
        IFlowJournal journal,
        ILeaseStore leases,
        IRecoveryIndex? recoveryIndex = null,
        ITimerIndex? timerIndex = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(leases);

        Journal = journal;
        Leases = leases;
        RecoveryIndex = recoveryIndex;
        TimerIndex = timerIndex;
    }

    /// <summary>Where step boundaries are committed.</summary>
    public IFlowJournal Journal { get; }

    /// <summary>Where exclusive ownership and fencing tokens come from.</summary>
    public ILeaseStore Leases { get; }

    /// <summary>How abandoned instances are found, or null when this host does not look.</summary>
    public IRecoveryIndex? RecoveryIndex { get; }

    /// <summary>Whether this host can run a recovery scan at all.</summary>
    public bool CanScan => RecoveryIndex is not null;

    /// <summary>How parked instances that are due are found, or null when this host does not look.</summary>
    /// <remarks>
    /// Separate from <see cref="RecoveryIndex"/> rather than the same optional service, because
    /// the two sweeps ask opposite questions of disjoint sets of rows: one looks for instances
    /// a node died holding, the other for instances parked by design whose clock has come
    /// round. A deployment may reasonably run either without the other.
    /// </remarks>
    public ITimerIndex? TimerIndex { get; }

    /// <summary>Whether this host can run a timer sweep at all.</summary>
    public bool CanWake => TimerIndex is not null;

    /// <summary>The lease policy these options describe.</summary>
    /// <param name="options">The validated host options.</param>
    internal static LeasePolicy PolicyFor(FlowXOptions options) => new()
    {
        Ttl = options.LeaseTtl,
        RenewalInterval = options.LeaseRenewalInterval,
    };
}
