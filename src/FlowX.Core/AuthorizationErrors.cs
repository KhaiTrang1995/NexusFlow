namespace FlowX;

/// <summary>
/// The refusals an authorisation stance produces.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Values, not exceptions</strong>
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0007-result-over-exceptions.md">ADR-0007</a>).
/// "You are not allowed to do that" is an outcome the caller was always going to have to
/// handle: it is the answer to the question the request asked, not a fault in the platform.
/// Thrown, it would have to be caught by every trigger's consumer loop, and a bus consumer
/// would dead-letter a message that was merely not permitted.
/// </para>
/// <para>
/// <strong>Here rather than on <c>FlowErrors</c>, and the layering is the reason.</strong>
/// <c>FlowErrors</c> lives in <c>FlowX.Runtime</c>; the decision that produces these is
/// <see cref="StepAuthorization.Decide"/>, in <c>FlowX.Core</c>, one layer below it.
/// Codes are constants so a caller can branch on them without repeating a string —
/// the reason <c>FlowErrors.StepTimedOutCode</c> is one.
/// </para>
/// <para>
/// <strong>Every one of them is <see cref="ErrorCategory.Forbidden"/>, including the one
/// <c>docs/15-Security.md §4</c> draws as a 401</strong>, and
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0029-a-refusal-is-a-result-failure.md">ADR-0029</a>
/// is that decision. <see cref="ErrorCategory"/> is a closed set (ADR-0009) with no
/// authentication member, and adding one would change the transport mapping table for every
/// existing consumer — so an anonymous caller and an under-privileged one both answer 403.
/// </para>
/// </remarks>
public static class AuthorizationErrors
{
    /// <summary>The code <see cref="NotAuthenticated"/> raises.</summary>
    public const string NotAuthenticatedCode = "authorization.not_authenticated";

    /// <summary>
    /// The step's capability requires a principal and the invocation carried none, or one
    /// no scheme authenticated.
    /// </summary>
    /// <param name="capabilityId">What was being authorised.</param>
    /// <remarks>
    /// <para>
    /// <see cref="ErrorCategory.Forbidden"/> and therefore <c>403</c>, where
    /// <c>docs/15-Security.md §4</c>'s flowchart draws <c>401</c>. That is a deliberate
    /// downgrade in precision recorded in ADR-0029, not an oversight: a <c>401</c> is
    /// obliged by RFC 9110 to carry a <c>WWW-Authenticate</c> header naming a challenge, and
    /// the engine — which is transport-agnostic by construction (ADR-0004) — has no
    /// challenge to name and no header to put it in.
    /// </para>
    /// <para>
    /// Distinct from <see cref="PermissionDenied"/> even though both are a 403, because the
    /// two lead to different repairs: this one means "sign in", that one means "ask for a
    /// grant", and an operator reading a log needs to tell them apart.
    /// </para>
    /// </remarks>
    public static Error NotAuthenticated(string capabilityId) =>
        new Error(
            NotAuthenticatedCode,
            $"Capability '{capabilityId}' requires an authenticated caller and this " +
            "invocation carried none. Identity comes from validated claims only, never " +
            "from a header or the payload.",
            ErrorCategory.Forbidden)
            .With("capabilityId", capabilityId);

    /// <summary>The code <see cref="PermissionDenied"/> raises.</summary>
    public const string PermissionDeniedCode = "authorization.permission_denied";

    /// <summary>
    /// The caller is authenticated and does not hold the permission the capability names.
    /// </summary>
    /// <param name="capabilityId">What was being authorised.</param>
    /// <param name="permission">The grant that was required.</param>
    /// <remarks>
    /// <para>
    /// <strong>The permission is named and the principal is not.</strong> The grant is
    /// already public — it is in <c>flowx.manifest.json</c>, which is the whole point of
    /// attaching the stance to the capability — so naming it tells the caller what to ask
    /// for. Putting the caller's claims in an error that reaches an RFC 7807 body would be
    /// the information disclosure <c>docs/15-Security.md §3</c>'s Boundary 1 row refuses.
    /// </para>
    /// <para>
    /// <see cref="ErrorCategory.Forbidden"/> is terminal, so no retry policy acts on it —
    /// which is correct and load-bearing: asking the same question of the same principal
    /// three times gets the same answer three times, while the backoff spends the flow's
    /// deadline.
    /// </para>
    /// </remarks>
    public static Error PermissionDenied(string capabilityId, string permission) =>
        new Error(
            PermissionDeniedCode,
            $"Capability '{capabilityId}' requires the '{permission}' permission and the " +
            "caller does not hold it.",
            ErrorCategory.Forbidden)
            .With("capabilityId", capabilityId)
            .With("permission", permission);

    /// <summary>The code <see cref="StanceNotEnforceable"/> raises.</summary>
    public const string StanceNotEnforceableCode = "authorization.stance_not_enforceable";

    /// <summary>
    /// A stance reached the engine that the engine cannot decide.
    /// </summary>
    /// <param name="capabilityId">What was being authorised.</param>
    /// <param name="stance">The stance nothing here can evaluate.</param>
    /// <remarks>
    /// <para>
    /// <strong>Unreachable from compiled source, and it fails closed anyway.</strong>
    /// <c>FLOWX1037</c> is an error, so <see cref="Authorization.Policy"/> cannot be built;
    /// this exists for the plan assembled by hand and for the stance added to the enum by a
    /// later release and forgotten in <see cref="StepAuthorization.Decide"/>.
    /// </para>
    /// <para>
    /// A permit would be the defect this whole package exists to fix, arriving through a
    /// <c>switch</c>'s default arm: a published authorisation contract that nothing enforces
    /// and nothing reports. <see cref="ErrorCategory.Forbidden"/> rather than
    /// <see cref="ErrorCategory.Internal"/> because the outcome for the caller is the same —
    /// the operation did not happen and retrying will not change that — and because a 500
    /// would invite a retry of something that is not transient.
    /// </para>
    /// </remarks>
    public static Error StanceNotEnforceable(string capabilityId, Authorization stance) =>
        new Error(
            StanceNotEnforceableCode,
            $"Capability '{capabilityId}' declares Authorization.{stance}, which this " +
            "runtime cannot decide, so the step is refused rather than run unauthorised. " +
            "See FLOWX1037.",
            ErrorCategory.Forbidden)
            .With("capabilityId", capabilityId)
            .With("stance", stance.ToString());
}
