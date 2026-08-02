namespace FlowX.Compiler.Model;

/// <summary>
/// One flow's <c>[AgentTrigger]</c>, reduced to everything the tool binding needs and
/// nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Notice what is absent: the description, the confirmation mode, the permissions,
/// the side effects.</strong> Every one of them is on the <see cref="TriggerModel"/> the
/// manifest published or derivable from the capabilities it published, and every one of
/// them is read back out of the manifest at run time by <c>FlowX.Mcp.McpToolCatalog</c>.
/// Copying them into generated source would produce a second description of the same tool,
/// compiled into the user's assembly, able to disagree with the document <c>flowx diff</c>
/// gates on — which is the defect the manifest exists to remove, and which
/// docs/25-Remaining-Platform.md §3 rules out in one sentence: descriptors come from the
/// manifest, not from reflection, and not from a copy either.
/// </para>
/// <para>
/// So this model carries only what the manifest <em>cannot</em> carry, because it is not
/// data: the flow's compiled plan, its dispatcher, its projection, and the two contracts a
/// serialiser needs type metadata for. The flow id is the join between the two halves, and
/// it is the manifest's own.
/// </para>
/// <para>
/// The contrast with <see cref="HttpEndpointModel"/> is deliberate and is the point. An
/// HTTP endpoint's <em>address</em> — its method and route — has to be in generated source,
/// because a router needs it before any manifest is read. An agent tool has no address: it
/// is named by the flow's id, which the manifest already publishes, so nothing about the
/// tool's surface needs to be restated here.
/// </para>
/// </remarks>
public sealed class AgentToolModel
{
    /// <summary>Creates a model of one flow's agent tool binding.</summary>
    /// <param name="flowId">The flow's business id — the join to the manifest's descriptor.</param>
    /// <param name="flowTypeName">The flow's fully qualified type name.</param>
    /// <param name="methodName">The C# name of the generated extension method.</param>
    /// <param name="inputTypeName">The flow's input contract, fully qualified.</param>
    /// <param name="outputTypeName">The flow's output contract, fully qualified.</param>
    /// <param name="jsonContextTypeName">
    /// The fully qualified serialiser context declaring both contracts, or <c>null</c> when
    /// this compilation has no single unambiguous one.
    /// </param>
    public AgentToolModel(
        string flowId,
        string flowTypeName,
        string methodName,
        string inputTypeName,
        string outputTypeName,
        string? jsonContextTypeName)
    {
        FlowId = flowId;
        FlowTypeName = flowTypeName;
        MethodName = methodName;
        InputTypeName = inputTypeName;
        OutputTypeName = outputTypeName;
        JsonContextTypeName = jsonContextTypeName;
    }

    /// <summary>The flow's business id, exactly as the manifest states it.</summary>
    public string FlowId { get; }

    /// <summary>The flow's fully qualified type name.</summary>
    public string FlowTypeName { get; }

    /// <summary>The C# name of the generated extension method, e.g. <c>AddPlaceOrderFlowTool</c>.</summary>
    public string MethodName { get; }

    /// <summary>The flow's input contract, fully qualified.</summary>
    public string InputTypeName { get; }

    /// <summary>The flow's output contract, fully qualified.</summary>
    public string OutputTypeName { get; }

    /// <summary>
    /// The serialiser context to read both contracts' metadata from, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <c>null</c> means the compilation offered no single <c>JsonSerializerContext</c>
    /// declaring <c>[JsonSerializable]</c> for both contracts — none, or several. The
    /// binding is still generated; only the no-argument overload that would have had to
    /// pick one is not, which is <c>EndpointEmitter</c>'s rule for the same condition.
    /// </remarks>
    public string? JsonContextTypeName { get; }
}
