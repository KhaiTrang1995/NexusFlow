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
        bool hasParallel,
        bool hasSubFlow,
        bool hasCompensationPolicies,
        bool hasEmit)
    {
        Flow = flow;
        Graph = graph;
        CompensableStepIndices = compensableStepIndices;
        SideEffects = sideEffects;
        HasParallel = hasParallel;
        HasSubFlow = hasSubFlow;
        HasCompensationPolicies = hasCompensationPolicies;
        HasEmit = hasEmit;
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
    /// True when more than one thread can touch this flow's context at once: the flow
    /// contains a <see cref="StepKind.Parallel"/> fork, or a <see cref="StepKind.ForEach"/>
    /// that may run several iterations concurrently.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Precomputed here for the same reason <see cref="CompensableStepIndices"/> is, but
    /// with a sharper consequence: it is what the runtime reads to decide whether the
    /// flow's state bag needs guarding. A flow that does not fork pays nothing for the
    /// possibility that another one does, which is what keeps budget B2 at a hard zero for
    /// the linear, conditional and switch paths.
    /// </para>
    /// <para>
    /// <strong>A <c>ForEach</c> counts only when its bound is greater than one.</strong>
    /// The question this property answers is not "does the flow loop" but "can two threads
    /// reach the context", and a loop that runs one element at a time cannot — so a
    /// sequential iteration keeps the unguarded fast path, exactly as a conditional does.
    /// </para>
    /// </remarks>
    public bool HasParallel { get; }

    /// <summary>True when any step composes another flow.</summary>
    /// <remarks>
    /// <para>
    /// Precomputed for the same reason <see cref="HasParallel"/> is, and read on the
    /// <em>success</em> path, which is the unusual part. A synchronous sub-flow that
    /// completed with compensations pending keeps its context rented until the parent
    /// finishes — the parent may still have to undo it — so a flow that composes another
    /// has one thing to do at the end that no other flow does: give those contexts back to
    /// the pool. Walking the compensation stack to find them costs an iterator, and a flow
    /// with no sub-flow must not pay it. That is what this flag buys, and it is why budget
    /// B2 stays a hard zero for the shapes that have always had it.
    /// </para>
    /// </remarks>
    public bool HasSubFlow { get; }

    /// <summary>True when any step's compensation carries a policy the runtime executes.</summary>
    /// <remarks>
    /// <para>
    /// Precomputed for the reason <see cref="HasParallel"/> is, and read in the same shape: a
    /// flow whose undos declare nothing must not pay for the ones that do. The unwind of a
    /// plain saga therefore stays the single dispatch per entry it always was — one predictable
    /// always-false comparison, no retry bookkeeping, no clock read, and the failure-path
    /// allocation figure `EngineAllocationTests` records is unchanged.
    /// </para>
    /// <para>
    /// <strong>It is the whole of the gate.</strong> The one policy this runtime executes is a
    /// compensation retry, and a flow that declares none executes no policy at all — which is
    /// what keeps the Policy Engine in P4 while the unwind gets the slice it cannot do without.
    /// </para>
    /// </remarks>
    public bool HasCompensationPolicies { get; }

    /// <summary>True when any step publishes a domain event.</summary>
    /// <remarks>
    /// <para>
    /// Precomputed for the reason <see cref="HasParallel"/> is, and read in the same shape: a
    /// flow that emits nothing must not pay for the ones that do. The step-commit path reads
    /// it before it reads <c>StepJournalEntry.Event</c>, so a plan with no <c>Emit</c> node
    /// never touches the outbox seam at all — no list, no array, no branch beyond one
    /// predictable always-false comparison on a field the plan already holds.
    /// </para>
    /// <para>
    /// <strong>It is also what keeps budget B2 a hard zero.</strong> An <c>Emit</c> step is
    /// legal under either profile, and under <see cref="ExecutionProfile.Ephemeral"/> there
    /// is no transaction for an event to be part of — so the ephemeral path stages nothing
    /// and allocates nothing, whatever the dispatcher would have described.
    /// <c>EngineAllocationTests</c> measures exactly that plan.
    /// </para>
    /// </remarks>
    public bool HasEmit { get; }

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
        var subFlow = false;
        var compensationPolicies = false;
        var emit = false;

        foreach (var step in graph.Steps)
        {
            if (step.IsCompensable)
            {
                compensable.Add(step.Index);
            }

            compensationPolicies |= step.CompensationRetry.IsRetrying;

            parallel |= step.Kind == StepKind.Parallel ||
                        (step.Kind == StepKind.ForEach && step.MaxDegreeOfParallelism > 1);

            // A detached sub-flow counts too. It never touches this flow's context — it
            // gets its own — so it deliberately does *not* set `parallel`; but it is still
            // a composition, and the flag is read to decide whether the end of the flow has
            // sub-flow bookkeeping to do.
            subFlow |= step.Kind == StepKind.SubFlow;

            emit |= step.Kind == StepKind.Emit;

            AddEffects(effects, step.Capability);
            AddEffects(effects, step.Compensation);
        }

        return new ExecutionPlan(
            flow,
            graph,
            compensable.ToImmutable(),
            [.. effects],
            parallel,
            subFlow,
            compensationPolicies,
            emit);
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
