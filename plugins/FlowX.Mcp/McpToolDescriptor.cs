namespace FlowX.Mcp;

/// <summary>
/// One agent tool, as <c>tools/list</c> publishes it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every field is read out of <c>flowx.manifest.json</c> and none is read anywhere
/// else.</strong> The manifest already carries a complete description of each flow — its
/// business identity, its input contract, its declared triggers, and through its steps the
/// authorisation stance, idempotency and side effects of every capability it runs — so a
/// tool descriptor is a projection of an existing artifact rather than a second description
/// of the same flow (docs/25-Remaining-Platform.md §3).
/// </para>
/// <para>
/// The alternative was reflection over the flow's CLR types at start-up. It would have
/// produced a second account of the same facts, published to agents, able to disagree with
/// the one <c>flowx diff</c> gates on — which is the defect class the manifest exists to
/// remove. <see cref="McpToolCatalog.From"/> takes the manifest and nothing else, so there
/// is no second input for a descriptor to be derived from.
/// </para>
/// </remarks>
public sealed class McpToolDescriptor
{
    internal McpToolDescriptor(
        string name,
        string flowId,
        string? description,
        string inputContract,
        IReadOnlyList<string> sensitiveInputMembers,
        IReadOnlyList<string> requiredPermissions,
        IReadOnlyList<string> sideEffects,
        bool idempotent,
        bool confirmationRequired)
    {
        Name = name;
        FlowId = flowId;
        Description = description;
        InputContract = inputContract;
        SensitiveInputMembers = sensitiveInputMembers;
        RequiredPermissions = requiredPermissions;
        SideEffects = sideEffects;
        Idempotent = idempotent;
        ConfirmationRequired = confirmationRequired;
    }

    /// <summary>
    /// The tool name an agent calls, e.g. <c>order_place</c>.
    /// </summary>
    /// <remarks>
    /// The flow's own id with every <c>.</c> replaced by <c>_</c>, which is the spelling
    /// docs/13-AI-Native.md §6's descriptor shows and the only one MCP's name grammar
    /// accepts. A derivation rather than a declaration: an <c>[AgentTrigger]</c> names no
    /// tool, so there is nothing here that could disagree with the flow's identity.
    /// Two flows whose ids collide under that substitution are refused by
    /// <see cref="McpToolCatalog.From"/> rather than silently sharing a name.
    /// </remarks>
    public string Name { get; }

    /// <summary>The flow's business identity, exactly as the manifest publishes it.</summary>
    public string FlowId { get; }

    /// <summary>
    /// What the model is shown when it selects a tool — the <c>[AgentTrigger]</c>'s
    /// <c>Description</c>, as the manifest's <c>trigger.description</c>.
    /// </summary>
    /// <remarks>
    /// Nullable, because a trigger attribute whose arguments a build could not interpret
    /// reaches the manifest as a bare kind. A tool with no description is still a tool; one
    /// with a description invented here would be a claim about the flow that its author
    /// never made.
    /// </remarks>
    public string? Description { get; }

    /// <summary>
    /// The fully-qualified CLR type of the flow's input, from the manifest's
    /// <c>flow.input.type</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is published instead of a JSON Schema, and that is a limitation of the
    /// manifest rather than a choice made here.</strong> docs/13-AI-Native.md §6 shows
    /// <c>"inputSchema": { "$ref": "#/schemas/PlaceOrder" }</c>, and the top-level
    /// <c>schemas</c> map that <c>$ref</c> points into is one of the thirteen fields the
    /// committed schema declares and nothing writes — ADR-0017's first unmet freeze
    /// condition, restated in that document's own opening note.
    /// </para>
    /// <para>
    /// Generating a schema here by reflecting over the contract would supply the missing
    /// field from a second source, published to agents, unversioned and outside
    /// <c>flowx diff</c>. So the descriptor states the contract's identity and an open
    /// object schema, which is everything the manifest actually knows. On the build where
    /// <c>schemas</c> is written, the <c>inputSchema</c> this descriptor is rendered into
    /// gains the <c>$ref</c> and nothing else changes.
    /// </para>
    /// </remarks>
    public string InputContract { get; }

    /// <summary>
    /// Members of the input contract carrying <c>[Sensitive]</c>, from the manifest's
    /// <c>flow.input.sensitive</c>.
    /// </summary>
    /// <remarks>
    /// Published so a client can keep them out of a transcript, a log or a confirmation
    /// prompt. Empty for a contract that declares none, which is most of them.
    /// </remarks>
    public IReadOnlyList<string> SensitiveInputMembers { get; }

    /// <summary>
    /// Every permission the flow's capabilities name, deduplicated and ordinally sorted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A list, where docs/13-AI-Native.md §6 shows a single
    /// <c>requiresPermission</c>.</strong> A flow is a sequence of capabilities and each
    /// declares its own stance, so a flow that captures a payment and reserves inventory
    /// genuinely requires both grants. Publishing the first of them would tell an agent it
    /// could call a tool that the step loop will refuse halfway through.
    /// </para>
    /// <para>
    /// Advisory, and only advisory. The decision is taken in the step loop against the
    /// agent's own claims (ADR-0027); this is what an agent is told so it can ask for the
    /// grant instead of guessing.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> RequiredPermissions { get; }

    /// <summary>
    /// The union of the declared side effects of every capability the flow runs, ordinally
    /// sorted.
    /// </summary>
    /// <remarks>
    /// The second of docs/13-AI-Native.md §6's three inherited safety properties: a
    /// confirmation prompt naming <c>payment-gateway</c> is accurate because the capability
    /// declared it, rather than guessed from a method name.
    /// </remarks>
    public IReadOnlyList<string> SideEffects { get; }

    /// <summary>
    /// Whether every capability the flow runs declares itself idempotent.
    /// </summary>
    /// <remarks>
    /// The conjunction, not the disjunction: a flow is safe to call twice only if every
    /// step of it is. A flow with no capability steps at all is idempotent, which is the
    /// truthful reading of one that does nothing an agent could repeat.
    /// </remarks>
    public bool Idempotent { get; }

    /// <summary>
    /// Whether the declared <c>ConfirmationMode</c> demands a human before this call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Always</c> is true, <c>Never</c> is false, and <c>RequiredForSideEffects</c> —
    /// the attribute's default — is true exactly when <see cref="SideEffects"/> is
    /// non-empty. Both halves are in the manifest, so the answer is too.
    /// </para>
    /// <para>
    /// <strong>An annotation, not a round trip.</strong> docs/13-AI-Native.md §6's diagram
    /// draws the MCP server prompting a human directly; MCP puts human-in-the-loop on the
    /// client, which is the side that has a human attached to it. The server's job is to
    /// state the requirement accurately, which is what this is.
    /// </para>
    /// </remarks>
    public bool ConfirmationRequired { get; }
}
