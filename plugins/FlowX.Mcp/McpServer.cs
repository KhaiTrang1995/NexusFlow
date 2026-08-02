using System.Text.Json;
using FlowX.Runtime;

namespace FlowX.Mcp;

/// <summary>
/// The agent surface: <c>tools/list</c> out of the manifest, <c>tools/call</c> into a flow.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Transport-neutral on purpose.</strong> Nothing here knows about HTTP: it takes a
/// <see cref="FlowInvocation"/> the transport already built and a <see cref="JsonElement"/>
/// the transport already parsed. <see cref="FlowMcpEndpointExtensions"/> is the one binding
/// that exists today, and a stdio one would supply the same two things — which is ADR-0004
/// applied to a transport MCP itself has more than one of.
/// </para>
/// <para>
/// <strong>The catalogue and the bindings are checked against each other at construction,
/// and a mismatch is a start-up failure.</strong> A tool published and unserved is an agent
/// told it can do something that will fail when it tries; a tool served and unpublished is
/// a flow reachable by a name the manifest does not contain, which is the transport
/// escaping the document that is supposed to describe it. Both are refused here rather than
/// discovered on a call, which is the trade <c>FlowBusSubscriptionRegistration</c> makes for
/// a transport it cannot serve.
/// </para>
/// </remarks>
public sealed class McpServer
{
    private readonly Dictionary<string, IFlowAgentTool> _bindings;

    /// <summary>Builds the surface from a manifest and the bindings generated for it.</summary>
    /// <param name="manifest">
    /// The application's <c>flowx.manifest.json</c> — in a generated host,
    /// <c>FlowX.Generated.FlowXManifest.Json</c>.
    /// </param>
    /// <param name="tools">Every flow bound as an agent tool, in any order.</param>
    /// <exception cref="InvalidOperationException">
    /// A published tool has no binding, or a binding names a flow the manifest does not
    /// publish as one.
    /// </exception>
    public McpServer(McpManifest manifest, IEnumerable<IFlowAgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(tools);

        Catalog = McpToolCatalog.From(manifest.Json);
        Resources = McpResourceCatalog.From(manifest.Json);

        var byFlow = new Dictionary<string, IFlowAgentTool>(StringComparer.Ordinal);

        foreach (var tool in tools)
        {
            byFlow[tool.FlowId] = tool;
        }

        _bindings = Bind(Catalog, byFlow);
    }

    /// <summary>The published tools, projected from the manifest.</summary>
    public McpToolCatalog Catalog { get; }

    /// <summary>
    /// The readable resources, projected from the same manifest.
    /// </summary>
    /// <remarks>
    /// Two projections of one document rather than one projection with two shapes, because they
    /// answer different questions and are read at different moments: a tool descriptor says what
    /// may be called and is read when a model chooses, and a resource is the graph itself and is
    /// read when a model is deciding whether calling anything is the right move. Both are pure
    /// functions of <see cref="McpManifest.Json"/>, so neither can drift from the other or from
    /// the artifact.
    /// </remarks>
    public McpResourceCatalog Resources { get; }

    /// <summary>The <c>tools/list</c> result, written straight to the response.</summary>
    /// <param name="writer">The response writer.</param>
    public void WriteToolList(Utf8JsonWriter writer) => McpToolJson.WriteToolList(writer, Catalog);

    /// <summary>The <c>resources/list</c> result, written straight to the response.</summary>
    /// <param name="writer">The response writer.</param>
    public void WriteResourceList(Utf8JsonWriter writer) => Resources.WriteList(writer);

    /// <summary>Runs the tool an agent named.</summary>
    /// <param name="name">The tool name from <c>params.name</c>.</param>
    /// <param name="arguments">The <c>params.arguments</c> object, or <c>null</c>.</param>
    /// <param name="invocation">
    /// What the transport read: correlation, tenant, idempotency and the agent's principal.
    /// </param>
    /// <param name="services">The request's scope.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <remarks>
    /// An unknown name is <see cref="McpErrors.UnknownTool"/> and not an exception, for
    /// ADR-0007's reason: a model choosing a tool that is not there has made a mistake it
    /// can correct from the answer, and every other outcome of this method is already a
    /// value.
    /// </remarks>
    public ValueTask<McpToolOutcome> CallAsync(
        string name,
        JsonElement? arguments,
        FlowInvocation invocation,
        IServiceProvider services,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _bindings.TryGetValue(name, out var tool)
            ? tool.InvokeAsync(name, arguments, invocation, services, ct)
            : ValueTask.FromResult(McpToolOutcome.Failed(McpErrors.UnknownTool(name)));
    }

    /// <summary>
    /// Joins the published tools to the registered bindings, on the flow's identity.
    /// </summary>
    /// <remarks>
    /// Keyed on the flow id rather than on the tool name, because the id is what both sides
    /// genuinely have: the manifest publishes it and the generated registration passes it.
    /// The name is derived from the id in one place — <see cref="McpToolCatalog"/> — so
    /// there is no second spelling of it for a binding to get wrong.
    /// </remarks>
    private static Dictionary<string, IFlowAgentTool> Bind(
        McpToolCatalog catalogue, Dictionary<string, IFlowAgentTool> byFlow)
    {
        var bindings = new Dictionary<string, IFlowAgentTool>(StringComparer.Ordinal);
        var unserved = new List<string>();

        foreach (var descriptor in catalogue.Tools)
        {
            if (byFlow.Remove(descriptor.FlowId, out var tool))
            {
                bindings[descriptor.Name] = tool;
            }
            else
            {
                unserved.Add(descriptor.FlowId);
            }
        }

        if (unserved.Count > 0)
        {
            throw new InvalidOperationException(
                $"The manifest publishes [{string.Join(", ", unserved)}] as agent tools and " +
                "this host has bound no flow to serve them. tools/list would advertise a " +
                "tool every tools/call is going to fail. Call the generated " +
                "AddFlowXAgentTools() from the assembly that declares the flows, or remove " +
                "the [AgentTrigger].");
        }

        if (byFlow.Count > 0)
        {
            throw new InvalidOperationException(
                $"[{string.Join(", ", byFlow.Keys)}] are bound as agent tools and the " +
                "manifest publishes no [AgentTrigger] for them. A flow reachable by a name " +
                "the manifest does not carry is a transport that has escaped the document " +
                "describing it. Declare [AgentTrigger] on the flow, or drop the binding.");
        }

        return bindings;
    }
}

/// <summary>
/// The manifest, as the container hands it to <see cref="McpServer"/>.
/// </summary>
/// <param name="Json">
/// The document — <c>FlowX.Generated.FlowXManifest.Json</c> in a generated host, which is
/// the same bytes the published <c>flowx.manifest.json</c> carries.
/// </param>
/// <remarks>
/// A wrapper rather than a registered <c>string</c>, because a bare <c>string</c> in a
/// container is a service any other registration can win and no reader can identify. This
/// names what it is.
/// </remarks>
public sealed record McpManifest(string Json);
