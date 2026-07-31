using System.Collections.Immutable;

namespace FlowX;

/// <summary>What a step does.</summary>
/// <remarks>
/// <para>
/// <see cref="Branch"/>, <see cref="Switch"/> and <see cref="Jump"/> are how a
/// conditional is represented: structured jumps <em>inside</em> the flat step array, not
/// a nested graph. That is what keeps the engine one loop over one array — see
/// <see cref="StepNode.Target"/> for the layout and <c>FlowEngine</c> for the loop it
/// buys.
/// </para>
/// <para>
/// <see cref="Parallel"/> is the first kind that is <em>not</em> a jump. It uses the same
/// flat layout — one node carrying a target per block — but the engine runs every block
/// instead of choosing one, so the loop index stops describing execution for the duration
/// of the step. See <see cref="StepNode.BranchTargets"/> for what the layout still buys.
/// </para>
/// <para>
/// <see cref="ForEach"/> is the first kind whose block runs <em>more than once</em>. It
/// keeps the flat layout — the body is the contiguous span between the node and its join
/// — and the engine runs that one span once per element. Nothing in the array is
/// duplicated and no target points backwards, so the termination proof is unchanged; what
/// changes is that the number of times a step runs is no longer bounded by the array's
/// length. See <see cref="ForEach"/> for what bounds it instead.
/// </para>
/// <para>
/// The remaining branching kind — sub-flow — arrives with the DSL surface that can express
/// it. Adding it here first would be speculative: a shape nothing can construct and no
/// test can exercise.
/// </para>
/// </remarks>
public enum StepKind
{
    /// <summary>Invokes a capability.</summary>
    Capability = 0,

    /// <summary>Publishes a domain event.</summary>
    Emit = 1,

    /// <summary>Suspends until an external signal arrives. Durable flows only.</summary>
    AwaitSignal = 2,

    /// <summary>
    /// Evaluates a predicate: control continues at the next step when it holds, and at
    /// <see cref="StepNode.Target"/> when it does not.
    /// </summary>
    Branch = 3,

    /// <summary>Transfers control unconditionally to <see cref="StepNode.Target"/>.</summary>
    Jump = 4,

    /// <summary>
    /// Selects one of <see cref="StepNode.CaseTargets"/> by matching a value, and
    /// continues at <see cref="StepNode.Target"/> when none of them matches.
    /// </summary>
    /// <remarks>
    /// A <see cref="Branch"/> asks a yes/no question and so needs one target; this asks
    /// <em>which one</em> and so needs several. Compiling it into a chain of branches
    /// instead would have re-evaluated the selector once per case and published the
    /// author's <c>Switch</c> to the manifest as a nest of conditionals — two lies for no
    /// saving, since the flat layout is identical either way.
    /// </remarks>
    Switch = 5,

    /// <summary>
    /// Runs every one of <see cref="StepNode.BranchTargets"/> concurrently and continues at
    /// <see cref="StepNode.Target"/> once <see cref="StepNode.Merge"/> is satisfied.
    /// </summary>
    /// <remarks>
    /// The one kind whose blocks all run. A <see cref="Switch"/> and a <see cref="Parallel"/>
    /// have the identical flat layout — a node, then one block per arm, each closed by a
    /// jump to the join — and differ only in what the engine does when it reaches the node.
    /// Keeping the layout identical is what let the branch shape reuse the arithmetic,
    /// the graph validation and the manifest's <c>branches</c> array unchanged.
    /// </remarks>
    Parallel = 6,

    /// <summary>
    /// Runs the span between this node and <see cref="StepNode.Target"/> once per element
    /// of a collection, with at most
    /// <see cref="StepNode.MaxDegreeOfParallelism"/> iterations in flight.
    /// </summary>
    /// <remarks>
    /// The one kind whose block runs repeatedly. A <see cref="Parallel"/> runs each of its
    /// blocks once; this runs its single block <em>n</em> times, where <em>n</em> comes
    /// from the collection rather than from the plan. The layout is otherwise a fork with
    /// exactly one branch, which is why the engine reuses the same range machinery.
    /// </remarks>
    ForEach = 7,
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

    /// <summary>
    /// Where control transfers, for the two control-flow kinds. <c>null</c> for every
    /// other kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <see cref="StepKind.Branch"/> carries its <em>false</em> target. The true path
    /// needs no target at all: the <c>then</c> block is laid out immediately after the
    /// branch, so taking it means continuing at <c>Index + 1</c> exactly as an ordinary
    /// step does. A <see cref="StepKind.Jump"/> carries its unconditional destination,
    /// and exists only to close a <c>then</c> block so control skips the
    /// <c>Otherwise</c> block that follows it.
    /// </para>
    /// <para>
    /// One property rather than two named ones, because the engine reads it the same way
    /// in both cases, and because a node holding two mutually exclusive targets would be
    /// a shape the factories then have to forbid.
    /// </para>
    /// <para>
    /// A <see cref="StepKind.Switch"/> carries its <em>default</em> target here — where
    /// control goes when no case matched — for the same reason: it is the one destination
    /// that is not in <see cref="CaseTargets"/>, and the engine reads it the same way it
    /// reads a branch's.
    /// </para>
    /// <para>
    /// A target equal to the graph's length is legal and means <em>past the last step</em>:
    /// a conditional at the end of a flow jumps out of it. Targets are validated for range
    /// by <see cref="StepGraph"/>, which is the only place that knows the length.
    /// </para>
    /// </remarks>
    public int? Target { get; private init; }

    /// <summary>
    /// Where each case of a <see cref="StepKind.Switch"/> begins, in declaration order.
    /// Empty for every other kind.
    /// </summary>
    /// <remarks>
    /// Indexed by the arm the dispatcher returns from <c>IStepDispatcher.Select</c>, so
    /// selecting a case is one array read and one assignment to the loop index — no
    /// dictionary, no boxing of the selector's value, and nothing allocated. The values
    /// being compared never appear here at all; they live in the generated dispatcher,
    /// which is what keeps business data out of the plan and out of the manifest.
    /// </remarks>
    public ImmutableArray<int> CaseTargets { get; private init; } = ImmutableArray<int>.Empty;

    /// <summary>
    /// Where each branch of a <see cref="StepKind.Parallel"/> begins, in declaration order.
    /// Empty for every other kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Strictly ascending, and every entry lies between this node and <see cref="Target"/>.
    /// That is not tidiness: it is what makes each branch's <em>range</em> derivable without
    /// storing it. Branch <c>k</c> occupies <c>[BranchTargets[k], BranchTargets[k + 1])</c>,
    /// and the last occupies <c>[BranchTargets[^1], Target)</c> — so the engine runs a
    /// branch by running the ordinary step loop over a sub-range, and the same forward-only
    /// target rule that proves a linear flow terminates proves a branch does.
    /// </para>
    /// <para>
    /// Distinct from <see cref="CaseTargets"/> even though the arithmetic is identical,
    /// because the two mean opposite things: a case target is a destination control
    /// <em>may</em> take, a branch target is a range that <em>will</em> run. Sharing one
    /// property would have made <c>IsControlTransfer</c>, the engine's dispatch and every
    /// reader of a plan ambiguous about which it was looking at.
    /// </para>
    /// </remarks>
    public ImmutableArray<int> BranchTargets { get; private init; } = ImmutableArray<int>.Empty;

    /// <summary>
    /// How a <see cref="StepKind.Parallel"/> joins its branches.
    /// <see cref="MergeStrategy.AllMustSucceed"/> for every other kind, and unread there.
    /// </summary>
    /// <remarks>
    /// Structure, not a value: it says how the flow is shaped, never anything about the
    /// data flowing through it, which is why it may safely reach the manifest where a
    /// predicate and a case value may not.
    /// </remarks>
    public MergeStrategy Merge { get; private init; }

    /// <summary>
    /// How many iterations of a <see cref="StepKind.ForEach"/> may be in flight at once.
    /// <c>1</c> for every other kind, and unread there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Already capped: the factory clamps the author's number to
    /// <see cref="MaxIterationConcurrency"/>, so the plan carries the bound the engine will
    /// actually honour rather than the wish the author expressed. Reading it back therefore
    /// tells the truth, which a number clamped later at the point of use would not.
    /// </para>
    /// <para>
    /// Structure, not a value — it says how the flow is shaped and nothing about the data
    /// flowing through it. It stays out of the manifest all the same, because the published
    /// schema has no field for it and inventing one to carry a tuning number is not worth a
    /// schema change.
    /// </para>
    /// </remarks>
    public int MaxDegreeOfParallelism { get; private init; } = 1;

    /// <summary>
    /// Whether a <see cref="StepKind.ForEach"/> runs its remaining elements after one of
    /// them fails. <c>false</c> for every other kind, and unread there.
    /// </summary>
    public bool ContinueOnError { get; private init; }

    /// <summary>True when this step declared a compensation.</summary>
    public bool IsCompensable => Compensation is not null;

    /// <summary>True when this step moves the instruction pointer rather than doing work.</summary>
    public bool IsControlTransfer => Kind is StepKind.Branch or StepKind.Jump or StepKind.Switch;

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

    /// <summary>Creates a conditional branch.</summary>
    /// <param name="index">Position in the graph.</param>
    /// <param name="falseTarget">
    /// Where control continues when the predicate does not hold. Must point forward.
    /// </param>
    /// <exception cref="InvalidFlowPlanException">The target does not point forward.</exception>
    /// <remarks>
    /// The predicate itself is not here. The engine cannot invoke a delegate it has no
    /// types for, so the predicate lives with the generated dispatcher and is reached by
    /// step index through <c>IStepDispatcher.Evaluate</c> — the same split that keeps the
    /// engine reflection-free for capabilities.
    /// </remarks>
    public static StepNode ForBranch(int index, int falseTarget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        return new StepNode(index, StepKind.Branch)
        {
            Target = RequireForwardTarget(index, falseTarget, "branch"),
        };
    }

    /// <summary>Creates an unconditional jump.</summary>
    /// <param name="index">Position in the graph.</param>
    /// <param name="target">Where control continues. Must point forward.</param>
    /// <exception cref="InvalidFlowPlanException">The target does not point forward.</exception>
    public static StepNode ForJump(int index, int target)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        return new StepNode(index, StepKind.Jump)
        {
            Target = RequireForwardTarget(index, target, "jump"),
        };
    }

    /// <summary>Creates a value branch.</summary>
    /// <param name="index">Position in the graph.</param>
    /// <param name="caseTargets">
    /// Where each case begins, in declaration order. Copied, never aliased. Must point
    /// forward, and must not be empty.
    /// </param>
    /// <param name="defaultTarget">
    /// Where control continues when no case matched. Must point forward. Equal to the
    /// join index when the author declared no <c>Default</c>, which is how a miss falls
    /// through to whatever follows the switch.
    /// </param>
    /// <exception cref="InvalidFlowPlanException">
    /// There are no cases, or a target does not point forward.
    /// </exception>
    /// <remarks>
    /// Like <see cref="ForBranch"/>, this carries no values: the selector and the case
    /// values live with the generated dispatcher and are reached by step index through
    /// <c>IStepDispatcher.Select</c>. A plan that carried them would have to know their
    /// types, which is the one thing the engine is built not to know.
    /// </remarks>
    public static StepNode ForSwitch(int index, IReadOnlyList<int> caseTargets, int defaultTarget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentNullException.ThrowIfNull(caseTargets);

        if (caseTargets.Count == 0)
        {
            throw new InvalidFlowPlanException(
                $"Step {index} is a switch with no cases. There is nothing to select " +
                "between, so it would always take its default — which is an unconditional " +
                "transfer, and a step that pretends to be a decision is worse than no step.");
        }

        var targets = ImmutableArray.CreateBuilder<int>(caseTargets.Count);

        foreach (var target in caseTargets)
        {
            targets.Add(RequireForwardTarget(index, target, "switch case"));
        }

        return new StepNode(index, StepKind.Switch)
        {
            CaseTargets = targets.MoveToImmutable(),
            Target = RequireForwardTarget(index, defaultTarget, "switch default"),
        };
    }

    /// <summary>Creates a concurrent fork.</summary>
    /// <param name="index">Position in the graph.</param>
    /// <param name="branchTargets">
    /// Where each branch begins, in declaration order. Copied, never aliased. Must be
    /// strictly ascending, must point forward, and there must be at least two — one branch
    /// is not parallel.
    /// </param>
    /// <param name="joinTarget">
    /// Where control continues once the merge is satisfied: the first step after every
    /// branch. Must be greater than the last branch target, so every branch has a
    /// non-empty range.
    /// </param>
    /// <param name="merge">How the branches are joined.</param>
    /// <exception cref="InvalidFlowPlanException">
    /// There are fewer than two branches, a target does not point forward, the targets are
    /// not strictly ascending, or the join does not lie past the last branch.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <strong>Two branches minimum, enforced here.</strong> A one-branch <c>Parallel</c>
    /// is a sequence wearing a costume: it would buy a linked token, a task array and an
    /// await for work that runs in exactly one order anyway. The generator lays such a
    /// declaration out inline instead, exactly as it does a <c>Switch</c> with no
    /// <c>Case</c>, so this rejects a layout bug rather than a thing an author can write.
    /// </para>
    /// <para>
    /// <strong>Empty branches are unrepresentable</strong>, because two equal targets are
    /// not strictly ascending. An empty branch would contribute nothing to the merge while
    /// still counting towards a quorum, which is a silent way to make
    /// <c>Quorum(2)</c> mean <c>Quorum(1)</c>.
    /// </para>
    /// </remarks>
    public static StepNode ForParallel(
        int index,
        IReadOnlyList<int> branchTargets,
        int joinTarget,
        MergeStrategy merge = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentNullException.ThrowIfNull(branchTargets);

        if (branchTargets.Count < 2)
        {
            throw new InvalidFlowPlanException(
                $"Step {index} is a parallel fork with {branchTargets.Count} branch(es). " +
                "Concurrency needs at least two things to be concurrent; one branch is a " +
                "sequence, and running it through a fork buys a linked token and a task " +
                "array for work that happens in exactly one order anyway.");
        }

        var targets = ImmutableArray.CreateBuilder<int>(branchTargets.Count);
        var previous = index;

        foreach (var target in branchTargets)
        {
            if (target <= previous)
            {
                throw new InvalidFlowPlanException(
                    $"Step {index} is a parallel fork whose branch targets are not strictly " +
                    $"ascending: {target} does not follow {previous}. Each branch owns the " +
                    "range from its own target up to the next one, so equal or descending " +
                    "targets would give a branch an empty or overlapping range — and a " +
                    "branch that runs no steps still counts towards a quorum.");
            }

            targets.Add(RequireForwardTarget(index, target, "parallel branch"));
            previous = target;
        }

        if (joinTarget <= previous)
        {
            throw new InvalidFlowPlanException(
                $"Step {index} is a parallel fork joining at step {joinTarget}, which is " +
                $"not past its last branch at step {previous}. The join is where control " +
                "resumes after every branch has run, so it must lie beyond all of them.");
        }

        return new StepNode(index, StepKind.Parallel)
        {
            BranchTargets = targets.MoveToImmutable(),
            Target = RequireForwardTarget(index, joinTarget, "parallel join"),
            Merge = merge,
        };
    }

    /// <summary>
    /// The most iterations the runtime will ever run at once, whatever an author asks for.
    /// </summary>
    /// <remarks>
    /// <c>08-Flow-Definition.md</c> §3.4 says a <c>ForEach</c> is "bounded by construction:
    /// <c>MaxDegreeOfParallelism</c> is required and capped by the runtime". This is that
    /// cap, and it is a constant rather than something derived from
    /// <c>Environment.ProcessorCount</c> on purpose: a plan whose shape depended on the
    /// machine that built it would compile to different graphs on a laptop and in CI, and
    /// the manifest would stop being reproducible.
    /// </remarks>
    public const int MaxIterationConcurrency = 64;

    /// <summary>Creates a bounded iteration over a collection.</summary>
    /// <param name="index">Position in the graph.</param>
    /// <param name="joinTarget">
    /// Where control continues once every element has been processed: the first step after
    /// the body. Must be at least two past this node, so the body is non-empty.
    /// </param>
    /// <param name="options">
    /// The author's <c>ForEachOptions</c>. Its concurrency is clamped to
    /// <see cref="MaxIterationConcurrency"/>.
    /// </param>
    /// <exception cref="InvalidFlowPlanException">The body is empty.</exception>
    /// <remarks>
    /// <para>
    /// <strong>The body has no target of its own.</strong> It is the span
    /// <c>[index + 1, joinTarget)</c> — a fork's branch range, with the start implied
    /// rather than stored, because there is only ever one block and it is always laid out
    /// immediately after the node. Storing a number the layout already fixes would be a
    /// second copy of a fact, and a chance for the two to disagree about which steps the
    /// loop runs.
    /// </para>
    /// <para>
    /// <strong>An empty body is refused, for the reason an empty parallel branch is.</strong>
    /// A loop that runs nothing per element still evaluates its selector and still costs an
    /// iteration each time; it is a statement about the flow that the flow does not make.
    /// The generator lays such a declaration out as nothing at all, so this rejects a
    /// layout bug rather than something an author can write.
    /// </para>
    /// <para>
    /// <strong>Termination.</strong> Every other kind is proved to terminate by the
    /// forward-target rule alone, because a step then runs at most once. That is no longer
    /// true here: the body's span is re-entered per element. What replaces it is that the
    /// element count is read once, from a materialised <c>IReadOnlyList</c>, before the
    /// first iteration — so the loop is bounded by a number fixed before it starts, and
    /// each individual pass over the body still terminates for exactly the old reason.
    /// </para>
    /// </remarks>
    public static StepNode ForEach(int index, int joinTarget, ForEachOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentNullException.ThrowIfNull(options);

        if (joinTarget <= index + 1)
        {
            throw new InvalidFlowPlanException(
                $"Step {index} is an iteration joining at step {joinTarget}, which leaves it " +
                "no body. A loop that runs no steps per element still evaluates its " +
                "selector and still costs an iteration each time, so it claims the flow " +
                "does something it does not.");
        }

        if (options.MaxDegreeOfParallelism < 1)
        {
            throw new InvalidFlowPlanException(
                $"Step {index} is an iteration with a maximum degree of parallelism of " +
                $"{options.MaxDegreeOfParallelism}. The bound is required precisely so that " +
                "it is a number somebody chose; zero is not a choice, it is a loop that " +
                "never runs anything.");
        }

        return new StepNode(index, StepKind.ForEach)
        {
            Target = RequireForwardTarget(index, joinTarget, "iteration join"),

            // Clamped, not rejected. An author asking for a thousand concurrent
            // reservations has asked for something reasonable that this runtime will not
            // do; failing the build over it would be refusing to run a correct flow.
            MaxDegreeOfParallelism = Math.Min(options.MaxDegreeOfParallelism, MaxIterationConcurrency),
            ContinueOnError = options.ContinueOnError,
        };
    }

    /// <summary>
    /// Rejects a target that does not point forward.
    /// </summary>
    /// <remarks>
    /// A backward target is a loop, and nothing in the conditional DSL — <c>When</c>,
    /// <c>Otherwise</c>, <c>Switch</c>, <c>Case</c>, <c>Default</c> — can express one. So
    /// a backward target is never something an author asked for, it is a layout bug in
    /// the generator. Left unchecked it is an infinite loop at run time, inside a step
    /// loop that has no iteration cap by design. Refusing it in the factory makes the
    /// shape unrepresentable rather than merely unlikely.
    /// </remarks>
    private static int RequireForwardTarget(int index, int target, string what)
    {
        if (target > index)
        {
            return target;
        }

        throw new InvalidFlowPlanException(
            $"Step {index} is a {what} targeting step {target}, which does not point " +
            "forward. The conditional DSL can only skip steps, never repeat them, so a " +
            "backward target is a layout bug — and one that would make the engine's " +
            "step loop run forever.");
    }

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        StepKind.Capability => $"[{Index}] {Capability}",
        StepKind.Emit => $"[{Index}] emit {EventType}",
        StepKind.AwaitSignal => $"[{Index}] await {SignalType} ({SignalTimeout})",
        StepKind.Branch => $"[{Index}] branch, else {Target}",
        StepKind.Jump => $"[{Index}] jump {Target}",
        StepKind.Switch => $"[{Index}] switch {string.Join(", ", CaseTargets)}, else {Target}",
        StepKind.Parallel => $"[{Index}] parallel {string.Join(", ", BranchTargets)} ({Merge}), join {Target}",
        StepKind.ForEach =>
            $"[{Index}] foreach {Index + 1}..{Target} (max {MaxDegreeOfParallelism}" +
            (ContinueOnError ? ", continue on error)" : ")"),
        _ => $"[{Index}] {Kind}",
    };
}
