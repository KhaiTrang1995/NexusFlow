namespace FlowX;

/// <summary>
/// The refusals tenant resolution produces.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Values, not exceptions</strong>
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0007-result-over-exceptions.md">ADR-0007</a>),
/// for the reason <c>AuthorizationErrors</c> gives: "this call names no tenant" is the
/// answer to the question the request asked, not a fault in the platform. Thrown, a bus
/// consumer would dead-letter a message that was merely not admissible.
/// </para>
/// <para>
/// <strong><see cref="ErrorCategory.Forbidden"/>, and the category is the decision.</strong>
/// A refusal here is the same kind of answer as an authorisation refusal — the operation did
/// not happen, nothing about it was transient, and retrying reaches the same absent claim —
/// so it maps to the same <c>403</c> and is outside every retryable set. Reporting it as
/// <see cref="ErrorCategory.Validation"/> would invite a caller to fix its payload, when what
/// is missing is a claim only its token issuer can add.
/// </para>
/// </remarks>
public static class TenantErrors
{
    /// <summary>The code <see cref="TenantRequired"/> raises.</summary>
    public const string TenantRequiredCode = "tenant.required";

    /// <summary>
    /// The deployment isolates by tenant and the call carried none.
    /// </summary>
    /// <param name="isolation">The level this deployment declares.</param>
    /// <remarks>
    /// <para>
    /// <strong>Refused rather than defaulted, and that is the whole feature.</strong> A
    /// deployment that answered this with a default tenant would proceed, read successfully,
    /// and return the wrong customer's data — which is why <c>docs/16 §9</c> lists a default
    /// tenant as an anti-pattern and why <c>HttpTriggerReader.ReadTenant</c> already returns
    /// <c>null</c> rather than falling back. This is that same stance, made enforceable: the
    /// null now stops the call instead of travelling with it.
    /// </para>
    /// <para>
    /// Distinct from <see cref="CrossTenantDenied"/> because the two lead to different
    /// repairs: this one means "your token carries no tenant claim", that one means "your
    /// token carries a different one from the tenant you asked for".
    /// </para>
    /// </remarks>
    public static Error TenantRequired(TenantIsolation isolation) =>
        new Error(
            TenantRequiredCode,
            $"This deployment declares TenantIsolation.{isolation}, so every call must name " +
            "a tenant and this one named none. A tenant is read from validated claims only, " +
            "never from a header, a query string or the payload — so the repair is a token " +
            "carrying a tenant claim, not a different request.",
            ErrorCategory.Forbidden)
            .With("isolation", isolation.ToString());

    /// <summary>The code <see cref="CrossTenantDenied"/> raises.</summary>
    public const string CrossTenantDeniedCode = "tenant.cross_tenant_denied";

    /// <summary>
    /// The call named a tenant the caller's validated claims do not support.
    /// </summary>
    /// <param name="claimed">The tenant the invocation asserted.</param>
    /// <remarks>
    /// <para>
    /// <strong>This is the escalation the resolver exists to catch.</strong>
    /// <c>docs/16 §3</c> states the rule it breaks in one line — "a resolver returning a
    /// tenant not present in validated claims fails <c>CrossTenantAccessTest</c>" — and until
    /// a resolver existed there was nothing to compare the two against: whatever a transport
    /// put on the invocation was believed. A caller that can name its own tenant has the
    /// whole platform's isolation at its disposal.
    /// </para>
    /// <para>
    /// <strong>The caller's own tenant is not named in the message.</strong> Telling a caller
    /// which tenant its claims <em>do</em> place it in is harmless; telling it that the one it
    /// guessed exists is not, and an error that distinguished "wrong tenant" from "no such
    /// tenant" would be an oracle for enumerating them. So the message names what the caller
    /// already knows — what it asked for — and stops there, which is the same reason
    /// <c>AuthorizationErrors.PermissionDenied</c> names the grant and never the
    /// principal.
    /// </para>
    /// </remarks>
    public static Error CrossTenantDenied(string claimed) =>
        new Error(
            CrossTenantDeniedCode,
            $"This call named tenant '{claimed}', which the caller's validated claims do not " +
            "support. A tenant is derived from claims and never believed from the call.",
            ErrorCategory.Forbidden)
            .With("claimedTenantId", claimed);

    /// <summary>The code <see cref="IsolationNotSupported"/> raises.</summary>
    public const string IsolationNotSupportedCode = "tenant.isolation_not_supported";

    /// <summary>
    /// A deployment asked for an isolation level this runtime cannot enforce.
    /// </summary>
    /// <param name="isolation">The level nothing here implements.</param>
    /// <remarks>
    /// <para>
    /// <strong>It fails closed, and it fails at startup.</strong> The alternative — serving
    /// <see cref="TenantIsolation.Row"/> when a deployment asked for
    /// <see cref="TenantIsolation.Schema"/> — would give an operator a weaker guarantee than
    /// the one they configured while reporting success, which is precisely the "declared and
    /// inert" shape this whole work package exists to remove. Better a pod that never becomes
    /// ready than a pod that isolates less than its configuration claims.
    /// </para>
    /// <para>
    /// <see cref="ErrorCategory.Internal"/> rather than <see cref="ErrorCategory.Forbidden"/>:
    /// unlike the other two, no caller did anything wrong and no caller can repair it. It is a
    /// wiring defect in the deployment, and it is the same category
    /// <c>FlowErrors.DurabilityNotConfigured</c> uses for the same kind of mistake.
    /// </para>
    /// </remarks>
    public static Error IsolationNotSupported(TenantIsolation isolation) =>
        new Error(
            IsolationNotSupportedCode,
            $"TenantIsolation.{isolation} is declared and this runtime does not implement " +
            "it. Only None and Row are enforceable today (docs/16-Multi-Tenant.md §5); the " +
            "remaining levels need a per-tenant store resolver that no record has decided. " +
            "The level is refused rather than downgraded, because a deployment that asked " +
            "for more separation must not silently receive less.",
            ErrorCategory.Internal)
            .With("isolation", isolation.ToString());
}
