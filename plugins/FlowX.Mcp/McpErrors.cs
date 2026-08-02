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
/// <strong>Two errors and no more, because everything past them is the flow's.</strong>
/// Once the argument document has become the flow's input contract, every subsequent
/// refusal — including authorisation — is produced by the step loop and reaches the agent
/// as the flow's own <see cref="Error"/>. There is deliberately no MCP-specific
/// authorisation error here: a second one would be a second authorisation path, which is
/// what docs/25-Remaining-Platform.md §3 rules out.
/// </para>
/// </remarks>
public static class McpErrors
{
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
