namespace FlowX.Ai;

/// <summary>How much a finding wants from the reader.</summary>
/// <remarks>
/// Two values and not three. There is no <c>Error</c>, because nothing here stops anything: a
/// review is a report, and docs/13 §5's boundary table says the AI layer's output is "a pull
/// request or a report, never an auto-applied change". A severity that implied a build failure
/// would be the first step towards being one.
/// </remarks>
public enum FindingSeverity
{
    /// <summary>Worth knowing. Not a defect.</summary>
    Information = 0,

    /// <summary>A defect, or a shape that becomes one under a failure the document describes.</summary>
    Warning = 1,
}

/// <summary>One thing the manifest says about itself.</summary>
/// <param name="Severity">How much it wants from the reader.</param>
/// <param name="Code">
/// A stable identifier, so a finding can be suppressed, counted or tracked between builds
/// without matching on prose.
/// </param>
/// <param name="Subject">
/// What the finding is about — a flow id, a capability id, an event type. The address a reader
/// goes to, and the term a <c>flowx graph</c> node is named by.
/// </param>
/// <param name="Message">What is wrong, and what it costs.</param>
/// <remarks>
/// Deliberately carries no file and no line. <c>flow.source</c> exists in the manifest and
/// <c>capability.source</c> does not (ADR-0017's unmet condition), so half the findings could
/// cite a location and half could not — and a report where the presence of a line number depends
/// on which node kind a finding is about reads as though the missing ones were less real.
/// </remarks>
public sealed record FlowFinding(FindingSeverity Severity, string Code, string Subject, string Message)
{
    /// <summary>An event nothing consumes, or one nothing produces.</summary>
    public const string OrphanEvent = "ai.event_has_one_end";

    /// <summary>A capability with declared side effects and no authorisation stance.</summary>
    public const string PublicSideEffect = "ai.public_capability_has_side_effects";

    /// <summary>An agent tool that has declared consequences and asks nobody.</summary>
    public const string UnconfirmedAgentTool = "ai.agent_tool_declares_no_confirmation";

    /// <summary>An agent tool whose arguments carry a member marked <c>[Sensitive]</c>.</summary>
    public const string AgentToolTakesSecret = "ai.agent_tool_takes_a_sensitive_argument";

    /// <summary>A flow that compensates some effectful steps and not others.</summary>
    public const string PartialSaga = "ai.saga_compensates_partially";
}
