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

    /// <summary>
    /// Permission name when <see cref="Authorization"/> is
    /// <see cref="FlowX.Authorization.Permission"/>. Mandatory there — omitting it fails
    /// the build (FLOWX1030).
    /// </summary>
    /// <remarks>
    /// Reaches <c>flowx.manifest.json</c> as <c>authorization.value</c>, which is what
    /// <c>flowx diff</c>'s <c>FLOWX-DIFF-015</c> compares: changing this name is a
    /// breaking change even though no signature moves, because callers holding the
    /// previous grant are now denied. A name declared beside a stance that does not read
    /// it — <c>Permission</c> under <see cref="FlowX.Authorization.Public"/>, say — is
    /// not published, because it names nothing the stance consults.
    /// </remarks>
    public string? Permission { get; init; }

    /// <summary>
    /// Policy name when <see cref="Authorization"/> is
    /// <see cref="FlowX.Authorization.Policy"/>. Mandatory there — omitting it fails the
    /// build (FLOWX1030).
    /// </summary>
    /// <remarks>Published and compared exactly as <see cref="Permission"/> is.</remarks>
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
/// Marks a contract member as sensitive.
/// </summary>
/// <remarks>
/// <para>
/// The compiler reads the attribute and does two things with it. It lists the member
/// under the contract's <c>sensitive</c> array in <c>flowx.manifest.json</c>, so a
/// reviewer, <c>flowx diff</c> or an agent can see which fields carry secrets. And it
/// emits the names onto the flow's partial class as <c>SensitiveMembers</c>, which the
/// HTTP endpoint uses to strip matching structured detail out of an error response.
/// </para>
/// <para>
/// <strong>That is one path, not every path.</strong> A Problem Details body is the only
/// thing this release serialises that a capability can attach a value to. There is no
/// logging scope, no journal and no replay view yet, so there is nothing else to redact
/// from — and equally, nothing stops your own code from writing the value somewhere the
/// platform does not see.
/// </para>
/// <para>
/// This comment previously claimed redaction was applied by the generated serialiser
/// "with no code path able to bypass it", while nothing read the attribute at all. An
/// attribute that reads as a control while doing nothing is worse than no attribute: a
/// reviewer sees the field marked and concludes it is handled. The full intent is
/// <c>docs/12-Observability.md §4</c>.
/// </para>
/// </remarks>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter,
    AllowMultiple = false,
    Inherited = false)]
public sealed class SensitiveAttribute : Attribute;
