using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace AiAgent.Tests;

/// <summary>
/// An MCP client that can be asked something, which is the only kind that can exercise this
/// surface.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every other test client in this repository sends a request and reads a body.</strong>
/// That is not enough here: a <c>tools/call</c> for a tool with declared consequences is answered
/// with an event stream carrying a server-initiated <c>elicitation/create</c>, and the flow does
/// not start until this class posts an answer back on a <em>second</em> HTTP request. So the
/// client has to hold the first response open, read frames, and reply — which is the whole of
/// what MCP's Streamable HTTP transport is for and the whole of what makes a server-enforced
/// confirmation possible over HTTP.
/// </para>
/// <para>
/// <see cref="Asked"/> records what the server asked and in what order. A test asserting that a
/// flow did not run proves nothing on its own — a broken server that answered "refused" without
/// asking anybody would pass it — so the assertions that matter check both: the question was put,
/// and the answer was obeyed.
/// </para>
/// </remarks>
internal sealed class AgentSession
{
    private const string Route = "/mcp";

    private readonly HttpClient _client;
    private readonly string? _token;
    private readonly Func<string, JsonElement, string?> _respond;
    private readonly bool _answer;

    /// <summary>Creates a session.</summary>
    /// <param name="client">The test server's client.</param>
    /// <param name="token">The bearer token every request carries, or <c>null</c> for anonymous.</param>
    /// <param name="respond">
    /// Answers a server-initiated request: given the method and its <c>params</c>, returns the
    /// JSON text of the <c>result</c> to send back, or <c>null</c> to answer with a JSON-RPC
    /// error — which is what a client that does not serve the method does.
    /// </param>
    /// <param name="answer">
    /// Whether to answer at all. <c>false</c> reads the question and stays silent, which is the
    /// client the server's timeout exists for and is otherwise indistinguishable from a client
    /// that crashed with the prompt on screen.
    /// </param>
    public AgentSession(
        HttpClient client,
        string? token = null,
        Func<string, JsonElement, string?>? respond = null,
        bool answer = true)
    {
        _client = client;
        _token = token;
        _respond = respond ?? (static (_, _) => null);
        _answer = answer;
    }

    /// <summary>What the server asked, in order: the method and the raw <c>params</c>.</summary>
    public List<(string Method, string Parameters)> Asked { get; } = [];

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Sends one message and reads the plain JSON answer.</summary>
    /// <remarks>
    /// No <c>Accept: text/event-stream</c>, so the server answers with a body. This is the shape
    /// every MCP client that cannot be asked anything uses, and the shape <c>initialize</c>,
    /// <c>tools/list</c> and <c>resources/read</c> are always answered in.
    /// </remarks>
    public async Task<JsonDocument> SendAsync(string body)
    {
        using var request = Message(body);
        using var response = await _client.SendAsync(request, Cancellation);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));
    }

    /// <summary>
    /// Sends one message over an event stream, answering whatever the server asks, and returns
    /// the final message.
    /// </summary>
    public async Task<JsonDocument> StreamAsync(string body)
    {
        using var request = Message(body);

        request.Headers.Add("Accept", "application/json, text/event-stream");

        using var response = await _client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, Cancellation);

        response.Content.Headers.ContentType?.MediaType.ShouldBe(
            "text/event-stream",
            "The server answered a tools/call with a plain body although the client accepted an " +
            "event stream, so it can never ask this client anything.");

        await using var stream = await response.Content.ReadAsStreamAsync(Cancellation);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(Cancellation) is { } line)
        {
            const string prefix = "data: ";

            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var frame = JsonDocument.Parse(line[prefix.Length..]);

            if (!frame.RootElement.TryGetProperty("method", out var method) ||
                method.GetString() is not { Length: > 0 } asked)
            {
                // No method: this is the answer to the call that opened the stream.
                return frame;
            }

            var parameters = frame.RootElement.GetProperty("params");

            Asked.Add((asked, parameters.GetRawText()));

            await AnswerAsync(frame.RootElement.GetProperty("id").GetString()!, asked, parameters);

            frame.Dispose();
        }

        throw new InvalidOperationException(
            "The event stream ended without answering the call that opened it.");
    }

    /// <summary>
    /// Posts this client's answer to a server-initiated request, as a separate HTTP request.
    /// </summary>
    /// <remarks>
    /// Separate because MCP says so: a server request travels down the stream of the POST being
    /// served and the client's response travels up as a new POST. The server joins the two on the
    /// id, which is why this echoes it verbatim.
    /// </remarks>
    private async Task AnswerAsync(string id, string method, JsonElement parameters)
    {
        if (!_answer)
        {
            return;
        }

        var result = _respond(method, parameters);

        var body = result is null
            ? "{\"jsonrpc\":\"2.0\",\"id\":\"" + id +
              "\",\"error\":{\"code\":-32601,\"message\":\"Method not found\"}}"
            : "{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"result\":" + result + "}";

        using var request = Message(body);
        using var response = await _client.SendAsync(request, Cancellation);

        response.StatusCode.ShouldBe(
            System.Net.HttpStatusCode.Accepted,
            $"The server did not accept this client's answer to its own {method} request, so " +
            "nothing is waiting for it and the call will time out.");
    }

    private HttpRequestMessage Message(string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrEmpty(_token))
        {
            request.Headers.Add("Authorization", "Bearer " + _token);
        }

        return request;
    }
}
