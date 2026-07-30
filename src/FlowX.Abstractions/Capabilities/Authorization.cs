namespace FlowX;

/// <summary>
/// The authorisation stance of a capability. Attached to the business operation
/// rather than to a URL, so it survives a transport change — a capability requiring
/// a permission requires it over HTTP, over Kafka, and from an AI agent alike
/// (principle P11).
/// </summary>
/// <remarks>
/// There is no default. A capability without a declared stance fails the build with
/// FLOWX1010. This is friction on day one, and it is the correct friction:
/// <see cref="Public"/> is an explicit, greppable, reviewable statement.
/// </remarks>
public enum Authorization
{
    /// <summary>
    /// Anyone may invoke it. Requires an <see cref="ApprovedByAttribute"/> naming the
    /// reviewer — asserted by the <c>PublicCapabilitiesAreReviewed</c> fitness function.
    /// </summary>
    Public = 0,

    /// <summary>Any authenticated principal may invoke it.</summary>
    Authenticated = 1,

    /// <summary>A named permission is required; see <see cref="CapabilityAttribute.Permission"/>.</summary>
    Permission = 2,

    /// <summary>A named authorisation policy must be satisfied; see <see cref="CapabilityAttribute.Policy"/>.</summary>
    Policy = 3,

    /// <summary>
    /// Reachable only from another flow. The Trigger Engine rejects it at admission and
    /// the compiler excludes it from the generated agent tool surface.
    /// </summary>
    Internal = 4,
}

/// <summary>
/// Records who reviewed a deliberately permissive declaration. Required on any
/// capability declaring <see cref="Authorization.Public"/>.
/// </summary>
/// <param name="reviewer">Team or individual accountable for the decision.</param>
/// <param name="date">ISO-8601 date of the review.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ApprovedByAttribute(string reviewer, string date) : Attribute
{
    /// <summary>Team or individual accountable for the decision.</summary>
    public string Reviewer { get; } = reviewer;

    /// <summary>ISO-8601 date of the review.</summary>
    public string Date { get; } = date;

    /// <summary>Optional rationale, surfaced in the manifest and in access reviews.</summary>
    public string? Rationale { get; init; }
}
