namespace FlowX;

/// <summary>
/// A journal that can bind itself to one tenant, so that every row it reaches afterwards is
/// that tenant's.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Separate from <see cref="IFlowJournal"/> for the reason
/// <see cref="IRecoveryIndex"/> is.</strong> Every member of the journal's own contract is
/// what an <em>executing</em> instance needs, and every one of them is pinned by
/// <c>JournalConformance</c>. Scoping is not part of executing an instance: it is a property
/// of how a store is reached, it depends on whether the technology behind it has anything to
/// scope <em>with</em>, and a store that has nothing — an in-memory dictionary, a
/// single-tenant deployment's — implements this and returns itself, or does not implement it
/// at all. Folding it into <see cref="IFlowJournal"/> would widen a contract every store must
/// satisfy in order to serve a level some deployments never select.
/// </para>
/// <para>
/// <strong>Why a bound instance rather than a parameter on every call.</strong> The scope has
/// to reach the <em>connection</em>, because that is where a database-enforced policy reads
/// it, and a connection is opened inside the adapter on each call. Threading a tenant through
/// all six journal members would put it on <see cref="IFlowJournal.FenceAsync"/> and
/// <see cref="IFlowJournal.ReadOutboxAsync"/>, which have no use for it, and would give every
/// caller a chance to pass the wrong one — the "someone forgets it once, in one query"
/// failure <c>docs/16 §1</c> opens by naming. Binding once, where the tenant is resolved,
/// means the call sites cannot get it wrong because they never see it.
/// </para>
/// <para>
/// <strong>An ambient scope was rejected.</strong> An <c>AsyncLocal</c> tenant would make the
/// isolation depend on where a continuation happened to be running, which is the objection
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0028-identity-arrives-on-the-invocation.md">ADR-0028</a>
/// already sustained against an ambient principal — and it applies with more force here,
/// because a lost identity refuses a step while a lost tenant scope reads the wrong rows.
/// </para>
/// </remarks>
public interface ITenantScopedJournal
{
    /// <summary>Returns this journal bound to one tenant.</summary>
    /// <param name="tenantId">
    /// The tenant every subsequent call is restricted to, or <c>null</c> for the untenanted
    /// rows a single-tenant deployment writes.
    /// </param>
    /// <returns>
    /// A journal over the same store, scoped. Never <c>null</c>, and never <c>this</c> for a
    /// store that genuinely isolates — a caller holds the scoped journal for the life of one
    /// execution and the unscoped one must stay unscoped for everything else.
    /// </returns>
    /// <remarks>
    /// <strong><c>null</c> is a scope, not the absence of one.</strong> It restricts the
    /// journal to rows with no tenant, which is what a single-tenant deployment's rows are —
    /// rather than lifting the restriction and seeing everything. That distinction is what
    /// makes a runtime that forgets to resolve a tenant fail closed: it reaches no tenant's
    /// data instead of reaching all of it.
    /// </remarks>
    IFlowJournal ForTenant(string? tenantId);

    /// <summary>The strongest isolation level this store actually enforces.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Declared by the store, so that a host can refuse a level the store cannot
    /// serve.</strong> <see cref="ForTenant"/> alone cannot say how far apart the two journals
    /// it returns are kept: a row-filtered store and a schema-per-tenant store present exactly
    /// the same seam, and a deployment that configured <see cref="TenantIsolation.Schema"/> and
    /// silently received <see cref="TenantIsolation.Row"/> would have been told nothing. This is
    /// what <c>FlowDurability.IsolationEnforced</c> reports and what
    /// <c>FlowHost</c> compares against <c>FlowXOptions.TenantIsolation</c> at construction.
    /// </para>
    /// <para>
    /// <see cref="TenantIsolation.Row"/> is the floor rather than
    /// <see cref="TenantIsolation.None"/>: a store implementing this interface at all can bind
    /// itself to a tenant, which is what <see cref="TenantIsolation.Row"/> means. A store that
    /// cannot does not implement it, and <c>FlowDurability</c> reports
    /// <see cref="TenantIsolation.None"/> for it.
    /// </para>
    /// </remarks>
    TenantIsolation Isolation => TenantIsolation.Row;
}
