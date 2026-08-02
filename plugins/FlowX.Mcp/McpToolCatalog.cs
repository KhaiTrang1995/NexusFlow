using System.Text.Json;

namespace FlowX.Mcp;

/// <summary>
/// The agent tools an application publishes, projected from <c>flowx.manifest.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="From"/> takes the manifest and takes nothing else, and that signature
/// is the whole argument.</strong> A descriptor cannot disagree with the manifest because
/// the manifest is the only thing a descriptor can be computed from: there is no
/// <c>Assembly</c> parameter, no <c>ExecutionPlan</c>, no service provider, and no
/// reflection anywhere on this path. docs/25-Remaining-Platform.md §3 states the rule as
/// "descriptors from the manifest, not from reflection"; a pure function of one string is
/// that rule expressed so that violating it requires changing the signature.
/// </para>
/// <para>
/// <strong>Read through <see cref="JsonDocument"/> rather than deserialised into a model of
/// the manifest.</strong> A model would be a third set of types describing the same
/// document — <c>FlowX.Compiler</c> writes it and <c>FlowX.Cli</c> reads it already — and
/// this plugin may reference neither (ADR-0009). It would also be a place for a default
/// value to be invented for a field the document does not carry, which is precisely how a
/// projection acquires a second source. Reading the DOM means every value published is a
/// value that was in the file, and an absent field is absent rather than defaulted.
/// </para>
/// <para>
/// <see cref="JsonDocument"/> is also the one JSON reader that needs no serialiser
/// metadata: no reflection, no source-generated context, nothing for the trim analyzer to
/// object to (constraint C2).
/// </para>
/// </remarks>
public sealed class McpToolCatalog
{
    /// <summary>The manifest's spelling of the trigger kind this package serves.</summary>
    internal const string AgentKind = "Agent";

    private readonly Dictionary<string, McpToolDescriptor> _byName;

    private McpToolCatalog(IReadOnlyList<McpToolDescriptor> tools)
    {
        Tools = tools;
        _byName = tools.ToDictionary(static tool => tool.Name, StringComparer.Ordinal);
    }

    /// <summary>Every tool this application publishes, ordered by name.</summary>
    public IReadOnlyList<McpToolDescriptor> Tools { get; }

    /// <summary>The tool of that name, or <c>null</c> when nothing publishes one.</summary>
    /// <param name="name">The name an agent called, e.g. <c>order_place</c>.</param>
    public McpToolDescriptor? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _byName.TryGetValue(name, out var tool) ? tool : null;
    }

    /// <summary>Projects the tool surface out of one manifest document.</summary>
    /// <param name="manifestJson">
    /// The contents of <c>flowx.manifest.json</c> — in a generated host, the
    /// <c>FlowX.Generated.FlowXManifest.Json</c> constant the compiler emitted, which is the
    /// same bytes the published artifact carries.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The document is not JSON, or two flows would produce one tool name.
    /// </exception>
    public static McpToolCatalog From(string manifestJson)
    {
        ArgumentNullException.ThrowIfNull(manifestJson);

        using var document = Parse(manifestJson);
        var root = document.RootElement;

        var capabilities = IndexCapabilities(root);
        var tools = new List<McpToolDescriptor>();

        foreach (var flow in Array(root, "flows"))
        {
            if (Project(flow, capabilities) is { } tool)
            {
                tools.Add(tool);
            }
        }

        tools.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        RefuseDuplicateNames(tools);

        return new McpToolCatalog(tools);
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
                "The manifest handed to the agent tool surface is not a JSON document: " +
                exception.Message + " This is normally FlowX.Generated.FlowXManifest.Json, " +
                "which the compiler writes; a hand-assembled string is the other way to " +
                "reach here.",
                nameof(manifestJson),
                exception);
        }
    }

    /// <summary>
    /// One flow's descriptor, or <c>null</c> when the flow declares no agent trigger.
    /// </summary>
    /// <remarks>
    /// A flow with no <c>[AgentTrigger]</c> is not a tool, and neither is a flow the
    /// manifest carries without an id — the second cannot be called, because a call is
    /// dispatched on the flow's identity.
    /// </remarks>
    private static McpToolDescriptor? Project(
        JsonElement flow, Dictionary<string, JsonElement> capabilities)
    {
        if (String(flow, "id") is not { Length: > 0 } flowId ||
            AgentTrigger(flow) is not { } trigger)
        {
            return null;
        }

        var steps = CapabilitiesOf(flow, capabilities);

        var sideEffects = steps
            .SelectMany(static capability => Strings(capability, "sideEffects"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static effect => effect, StringComparer.Ordinal)
            .ToList();

        var permissions = steps
            .Select(static capability => Object(capability, "authorization"))
            .Select(static authorization => authorization is { } value ? String(value, "value") : null)
            .Where(static permission => permission is { Length: > 0 })
            .Select(static permission => permission!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static permission => permission, StringComparer.Ordinal)
            .ToList();

        var input = Object(flow, "input");

        return new McpToolDescriptor(
            NameOf(flowId),
            flowId,
            String(trigger, "description"),
            (input is { } value ? String(value, "type") : null) ?? "object",
            input is { } members ? Strings(members, "sensitive") : [],
            permissions,
            sideEffects,
            steps.TrueForAll(static capability => Flag(capability, "idempotent")),
            RequiresConfirmation(String(trigger, "confirmation"), sideEffects.Count > 0));
    }

    /// <summary>
    /// The tool name for a flow id: MCP's name grammar admits no <c>.</c>, so
    /// <c>order.place</c> is called as <c>order_place</c>.
    /// </summary>
    internal static string NameOf(string flowId) => flowId.Replace('.', '_');

    /// <summary>
    /// Whether the declared <c>ConfirmationMode</c> demands a human before this call.
    /// </summary>
    /// <remarks>
    /// The unrecognised arm answers as <c>RequiredForSideEffects</c> does, which is the
    /// attribute's own default and the answer a manifest with no <c>confirmation</c> field
    /// deserves. It is not a permit: a mode nobody here recognises still requires
    /// confirmation whenever the flow has a declared consequence.
    /// </remarks>
    private static bool RequiresConfirmation(string? confirmation, bool hasSideEffects) =>
        confirmation switch
        {
            "Never" => false,
            "Always" => true,
            _ => hasSideEffects,
        };

    /// <summary>The flow's <c>Agent</c> trigger, or <c>null</c> when it declares none.</summary>
    /// <remarks>
    /// <c>[AgentTrigger]</c> is <c>AllowMultiple = false</c>, so the first is the only one.
    /// </remarks>
    private static JsonElement? AgentTrigger(JsonElement flow)
    {
        foreach (var trigger in Array(flow, "triggers"))
        {
            if (string.Equals(String(trigger, "kind"), AgentKind, StringComparison.Ordinal))
            {
                return trigger;
            }
        }

        return null;
    }

    /// <summary>
    /// Every capability entry the flow's steps name, including those inside branches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The walk descends into <c>branches</c> because a <c>Condition</c>, <c>Switch</c> or
    /// <c>Parallel</c> holds its steps there. A side effect declared only on the capability
    /// behind an <c>Otherwise</c> is still a side effect this tool can have, and a
    /// confirmation prompt that omitted it would be exactly the inaccurate prompt
    /// docs/13-AI-Native.md §6 claims cannot happen.
    /// </para>
    /// <para>
    /// A <c>SubFlow</c> step names a flow rather than a capability and is deliberately not
    /// followed: the child's own entry carries its steps, and the manifest publishes no
    /// aggregate. Recording that as a limitation rather than resolving it here, because
    /// resolving it means composing two flows' capability sets and calling the result the
    /// parent's — which the manifest does not say.
    /// </para>
    /// </remarks>
    private static List<JsonElement> CapabilitiesOf(
        JsonElement flow, Dictionary<string, JsonElement> capabilities)
    {
        var found = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        Walk(Array(flow, "steps"));

        return found;

        void Walk(IEnumerable<JsonElement> steps)
        {
            foreach (var step in steps)
            {
                if (String(step, "capability") is { Length: > 0 } key &&
                    seen.Add(key) &&
                    capabilities.TryGetValue(key, out var capability))
                {
                    found.Add(capability);
                }

                foreach (var branch in Array(step, "branches"))
                {
                    Walk(Elements(branch));
                }
            }
        }
    }

    /// <summary>The manifest's capabilities, keyed as a step names one: <c>id@version</c>.</summary>
    private static Dictionary<string, JsonElement> IndexCapabilities(JsonElement root)
    {
        var index = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var capability in Array(root, "capabilities"))
        {
            if (String(capability, "id") is { Length: > 0 } id &&
                String(capability, "version") is { Length: > 0 } version)
            {
                index[id + "@" + version] = capability;
            }
        }

        return index;
    }

    /// <summary>
    /// Refuses two flows whose ids differ and whose tool names do not.
    /// </summary>
    /// <remarks>
    /// <c>order.place</c> and <c>order_place</c> are two flows and one tool name. Serving
    /// them both would dispatch every call to whichever sorted first — an agent silently
    /// running the wrong flow, which is the worst outcome this package can produce. A
    /// start-up failure naming both is the same trade <c>FlowBusSubscriptionRegistration</c>
    /// makes for a transport mismatch.
    /// </remarks>
    private static void RefuseDuplicateNames(List<McpToolDescriptor> tools)
    {
        for (var i = 1; i < tools.Count; i++)
        {
            if (!string.Equals(tools[i].Name, tools[i - 1].Name, StringComparison.Ordinal))
            {
                continue;
            }

            throw new ArgumentException(
                $"Flows '{tools[i - 1].FlowId}' and '{tools[i].FlowId}' both publish the " +
                $"agent tool '{tools[i].Name}'. A tool name is the flow id with '.' " +
                "replaced by '_', which MCP's name grammar requires, so two ids that " +
                "differ only in that character collide. Rename one of the flows.",
                nameof(tools));
        }
    }

    // ------------------------------------------------------------------ DOM readers
    //
    // Every one of these answers "what does the document say", never "what should it have
    // said". An absent or wrongly-typed member reads as absent, because a manifest from a
    // newer compiler carrying a field this build does not know must not stop an agent
    // calling the tools it does — the reason ManifestDocument's own remarks give for
    // making everything nullable.

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Flag(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static JsonElement? Object(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static IEnumerable<JsonElement> Array(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value))
        {
            yield break;
        }

        foreach (var item in Elements(value))
        {
            yield return item;
        }
    }

    private static IEnumerable<JsonElement> Elements(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in array.EnumerateArray())
        {
            yield return item;
        }
    }

    private static List<string> Strings(JsonElement element, string name) =>
        Array(element, name)
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()!)
            .ToList();
}
