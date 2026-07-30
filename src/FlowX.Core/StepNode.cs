namespace FlowX;

/// <summary>What a step does. Only the kinds a linear P0 flow can contain.</summary>
/// <remarks>
/// Branching kinds — condition, switch, parallel, for-each, sub-flow — arrive with
/// the generator at WP-5, together with the DSL surface that can express them.
/// Adding them here first would be speculative: shapes nothing can construct and no
/// test can exercise.
/// </remarks>
public enum StepKind
{
    /// <summary>Invokes a capability.</summary>
    Capability = 0,

    /// <summary>Publishes a domain event.</summary>
    Emit = 1,

    /// <summary>Suspends until an external signal arrives. Durable flows only.</summary>
    AwaitSignal = 2,
}

/// <summary>
/// One node in a compiled flow graph.
/// </summary>
/// <remarks>
/// Constructed only through the factories below, so an invalid shape — an emit step
/// carrying a capability, a step compensating itself — cannot be represented. The
/// engine's step loop therefore needs no defensive checks on the hot path.
/// </remarks>
public sealed record StepNode
{
    private StepNode(int index, StepKind kind)
    {
        Index = index;
        Kind = kind;
    }

    /// <summary>Position in the graph, contiguous from zero.</summary>
    public int Index { get; }

    /// <summary>What this step does.</summary>
    public StepKind Kind { get; }

    /// <summary>The capability invoked, or <c>null</c> for non-capability kinds.</summary>
    public CapabilityDescriptor? Capability { get; private init; }

    /// <summary>The business inverse, run on the failure path. <c>null</c> when the step is not compensable.</summary>
    public CapabilityDescriptor? Compensation { get; private init; }

    /// <summary>Policies wrapping this step, in execution order.</summary>
    public PolicyChain Policies { get; private init; } = PolicyChain.Empty;

    /// <summary>The event published by an <see cref="StepKind.Emit"/> step.</summary>
    public string? EventType { get; private init; }

    /// <summary>The signal awaited by an <see cref="StepKind.AwaitSignal"/> step.</summary>
    public string? SignalType { get; private init; }

    /// <summary>How long an <see cref="StepKind.AwaitSignal"/> step waits before timing out.</summary>
    public TimeSpan? SignalTimeout { get; private init; }

    /// <summary>True when this step declared a compensation.</summary>
    public bool IsCompensable => Compensation is not null;

    /// <summary>Creates a capability step.</summary>
    /// <param name="index">Position in the graph.</param>
    /// <param name="capability">The capability to invoke.</param>
    /// <param name="compensation">The business inverse, if this step is compensable.</param>
    /// <param name="policies">Policies wrapping the step.</param>
    /// <exception cref="InvalidFlowPlanException">The compensation is the capability itself.</exception>
    public static StepNode ForCapability(
        int index,
        CapabilityDescriptor capability,
        CapabilityDescriptor? compensation = null,
        PolicyChain? policies = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentNullException.ThrowIfNull(capability);

        if (compensation is not null && compensation.Id == capability.Id)
        {
            throw new InvalidFlowPlanException(
                $"Step {index} declares '{capability.Id}' as its own compensation. A " +
                "compensation must undo the step, not repeat it.");
        }

        return new StepNode(index, StepKind.Capability)
        {
            Capability = capability,
            Compensation = compensation,
            Policies = policies ?? PolicyChain.Empty,
        };
    }

    /// <summary>Creates an event-publishing step.</summary>
    /// <param name="index">Position in the graph.</param>
    /// <param name="eventType">Event identity, <c>&lt;domain&gt;.&lt;event&gt;</c>.</param>
    public static StepNode ForEmit(int index, string eventType)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        return new StepNode(index, StepKind.Emit)
        {
            EventType = Identifiers.RequireIdentity(eventType, nameof(eventType)),
        };
    }

    /// <summary>Creates a suspension point.</summary>
    /// <param name="index">Position in the graph.</param>
    /// <param name="signalType">Signal identity, <c>&lt;domain&gt;.&lt;signal&gt;</c>.</param>
    /// <param name="timeout">How long to wait. Must be positive.</param>
    /// <remarks>
    /// Whether this step is <em>permitted</em> depends on the flow's execution
    /// profile, which the node cannot see. <see cref="ExecutionPlan"/> enforces it.
    /// </remarks>
    public static StepNode ForAwaitSignal(int index, string signalType, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        return new StepNode(index, StepKind.AwaitSignal)
        {
            SignalType = Identifiers.RequireIdentity(signalType, nameof(signalType)),
            SignalTimeout = timeout,
        };
    }

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        StepKind.Capability => $"[{Index}] {Capability}",
        StepKind.Emit => $"[{Index}] emit {EventType}",
        StepKind.AwaitSignal => $"[{Index}] await {SignalType} ({SignalTimeout})",
        _ => $"[{Index}] {Kind}",
    };
}
