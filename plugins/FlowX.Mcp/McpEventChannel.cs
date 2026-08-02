using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace FlowX.Mcp;

/// <summary>
/// One <c>tools/call</c> served as an event stream, so the server can ask before it answers.
/// </summary>
/// <remarks>
/// <para>
/// <strong>MCP's Streamable HTTP transport, used for the one thing it exists for.</strong> A POST
/// may be answered either with a JSON body or with an SSE stream, and the stream is what lets the
/// server send its own requests — <c>elicitation/create</c>, <c>sampling/createMessage</c> —
/// before the response to the call that is still being served. The client's answers arrive as
/// separate POSTs to the same endpoint and are joined to the waiting request by
/// <see cref="McpPendingClientRequests"/>.
/// </para>
/// <para>
/// <strong>Which mode a call gets is the client's declaration, not this server's preference.</strong>
/// A client that sends <c>Accept: text/event-stream</c> is saying it can read one; a client that
/// does not gets the JSON body it asked for, and therefore gets no elicitation and no sampling.
/// Choosing for it would break every client that worked yesterday, which is the failure a
/// transport change makes silently.
/// </para>
/// <para>
/// <strong>Writes are serialised.</strong> Two frames can be produced concurrently — the tool's
/// own result, and a <c>sampling/createMessage</c> a capability raised from inside the flow — and
/// a <see cref="Utf8JsonWriter"/> interleaved on one <c>PipeWriter</c> produces a body no parser
/// can read.
/// </para>
/// </remarks>
public sealed class McpEventChannel : IMcpClientChannel, IAsyncDisposable
{
    /// <summary>The media type an event-stream response is served as.</summary>
    public const string ContentType = "text/event-stream";

    private readonly HttpResponse _response;
    private readonly McpPendingClientRequests _pending;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _writing = new(1, 1);

    private McpEventChannel(HttpResponse response, McpPendingClientRequests pending, TimeSpan timeout)
    {
        _response = response;
        _pending = pending;
        _timeout = timeout;
    }

    /// <summary>Whether this request asked to be answered with an event stream.</summary>
    /// <param name="request">The incoming POST.</param>
    /// <remarks>
    /// A substring match on the <c>Accept</c> header rather than a parsed media-range list,
    /// because the question is whether the client named this type at all and MCP requires it to
    /// be named explicitly. A client sending only <c>*/*</c> is not saying it can read an event
    /// stream; it is saying it has not thought about it, and answering with one would be the
    /// server choosing.
    /// </remarks>
    public static bool IsAccepted(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        foreach (var accept in request.Headers.Accept)
        {
            if (accept?.Contains(ContentType, StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Commits the response as an event stream and returns the channel over it.</summary>
    /// <param name="context">The request being served.</param>
    /// <param name="pending">Where a server-initiated request registers its wait.</param>
    /// <param name="timeout">How long a server-initiated request waits for an answer.</param>
    /// <remarks>
    /// The headers are sent before anything else happens, because a client cannot begin reading
    /// frames until they arrive — and the first frame may be a question whose answer this request
    /// is about to block on. Committing lazily deadlocks exactly that exchange.
    /// </remarks>
    public static async ValueTask<McpEventChannel> OpenAsync(
        HttpContext context, McpPendingClientRequests pending, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pending);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = ContentType;
        context.Response.Headers.CacheControl = "no-cache";

        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);

        return new McpEventChannel(context.Response, pending, timeout);
    }

    /// <inheritdoc />
    public async ValueTask<McpClientAnswer> RequestAsync(
        string method, Action<Utf8JsonWriter> parameters, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(parameters);

        var id = _pending.Open(out var waiter);

        try
        {
            await WriteAsync(writer => McpJsonRpc.WriteRequest(writer, id, method, parameters), ct)
                .ConfigureAwait(false);

            var answered = await Task
                .WhenAny(waiter, Task.Delay(_timeout, ct))
                .ConfigureAwait(false);

            if (answered != waiter)
            {
                return McpClientAnswer.Refused(McpErrors.ConfirmationRefused(
                    method,
                    "timed_out",
                    "the client did not answer within " +
                    _timeout.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) +
                    " seconds."));
            }

            var resultJson = await waiter.ConfigureAwait(false);

            return resultJson is null
                ? McpClientAnswer.Refused(
                    McpErrors.ConfirmationRefused(method, "errored", "the client answered with an error."),
                    unsupported: true)
                : McpClientAnswer.Answered(resultJson);
        }
        finally
        {
            // Whether the answer arrived, timed out or the caller gave up, nothing is waiting on
            // this id any more. Left behind, the entry is a leak that a later reply completes.
            _pending.Abandon(id);
        }
    }

    /// <summary>Writes one frame.</summary>
    /// <param name="body">Writes the JSON-RPC message this frame carries.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    public async ValueTask WriteAsync(Action<Utf8JsonWriter> body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        await _writing.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            using var buffer = new MemoryStream();

            using (var writer = new Utf8JsonWriter(buffer))
            {
                body(writer);
            }

            // The SSE framing, written by hand because it is two ASCII strings around a payload
            // and taking a dependency to produce them would be the whole of what the dependency
            // did. A frame is one `data:` line: the JSON writer never emits a newline, so there
            // is no multi-line case to fold.
            await _response.Body.WriteAsync(Encoding.UTF8.GetBytes("data: "), ct).ConfigureAwait(false);
            await _response.Body.WriteAsync(buffer.ToArray(), ct).ConfigureAwait(false);
            await _response.Body.WriteAsync(Encoding.UTF8.GetBytes("\n\n"), ct).ConfigureAwait(false);
            await _response.Body.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _writing.Dispose();

        return ValueTask.CompletedTask;
    }
}
