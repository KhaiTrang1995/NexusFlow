using System.Collections.Immutable;

namespace FlowX;

/// <summary>
/// The compile-time projection of a <c>[Capability]</c> declaration: everything the
/// plan, the engine and the manifest need to know about a capability without loading
/// its type.
/// </summary>
/// <remarks>
/// Deliberately holds no <see cref="Type"/> and no delegate. Keeping the descriptor
/// reflection-free is what lets the manifest be produced at build time and lets the
/// runtime stay NativeAOT-compatible (constraint C2).
/// </remarks>
public sealed record CapabilityDescriptor
{
    private CapabilityDescriptor(
        string id,
        string version,
        bool isIdempotent,
        ImmutableArray<string> sideEffects,
        Authorization? authorization,
        string? authorizationValue)
    {
        Id = id;
        Version = version;
        IsIdempotent = isIdempotent;
        SideEffects = sideEffects;
        Authorization = authorization;
        AuthorizationValue = authorizationValue;
    }

    /// <summary>Stable business identity, <c>&lt;domain&gt;.&lt;verb&gt;</c>.</summary>
    public string Id { get; }

    /// <summary>SemVer of the contract, not of the implementation.</summary>
    public string Version { get; }

    /// <summary>
    /// Whether invoking twice with the same idempotency key is safe. Gates whether a
    /// retry policy may be attached at all — see <see cref="PolicyChain"/>.
    /// </summary>
    public bool IsIdempotent { get; }

    /// <summary>Named external effects, in declaration order.</summary>
    public ImmutableArray<string> SideEffects { get; }

    /// <summary>
    /// Who may invoke it, or <c>null</c> when this descriptor was built without a stance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Nullable, and there is deliberately no default.</strong>
    /// <see cref="FlowX.Authorization.Public"/> is the zero value of its enum, so a
    /// non-nullable property would make "nobody said" and "anyone may invoke it" the same
    /// value — the permissive default <c>FLOWX1010</c>, <c>NoPermissiveDefaults</c> and
    /// <c>docs/15-Security.md §1</c> all exist to refuse.
    /// </para>
    /// <para>
    /// <c>null</c> is unreachable from compiled source: <c>FLOWX1010</c> is an error, so
    /// every capability the generator emits a descriptor for has declared one, and
    /// <c>FlowEmitter</c> emits it. It is reachable only from a descriptor built by hand — a
    /// test, a benchmark — and such a step is not gated rather than being given a stance
    /// nobody wrote.
    /// </para>
    /// <para>
    /// <strong>This is the same reading the manifest publishes.</strong> Both come from
    /// <c>CapabilityReader</c>, once, and travel through <c>StepModel</c> to
    /// <c>ManifestWriter</c> and to <c>FlowEmitter</c> — so the stance the runtime enforces
    /// and the stance <c>flowx diff</c>'s <c>FLOWX-DIFF-015</c> compares cannot disagree.
    /// </para>
    /// </remarks>
    public Authorization? Authorization { get; }

    /// <summary>
    /// The permission or policy the stance names, or <c>null</c> when it names nothing.
    /// </summary>
    /// <remarks>
    /// Carried only for the stance that reads it, exactly as
    /// <c>flowx.manifest.json</c>'s <c>authorization.value</c> is — a <c>Permission = "x"</c>
    /// written beside <see cref="FlowX.Authorization.Public"/> names nothing the stance
    /// consults, so it reaches neither the manifest nor this.
    /// </remarks>
    public string? AuthorizationValue { get; }

    /// <summary>True when this capability changes something outside the process.</summary>
    public bool HasSideEffects => !SideEffects.IsEmpty;

    /// <summary>Identity and version together, as they appear in the manifest and in traces.</summary>
    public string QualifiedName => $"{Id}@{Version}";

    /// <summary>Creates a validated descriptor.</summary>
    /// <param name="id">Business identity, <c>&lt;domain&gt;.&lt;verb&gt;</c>.</param>
    /// <param name="version">Semantic version of the contract.</param>
    /// <param name="isIdempotent">Whether a retry is safe.</param>
    /// <param name="sideEffects">Named external effects. Copied, never aliased.</param>
    /// <exception cref="ArgumentException">The identity or version is malformed.</exception>
    public static CapabilityDescriptor Create(
        string id,
        string version,
        bool isIdempotent,
        params string[] sideEffects)
    {
        ArgumentNullException.ThrowIfNull(sideEffects);

        return new CapabilityDescriptor(
            Identifiers.RequireIdentity(id, nameof(id)),
            Identifiers.RequireSemanticVersion(version, nameof(version)),
            isIdempotent,
            [.. sideEffects],
            authorization: null,
            authorizationValue: null);
    }

    /// <summary>Creates a validated descriptor carrying its declared authorisation stance.</summary>
    /// <param name="id">Business identity, <c>&lt;domain&gt;.&lt;verb&gt;</c>.</param>
    /// <param name="version">Semantic version of the contract.</param>
    /// <param name="isIdempotent">Whether a retry is safe.</param>
    /// <param name="authorization">The declared stance. There is no default; see FLOWX1010.</param>
    /// <param name="authorizationValue">
    /// The permission or policy the stance names. Required by
    /// <see cref="FlowX.Authorization.Permission"/> and <see cref="FlowX.Authorization.Policy"/>
    /// — see FLOWX1030 — and ignored by the three stances that name nothing.
    /// </param>
    /// <param name="sideEffects">Named external effects. Copied, never aliased.</param>
    /// <exception cref="ArgumentException">The identity or version is malformed.</exception>
    /// <exception cref="InvalidFlowPlanException">
    /// A stance that needs a name was given none. FLOWX1030 refuses this at build time; this
    /// is the same rule enforced a second time, at the second place, exactly as
    /// <c>PolicyChain.ForStep</c> re-enforces FLOWX1014 and FLOWX1018 — because a plan built
    /// by hand did not go through the analyzer.
    /// </exception>
    public static CapabilityDescriptor Create(
        string id,
        string version,
        bool isIdempotent,
        Authorization authorization,
        string? authorizationValue = null,
        params string[] sideEffects)
    {
        ArgumentNullException.ThrowIfNull(sideEffects);

        var named = string.IsNullOrWhiteSpace(authorizationValue) ? null : authorizationValue;

        if (StepAuthorization.NeedsAName(authorization) && named is null)
        {
            throw new InvalidFlowPlanException(
                $"Capability '{id}' declares Authorization.{authorization} and names no " +
                $"{authorization}. A stance that claims a named grant is required, naming " +
                "none, publishes a requirement nothing can be checked against — which is " +
                "FLOWX1030 at build time and this at plan construction.");
        }

        return new CapabilityDescriptor(
            Identifiers.RequireIdentity(id, nameof(id)),
            Identifiers.RequireSemanticVersion(version, nameof(version)),
            isIdempotent,
            [.. sideEffects],
            authorization,
            StepAuthorization.NeedsAName(authorization) ? named : null);
    }

    /// <inheritdoc />
    public override string ToString() => QualifiedName;
}
