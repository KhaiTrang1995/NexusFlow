using System.Text;

namespace FlowX.Runtime;

/// <summary>
/// Builds the keys stage 1 and stage 3 address their stores by, in one place.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Length-prefixed, not separator-joined, and that is the whole design.</strong> A key
/// built as <c>a:b:c</c> from values a caller controls is one where a tenant called
/// <c>acme:payment.capture</c> can be made to collide with a different tenant's key — the same
/// class of defect as a SQL injection, and closed the same way: by construction rather than by
/// validating the input. Each component is written as its length, a colon, then the component,
/// so no component's content can be read as a boundary.
/// </para>
/// <para>
/// <strong>Keyed by capability id and never by step index.</strong> An index is a position in a
/// graph, so inserting a step before a policed one would silently invalidate every live record
/// and every live bucket — for an idempotency window that means replaying the wrong step's
/// result for the length of the window. The capability is what the step is *about*, is stable
/// across a graph edit, and is what <c>CircuitBreakerState</c> and <c>BulkheadGate</c> are
/// already keyed by. See
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0041-an-idempotency-record-is-keyed-by-the-invocations-key.md">ADR-0041</a>
/// §1.4.
/// </para>
/// <para>
/// <strong>An absent tenant or principal is keyed explicitly rather than folded away.</strong> A
/// <c>Tenant</c>-scoped limit on an untenanted invocation keys on the absence, not on the
/// capability alone: collapsing it into the <c>Global</c> key would put every untenanted caller
/// into one bucket sized for one tenant, which is a global limit wearing a per-tenant
/// declaration.
/// </para>
/// </remarks>
internal static class PolicyKeys
{
    /// <summary>The prefix every rate-limit bucket carries.</summary>
    internal const string RateLimitPrefix = "flowx:rl";

    /// <summary>The prefix every idempotency record carries.</summary>
    internal const string IdempotencyPrefix = "flowx:idem";

    /// <summary>The prefix every step-level quota counter carries.</summary>
    /// <remarks>
    /// Distinct from <see cref="TenantQuotaPrefix"/>, which is a tenant's whole admission
    /// budget, for the reason <see cref="RateLimitPrefix"/> is distinct from
    /// <see cref="TenantRatePrefix"/>: this one belongs to a capability and is spent by the
    /// steps that call it, and folding the two would make one step's declared plan limit bound
    /// everything the tenant does.
    /// </remarks>
    internal const string QuotaPrefix = "flowx:quota";

    /// <summary>The prefix a tenant's admission rate bucket carries.</summary>
    /// <remarks>
    /// Distinct from <see cref="RateLimitPrefix"/> rather than a <c>RateLimitScope.Tenant</c>
    /// bucket with an empty capability. The two are the same mechanism over the same store —
    /// see <see cref="TenantFairness.PermitsPerWindow"/> — and they are deliberately not the
    /// same <em>budget</em>: a tenant's admission bucket is spent by every flow it starts, and
    /// folding it into a step's bucket would make one capability's declared limit silently
    /// bound the whole tenant.
    /// </remarks>
    internal const string TenantRatePrefix = "flowx:tenant:rate";

    /// <summary>The prefix a tenant's long-window quota bucket carries.</summary>
    internal const string TenantQuotaPrefix = "flowx:tenant:quota";

    /// <summary>The prefix a tenant's journal write budget carries.</summary>
    /// <remarks>
    /// A third bucket rather than a share of the admission one, because the two are counted in
    /// different units: that one is spent once per call and this one once per row, and an
    /// instance writes as many rows as its data says. Folding them together would make a
    /// tenant's plan limit depend on how loop-heavy its flows happen to be.
    /// </remarks>
    internal const string TenantWritesPrefix = "flowx:tenant:writes";

    /// <summary>The component an invocation that carried no tenant or principal keys under.</summary>
    /// <remarks>
    /// A one-character component that no present value can produce, because every present value
    /// is written behind <see cref="Present"/>. So "no tenant", "a tenant whose id is the empty
    /// string" and "a tenant literally called <c>-</c>" are three keys — the last of which the
    /// obvious sentinel-word approach gets wrong.
    /// </remarks>
    internal const string Absent = "-";

    /// <summary>What every value the invocation did carry is written behind.</summary>
    internal const char Present = '+';

    /// <summary>The bucket one step's rate limit is counted in.</summary>
    /// <param name="capabilityId">The dependency being bounded.</param>
    /// <param name="scope">What the budget is shared by.</param>
    /// <param name="tenantId">The invocation's tenant, or null.</param>
    /// <param name="principal">The caller's name, or null.</param>
    public static string RateLimit(
        string capabilityId,
        RateLimitScope scope,
        string? tenantId,
        string? principal)
    {
        var discriminant = scope switch
        {
            RateLimitScope.Tenant => Component(tenantId),
            RateLimitScope.Principal => Component(principal),
            _ => string.Empty,
        };

        return Build(RateLimitPrefix, scope.ToString(), capabilityId, discriminant);
    }

    /// <summary>The counter one step's long-window quota is spent from.</summary>
    /// <param name="capabilityId">The dependency whose plan limit is being spent.</param>
    /// <param name="scope">Whose budget it is.</param>
    /// <param name="tenantId">The invocation's tenant, or null.</param>
    /// <param name="principal">The caller's name, or null.</param>
    /// <remarks>
    /// <strong>The scope identity is in the key, and that is the whole of the fairness
    /// property.</strong> Two tenants calling one capability under a <c>Tenant</c>-scoped quota
    /// address two counters, so one exhausting its plan cannot refuse the other — which is what
    /// <c>docs/16 §4</c> asks of a per-tenant bound and what a key built from the capability
    /// alone would silently not provide.
    /// </remarks>
    public static string Quota(
        string capabilityId,
        QuotaScope scope,
        string? tenantId,
        string? principal)
    {
        var discriminant = scope switch
        {
            QuotaScope.Tenant => Component(tenantId),
            QuotaScope.Principal => Component(principal),
            _ => string.Empty,
        };

        return Build(QuotaPrefix, scope.ToString(), capabilityId, discriminant);
    }

    /// <summary>The bucket one tenant's admission rate is counted in.</summary>
    /// <param name="tenantId">The resolved tenant. Never null — admission has refused that.</param>
    public static string TenantRate(string tenantId) => Build(TenantRatePrefix, tenantId);

    /// <summary>The bucket one tenant's long-window quota is counted in.</summary>
    /// <param name="tenantId">The resolved tenant. Never null — admission has refused that.</param>
    public static string TenantQuota(string tenantId) => Build(TenantQuotaPrefix, tenantId);

    /// <summary>The bucket one tenant's blocks of journal write credit are drawn from.</summary>
    /// <param name="tenantId">The resolved tenant. Never null — the host binds no budget without one.</param>
    public static string TenantWrites(string tenantId) => Build(TenantWritesPrefix, tenantId);

    /// <summary>The breaker key for one capability under one tenant.</summary>
    /// <param name="capabilityId">The dependency being guarded.</param>
    /// <param name="tenantId">The invocation's tenant.</param>
    /// <remarks>
    /// <c>docs/10 §6</c> describes a composite breaker key — <c>Capability | Downstream | Tenant
    /// | Partition</c> — and this is the second component becoming expressible. Built here
    /// rather than by concatenation at the call site so that a tenant id containing the
    /// separator cannot be made to collide with another tenant's breaker, which would trip one
    /// tenant's calls on another's failures: the precise inversion of what the widening is for.
    /// </remarks>
    public static string Breaker(string capabilityId, string tenantId) =>
        Build("flowx:cb", capabilityId, tenantId);

    /// <summary>The record one step's idempotency window is stored under.</summary>
    /// <param name="idempotencyKey">
    /// <c>ctx.IdempotencyKey</c> — the invocation's own, stable across the flow and across every
    /// attempt of a retried step, and never anything minted here.
    /// </param>
    /// <param name="capabilityId">The step's dependency, which narrows one key to one step.</param>
    /// <param name="scope">What the key is namespaced by.</param>
    /// <param name="tenantId">The invocation's tenant, or null.</param>
    public static string Idempotency(
        string idempotencyKey,
        string capabilityId,
        IdempotencyScope scope,
        string? tenantId)
    {
        var discriminant = scope == IdempotencyScope.Tenant ? Component(tenantId) : string.Empty;

        return Build(IdempotencyPrefix, scope.ToString(), capabilityId, idempotencyKey, discriminant);
    }

    /// <summary>A component that says whether the invocation carried the value at all.</summary>
    private static string Component(string? value) => value is null ? Absent : Present + value;

    private static string Build(string prefix, params string[] components)
    {
        var builder = new StringBuilder(prefix);

        foreach (var component in components)
        {
            builder.Append(':').Append(component.Length).Append(':').Append(component);
        }

        return builder.ToString();
    }
}
