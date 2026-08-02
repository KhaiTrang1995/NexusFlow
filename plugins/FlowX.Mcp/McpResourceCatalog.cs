using System.Text.Json;

namespace FlowX.Mcp;

/// <summary>One readable resource, as <c>resources/list</c> publishes it.</summary>
/// <param name="Uri">The address <c>resources/read</c> takes.</param>
/// <param name="Name">A short name a client shows in a picker.</param>
/// <param name="Description">What reading it gives you.</param>
public sealed record McpResource(string Uri, string Name, string Description);

/// <summary>
/// The graph, as something an agent can read rather than something it can only be told about.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is docs/13 §1's sentence made addressable.</strong> "AI does not read your code.
/// It reads your graph" names an artifact — <c>flowx.manifest.json</c> — and until now the only
/// way an agent could see any of it was the slice <c>tools/list</c> projects. A tool descriptor
/// answers "what may I call"; a model deciding <em>whether</em> to call something needs the
/// flow's steps, its errors, its policies and the stances of the capabilities behind them, none
/// of which a descriptor carries and all of which the manifest does.
/// </para>
/// <para>
/// <strong>Every resource is a verbatim slice of the document, and that is enforced by how they
/// are produced rather than promised.</strong> <see cref="Read"/> returns
/// <c>JsonElement.GetRawText()</c> over the parsed manifest: the bytes an agent reads are the
/// bytes the build published, down to the member order. There is no projection, no summary and
/// no rewriting — which is what makes "the AI runs on the manifest, not on data" (docs/13 §5) a
/// property of this class instead of an operating rule somebody has to follow.
/// </para>
/// <para>
/// <strong>Two shapes and not more.</strong> The whole document, and one flow's entry. The whole
/// document because a manifest is small and complete, and a consumer that wants the capability
/// graph or the event graph already has it. One flow's entry because that is the unit an agent
/// asks about — it is holding a tool name, and a tool name is a flow id — and because slicing at
/// any other seam would mean composing an answer the document does not contain. A capability
/// resource was left out for that reason: a capability's entry is already inside the manifest
/// resource, and a per-capability address would be the first place this class had to decide what
/// belongs in an answer.
/// </para>
/// <para>
/// <see cref="McpToolCatalog"/>'s argument about its own signature applies unchanged:
/// <see cref="From"/> takes the manifest and takes nothing else, so a resource cannot disagree
/// with the artifact for want of a second input to disagree from.
/// </para>
/// </remarks>
public sealed class McpResourceCatalog
{
    /// <summary>The scheme every resource this surface publishes is addressed under.</summary>
    public const string Scheme = "flowx";

    /// <summary>The address of the whole document.</summary>
    /// <remarks>
    /// Composed from <see cref="Scheme"/> rather than written out, because a literal
    /// <c>scheme://host</c> is a hard-coded URI (S1075) and because the scheme is then stated
    /// once for every address this class publishes.
    /// </remarks>
    public const string ManifestUri = Scheme + "://manifest";

    /// <summary>The prefix a single flow's entry is addressed under.</summary>
    public const string FlowUriPrefix = Scheme + "://flow/";

    /// <summary>The media type every resource here is returned as.</summary>
    public const string MimeType = "application/json";

    private readonly string _manifestJson;
    private readonly Dictionary<string, string> _byUri;

    private McpResourceCatalog(
        string manifestJson, IReadOnlyList<McpResource> resources, Dictionary<string, string> byUri)
    {
        _manifestJson = manifestJson;
        _byUri = byUri;
        Resources = resources;
    }

    /// <summary>Every resource this application publishes, ordered by URI.</summary>
    public IReadOnlyList<McpResource> Resources { get; }

    /// <summary>Projects the resource surface out of one manifest document.</summary>
    /// <param name="manifestJson">
    /// The contents of <c>flowx.manifest.json</c> — in a generated host, the
    /// <c>FlowX.Generated.FlowXManifest.Json</c> constant the compiler emitted.
    /// </param>
    /// <exception cref="ArgumentException">The document is not JSON.</exception>
    public static McpResourceCatalog From(string manifestJson)
    {
        ArgumentNullException.ThrowIfNull(manifestJson);

        using var document = Parse(manifestJson);
        var root = document.RootElement;

        var resources = new List<McpResource>
        {
            new(
                ManifestUri,
                ApplicationName(root) + " manifest",
                "The complete flowx.manifest.json this build published: every flow, step, " +
                "capability, contract, trigger, event, error, side effect and authorisation " +
                "stance in the application."),
        };

        var byUri = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var flow in Array(root, "flows"))
        {
            if (String(flow, "id") is not { Length: > 0 } flowId)
            {
                continue;
            }

            var uri = FlowUriPrefix + flowId;

            // Last writer wins rather than a start-up failure, because two flows sharing an id
            // is a defect the compiler already refuses and this class must not be a second place
            // that has an opinion about it. What matters here is that the address resolves.
            byUri[uri] = flow.GetRawText();

            resources.Add(new McpResource(
                uri,
                flowId,
                $"The manifest entry for the flow '{flowId}': its profile, deadline, input and " +
                "output contracts, declared triggers, steps in order with their policies and " +
                "compensations, and the errors it can produce."));
        }

        resources.Sort(static (left, right) => string.CompareOrdinal(left.Uri, right.Uri));

        return new McpResourceCatalog(manifestJson, resources, byUri);
    }

    /// <summary>The document at that address, or <c>null</c> when nothing publishes one.</summary>
    /// <param name="uri">The URI from <c>params.uri</c>.</param>
    /// <returns>JSON text, taken verbatim out of the manifest.</returns>
    public string? Read(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (string.Equals(uri, ManifestUri, StringComparison.Ordinal))
        {
            return _manifestJson;
        }

        return _byUri.TryGetValue(uri, out var slice) ? slice : null;
    }

    /// <summary>Writes the <c>result</c> body of a <c>resources/list</c> response.</summary>
    /// <param name="writer">The response writer.</param>
    public void WriteList(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WritePropertyName("resources");
        writer.WriteStartArray();

        foreach (var resource in Resources)
        {
            writer.WriteStartObject();
            writer.WriteString("uri", resource.Uri);
            writer.WriteString("name", resource.Name);
            writer.WriteString("description", resource.Description);
            writer.WriteString("mimeType", MimeType);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>Writes the <c>result</c> body of a <c>resources/read</c> response.</summary>
    /// <param name="writer">The response writer.</param>
    /// <param name="uri">The address that was read.</param>
    /// <param name="json">The document, as <see cref="Read"/> returned it.</param>
    /// <remarks>
    /// The slice travels in <c>text</c>, which MCP defines as a string, so it is written as a
    /// JSON string containing JSON rather than as an embedded object. That is the protocol's
    /// shape and not a choice: a client reading <c>contents[0].text</c> gets exactly the bytes
    /// this catalogue cut out of the manifest.
    /// </remarks>
    public static void WriteContents(Utf8JsonWriter writer, string uri, string json)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WritePropertyName("contents");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("uri", uri);
        writer.WriteString("mimeType", MimeType);
        writer.WriteString("text", json);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static JsonDocument Parse(string manifestJson)
    {
        try
        {
            return JsonDocument.Parse(manifestJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "The manifest handed to the agent resource surface is not a JSON document: " +
                exception.Message,
                nameof(manifestJson),
                exception);
        }
    }

    private static string ApplicationName(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("application", out var application)
            ? String(application, "name") ?? "FlowX"
            : "FlowX";

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IEnumerable<JsonElement> Array(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in value.EnumerateArray())
        {
            yield return item;
        }
    }
}
