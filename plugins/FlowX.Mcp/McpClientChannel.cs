using System.Globalization;
using System.Text.Json;

namespace FlowX.Mcp;

/// <summary>
/// The direction MCP has and HTTP does not: a request from the server to the client.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two methods travel this way and both are the same shape.</strong>
/// <c>elicitation/create</c> asks the client's human a question; <c>sampling/createMessage</c>
/// asks the client's model one. Neither is something the server can answer for itself, and both
/// are ordinary JSON-RPC requests carrying an id the client echoes — so there is one channel
/// here rather than two, and the difference between them is the method name and the parameters.
/// </para>
/// <para>
/// <strong>Why an interface at all, when there is one implementation.</strong> The runtime cost
/// of a server-initiated request is an open response stream and a suspended tool call, and
/// nothing about that is testable through an <c>HttpResponse</c>-shaped API. A capability
/// that borrows the caller's model — <c>IAgentSampler</c> — is written against this and runs in
/// a unit test against a stub, which is the same trade <c>IFlowJournal</c> makes.
/// </para>
/// </remarks>
public interface IMcpClientChannel
{
    /// <summary>Sends one request to the client and waits for its answer.</summary>
    /// <param name="method">The JSON-RPC method, e.g. <c>elicitation/create</c>.</param>
    /// <param name="parameters">Writes the <c>params</c> member's value.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <remarks>
    /// Never throws for a client that refuses, cannot answer, or does not answer at all. All
    /// three are outcomes of the question rather than faults in asking it, so all three are
    /// values on <see cref="McpClientAnswer"/> — ADR-0007 applied to the one call in this
    /// package that leaves the process.
    /// </remarks>
    ValueTask<McpClientAnswer> RequestAsync(
        string method, Action<Utf8JsonWriter> parameters, CancellationToken ct);
}

/// <summary>What a client said, or why it said nothing.</summary>
/// <remarks>
/// <para>
/// The JSON is carried as a string rather than as a <see cref="JsonElement"/>, because an element
/// is a window onto a <see cref="JsonDocument"/> whose lifetime the reader would then own — and
/// the reader here is a capability that may hold the value across an <c>await</c>. A string is
/// the shortest thing that survives the document being disposed.
/// </para>
/// <para>
/// <see cref="Unsupported"/> is separate from <see cref="Error"/> deliberately. A client that
/// answers <c>-32601 method not found</c> has told the server something about itself, not about
/// the request, and a caller that wants to fall back — the review flow's narration does — needs
/// to tell "this client has no model" from "the model refused".
/// </para>
/// </remarks>
public readonly struct McpClientAnswer : IEquatable<McpClientAnswer>
{
    private McpClientAnswer(string? resultJson, Error? error, bool unsupported)
    {
        ResultJson = resultJson;
        Error = error;
        Unsupported = unsupported;
    }

    /// <summary>The <c>result</c> member, as JSON text, when the client answered one.</summary>
    public string? ResultJson { get; }

    /// <summary>Why there is no result, when there is none.</summary>
    public Error? Error { get; }

    /// <summary>Whether the client said it does not serve this method.</summary>
    public bool Unsupported { get; }

    /// <summary>Whether the client answered.</summary>
    public bool IsSuccess => ResultJson is not null;

    /// <summary>The client answered.</summary>
    /// <param name="resultJson">The <c>result</c> member, as JSON text.</param>
    public static McpClientAnswer Answered(string resultJson) =>
        new(resultJson, error: null, unsupported: false);

    /// <summary>The client refused, or the request never reached one.</summary>
    /// <param name="error">Why.</param>
    /// <param name="unsupported">Whether the reason is that the client does not serve the method.</param>
    public static McpClientAnswer Refused(Error error, bool unsupported = false) =>
        new(resultJson: null, error, unsupported);

    /// <inheritdoc />
    public bool Equals(McpClientAnswer other) =>
        string.Equals(ResultJson, other.ResultJson, StringComparison.Ordinal) &&
        Equals(Error, other.Error) &&
        Unsupported == other.Unsupported;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is McpClientAnswer other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(ResultJson, Error, Unsupported);

    /// <summary>Value equality.</summary>
    public static bool operator ==(McpClientAnswer left, McpClientAnswer right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(McpClientAnswer left, McpClientAnswer right) => !left.Equals(right);
}

/// <summary>
/// The requests this server has sent to clients and not yet had answered.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the table exists at all.</strong> MCP's Streamable HTTP transport carries a
/// server-initiated request <em>down</em> the response stream of the POST that is still being
/// served, and the client sends its answer <em>up</em> as a separate POST to the same endpoint.
/// So the two halves of one exchange arrive on two HTTP requests, and something outside both has
/// to join them.
/// </para>
/// <para>
/// <strong>Keyed on an id this process minted, not on the client's.</strong> A JSON-RPC id is
/// chosen by whoever sends the request, and here that is the server — so the id can be made
/// unique across the process rather than merely unique to one client, and two clients that both
/// number their own requests <c>1</c> cannot collide. It also removes the reason a session id
/// would be needed for correlation: a session scopes a stream, and what an answer has to be
/// matched to is a request.
/// </para>
/// <para>
/// A pending entry is removed by whichever side finishes first — the answer arriving, or the
/// asker's timeout — so an answer to a request nobody is waiting for any more finds nothing and
/// is discarded rather than completing a task twice.
/// </para>
/// </remarks>
public sealed class McpPendingClientRequests
{
    private readonly Dictionary<string, TaskCompletionSource<string?>> _waiting =
        new(StringComparer.Ordinal);

    private readonly Lock _sync = new();

    private long _next;

    /// <summary>Opens a wait and returns the id the client must echo.</summary>
    /// <param name="waiter">The task completed by the client's answer, as JSON text.</param>
    /// <remarks>
    /// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>, so the POST delivering
    /// an answer is not the thread that then runs a flow. Without it the answering request's
    /// pipeline would execute the tool call inline and its own response would wait on it.
    /// </remarks>
    public string Open(out Task<string?> waiter)
    {
        var source = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = "flowx-" + Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture);

        lock (_sync)
        {
            _waiting[id] = source;
        }

        waiter = source.Task;

        return id;
    }

    /// <summary>Delivers a client's answer, if anything is still waiting for it.</summary>
    /// <param name="id">The <c>id</c> the client echoed.</param>
    /// <param name="resultJson">The <c>result</c> member as JSON text, or <c>null</c> for an error.</param>
    /// <returns>Whether anything was waiting.</returns>
    public bool Complete(string id, string? resultJson)
    {
        ArgumentNullException.ThrowIfNull(id);

        TaskCompletionSource<string?>? source;

        lock (_sync)
        {
            if (!_waiting.Remove(id, out source))
            {
                return false;
            }
        }

        return source.TrySetResult(resultJson);
    }

    /// <summary>Abandons a wait whose asker has given up.</summary>
    /// <param name="id">The id returned by <see cref="Open"/>.</param>
    public void Abandon(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        lock (_sync)
        {
            _waiting.Remove(id);
        }
    }
}
