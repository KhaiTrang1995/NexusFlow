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
        bool hasStepPolicies,
        bool hasEmit,
        bool hasTimers,
        bool hasAuthorizedSteps,
        bool hasAuditedSteps)
    {
        HasStepPolicies = hasStepPolicies;
        HasTimers = hasTimers;
        HasAuthorizedSteps = hasAuthorizedSteps;
        HasAuditedSteps = hasAuditedSteps;
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
    /// <strong>It is the whole of the gate on this path.</strong> The one policy an undo can
    /// carry is a compensation retry, and a flow that declares none unwinds exactly as it did
    /// before the policy engine. The forward path has a gate of its own,
    /// <see cref="HasStepPolicies"/>, because it counts different kinds and is read in a
    /// different loop.
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

    /// <summary>True when any step carries a forward policy the runtime executes.</summary>
    /// <remarks>
    /// <para>
    /// Precomputed for the reason <see cref="HasParallel"/> is, and read in the same shape: a
    /// flow whose steps declare nothing the engine applies must not pay for the ones that do.
    /// The step loop reads it before it reads <see cref="StepNode.StepPolicy"/>, so an
    /// unpoliced plan reaches no attempt loop, no clock read and no timeout source — one
    /// predictable always-false comparison on a field the plan already holds, and the
    /// figure <c>EngineAllocationTests</c> records is unchanged.
    /// </para>
    /// <para>
    /// <strong>It counts what executes, not what was declared.</strong> A step whose chain
    /// holds only a <c>RateLimit</c>, an <c>Idempotency</c> window, a <c>Cache</c> or an
    /// <c>Audit</c> leaves this false: those stages are not implemented, <c>StepPolicy.From</c>
    /// resolves them to <see cref="StepPolicy.None"/>, and a flag that were true for them
    /// would charge the flow for a policy nothing applies — which is the exact cost this flag
    /// exists to refuse. See
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0023-policy-stages-hook-through-the-plan.md">ADR-0023</a>.
    /// </para>
    /// </remarks>
    public bool HasStepPolicies { get; }

    /// <summary>True when any step waits on a clock: a timer, or a suspension point.</summary>
    /// <remarks>
    /// <para>
    /// Precomputed for the reason <see cref="HasParallel"/> is, and read in the same shape: a
    /// flow that never waits must not pay for the ones that do. It is what a timer sweep reads
    /// to decide whether a registered flow can have a parked instance at all, and what the
    /// step loop reads before it consults the instance's recorded wake instant.
    /// </para>
    /// <para>
    /// <strong>It is false for every <see cref="ExecutionProfile.Ephemeral"/> plan, by
    /// construction rather than by convention.</strong> Both kinds it counts are refused below
    /// <see cref="ExecutionProfile.Durable"/> by
    /// <see cref="ValidateProfileSupportsEveryStep"/>, so the ephemeral path reaches nothing
    /// this flag guards and budget B2 is untouched — the same bargain <see cref="HasEmit"/>
    /// struck for the outbox.
    /// </para>
    /// </remarks>
    public bool HasTimers { get; }

    /// <summary>True when any step of this plan can refuse a caller.</summary>
    /// <remarks>
    /// <para>
    /// Precomputed for the reason <see cref="HasParallel"/> is, and read in the same shape as
    /// <see cref="HasStepPolicies"/>: the step loop reads it before it reads
    /// <see cref="StepNode.StepAuthorization"/>, so a plan that can refuse nobody reaches no
    /// claim lookup and no principal read — one predictable always-false comparison on a field
    /// the plan already holds.
    /// </para>
    /// <para>
    /// <strong>It counts what can refuse, not what was declared.</strong> This is the whole of
    /// why it is not simply "some step declared a stance": <c>FLOWX1010</c> makes a stance
    /// mandatory, so a flag of that shape would be true for every compiled flow in existence
    /// and would gate nothing. A step declaring <see cref="Authorization.Public"/> or
    /// <see cref="Authorization.Internal"/> admits every caller — the first by declaration,
    /// the second because a trigger addresses a flow and never a capability — so both leave
    /// this false and cost the flow nothing.
    /// </para>
    /// <para>
    /// See
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0027-authorisation-runs-in-the-step-loop.md">ADR-0027</a>,
    /// which is
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0023-policy-stages-hook-through-the-plan.md">ADR-0023</a>'s
    /// bargain struck a second time, for a second stage.
    /// </para>
    /// </remarks>
    public bool HasAuthorizedSteps { get; }

    /// <summary>True when any step of this plan writes an audit record.</summary>
    /// <remarks>
    /// <para>
    /// The same shape as <see cref="HasAuthorizedSteps"/> and for the same reason, applied to
    /// stage 7: the step loop reads this before it reads <see cref="StepNode.StepAudit"/>, so a
    /// flow that audits nothing reads no principal, asks the dispatcher for no payload and
    /// touches no sink. One predictable always-false comparison against a field the plan
    /// already holds.
    /// </para>
    /// <para>
    /// <strong>It counts what writes a record, never what was declared.</strong>
    /// <see cref="StepAudit.From"/> resolves an <c>Audit</c> with a blank category to
    /// <see cref="StepAudit.None"/>, so a declaration that could produce nothing a compliance
    /// query could select leaves this false — the bargain <see cref="HasStepPolicies"/> and
    /// <see cref="HasAuthorizedSteps"/> both strike.
    /// </para>
    /// <para>
    /// This is the fourth flag of
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0023-policy-stages-hook-through-the-plan.md">ADR-0023</a>'s
    /// shape, and that record's own "revisit when" names a third as the point at which the set
    /// of them should probably become one bit set. It is noted rather than acted on here: the
    /// four are read at four different points of the loop and a bit set would be one field read
    /// plus a mask at each, which is not cheaper and is harder to read.
    /// </para>
    /// </remarks>
    public bool HasAuditedSteps { get; }

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
        var stepPolicies = false;
        var emit = false;
        var timers = false;
        var authorized = false;
        var audited = false;

        foreach (var step in graph.Steps)
        {
            if (step.IsCompensable)
            {
                compensable.Add(step.Index);
            }

            compensationPolicies |= step.CompensationRetry.IsRetrying;

            // The forward half of the same bargain, and the same shape: what is counted is
            // what the engine will apply, never what the author declared. A chain of policies
            // whose stages are all unimplemented resolves to StepPolicy.None and leaves this
            // false, so declaring one costs the flow nothing until the stage that runs it lands.
            stepPolicies |= step.StepPolicy.IsActive;

            // The same bargain again, for the authorisation stage: what is counted is what can
            // say no. A Public or Internal step resolves to StepAuthorization.None and leaves
            // this false, so a flow nobody can be refused from takes the path it always took.
            authorized |= step.StepAuthorization.CanRefuse;

            // And once more for stage 7. An Audit is resolved off the step's own chain and
            // read after its commit, which is why it is a flag of its own rather than a field
            // of StepPolicy: the four resilience kinds and the cache all wrap the dispatch,
            // and this one follows it.
            audited |= step.StepAudit.IsAudited;

            parallel |= step.Kind == StepKind.Parallel ||
                        (step.Kind == StepKind.ForEach && step.MaxDegreeOfParallelism > 1);

            // A detached sub-flow counts too. It never touches this flow's context — it
            // gets its own — so it deliberately does *not* set `parallel`; but it is still
            // a composition, and the flag is read to decide whether the end of the flow has
            // sub-flow bookkeeping to do.
            subFlow |= step.Kind == StepKind.SubFlow;

            emit |= step.Kind == StepKind.Emit;

            // Both kinds, because both park the instance until an instant the row records and
            // both are woken by the same sweep. The flag answers "can an instance of this flow
            // be waiting on a clock", and a suspension point with an armed timeout can.
            timers |= step.Kind is StepKind.Delay or StepKind.AwaitSignal;

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
            stepPolicies,
            emit,
            timers,
            authorized,
            audited);
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
            if (step.Kind == StepKind.AwaitSignal)
            {
                throw new InvalidFlowPlanException(
                    $"Flow '{flow.Id}' runs under the {flow.Profile} profile, but step " +
                    $"{step.Index} awaits signal '{step.SignalType}'. A suspension point " +
                    "requires the Durable profile: an in-memory wait does not survive a " +
                    "deployment, a crash, or a scale-in.");
            }

            // The same rule and the same sentence, because it is the same fact. A timer
            // outside a journal has nowhere to record when it is due, so the only way to
            // honour it in memory is to hold the process for the duration — which is a
            // Task.Delay, and is exactly what a durable timer exists not to be.
            if (step.Kind == StepKind.Delay)
            {
                throw new InvalidFlowPlanException(
                    $"Flow '{flow.Id}' runs under the {flow.Profile} profile, but step " +
                    $"{step.Index} delays for {step.Delay}. A durable timer requires the " +
                    "Durable profile: there is nowhere outside a journal to record when it " +
                    "is due, and a wait that survives nothing is a held thread.");
            }
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"{Flow.QualifiedName}: {Graph.Count} step(s)";
}
