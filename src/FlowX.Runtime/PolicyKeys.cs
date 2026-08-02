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
