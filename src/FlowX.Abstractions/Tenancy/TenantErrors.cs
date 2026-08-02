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

    /// <summary>The code <see cref="ResidencyRefused"/> raises.</summary>
    public const string ResidencyRefusedCode = "tenant.residency_refused";

    /// <summary>
    /// The tenant's data may not be processed in the region this deployment runs in.
    /// </summary>
    /// <param name="tenantId">The resolved tenant.</param>
    /// <param name="required">The region the tenant is pinned to.</param>
    /// <param name="actual">The region this deployment declares, or null when it declares none.</param>
    /// <remarks>
    /// <para>
    /// <strong>Refused rather than forwarded.</strong> Proxying the call to the right region
    /// would be the platform routing personal data across a boundary a customer asked it not to
    /// cross, on the strength of a configuration entry — the one action a residency control
    /// must not take on its own. The caller is told which region to address and does the
    /// addressing.
    /// </para>
    /// <para>
    /// <strong>The required region is named, and that is a deliberate disclosure.</strong> It
    /// is the caller's own tenant's configuration, the caller already holds a token for that
    /// tenant, and an error that withheld it would leave a client with a retry loop against an
    /// endpoint that will never answer. This is not <see cref="CrossTenantDenied"/>, which
    /// withholds because naming the other side would be an oracle over somebody else's data.
    /// </para>
    /// <para>
    /// <see cref="ErrorCategory.Forbidden"/>: terminal, not transient. Waiting does not move
    /// the pod.
    /// </para>
    /// </remarks>
    public static Error ResidencyRefused(string tenantId, string required, string? actual) =>
        new Error(
            ResidencyRefusedCode,
            $"Tenant '{tenantId}' is pinned to region '{required}' and this deployment runs in " +
            $"'{actual ?? "(none declared)"}'. The call was refused rather than forwarded: " +
            "moving the request would be the platform carrying the data across the boundary " +
            "the pin exists to hold. Address the deployment in the required region.",
            ErrorCategory.Forbidden)
            .With("tenantId", tenantId)
            .With("requiredRegion", required)
            .With("region", actual ?? string.Empty);

    /// <summary>The code <see cref="RateLimited"/> raises.</summary>
    public const string RateLimitedCode = "tenant.rate_limited";

    /// <summary>
    /// The tenant has spent its permits for this window, so the call was refused at admission.
    /// </summary>
    /// <param name="tenantId">Whose budget is spent.</param>
    /// <param name="retryAfter">How long until a permit accrues, as the store reported it.</param>
    /// <remarks>
    /// <para>
    /// <see cref="ErrorCategory.Unavailable"/> rather than <see cref="ErrorCategory.Forbidden"/>,
    /// which is what separates this from every other refusal in this class. The other three are
    /// terminal — asking again reaches the same absent claim — and this one is the opposite: the
    /// caller is entitled to the call and is simply not getting it right now, so waiting is the
    /// correct response and the category is the half of the error that says so. It is
    /// <c>docs/16 §4</c>'s "429 + Retry-After", and it shares
    /// <c>FlowErrors.RateLimited</c>'s category for exactly the same reason.
    /// </para>
    /// <para>
    /// <strong>Refused before anything was allocated.</strong> §4 requires every limit at stage 1
    /// — "<em>before authentication, before any allocation, before any journal write</em>" — on
    /// the grounds that "<em>rejecting expensively is how rate limiting becomes the DoS</em>". A
    /// tenant refused here has cost the platform one store round trip and no lease, no instance
    /// row and no step.
    /// </para>
    /// </remarks>
    public static Error RateLimited(string tenantId, TimeSpan retryAfter) =>
        new Error(
            RateLimitedCode,
            $"Tenant '{tenantId}' has no rate-limit permit left. Retry after {retryAfter}. The " +
            "call was refused at admission, before a flow instance existed.",
            ErrorCategory.Unavailable)
            .With("tenantId", tenantId)
            .With("retryAfter", retryAfter);

    /// <summary>The code <see cref="QuotaExhausted"/> raises.</summary>
    public const string QuotaExhaustedCode = "tenant.quota_exhausted";

    /// <summary>
    /// The tenant has spent its budget for the long window, so the call was refused at admission.
    /// </summary>
    /// <param name="tenantId">Whose budget is spent.</param>
    /// <param name="retryAfter">How long until budget accrues, as the store reported it.</param>
    /// <remarks>
    /// <strong>A separate code from <see cref="RateLimited"/>, and the separation is the point of
    /// having both.</strong> The two are the same token bucket over the same store under
    /// different keys, so an implementation could reasonably have folded them into one refusal —
    /// but the repairs are not the same and never will be. A rate limit says "you are sending
    /// faster than your plan smooths"; a quota says "you have used the plan". The first is fixed
    /// by backing off and the second by buying more, and an operator reading one code for both
    /// has to guess which.
    /// </remarks>
    public static Error QuotaExhausted(string tenantId, TimeSpan retryAfter) =>
        new Error(
            QuotaExhaustedCode,
            $"Tenant '{tenantId}' has exhausted its quota for the current window. Budget " +
            $"accrues again in {retryAfter}. This is a plan limit rather than a burst: backing " +
            "off reaches the same answer until the window turns over.",
            ErrorCategory.Unavailable)
            .With("tenantId", tenantId)
            .With("retryAfter", retryAfter);

    /// <summary>The code <see cref="Saturated"/> raises.</summary>
    public const string SaturatedCode = "tenant.saturated";

    /// <summary>
    /// The tenant already has as many flows executing on this node as its bulkhead allows.
    /// </summary>
    /// <param name="tenantId">Whose pool is full.</param>
    /// <param name="maxConcurrency">The bound, per node.</param>
    /// <remarks>
    /// <strong>Shed, not queued</strong> — <c>docs/16 §4</c>'s "429 · shed early, do not queue".
    /// A caller waiting for a bulkhead slot at admission is a thread and a socket held open for
    /// work the platform has already decided it is not going to do, and a queue in front of a
    /// full pool is how a saturated node becomes an unresponsive one.
    /// </remarks>
    public static Error Saturated(string tenantId, int maxConcurrency) =>
        new Error(
            SaturatedCode,
            $"Tenant '{tenantId}' already has {maxConcurrency} flow(s) executing on this node, " +
            "which is its bulkhead. The call was shed rather than queued.",
            ErrorCategory.Unavailable)
            .With("tenantId", tenantId)
            .With("maxConcurrency", maxConcurrency);

    /// <summary>The code <see cref="FairnessUnavailable"/> raises.</summary>
    public const string FairnessUnavailableCode = "tenant.fairness_unavailable";

    /// <summary>
    /// A per-tenant budget is declared and the limiter could not decide.
    /// </summary>
    /// <param name="tenantId">The tenant whose budget could not be consulted.</param>
    /// <param name="cause">What the store reported.</param>
    /// <remarks>
    /// A refusal, for
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0040-a-rate-limit-is-shared-or-it-is-not-a-rate-limit.md">ADR-0040</a>'s
    /// reason and <c>FlowErrors.RateLimiterUnavailable</c>'s: a limiter that cannot reach its
    /// server does not know whether this tenant is inside its budget, and admitting on doubt
    /// turns an outage of the limiter into the unbounded flood it was bounding. The startup
    /// validator refuses the missing-store case outright, so what reaches here is the store that
    /// was registered and then stopped answering.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="cause"/> is null.</exception>
    public static Error FairnessUnavailable(string tenantId, Error cause)
    {
        ArgumentNullException.ThrowIfNull(cause);

        return new Error(
            FairnessUnavailableCode,
            $"Tenant '{tenantId}' declares a per-tenant budget and its limiter did not answer: " +
            $"{cause.Message} The call was refused rather than admitted.",
            ErrorCategory.Unavailable)
            .With("tenantId", tenantId);
    }

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
            $"TenantIsolation.{isolation} is declared and this runtime does not implement it. " +
            "None, Row and Schema are enforceable; Database is not, and the reason is that it " +
            "is not a runtime mode at all. It is docs/16 §2's L3/L4 — a dedicated deployment " +
            "per tenant — so the pod serving one tenant's database declares None and points " +
            "its connection string at that tenant's store, and there is no second tenant in " +
            "the process to be kept apart from. Inside a shared process, a database per tenant " +
            "is L2 and TenantIsolation.Schema already serves it. The level is refused rather " +
            "than downgraded, because a deployment that asked for more separation must not " +
            "silently receive less.",
            ErrorCategory.Internal)
            .With("isolation", isolation.ToString());

    /// <summary>The code <see cref="IsolationNotEnforceable"/> raises.</summary>
    public const string IsolationNotEnforceableCode = "tenant.isolation_not_enforceable";

    /// <summary>
    /// The runtime implements the declared level and the store behind this host does not.
    /// </summary>
    /// <param name="declared">The level the deployment configured.</param>
    /// <param name="enforced">The strongest level the journal reports it can serve.</param>
    /// <remarks>
    /// <para>
    /// <strong>Distinct from <see cref="IsolationNotSupported"/> because the repairs are
    /// different.</strong> That one means "no build of FlowX does this"; this one means "this
    /// one does, and the store you wired underneath it does not" — repaired by a registration,
    /// typically <c>PostgresJournalOptions.TenantSchemas</c>, rather than by choosing a
    /// different level.
    /// </para>
    /// <para>
    /// <strong><see cref="TenantIsolation.Row"/> over a store that cannot scope is deliberately
    /// not this error.</strong> That deployment still gets admission-time refusal and is told
    /// what it is missing by <c>FlowDurability.CanIsolateTenants</c>; ADR-0046 §3 accepted it in
    /// as many words. What is refused here is a store that isolates less <em>than the level
    /// names</em> — Schema served by row filtering — because that one has no honest reading:
    /// every tenant's rows would sit in one schema while the configuration said each had its
    /// own.
    /// </para>
    /// </remarks>
    public static Error IsolationNotEnforceable(TenantIsolation declared, TenantIsolation enforced) =>
        new Error(
            IsolationNotEnforceableCode,
            $"TenantIsolation.{declared} is declared and the journal behind this host enforces " +
            $"only TenantIsolation.{enforced}. The level is not downgraded: a deployment " +
            "running schema-per-tenant against a store that keeps every tenant in one schema " +
            "would be told it had separation it does not have. Wire a store that serves the " +
            "declared level — on PostgreSQL that is PostgresJournalOptions.TenantSchemas — or " +
            "declare the level the store enforces.",
            ErrorCategory.Internal)
            .With("declared", declared.ToString())
            .With("enforced", enforced.ToString());
}
