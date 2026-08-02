namespace FlowX.Runtime;

/// <summary>
/// Decides which tenant an invocation belongs to, or refuses it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The seam that did not exist.</strong> <c>TriggerEnvelope.TenantId</c>,
/// <see cref="FlowInvocation.TenantId"/>, <c>FlowInstanceStart.TenantId</c>,
/// <c>flow_instance.tenant_id</c> and its index have all been present since migration
/// <c>0001</c>, and <c>FlowTelemetry</c> has tagged spans with the value throughout. Nothing
/// derived it, nothing validated it and nothing isolated on it: a caller supplied a tenant and
/// it was believed. This is where that stops being true.
/// </para>
/// <para>
/// <strong>Synchronous, and deliberately.</strong> A resolver that could do I/O would put a
/// store round trip on the admission path of every invocation, and admission is the one place
/// <c>docs/16 §4</c> insists must stay cheap — "<em>rejecting expensively is how rate limiting
/// becomes the DoS</em>". A deployment needing a tenant directory looks it up when it issues
/// the token, not when it spends one. This is the same constraint
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0027-authorisation-runs-in-the-step-loop.md">ADR-0027</a>
/// accepted for <c>StepAuthorization.Decide</c>, for the same reason, and it reopens on the
/// same trigger.
/// </para>
/// <para>
/// <strong>It takes the invocation, not the envelope.</strong> <c>docs/25 §1</c>'s class view
/// draws <c>Resolve(TriggerEnvelope)</c>; the host that has to call it holds a
/// <see cref="FlowInvocation"/> and never sees an envelope, and putting the decision back at
/// the transports would scatter it across every plugin — which is
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0004-universal-trigger-model.md">ADR-0004</a>'s
/// "no parallel system to get out of sync" lost at the first opportunity. See
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0043-a-tenant-is-resolved-at-admission.md">ADR-0043</a>.
/// </para>
/// </remarks>
public interface ITenantResolver
{
    /// <summary>Decides the tenant for one invocation.</summary>
    /// <param name="invocation">
    /// What the trigger produced: the tenant it asserts, the principal it authenticated, and
    /// whether the platform is continuing work it already admitted.
    /// </param>
    /// <returns>
    /// The tenant, a refusal, or <see cref="TenantResolution.NotConfigured"/> — three answers
    /// rather than a nullable string, because the last two must never be confused.
    /// </returns>
    /// <remarks>
    /// Takes the invocation by reference: it is a struct carrying six fields and this is on the
    /// admission path of every call.
    /// </remarks>
    TenantResolution Resolve(in FlowInvocation invocation);
}
