namespace FlowX.Runtime;

/// <summary>
/// What a tenant resolver decided: a tenant, a refusal, or neither.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Three outcomes, not two, and the third is the reason this type exists.</strong> A
/// resolver returning <c>string?</c> collapses "this deployment does not isolate by tenant"
/// into "this call named no tenant", and those must not be the same value: the first is the
/// ordinary state of every single-tenant deployment ever run, and the second is the defect the
/// whole feature exists to refuse. A caller that cannot tell them apart has to guess, and the
/// convenient guess — treat a null tenant as "carry on" — is the cross-tenant read.
/// </para>
/// <para>
/// So <see cref="Refused"/> carries the refusal, <see cref="NotConfigured"/> carries the
/// absence, and both have a <c>null</c> <see cref="TenantId"/> while meaning opposite things.
/// <c>docs/25 §1</c> puts it in one line: "<em>a null tenant on a multi-tenant deployment is
/// the bug this feature exists to prevent, so the resolver has to be able to say no and be
/// distinguishable from not configured</em>".
/// </para>
/// <para>
/// A readonly record struct: resolving a tenant allocates nothing, so a deployment that
/// isolates pays a field copy per invocation and one that does not pays a comparison.
/// </para>
/// </remarks>
public readonly record struct TenantResolution
{
    private readonly string? _reason;

    private TenantResolution(string? tenantId, bool refused, string reason)
    {
        TenantId = tenantId;
        Refused = refused;
        _reason = reason;
    }

    /// <summary>The resolved tenant, or <c>null</c> when there is none to resolve.</summary>
    /// <remarks>
    /// Null on both a refusal and a not-configured answer. Reading it without first reading
    /// <see cref="Refused"/> is the mistake this type is shaped to make visible rather than
    /// impossible — a struct cannot forbid it — which is why <see cref="Reason"/> is never
    /// empty on a refusal and always empty otherwise.
    /// </remarks>
    public string? TenantId { get; }

    /// <summary>Whether the call is refused rather than admitted.</summary>
    public bool Refused { get; }

    /// <summary>
    /// Why it was refused, in a sentence an operator can act on. Empty when it was not.
    /// </summary>
    /// <remarks>
    /// Computed over a nullable field rather than being an auto-property, so that
    /// <see cref="NotConfigured"/> — which is <c>default</c>, and is reached by every
    /// single-tenant deployment on every call — reads as empty rather than as null. A
    /// contract that says "empty when it was not" and hands back a null on its most common
    /// value is a contract that gets a <c>NullReferenceException</c> written against it.
    /// </remarks>
    public string Reason => _reason ?? string.Empty;

    /// <summary>
    /// The deployment does not isolate by tenant, so there is nothing to resolve and nothing
    /// to refuse.
    /// </summary>
    /// <remarks>
    /// The default value on purpose. A single-tenant deployment's answer is the one that costs
    /// nothing to produce, and <c>default(TenantResolution)</c> being the safe answer rather
    /// than an ambiguous one is worth having.
    /// </remarks>
    public static TenantResolution NotConfigured => default;

    /// <summary>The call is admitted, under this tenant.</summary>
    /// <param name="tenantId">The tenant, derived from validated claims.</param>
    /// <returns>The resolution.</returns>
    /// <exception cref="ArgumentException"><paramref name="tenantId"/> is null or blank.</exception>
    /// <remarks>
    /// A blank tenant is refused at construction rather than carried. It would reach the store
    /// as the empty string, which migration <c>0006</c>'s policies fold together with "no
    /// tenant" — so a resolver that produced one would silently scope an isolating deployment
    /// to its untenanted rows.
    /// </remarks>
    public static TenantResolution Tenant(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return new TenantResolution(tenantId, refused: false, string.Empty);
    }

    /// <summary>The call is refused, and does not reach the engine.</summary>
    /// <param name="reason">Why, for the error and for the operator.</param>
    /// <returns>The resolution.</returns>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is null or blank.</exception>
    public static TenantResolution Refuse(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new TenantResolution(null, refused: true, reason);
    }
}
