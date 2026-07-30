using System.Collections.Immutable;

namespace FlowX;

/// <summary>
/// A flow, compiled: its identity, its graph, and the facts about it that the engine
/// would otherwise have to derive at run time.
/// </summary>
/// <remarks>
/// <para>
/// This is the artifact
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0002-compile-time-orchestration.md">ADR-0002</a>
/// is a bet on. Everything precomputed here — the compensable step indices, the
/// aggregated side effects — is work the engine does not do per execution.
/// </para>
/// <para>
/// The compensable indices matter most: they are read on the <em>failure</em> path,
/// where an incident is already in progress and scanning the graph would add
/// latency at exactly the wrong moment.
/// </para>
/// </remarks>
public sealed class ExecutionPlan
{
    private ExecutionPlan(
        FlowDescriptor flow,
        StepGraph graph,
        ImmutableArray<int> compensableStepIndices,
        ImmutableArray<string> sideEffects,
        bool hasParallel)
    {
        Flow = flow;
        Graph = graph;
        CompensableStepIndices = compensableStepIndices;
        SideEffects = sideEffects;
        HasParallel = hasParallel;
    }

    /// <summary>The flow this plan executes.</summary>
    public FlowDescriptor Flow { get; }

    /// <summary>The compiled step sequence.</summary>
    public StepGraph Graph { get; }

    /// <summary>
    /// Indices of steps declaring a compensation, ascending. Precomputed so the
    /// failure path never scans.
    /// </summary>
    public ImmutableArray<int> CompensableStepIndices { get; }

    /// <summary>
    /// Every distinct side effect any step can produce, sorted ordinally.
    /// </summary>
    /// <remarks>
    /// Sorted because this reaches the manifest, and two builds of identical source
    /// must produce byte-identical output — otherwise <c>flowx diff</c> reports
    /// changes nobody made, and people stop reading it.
    /// </remarks>
    public ImmutableArray<string> SideEffects { get; }

    /// <summary>True when any step declares a compensation.</summary>
    public bool HasCompensation => !CompensableStepIndices.IsEmpty;

    /// <summary>
    /// True when any step is a <see cref="StepKind.Parallel"/> fork, and therefore when
    /// more than one thread can touch this flow's context at once.
    /// </summary>
    /// <remarks>
    /// Precomputed here for the same reason <see cref="CompensableStepIndices"/> is, but
    /// with a sharper consequence: it is what the runtime reads to decide whether the
    /// flow's state bag needs guarding. A flow that does not fork pays nothing for the
    /// possibility that another one does, which is what keeps budget B2 at a hard zero for
    /// the linear, conditional and switch paths.
    /// </remarks>
    public bool HasParallel { get; }

    /// <summary>Builds a validated plan.</summary>
    /// <param name="flow">The flow's identity and profile.</param>
    /// <param name="graph">Its compiled step sequence.</param>
    /// <exception cref="InvalidFlowPlanException">
    /// The graph contains a step the flow's execution profile cannot support.
    /// </exception>
    public static ExecutionPlan Create(FlowDescriptor flow, StepGraph graph)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(graph);

        ValidateProfileSupportsEveryStep(flow, graph);

        var compensable = ImmutableArray.CreateBuilder<int>();
        var effects = new SortedSet<string>(StringComparer.Ordinal);
        var parallel = false;

        foreach (var step in graph.Steps)
        {
            if (step.IsCompensable)
            {
                compensable.Add(step.Index);
            }

            parallel |= step.Kind == StepKind.Parallel;

            AddEffects(effects, step.Capability);
            AddEffects(effects, step.Compensation);
        }

        return new ExecutionPlan(flow, graph, compensable.ToImmutable(), [.. effects], parallel);
    }

    private static void AddEffects(SortedSet<string> effects, CapabilityDescriptor? capability)
    {
        if (capability is null)
        {
            return;
        }

        foreach (var effect in capability.SideEffects)
        {
            effects.Add(effect);
        }
    }

    private static void ValidateProfileSupportsEveryStep(FlowDescriptor flow, StepGraph graph)
    {
        if (flow.Profile == ExecutionProfile.Durable)
        {
            return;
        }

        foreach (var step in graph.Steps)
        {
            if (step.Kind != StepKind.AwaitSignal)
            {
                continue;
            }

            throw new InvalidFlowPlanException(
                $"Flow '{flow.Id}' runs under the {flow.Profile} profile, but step " +
                $"{step.Index} awaits signal '{step.SignalType}'. A suspension point " +
                "requires the Durable profile: an in-memory wait does not survive a " +
                "deployment, a crash, or a scale-in.");
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"{Flow.QualifiedName}: {Graph.Count} step(s)";
}
