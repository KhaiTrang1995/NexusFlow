namespace FlowX.Mcp;

/// <summary>
/// What this deployment does about a tool whose descriptor says a human has to confirm it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two answers, and the deployment is the only thing that knows which is right</strong>
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0060-the-server-asks-the-caller-for-what-it-does-not-have.md">ADR-0060</a>).
/// <c>confirmationRequired</c> is computed from the flow's declared mode and its declared side
/// effects and is published on every descriptor regardless; what this setting decides is whether
/// the <em>server</em> refuses to run the flow until it has an answer, or leaves the asking to
/// the client that has the human attached to it.
/// </para>
/// <para>
/// It is not an authorisation setting and cannot be read as one. Every capability's stance is
/// decided in the step loop against the caller's own claims (ADR-0027) on both values, and an
/// approval grants nothing: a caller who approves a refund it holds no permission for is refused
/// at the step exactly as it would have been without being asked.
/// </para>
/// </remarks>
public enum ConfirmationPolicy
{
    /// <summary>
    /// Publish <c>confirmationRequired</c> and run the tool. The client prompts, or does not.
    /// </summary>
    /// <remarks>
    /// The default, because it is what every MCP client already expects and because a server
    /// that elicits from a client which cannot elicit has broken a tool that used to work. It
    /// is the correct answer wherever the client is the trusted half — a desktop agent driven
    /// by the same person the prompt would have gone to.
    /// </remarks>
    Annotate = 0,

    /// <summary>
    /// Ask the client for a decision over <c>elicitation/create</c> and run nothing until it
    /// arrives.
    /// </summary>
    /// <remarks>
    /// The answer for a deployment serving an agent it does not control. A declined, cancelled,
    /// timed-out or unanswerable elicitation is a refusal, and the flow is never entered — so
    /// "do it without asking the user" is refused by this process rather than by the model's
    /// cooperation, which is the property samples/ai-agent claims.
    /// </remarks>
    Elicit = 1,
}

/// <summary>
/// What the agent surface is allowed to do beyond answering, per deployment.
/// </summary>
/// <remarks>
/// Every member here is a property of the <em>deployment</em>, never of a flow. Anything that
/// varies per flow — its description, its declared consequences, whether a human is wanted — is
/// declared on the flow and reaches this surface through <c>flowx.manifest.json</c>, because a
/// second place to say it is a second thing to keep true.
/// </remarks>
public sealed class McpOptions
{
    /// <summary>The default this surface has served since it shipped.</summary>
    public static McpOptions Default { get; } = new McpOptions();

    /// <summary>Whether the server itself demands a confirmation before running a tool.</summary>
    public ConfirmationPolicy Confirmation { get; init; } = ConfirmationPolicy.Annotate;

    /// <summary>
    /// How long a server-initiated request waits for the client's answer before it is a refusal.
    /// </summary>
    /// <remarks>
    /// A bound rather than the caller's own token, because the two measure different things: the
    /// caller's token expires when the agent gives up, and this expires when the <em>human</em>
    /// has not answered. Without it a client that opens a stream and never replies holds a
    /// request, a scope and a connection until the deadline of a flow that has not started.
    /// </remarks>
    public TimeSpan ClientRequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
