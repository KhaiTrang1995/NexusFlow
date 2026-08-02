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

    /// <summary>Whether the journal behind this host can isolate one tenant from another.</summary>
    /// <remarks>
    /// False for an in-memory journal and for any store with nothing to scope with. A
    /// deployment declaring <see cref="TenantIsolation.Row"/> over such a store still gets
    /// admission-time refusal — an untenanted call is still refused, and a caller still cannot
    /// name a tenant its claims do not support — but it does not get the database's second
    /// wall, and this is what says so rather than leaving it to be discovered.
    /// </remarks>
    public bool CanIsolateTenants => Journal is ITenantScopedJournal;

    /// <summary>How far apart the journal behind this host can actually keep two tenants.</summary>
    /// <remarks>
    /// <strong>Reported rather than assumed, because the seam does not reveal it.</strong>
    /// <see cref="ITenantScopedJournal.ForTenant"/> looks identical whether the store filters
    /// rows or hands out a schema of its own, so a deployment declaring
    /// <see cref="TenantIsolation.Schema"/> over a row store would have been served the weaker
    /// level under the stronger name and told nothing. <c>FlowHost</c> compares this against
    /// what the deployment declared and refuses to be constructed when it asked for more.
    /// </remarks>
    public TenantIsolation IsolationEnforced =>
        Journal is ITenantScopedJournal scoped ? scoped.Isolation : TenantIsolation.None;

    /// <summary>
    /// The journal, bound to one tenant when the store can bind it.
    /// </summary>
    /// <param name="tenantId">
    /// The resolved tenant, or <c>null</c> for a deployment that does not isolate.
    /// </param>
    /// <returns>
    /// A scoped journal, or the unscoped one when there is no tenant to scope to or no store
    /// able to scope.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>A null tenant returns the unscoped journal here, which is the opposite of what
    /// <see cref="ITenantScopedJournal.ForTenant"/> means by null — and the asymmetry is
    /// deliberate.</strong> This method's null means "this deployment declares
    /// <see cref="TenantIsolation.None"/>, there is no tenant in the system at all"; that
    /// one's means "bind me to the rows that have no tenant". A single-tenant host must reach
    /// its journal exactly as it always did, and paying a bind statement per call to be
    /// restricted to the only rows that exist would be a cost for nothing.
    /// </para>
    /// <para>
    /// The consequence is that scoping is driven entirely by whether a tenant was
    /// <em>resolved</em>, and <c>FlowHost</c> resolves one or refuses the call. There is no
    /// path on which an isolating deployment reaches this with null.
    /// </para>
    /// </remarks>
    public IFlowJournal JournalFor(string? tenantId) =>
        tenantId is { Length: > 0 } && Journal is ITenantScopedJournal scoped
            ? scoped.ForTenant(tenantId)
            : Journal;

    /// <summary>The lease policy these options describe.</summary>
    /// <param name="options">The validated host options.</param>
    internal static LeasePolicy PolicyFor(FlowXOptions options) => new()
    {
        Ttl = options.LeaseTtl,
        RenewalInterval = options.LeaseRenewalInterval,
    };
}
