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
        ImmutableArray<string> sideEffects)
    {
        Id = id;
        Version = version;
        IsIdempotent = isIdempotent;
        SideEffects = sideEffects;
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
            [.. sideEffects]);
    }

    /// <inheritdoc />
    public override string ToString() => QualifiedName;
}
