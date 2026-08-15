using System.Security.Claims;
using FlowX.Runtime;
using Microsoft.AspNetCore.Http;

namespace FlowX.Http;

/// <summary>The header names FlowX reads and writes.</summary>
public static class FlowXHeaders
{
    /// <summary>W3C trace context. Continued, never restarted.</summary>
    public const string TraceParent = "traceparent";

    /// <summary>Fallback correlation header for callers that do not speak W3C trace context.</summary>
    public const string CorrelationId = "X-Correlation-ID";

    /// <summary>Caller-supplied deduplication key for mutating endpoints.</summary>
    public const string IdempotencyKey = "Idempotency-Key";
}

/// <summary>
/// Turns an HTTP request into a transport-agnostic <see cref="FlowInvocation"/>.
/// </summary>
/// <remarks>
/// This is the boundary where HTTP stops. Everything past it — the engine, the
/// capabilities, the flow — sees correlation, tenant and idempotency, and cannot tell
/// whether they came from a request, a Kafka record or a cron tick. That is what makes
/// quality goal Q4 hold in practice rather than on paper.
/// </remarks>
public static class HttpTriggerReader
{
    /// <summary>Claim types that may carry a tenant, in the order they are consulted.</summary>
    /// <remarks>
    /// <para>
    /// Claims only. A tenant read from a header or a body field is a tenant the caller
    /// chooses, which is a cross-tenant read waiting to happen (OWASP A01/A07). There is
    /// deliberately no configuration hook to add a header source.
    /// </para>
    /// <para>
    /// <strong>The list itself now lives in <see cref="ClaimTenantResolver"/>, and this is a
    /// projection of it.</strong> Both this transport and the admission-time resolver derive a
    /// tenant from the same claims, and until they shared a list they were two copies that
    /// could drift — a transport that gained an entry the resolver lacked would produce
    /// invocations the resolver then refused, and one that lost an entry would resolve a
    /// caller to no tenant at all.
    /// </para>
    /// </remarks>
    public static string[] TenantClaimTypes => ClaimTenantResolver.TenantClaimTypes;

    /// <summary>Claim types that may carry a processing purpose, in the order consulted.</summary>
    /// <remarks>
    /// A projection of <see cref="InvocationPurpose.PurposeClaimTypes"/>, for the reason
    /// <see cref="TenantClaimTypes"/> is a projection of the resolver's list: one rule, one
    /// place, read by the transport and by whatever else needs it.
    /// </remarks>
    public static string[] PurposeClaimTypes => InvocationPurpose.PurposeClaimTypes;

    /// <summary>Reads the invocation, or explains why the request cannot produce one.</summary>
    /// <param name="context">The request.</param>
    /// <param name="requireIdempotencyKey">
    /// True for endpoints declared <c>Idempotent = true</c>. When set, a request without
    /// the header is rejected rather than silently treated as unique — an endpoint that
    /// promises deduplication and then does not deduplicate is worse than one that never
    /// promised.
    /// </param>
    public static Result<FlowInvocation> Read(HttpContext context, bool requireIdempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(context);

        var idempotencyKey = context.Request.Headers[FlowXHeaders.IdempotencyKey].ToString();

        if (requireIdempotencyKey && string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Result.Fail<FlowInvocation>(new Error(
                "http.idempotency_key_required",
                $"This endpoint requires an '{FlowXHeaders.IdempotencyKey}' header. It is " +
                "passed to downstream systems so a retried request is not applied twice.",
                ErrorCategory.Validation));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            // Not required, so a per-request key is generated. It still makes retries
            // *within* the flow safe; it just cannot deduplicate across requests, which
            // is exactly what the caller declined to ask for.
            idempotencyKey = Guid.NewGuid().ToString("n");
        }

        return Result.Ok(new FlowInvocation(
            ReadCorrelationId(context),
            idempotencyKey,
            ReadTenant(context.User),
            Deadline: null,

            // The validated principal, and nothing derived from a header. This is the whole
            // of where identity crosses the boundary (ADR-0028): past this line the engine
            // decides a capability's authorisation stance against it and cannot tell whether
            // a request, a broker record or a cron tick produced it.
            //
            // `context.User` is never null in ASP.NET Core — an unauthenticated request
            // carries a ClaimsPrincipal whose identity is not authenticated — so it is
            // passed as it stands and StepAuthorization asks the question that matters,
            // which is whether the identity is authenticated rather than whether the object
            // exists.
            Principal: context.User,

            // Read from the same validated principal and from no header, which is what makes
            // a declared PolicySet.Consent(purpose) a limitation rather than a form field:
            // a caller that could name its own purpose would be granting itself the consent
            // the policy exists to check. Absent when the credential asserts none, and a
            // consent-gated step then refuses — deny by default.
            Purpose: ReadPurpose(context.User)));
    }

    /// <summary>
    /// Resolves the processing purpose from validated claims, and from nothing else.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="InvocationPurpose.FromClaims"/> for
    /// <see cref="ReadTenant(ClaimsPrincipal?)"/>'s reason: the derivation is the runtime's, so
    /// a transport cannot come to disagree with the engine about what a credential asserted.
    /// </remarks>
    public static string? ReadPurpose(ClaimsPrincipal? principal) =>
        InvocationPurpose.FromClaims(principal);

    /// <summary>
    /// Resolves the tenant from validated claims, and from nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns <c>null</c> rather than falling back to a header or a default when no
    /// claim is present. A default tenant is the shape of a cross-tenant data leak: the
    /// request proceeds, reads succeed, and the wrong customer's data comes back.
    /// </para>
    /// <para>
    /// <strong>The derivation is <see cref="ClaimTenantResolver"/>'s, and this delegates to
    /// it.</strong> What this method produces is checked at admission against what that class
    /// derives from the same principal, so two implementations of one rule would mean a
    /// transport whose every request refused itself. One rule, one place, called twice.
    /// </para>
    /// </remarks>
    public static string? ReadTenant(ClaimsPrincipal? principal) =>
        ClaimTenantResolver.FromClaims(principal);

    /// <summary>
    /// Continues the caller's trace, or starts one.
    /// </summary>
    /// <remarks>
    /// W3C <c>traceparent</c> first: continuing a distributed trace is the whole point,
    /// and restarting it severs the request from everything upstream of this service.
    /// </remarks>
    public static string ReadCorrelationId(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var traceParent = context.Request.Headers[FlowXHeaders.TraceParent].ToString();

        if (!string.IsNullOrWhiteSpace(traceParent))
        {
            return traceParent;
        }

        var correlationId = context.Request.Headers[FlowXHeaders.CorrelationId].ToString();

        return string.IsNullOrWhiteSpace(correlationId)
            ? context.TraceIdentifier
            : correlationId;
    }
}
