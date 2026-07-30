namespace FlowX;

/// <summary>
/// Declares a class as a capability and carries its contract metadata. Everything on
/// this attribute reaches <c>flowx.manifest.json</c>, so it is a published contract,
/// not documentation (ADR-0005).
/// </summary>
/// <param name="id">
/// Stable business identity in <c>&lt;domain&gt;.&lt;verb&gt;</c> form, e.g.
/// <c>inventory.reserve</c>. Used in traces, policy targeting, agent tool names and
/// impact analysis. Renaming it is a breaking change.
/// </param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CapabilityAttribute(string id) : Attribute
{
    /// <summary>Stable business identity, <c>&lt;domain&gt;.&lt;verb&gt;</c>.</summary>
    public string Id { get; } = id;

    /// <summary>
    /// SemVer of the <em>contract</em>, not of the implementation. Refactoring the body
    /// is not a version change; changing what it accepts or returns is.
    /// <c>flowx diff</c> fails the build on an unversioned breaking change.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>Who may invoke it. Mandatory — omitting it fails the build (FLOWX1010).</summary>
    public required Authorization Authorization { get; init; }

    /// <summary>Permission name when <see cref="Authorization"/> is <see cref="FlowX.Authorization.Permission"/>.</summary>
    public string? Permission { get; init; }

    /// <summary>Policy name when <see cref="Authorization"/> is <see cref="FlowX.Authorization.Policy"/>.</summary>
    public string? Policy { get; init; }

    /// <summary>
    /// True when invoking twice with the same <see cref="CapabilityContext.IdempotencyKey"/>
    /// is safe. <strong>This gates whether a retry policy is even allowed</strong> — a
    /// retry on a non-idempotent capability is a build error (FLOWX1014), because
    /// retrying a payment capture is a duplicate charge.
    /// </summary>
    public bool Idempotent { get; init; }

    /// <summary>
    /// Named external effects, e.g. <c>["payment-gateway", "payment-ledger"]</c>.
    /// Drives blast-radius analysis, agent confirmation prompts (so they state real
    /// consequences), and the <c>Cache</c> policy guard — caching a capability with
    /// side effects is a build error (FLOWX1018).
    /// </summary>
    public string[] SideEffects { get; init; } = [];

    /// <summary>Replacement id and removal date when this version is being retired.</summary>
    public string? Deprecated { get; init; }
}

/// <summary>
/// Marks a contract member as sensitive. Redaction is applied by the generated
/// serialiser, so it reaches logs, traces, the journal and replay output with no code
/// path able to bypass it (docs/12-Observability.md §4).
/// </summary>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter,
    AllowMultiple = false,
    Inherited = false)]
public sealed class SensitiveAttribute : Attribute;
