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
    /// Not independently exposed: the capability is composed into flows and is never
    /// addressed on its own. It admits every caller at the step, because at the step there
    /// is no external caller to refuse.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>It is satisfied by the trigger model, not by a check.</strong> A trigger
    /// addresses a <em>flow</em> and never a capability — <c>[HttpTrigger]</c> and
    /// <c>[AgentTrigger]</c> are declared on a <c>Flow&lt;,&gt;</c>
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0004-universal-trigger-model.md">ADR-0004</a>)
    /// — so every capability invocation that exists is already reached from a flow's step
    /// loop. There is nothing for the Trigger Engine to reject and nothing for the compiler
    /// to strip out, and <c>NoTriggerAttributeAddressesACapability</c> is what keeps that
    /// true rather than this sentence.
    /// </para>
    /// <para>
    /// <strong>It does not hide the capability from a caller, and must not be read as
    /// doing so.</strong> A flow may be triggered over HTTP, off a broker, on a schedule or
    /// by an agent, and its <c>Internal</c> steps run whoever started it —
    /// <c>samples/workflow/OnboardEmployeeFlow</c> is HTTP-triggered and steps through
    /// three of them. What is exposed is the flow; what guards it is the trigger it carries
    /// and the stances of its own steps. Marking a capability <c>Internal</c> to keep it
    /// away from an agent does nothing at all — publish a different set of flows instead.
    /// </para>
    /// <para>
    /// <strong>When to choose it.</strong> When there is no principal to check and saying
    /// otherwise would be a lie: a step reached only by composition, or one started by a
    /// broker delivery, which carries no caller. <see cref="Authenticated"/> or
    /// <see cref="Permission"/> on such a step refuses every message.
    /// </para>
    /// </remarks>
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
