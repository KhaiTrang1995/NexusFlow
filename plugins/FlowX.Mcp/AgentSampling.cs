using System.Text.Json;

namespace FlowX.Mcp;

/// <summary>
/// The <see cref="IAgentSampler"/> a request scope resolves: the caller's channel when there is
/// one, nothing when there is not.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A mutable scoped holder rather than a factory registration</strong>, because the
/// channel is only constructible once the response has been committed as an event stream — which
/// happens inside the request handler, after the container has already built the scope the flow
/// will run in. <c>MapFlowXMcp</c> is the only thing that sets it, and it clears it again when the
/// call ends: a sampler holding a closed stream is worse than one holding nothing.
/// </para>
/// <para>
/// <strong>Absent for every caller that is not an agent, and that is the ordinary case.</strong>
/// An HTTP request, a broker delivery and a cron fire all resolve this and all find no channel, so
/// a capability asking for a completion is told the caller has no model and answers without one.
/// So does an MCP client that could not accept an event stream, or that answers
/// <c>sampling/createMessage</c> with "method not found" — the three are indistinguishable to a
/// capability and the fallback is the same for all of them.
/// </para>
/// </remarks>
public sealed class McpCallScope : IAgentSampler
{
    /// <summary>The JSON-RPC method that asks a client's model to write something.</summary>
    public const string Method = "sampling/createMessage";

    /// <summary>
    /// The channel back to the client, set by the endpoint for the duration of one tool call.
    /// </summary>
    public IMcpClientChannel? Channel { get; set; }

    /// <inheritdoc />
    public async ValueTask<AgentSample> SampleAsync(
        AgentSamplingRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Channel is not { } channel)
        {
            return AgentSample.Unavailable();
        }

        var answer = await channel
            .RequestAsync(Method, writer => WriteRequest(writer, request), ct)
            .ConfigureAwait(false);

        if (answer.Unsupported)
        {
            // The client is connected and serves no model. Indistinguishable, to the capability,
            // from not being called by an agent at all — and it is the same fallback either way.
            return AgentSample.Unavailable();
        }

        if (answer.ResultJson is not { } resultJson)
        {
            return AgentSample.Failed(
                answer.Error ?? McpErrors.SamplingFailed("the client returned no result"));
        }

        return TextOf(resultJson) is { } text
            ? AgentSample.Written(text)
            : AgentSample.Failed(McpErrors.SamplingFailed(
                "the client's result carried no text content"));
    }

    /// <summary>Writes the <c>params</c> of a <c>sampling/createMessage</c> request.</summary>
    /// <remarks>
    /// One user message and a system prompt, which is the whole of what
    /// <see cref="AgentSamplingRequest"/> can express. No model preferences and no
    /// <c>includeContext</c>: the first would be this server choosing how the caller spends its
    /// own quota, and the second asks the client to add material to a request whose entire
    /// content is the point of the abstraction.
    /// </remarks>
    private static void WriteRequest(Utf8JsonWriter writer, AgentSamplingRequest request)
    {
        writer.WriteStartObject();

        writer.WritePropertyName("messages");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("role", "user");
        writer.WritePropertyName("content");
        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WriteString("text", request.UserMessage);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndArray();

        writer.WriteString("systemPrompt", request.SystemPrompt);
        writer.WriteNumber("maxTokens", request.MaxTokens);

        writer.WriteEndObject();
    }

    /// <summary>The text of the model's reply, or <c>null</c> when the reply carried none.</summary>
    private static string? TextOf(string resultJson)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(resultJson);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;

            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.Object &&
                content.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String
                    ? text.GetString()
                    : null;
        }
    }
}
