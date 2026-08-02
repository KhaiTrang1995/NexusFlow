namespace FlowX;

/// <summary>
/// Where a tenant's data may be processed, and where this deployment is.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is a refusal, not a placement, and the distinction is the whole feature.</strong>
/// A runtime running in one region cannot move a tenant's data to another one — the pod is
/// where it is, the database is where it is, and neither is a decision the request path gets
/// to make. What it can do is decline to process the request at all, before a lease is taken
/// and before a row exists, so that a misrouted call fails loudly in the wrong region instead
/// of succeeding there.
/// </para>
/// <para>
/// <strong>It therefore does not contradict
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0051-database-isolation-is-a-topology-not-a-runtime-level.md">ADR-0051</a>,
/// and this is the sentence to read if it looks like it does.</strong> That record refuses
/// `Database` isolation on the grounds that a store per tenant is a deployment topology rather
/// than a runtime level: nothing in the request path selects a store, because selecting one
/// would need a lease protocol and a tenant registry that no shared database can hold. Nothing
/// here selects a store either. The regional deployment is still the topology — one fleet per
/// region, each with its own database, placed by whoever writes the manifests — and this is the
/// check that the caller reached the fleet its tenant is allowed to be served by. A refusal
/// needs no registry, no second store and no protocol; it needs one comparison.
/// </para>
/// <para>
/// <strong>What it is worth, stated plainly.</strong> It is a control against
/// misconfiguration — a stale DNS record, a global load balancer that failed a tenant into the
/// wrong region, a client hard-coding an endpoint — and it is one half of a residency
/// guarantee. The other half is that the data was never in the wrong region to begin with,
/// which no runtime check can provide and which the deployment's own topology is what provides.
/// A residency claim resting on this alone would be false.
/// </para>
/// </remarks>
public sealed class TenantResidency
{
    private readonly Dictionary<string, string> _requirements = new(StringComparer.Ordinal);

    /// <summary>
    /// The region this deployment runs in, e.g. <c>eu-central-1</c>. Null when undeclared.
    /// </summary>
    /// <remarks>
    /// An opaque label, compared and never parsed. Which strings name regions is a question
    /// about a cloud provider, and a platform that validated them would be wrong the week the
    /// next region opens.
    /// </remarks>
    public string? Region { get; set; }

    /// <summary>The tenants whose data is pinned, and to where. Empty by default.</summary>
    /// <remarks>
    /// <strong>Absence means unpinned, not forbidden.</strong> A tenant with no entry is served
    /// wherever it arrives, which is the only default that lets an existing multi-region
    /// deployment adopt this one tenant at a time. A deployment that wants the opposite states
    /// every tenant.
    /// </remarks>
    public IDictionary<string, string> Requirements => _requirements;

    /// <summary>Whether any tenant is pinned, read once per admission.</summary>
    public bool IsEnabled => _requirements.Count > 0;

    /// <summary>
    /// The region this tenant's data must be processed in, or null when it is not pinned.
    /// </summary>
    /// <param name="tenantId">The resolved tenant.</param>
    /// <returns>The required region, or <c>null</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tenantId"/> is null.</exception>
    public string? RequiredRegion(string tenantId)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        return _requirements.TryGetValue(tenantId, out var region) ? region : null;
    }

    /// <summary>
    /// Whether this deployment may process work for this tenant.
    /// </summary>
    /// <param name="tenantId">The resolved tenant.</param>
    /// <returns><c>true</c> when the tenant is unpinned or pinned to this deployment's region.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tenantId"/> is null.</exception>
    /// <remarks>
    /// Ordinal comparison, case-sensitively, because a region label is an identifier rather
    /// than prose and two spellings that differ in case are two labels somebody has to reconcile
    /// somewhere. Reconciling them here would hide the inconsistency in exactly the control that
    /// exists to surface one.
    /// </remarks>
    public bool Permits(string tenantId) =>
        RequiredRegion(tenantId) is not { } required ||
        string.Equals(required, Region, StringComparison.Ordinal);
}
