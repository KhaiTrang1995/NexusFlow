namespace FlowX.Mcp;

/// <summary>
/// The refusals the agent surface itself produces, before a flow is entered.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Values, not exceptions</strong>
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0007-result-over-exceptions.md">ADR-0007</a>).
/// An agent that names a tool nobody publishes, or hands one an argument document its
/// contract cannot accept, has asked a question this surface can answer. Thrown, the answer
/// would be a stack trace on the wire and a 500 in the client's transcript — and a model
/// cannot repair either.
/// </para>
/// <para>
/// <strong>Nothing here is an authorisation error, and nothing here ever will be.</strong>
/// Once the argument document has become the flow's input contract, every subsequent
/// refusal — including authorisation — is produced by the step loop and reaches the agent
/// as the flow's own <see cref="Error"/>. A second authorisation error would be a second
/// authorisation path, which is what docs/25-Remaining-Platform.md §3 rules out.
/// </para>
/// <para>
/// <strong>The confirmation refusals are the exception that proves it.</strong> They are
/// raised here, before the flow, and they are <em>not</em> authorisation: a confirmation
/// asks whether the thing should happen and authorisation asks whether this caller may make
/// it happen, and the second is still decided in the step loop after the first is answered.
/// A caller who approves a refund it holds no grant for is refused at
/// <c>payment.refund</c> exactly as it would have been unasked
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0060-the-server-asks-the-caller-for-what-it-does-not-have.md">ADR-0060</a>).
/// </para>
/// </remarks>
public static class McpErrors
{
    /// <summary>The code every refusal to run an unconfirmed tool carries.</summary>
    /// <remarks>
    /// <para>
    /// One code for four outcomes — declined, cancelled, unanswered, and a client that cannot be
    /// asked — because they are one fact to the agent: <em>the flow did not run and no retry of
    /// this call will change that without a human</em>. The four are distinguished in
    /// <c>detail.outcome</c>, which is where a client that wants to say something different
    /// about each reads it.
    /// </para>
    /// <para>
    /// <see cref="ErrorCategory.Forbidden"/> rather than <see cref="ErrorCategory.Unavailable"/>,
    /// including for the client that cannot be asked. <c>Unavailable</c> is retryable
    /// (<c>ErrorCategoryExtensions.IsTerminal</c>) and would invite an agent to hammer a call a
    /// human has just refused, or that this client will never be able to answer.
    /// </para>
    /// </remarks>
    public const string ConfirmationRefusedCode = "mcp.confirmation_refused";

    /// <summary>The tool required a confirmation this call did not get.</summary>
    /// <param name="name">The tool that was called.</param>
    /// <param name="outcome">Which of the four ways the confirmation failed to arrive.</param>
    /// <param name="detail">What to tell the agent about that outcome.</param>
    public static Error ConfirmationRefused(string name, string outcome, string detail) =>
        new Error(
            ConfirmationRefusedCode,
            $"'{name}' declares consequences that require a human's confirmation, and {detail} " +
            "The flow was not entered.",
            ErrorCategory.Forbidden)
            .With("tool", name)
            .With("outcome", outcome);

    /// <summary>The code a failed <c>sampling/createMessage</c> carries.</summary>
    /// <remarks>
    /// Never reached by a caller with no model — that is
    /// <see cref="AgentSample.Unavailable"/>, which is not an error — so this names only a
    /// client that has one and did not answer with usable text.
    /// </remarks>
    public const string SamplingFailedCode = "mcp.sampling_failed";

    /// <summary>The caller's model was asked and produced nothing usable.</summary>
    /// <param name="reason">What went wrong.</param>
    public static Error SamplingFailed(string reason) =>
        new Error(
            SamplingFailedCode,
            $"The caller's model was asked to write a completion and {reason}.",
            ErrorCategory.Unavailable)
            .With("reason", reason);

    /// <summary>The code <see cref="UnknownResource"/> raises.</summary>
    public const string UnknownResourceCode = "mcp.unknown_resource";

    /// <summary>An agent read a resource URI this application does not publish.</summary>
    /// <param name="uri">The address that was read.</param>
    public static Error UnknownResource(string uri) =>
        new Error(
            UnknownResourceCode,
            $"No resource is published at '{uri}'. Every resource this application serves is a " +
            "slice of flowx.manifest.json addressed under the 'flowx' scheme; call " +
            "resources/list for the set.",
            ErrorCategory.NotFound)
            .With("uri", uri);

    /// <summary>The code <see cref="UnknownTool"/> raises.</summary>
    public const string UnknownToolCode = "mcp.unknown_tool";

    /// <summary>An agent called a tool name this application does not publish.</summary>
    /// <param name="name">The name that was called.</param>
    /// <remarks>
    /// <see cref="ErrorCategory.NotFound"/>: the tool is not there, which is a fact about
    /// the request rather than a fault in the server. The published names are not listed in
    /// the message — <c>tools/list</c> is one call away and is the answer that stays correct.
    /// </remarks>
    public static Error UnknownTool(string name) =>
        new Error(
            UnknownToolCode,
            $"No agent tool named '{name}' is published by this application. Tool names come " +
            "from flowx.manifest.json: a flow publishes one by declaring [AgentTrigger], and " +
            "its name is the flow's id with '.' replaced by '_'. Call tools/list for the set " +
            "this application actually serves.",
            ErrorCategory.NotFound)
            .With("tool", name);

    /// <summary>The code <see cref="MalformedArguments"/> raises.</summary>
    public const string MalformedArgumentsCode = "mcp.malformed_arguments";

    /// <summary>
    /// The <c>arguments</c> document could not be read as the flow's input contract.
    /// </summary>
    /// <param name="name">The tool that was called.</param>
    /// <param name="contract">The contract the arguments had to satisfy.</param>
    /// <param name="reason">The parser's own account.</param>
    /// <remarks>
    /// The parser's message names the offending member and offset, which is what a model
    /// needs to retry successfully. It describes the agent's own document rather than
    /// anything internal, so echoing it leaks nothing — the same reading
    /// <c>HttpErrors.MalformedBody</c> makes of the same message.
    /// </remarks>
    public static Error MalformedArguments(string name, string contract, string reason) =>
        new Error(
            MalformedArgumentsCode,
            $"The arguments handed to '{name}' could not be read as {contract}: {reason}",
            ErrorCategory.Validation)
            .With("tool", name)
            .With("contract", contract)
            .With("reason", reason);

    /// <summary>
    /// The <c>arguments</c> member was absent, or was the literal <c>null</c>.
    /// </summary>
    /// <param name="name">The tool that was called.</param>
    /// <param name="contract">The contract the arguments had to satisfy.</param>
    /// <remarks>
    /// The same code <see cref="MalformedArguments"/> raises, deliberately. A missing
    /// document and an unparseable one are the same fact to the caller — the flow has no
    /// input — and they differ only in which layer noticed. <c>HttpErrors</c> splits them
    /// because RFC 7807 consumers already branch on the two, and nothing branches on these.
    /// </remarks>
    public static Error MissingArguments(string name, string contract) =>
        new Error(
            MalformedArgumentsCode,
            $"'{name}' takes a {contract} and the call carried no arguments document. " +
            "tools/call requires params.arguments to be a JSON object.",
            ErrorCategory.Validation)
            .With("tool", name)
            .With("contract", contract);
}
