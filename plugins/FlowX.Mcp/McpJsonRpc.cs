using System.Globalization;
using System.Text.Json;

namespace FlowX.Mcp;

/// <summary>
/// The JSON-RPC 2.0 envelope MCP speaks, written without reflection.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two error channels, and which one an outcome takes is a decision rather than a
/// detail.</strong> A fault in the <em>request</em> — a body that is not JSON, a method
/// nobody serves, a tool name nobody publishes, an arguments document the contract cannot
/// accept — is a JSON-RPC <c>error</c> object, because the call never happened. A fault in
/// the <em>flow</em> is a successful JSON-RPC response whose <c>result</c> carries
/// <c>isError: true</c>, because the call did happen and the flow answered. MCP draws the
/// line in exactly that place, and so does this package: everything before the arguments
/// become the flow's input contract is <see cref="McpErrors"/>, and everything after is the
/// flow's own <see cref="Error"/> reaching the agent unaltered.
/// </para>
/// <para>
/// That distinction is what makes a refusal legible. An agent refused by
/// <c>authorization.permission_denied</c> receives the code, the category and the permission
/// it lacks in <c>structuredContent</c> — the same <see cref="Error"/> an HTTP caller
/// receives as an RFC 7807 body, because it is the same object produced by the same step
/// loop.
/// </para>
/// </remarks>
public static class McpJsonRpc
{
    /// <summary>The JSON-RPC version every message carries.</summary>
    public const string Version = "2.0";

    /// <summary>The media type a JSON-RPC response is returned as.</summary>
    public const string ContentType = "application/json";

    /// <summary>The MCP revision this surface implements.</summary>
    /// <remarks>
    /// Answered from <c>initialize</c> so a client can decide whether to proceed. A date
    /// rather than a SemVer, which is how MCP versions itself.
    /// </remarks>
    public const string ProtocolVersion = "2025-06-18";

    /// <summary>The request was not JSON at all.</summary>
    public const int ParseError = -32700;

    /// <summary>The document was JSON and was not a JSON-RPC request.</summary>
    public const int InvalidRequest = -32600;

    /// <summary>No method of that name is served.</summary>
    public const int MethodNotFound = -32601;

    /// <summary>
    /// The method is served and the parameters are not usable — an unknown tool name, or
    /// an arguments document the flow's contract cannot accept.
    /// </summary>
    public const int InvalidParams = -32602;

    /// <summary>Writes a successful response whose <c>result</c> the caller supplies.</summary>
    /// <param name="writer">The response writer.</param>
    /// <param name="id">The request's <c>id</c>, echoed verbatim.</param>
    /// <param name="result">Writes the <c>result</c> member's value.</param>
    public static void WriteResult(Utf8JsonWriter writer, JsonElement? id, Action<Utf8JsonWriter> result)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(result);

        writer.WriteStartObject();
        writer.WriteString("jsonrpc", Version);
        WriteId(writer, id);
        writer.WritePropertyName("result");
        result(writer);
        writer.WriteEndObject();
    }

    /// <summary>Writes an error response carrying a FlowX <see cref="Error"/> as its data.</summary>
    /// <param name="writer">The response writer.</param>
    /// <param name="id">The request's <c>id</c>, echoed verbatim.</param>
    /// <param name="code">One of the JSON-RPC codes on this type.</param>
    /// <param name="error">The refusal, whose code and category travel in <c>data</c>.</param>
    /// <remarks>
    /// The JSON-RPC code is a small closed set that says which layer refused; FlowX's own
    /// code says what was refused and is the one a caller branches on. Both are published
    /// because collapsing them would mean either losing the protocol's vocabulary or losing
    /// the platform's.
    /// </remarks>
    public static void WriteError(Utf8JsonWriter writer, JsonElement? id, int code, Error error)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(error);

        writer.WriteStartObject();
        writer.WriteString("jsonrpc", Version);
        WriteId(writer, id);
        writer.WritePropertyName("error");
        writer.WriteStartObject();
        writer.WriteNumber("code", code);
        writer.WriteString("message", error.Message);
        writer.WritePropertyName("data");
        WriteFlowError(writer, error);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>Writes the <c>result</c> of a <c>tools/call</c>.</summary>
    /// <param name="writer">The response writer.</param>
    /// <param name="outcome">What the flow did.</param>
    /// <remarks>
    /// <para>
    /// <c>content</c> is MCP's required, model-readable rendering and carries the flow's
    /// projected output as one text block — the JSON the flow's own <c>.Return(...)</c>
    /// produced, serialised through the flow's own contract metadata.
    /// <c>structuredContent</c> carries the machine-readable copy beside it, which is where
    /// a refusal's code and category go.
    /// </para>
    /// <para>
    /// A suspended flow is neither an error nor a result: it is reported as such, with the
    /// instance id an agent needs to deliver the signal that continues it. Serialising the
    /// projection here would build an answer out of steps that have not run (ADR-0022).
    /// </para>
    /// </remarks>
    public static void WriteToolResult(Utf8JsonWriter writer, McpToolOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();

        writer.WritePropertyName("content");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WriteString("text", TextOf(outcome));
        writer.WriteEndObject();
        writer.WriteEndArray();

        writer.WriteBoolean("isError", outcome.Error is not null);

        writer.WritePropertyName("structuredContent");
        writer.WriteStartObject();

        if (outcome.Error is { } error)
        {
            writer.WritePropertyName("error");
            WriteFlowError(writer, error);
        }
        else if (outcome.Content is { } content)
        {
            writer.WritePropertyName("output");
            writer.WriteRawValue(content);
        }

        writer.WriteBoolean("suspended", outcome.IsSuspended);

        if (outcome.InstanceId is { } instanceId)
        {
            writer.WriteString("instanceId", instanceId);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>Writes the <c>result</c> of <c>initialize</c>.</summary>
    /// <param name="writer">The response writer.</param>
    /// <param name="applicationName">
    /// What the server calls itself. The application's own name, which is the manifest's
    /// <c>application.name</c> and therefore the assembly the flows live in.
    /// </param>
    public static void WriteInitialize(Utf8JsonWriter writer, string applicationName)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteString("protocolVersion", ProtocolVersion);

        writer.WritePropertyName("capabilities");
        writer.WriteStartObject();
        writer.WritePropertyName("tools");
        writer.WriteStartObject();

        // The tool set is the manifest's, and the manifest is fixed at build time: it
        // cannot change while the process runs, so there is no list-changed notification to
        // promise. Declaring one would be a capability nothing can ever exercise.
        writer.WriteBoolean("listChanged", false);
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WritePropertyName("serverInfo");
        writer.WriteStartObject();
        writer.WriteString("name", applicationName);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    /// <summary>Writes one FlowX <see cref="Error"/> as a JSON object.</summary>
    /// <param name="writer">The response writer.</param>
    /// <param name="error">The refusal.</param>
    /// <remarks>
    /// <c>category</c> is written by name rather than as an HTTP status. An agent is not on
    /// HTTP by construction — ADR-0004 — and the category is the platform's own closed
    /// vocabulary, which is what a caller can branch on across every transport.
    /// </remarks>
    public static void WriteFlowError(Utf8JsonWriter writer, Error error)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(error);

        writer.WriteStartObject();
        writer.WriteString("code", error.Code);
        writer.WriteString("category", error.Category.ToString());
        writer.WriteString("message", error.Message);

        if (error.Data is { Count: > 0 } data)
        {
            writer.WritePropertyName("detail");
            writer.WriteStartObject();

            foreach (var pair in data)
            {
                writer.WritePropertyName(pair.Key);
                WriteValue(writer, pair.Value);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    /// <summary>Echoes the request's id, or writes <c>null</c> when it had none.</summary>
    /// <remarks>
    /// JSON-RPC requires the member to be present on every response, including one to a
    /// request whose id could not be read — which is the case a parse error answers.
    /// </remarks>
    private static void WriteId(Utf8JsonWriter writer, JsonElement? id)
    {
        writer.WritePropertyName("id");

        if (id is { } value && value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
        {
            value.WriteTo(writer);
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    /// <summary>The model-readable rendering of an outcome.</summary>
    private static string TextOf(McpToolOutcome outcome)
    {
        if (outcome.Error is { } error)
        {
            return error.ToString();
        }

        if (outcome.IsSuspended)
        {
            return "The flow is suspended at a wait and has produced no result yet" +
                (outcome.InstanceId is { } id
                    ? $". Instance {id:d}."
                    : ".");
        }

        return outcome.Content is { } content
            ? System.Text.Encoding.UTF8.GetString(content)
            : "{}";
    }

    /// <summary>
    /// Writes one structured detail value, over the closed set an <see cref="Error"/>
    /// carries.
    /// </summary>
    /// <remarks>
    /// <c>ProblemDetailsJson.WriteValue</c>'s shape and its reason: <c>Error.Data</c> is a
    /// dictionary of <c>object?</c>, and serialising <c>object</c> needs runtime type
    /// resolution, which constraint C2 forbids. Anything outside the set is written as its
    /// invariant string form rather than dropped — a detail that vanishes in production and
    /// not in a test is worse than one that reads oddly.
    /// </remarks>
    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case string s: writer.WriteStringValue(s); break;
            case bool b: writer.WriteBooleanValue(b); break;
            case int i: writer.WriteNumberValue(i); break;
            case long l: writer.WriteNumberValue(l); break;
            case double d: writer.WriteNumberValue(d); break;
            case decimal m: writer.WriteNumberValue(m); break;
            case DateTimeOffset dto: writer.WriteStringValue(dto); break;
            case DateTime dt: writer.WriteStringValue(dt); break;
            case Guid g: writer.WriteStringValue(g); break;

            default:
                writer.WriteStringValue(
                    Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                break;
        }
    }
}
