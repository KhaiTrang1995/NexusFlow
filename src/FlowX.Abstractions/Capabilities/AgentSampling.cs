namespace FlowX;

/// <summary>What a capability wants a model to write.</summary>
/// <param name="SystemPrompt">The instruction that frames the task.</param>
/// <param name="UserMessage">The material to work from.</param>
/// <param name="MaxTokens">A ceiling on the answer.</param>
/// <remarks>
/// Two strings and a number, and the narrowness is the point: whatever a capability puts in these
/// members is the whole of what leaves the process, so docs/13 §5's "AI runs on the manifest, not
/// on data" is a property of the call site rather than an operating rule. A request type that
/// carried a service provider, a store handle or an open-ended payload would move that guarantee
/// back into a review checklist.
/// </remarks>
public sealed record AgentSamplingRequest(string SystemPrompt, string UserMessage, int MaxTokens);

/// <summary>What the caller's model said, or why it said nothing.</summary>
/// <remarks>
/// <para>
/// Three states rather than two. <see cref="IsAvailable"/> is false when the caller has no model
/// at all — an HTTP request, a broker delivery, a cron fire, or an agent whose client does not
/// offer one — which is the ordinary case and not an error. <see cref="Error"/> is set only when
/// a model was reachable and did not produce usable text.
/// </para>
/// <para>
/// A capability that could not tell those apart would have to choose between failing for every
/// non-agent caller and swallowing a broken model silently, and both are wrong.
/// </para>
/// </remarks>
public readonly struct AgentSample : IEquatable<AgentSample>
{
    private AgentSample(string? text, Error? error, bool available)
    {
        Text = text;
        Error = error;
        IsAvailable = available;
    }

    /// <summary>What the model wrote, when it wrote something.</summary>
    public string? Text { get; }

    /// <summary>Why there is no text, when the caller had a model and it did not answer.</summary>
    public Error? Error { get; }

    /// <summary>Whether a model was reachable at all.</summary>
    public bool IsAvailable { get; }

    /// <summary>The model answered.</summary>
    /// <param name="text">What it wrote.</param>
    public static AgentSample Written(string text) => new(text, error: null, available: true);

    /// <summary>The caller has no model this process can ask.</summary>
    public static AgentSample Unavailable() => new(text: null, error: null, available: false);

    /// <summary>A model was reachable and produced no usable text.</summary>
    /// <param name="error">Why.</param>
    public static AgentSample Failed(Error error) => new(text: null, error, available: true);

    /// <inheritdoc />
    public bool Equals(AgentSample other) =>
        string.Equals(Text, other.Text, StringComparison.Ordinal) &&
        Equals(Error, other.Error) &&
        IsAvailable == other.IsAvailable;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AgentSample other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Text, Error, IsAvailable);

    /// <summary>Value equality.</summary>
    public static bool operator ==(AgentSample left, AgentSample right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(AgentSample left, AgentSample right) => !left.Equals(right);
}

/// <summary>
/// A model, borrowed from whoever called — never one this deployment holds a key for.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the abstraction docs/13 §5's <c>IAiProvider</c> turned out not to need</strong>
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0060-the-server-asks-the-caller-for-what-it-does-not-have.md">ADR-0060</a>).
/// A provider abstraction means a vendor SDK, an API key in the deployment, an egress rule and a
/// dependency row per provider — all to reach a model that the agent on the other end of the
/// connection already has. MCP's <c>sampling/createMessage</c> asks that one, so the platform's
/// entire model integration is this interface and the transport that implements it.
/// </para>
/// <para>
/// <strong>It lives here, in <c>FlowX.Abstractions</c>, because a capability may not reference a
/// transport (FLOWX1003) and this is the one thing a capability asks its <em>caller</em> for.</strong>
/// The implementation is <c>FlowX.Mcp.McpCallScope</c>, registered per request and closed over the
/// response stream of the <c>tools/call</c> being served — the same split as <c>IFlowJournal</c>
/// and its PostgreSQL adapter, and for the same reason.
/// </para>
/// <para>
/// <strong>Nothing here throws and there is no "no sampler" case to guard.</strong> A host with no
/// agent transport registers nothing and a capability that asks for one gets a resolution failure
/// at start-up, which is where a missing registration should be found; a host that has one and is
/// reached by an HTTP request gets <see cref="AgentSample.Unavailable"/>, which is a value. A flow
/// that is only usable when an agent called it is a flow with two behaviours, and the honest one
/// is the one that still answers.
/// </para>
/// </remarks>
public interface IAgentSampler
{
    /// <summary>Asks the caller's model.</summary>
    /// <param name="request">What to write, and how much of it.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    ValueTask<AgentSample> SampleAsync(AgentSamplingRequest request, CancellationToken ct);
}
