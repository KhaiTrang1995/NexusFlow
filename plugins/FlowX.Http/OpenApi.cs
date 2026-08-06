using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace FlowX.Http;

/// <summary>
/// Turns the compiled manifest into an OpenAPI 3.1 document.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Generated from the manifest, not from the endpoints.</strong> Every other framework
/// documents HTTP by reflecting over what was mapped, which describes the transport and nothing
/// else. The manifest already carries what a caller actually needs — the route, the method,
/// whether the endpoint deduplicates, the contracts on both sides and <em>every error code the
/// flow can produce</em> — because the compiler wrote it from the same source the endpoint was
/// generated from. So the document cannot drift from the application: there is one description and
/// two readers of it.
/// </para>
/// <para>
/// <strong>The shapes come from the JSON source generator, not from reflecting over the
/// contract.</strong> Every contract is required to be in a <c>JsonSerializerContext</c> — that is
/// what <c>FLOWX1006</c> enforces and what keeps the write path trim- and NativeAOT-safe — so the
/// same generated metadata describes it, and no member is reflected over.
/// </para>
/// <para>
/// <strong>Turning the manifest's type <em>name</em> into a type is the part that is not
/// trim-safe</strong>, and this method is annotated for it. An application that trims passes its
/// contracts in through the overload that takes them, and gets no warning; one that does not gets
/// a warning at its own call site rather than a silently emptier document.
/// </para>
/// <para>
/// <strong>What it deliberately does not claim.</strong> A contract whose type is not in the
/// context gets a schema of <c>type: object</c> and an <c>x-flowx-contract</c> extension naming
/// the type, rather than an invented shape. Guessing would produce a document that is confidently
/// wrong, which is worse for a client generator than one that is honestly incomplete.
/// </para>
/// </remarks>
public static class OpenApi
{
    /// <summary>The version of the specification this emits.</summary>
    public const string SpecificationVersion = "3.1.0";

    /// <summary>Writes the document for a manifest.</summary>
    /// <param name="manifestJson">
    /// The application's manifest, as <c>FlowX.Generated.FlowXManifest.Json</c> carries it.
    /// </param>
    /// <param name="resolver">
    /// The application's JSON contract metadata — the generated context it already serialises
    /// with. Null emits routes and errors with opaque bodies rather than guessed ones.
    /// </param>
    /// <returns>The document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="manifestJson"/> is null.</exception>
    /// <exception cref="JsonException">The manifest is not the document the compiler writes.</exception>
    [RequiresUnreferencedCode(
        "The manifest names contracts as strings, and resolving one to a type searches the loaded " +
        "assemblies. Pass the contracts explicitly to describe them in a trimmed application.")]
    public static string Write(string manifestJson, IJsonTypeInfoResolver? resolver) =>
        Write(manifestJson, resolver, null);

    /// <summary>Writes the document, with the contract types given rather than searched for.</summary>
    /// <param name="manifestJson">The application's manifest.</param>
    /// <param name="resolver">The application's JSON contract metadata.</param>
    /// <param name="contracts">
    /// The contract types, by the name the manifest spells them. This is the overload a trimmed or
    /// ahead-of-time-compiled application uses: nothing here searches for a type, so nothing warns.
    /// </param>
    /// <returns>The document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="manifestJson"/> is null.</exception>
    public static string Write(
        string manifestJson,
        IJsonTypeInfoResolver? resolver,
        IReadOnlyDictionary<string, Type>? contracts)
    {
        ArgumentNullException.ThrowIfNull(manifestJson);

        using var manifest = JsonDocument.Parse(manifestJson);
        var root = manifest.RootElement;

        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            var schemas = new SchemaSet(resolver, contracts);

            writer.WriteStartObject();
            writer.WriteString("openapi", SpecificationVersion);

            WriteInfo(writer, root);
            WritePaths(writer, root, schemas);
            WriteComponents(writer, schemas);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteInfo(Utf8JsonWriter writer, JsonElement root)
    {
        writer.WriteStartObject("info");

        var application = root.TryGetProperty("application", out var app) ? app : default;

        writer.WriteString(
            "title",
            application.ValueKind == JsonValueKind.Object
            && application.TryGetProperty("name", out var name)
                ? name.GetString()
                : "FlowX application");

        writer.WriteString(
            "version",
            application.ValueKind == JsonValueKind.Object
            && application.TryGetProperty("version", out var version)
                ? version.GetString()
                : "0.0.0");

        writer.WriteEndObject();
    }

    private static void WritePaths(Utf8JsonWriter writer, JsonElement root, SchemaSet schemas)
    {
        // Grouped by route first, because OpenAPI nests methods inside a path and two flows can
        // share a route under different methods. A document that emitted the path twice would be
        // rejected by every generator that reads it.
        var byRoute = new SortedDictionary<string, List<(string Method, JsonElement Flow, JsonElement Trigger)>>(
            StringComparer.Ordinal);

        if (root.TryGetProperty("flows", out var flows) && flows.ValueKind == JsonValueKind.Array)
        {
            foreach (var flow in flows.EnumerateArray())
            {
                if (!flow.TryGetProperty("triggers", out var triggers)
                    || triggers.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var trigger in triggers.EnumerateArray())
                {
                    if (!IsHttp(trigger)
                        || !trigger.TryGetProperty("route", out var route)
                        || route.GetString() is not { Length: > 0 } path)
                    {
                        continue;
                    }

                    var method = trigger.TryGetProperty("method", out var verb)
                        ? verb.GetString() ?? "POST"
                        : "POST";

                    if (!byRoute.TryGetValue(path, out var operations))
                    {
                        operations = [];
                        byRoute[path] = operations;
                    }

                    operations.Add((method.ToLowerInvariant(), flow, trigger));
                }
            }
        }

        writer.WriteStartObject("paths");

        foreach (var (path, operations) in byRoute)
        {
            writer.WriteStartObject(path);

            foreach (var (method, flow, trigger) in operations)
            {
                WriteOperation(writer, method, flow, trigger, schemas);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteOperation(
        Utf8JsonWriter writer,
        string method,
        JsonElement flow,
        JsonElement trigger,
        SchemaSet schemas)
    {
        writer.WriteStartObject(method);

        var id = Text(flow, "id") ?? "flow";

        writer.WriteString("operationId", id);
        writer.WriteString("summary", Text(flow, "source") ?? id);

        writer.WriteStartArray("tags");
        writer.WriteStringValue(id.Contains('.', StringComparison.Ordinal)
            ? id[..id.IndexOf('.', StringComparison.Ordinal)]
            : id);
        writer.WriteEndArray();

        WriteIdempotencyParameter(writer, trigger);
        WriteRequestBody(writer, flow, schemas);
        WriteResponses(writer, flow, schemas);

        writer.WriteEndObject();
    }

    /// <summary>
    /// The header an idempotent endpoint requires, declared as required.
    /// </summary>
    /// <remarks>
    /// <strong>Required, and that is not a nicety.</strong> An endpoint declared
    /// <c>Idempotent = true</c> refuses a request without the header — see
    /// <c>HttpTriggerReader.Read</c> — so a generated client that omitted it would fail every call
    /// with a 400 that looks like a server fault. Documenting it as optional would be documenting
    /// the opposite of what the endpoint does.
    /// </remarks>
    private static void WriteIdempotencyParameter(Utf8JsonWriter writer, JsonElement trigger)
    {
        if (!trigger.TryGetProperty("idempotent", out var idempotent)
            || idempotent.ValueKind != JsonValueKind.True)
        {
            return;
        }

        writer.WriteStartArray("parameters");
        writer.WriteStartObject();

        writer.WriteString("name", FlowXHeaders.IdempotencyKey);
        writer.WriteString("in", "header");
        writer.WriteBoolean("required", true);
        writer.WriteString(
            "description",
            "Deduplicates this call. The endpoint refuses a request without it rather than " +
            "silently treating the call as new.");

        writer.WriteStartObject("schema");
        writer.WriteString("type", "string");
        writer.WriteEndObject();

        writer.WriteEndObject();
        writer.WriteEndArray();
    }

    private static void WriteRequestBody(Utf8JsonWriter writer, JsonElement flow, SchemaSet schemas)
    {
        if (Contract(flow, "input") is not { Length: > 0 } type)
        {
            return;
        }

        writer.WriteStartObject("requestBody");
        writer.WriteBoolean("required", true);

        writer.WriteStartObject("content");
        writer.WriteStartObject("application/json");
        writer.WriteStartObject("schema");
        writer.WriteString("$ref", schemas.Reference(type));
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    /// <summary>
    /// The successful response, and one entry per status the flow's errors can produce.
    /// </summary>
    /// <remarks>
    /// <strong>The error codes are the manifest's, not a guess.</strong> The compiler resolved
    /// every error a flow's capabilities can return, so the document lists the statuses that can
    /// actually come back and — in the description — the exact <c>code</c> values a client will
    /// find in the problem body. That is the part of an API that is hardest to discover by trying
    /// it, and the only part a generated client cannot infer.
    /// </remarks>
    private static void WriteResponses(Utf8JsonWriter writer, JsonElement flow, SchemaSet schemas)
    {
        writer.WriteStartObject("responses");

        writer.WriteStartObject("200");
        writer.WriteString("description", "The flow completed.");

        if (Contract(flow, "output") is { Length: > 0 } output)
        {
            writer.WriteStartObject("content");
            writer.WriteStartObject("application/json");
            writer.WriteStartObject("schema");
            writer.WriteString("$ref", schemas.Reference(output));
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndObject();

        var codes = new List<string>();

        if (flow.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            codes.AddRange(
                errors.EnumerateArray()
                    .Select(static error => error.GetString())
                    .Where(static code => code is { Length: > 0 })
                    .Select(static code => code!));
        }

        WriteProblem(
            writer,
            "400",
            codes.Count > 0
                ? "Refused. The problem body's `code` is one of: " + string.Join(", ", codes) + "."
                : "Refused.");

        WriteProblem(writer, "401", "The caller presented no usable identity.");
        WriteProblem(writer, "403", "The caller holds no grant for this capability.");

        writer.WriteEndObject();
    }

    private static void WriteProblem(Utf8JsonWriter writer, string status, string description)
    {
        writer.WriteStartObject(status);
        writer.WriteString("description", description);

        writer.WriteStartObject("content");
        writer.WriteStartObject("application/problem+json");
        writer.WriteStartObject("schema");
        writer.WriteString("$ref", "#/components/schemas/ProblemDetails");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    private static void WriteComponents(Utf8JsonWriter writer, SchemaSet schemas)
    {
        writer.WriteStartObject("components");
        writer.WriteStartObject("schemas");

        schemas.WriteAll(writer);

        writer.WriteStartObject("ProblemDetails");
        writer.WriteString("type", "object");

        writer.WriteStartObject("properties");

        foreach (var (name, type) in new[]
                 {
                     ("type", "string"), ("title", "string"), ("status", "integer"),
                     ("detail", "string"), ("code", "string"),
                 })
        {
            writer.WriteStartObject(name);
            writer.WriteString("type", type);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static bool IsHttp(JsonElement trigger) =>
        trigger.TryGetProperty("kind", out var kind)
        && string.Equals(kind.GetString(), "Http", StringComparison.Ordinal);

    private static string? Contract(JsonElement flow, string side) =>
        flow.TryGetProperty(side, out var contract)
        && contract.ValueKind == JsonValueKind.Object
        && contract.TryGetProperty("type", out var type)
            ? type.GetString()
            : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
