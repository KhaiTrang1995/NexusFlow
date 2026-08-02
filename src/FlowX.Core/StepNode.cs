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
/// <see cref="SubFlow"/> is the last kind, and the one that does <em>not</em> fit the flat
/// array — not because it needs a target, but because it has none. Every other kind is a
/// statement about this array: a range of it, a destination in it, a span re-entered from
/// it. A sub-flow is one node that names <em>another flow's whole plan</em>, with its own
/// steps, its own compensation and its own deadline. So the array stays flat and stays the
/// engine's one loop; what stops being true is that one array describes one execution. See
/// <see cref="SubFlow"/> and <c>FlowEngine.RunSubFlowAsync</c> for what that costs.
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

    /// <summary>
    /// Runs another flow's compiled plan, identified by
    /// <see cref="StepNode.SubFlowId"/> and related to this one by
    /// <see cref="StepNode.Mode"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The one kind whose work is not in this graph.</strong> A
    /// <see cref="Branch"/> names an index here, a <see cref="Parallel"/> names ranges here,
    /// a <see cref="ForEach"/> names a span here. This names nothing here: it carries no
    /// target, occupies exactly one index, and the steps it runs live in a different
    /// <see cref="StepGraph"/> reached through the dispatcher. That is deliberate — the
    /// alternative, splicing the child's steps into the parent's array, would make the
    /// parent's manifest claim the child's capabilities as its own, discard the child's
    /// deadline and profile, and be impossible across an assembly boundary.
    /// </para>
    /// <para>
    /// <strong>Termination.</strong> The forward-target rule is untouched, because there is
    /// no target. What replaces it for the sub-flow graph is <c>FLOWX1021</c>, which refuses
    /// a cycle at build time, and a hard nesting bound in the engine for the cycles it
    /// cannot see across an assembly boundary.
    /// </para>
    /// </remarks>
    SubFlow = 8,

    /// <summary>
    /// Ends the flow with the business error the author declared, unwinding whatever
    /// completed before it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The one kind that is terminal, and the only one that cannot succeed.</strong>
    /// It carries no target because control does not continue: the engine reaches it, the
    /// dispatcher hands back a failed outcome, and the flow takes the ordinary failure path
    /// — which is exactly the point. A <c>Fail</c> that ended the flow "cleanly" would
    /// leave a completed <c>inventory.reserve</c> reserved forever, and
    /// <c>08-Flow-Definition.md §3.2</c> reaches for it precisely to reject a request
    /// <em>after</em> earlier steps have already had effects.
    /// </para>
    /// <para>
    /// <strong>The error itself is not here</strong>, for the reason a predicate and a case
    /// value are not: it is a business value, it lives with the generated dispatcher, and
    /// the plan stays something the engine can run without knowing a single contract type.
    /// </para>
    /// </remarks>
    Fail = 9,

    /// <summary>
    /// Suspends until a wall-clock instant. Durable flows only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong><see cref="AwaitSignal"/> with nothing to deliver.</strong> Both stop the
    /// invocation and leave the instance <see cref="FlowInstanceState.Suspended"/> at its
    /// resume frontier; they differ only in what ends the wait. That is why they share the
    /// engine's suspension path rather than having one each — a second way to park an
    /// instance is a second way to get parking wrong.
    /// </para>
    /// <para>
    /// <strong>Not a sleep.</strong> Nothing holds a thread, a pooled context or a lease
    /// while it waits: the instance records the instant it must wake and a sweep resumes it
    /// then, through the same <c>ExecuteAsync</c> a signal and a recovery scan re-enter. A
    /// <c>Task.Delay</c> across a seven-day wait would be a thread's worth of process for a
    /// row's worth of state.
    /// </para>
    /// <para>
    /// Appended rather than inserted beside <see cref="AwaitSignal"/>, because the members
    /// are explicitly numbered and a compiled plan a store or a manifest already describes
    /// has to keep meaning what it meant.
    /// </para>
    /// </remarks>
    Delay = 10,

    /// <summary>
    /// Invokes the capability at <c>Index + 1</c> once per attempt, suspending between
    /// attempts, until a predicate holds or the poll's own timeout runs out. Durable flows
    /// only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong><see cref="ForEach"/>'s layout and <see cref="AwaitSignal"/>'s ending.</strong>
    /// The body is the one step immediately after this node and is re-entered per attempt, so
    /// nothing in the array is duplicated and no target points backwards; what a delivered
    /// signal is to a suspension point, a satisfied predicate is to this — and
    /// <see cref="StepNode.Target"/> is where that path lands, one past the escalation block,
    /// for exactly the reason it is on an <see cref="AwaitSignal"/>.
    /// </para>
    /// <para>
    /// <strong>Termination is the loop's timeout, and it is measured from the journal.</strong>
    /// A <see cref="ForEach"/> is bounded by a count read before the first iteration; this is
    /// bounded by <see cref="StepNode.PollTimeout"/> measured from the instant the first
    /// attempt committed, which is a fact on a row rather than a number a node has to
    /// remember. That is what makes the bound survive the node that started the polling.
    /// </para>
    /// </remarks>
    Poll = 11,
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

    /// <summary>
    /// Policies wrapping this step's <em>compensation</em>, in execution order.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Policies"/> because it wraps a different capability and is
    /// checked against a different idempotency declaration: <c>payment.capture</c> is
    /// legitimately not idempotent while <c>payment.refund</c> is, and one chain could not
    /// say both. <c>docs/06-Execution-Engine.md §7</c> rule 2 — "each compensation carries its
    /// own policy chain" — is this property.
    /// </remarks>
    public PolicyChain CompensationPolicies { get; private init; } = PolicyChain.Empty;

    /// <summary>
    /// The compensation's retry, resolved out of <see cref="CompensationPolicies"/> when the
    /// plan was built.
    /// </summary>
    /// <remarks>
    /// Resolved here rather than at the point of use for the reason
    /// <see cref="ExecutionPlan.CompensableStepIndices"/> is precomputed: it is read on the
    /// failure path, where an incident is already in progress and walking an array of policy
    /// descriptors would add latency at exactly the wrong moment. It is also what keeps the
    /// success path free — the loop that runs the flow never touches it.
    /// </remarks>
    public CompensationPolicy CompensationRetry { get; private init; } = CompensationPolicy.None;

    /// <summary>
    /// The step's <see cref="PolicyStage.Resilience"/> policies, resolved out of
    /// <see cref="Policies"/> when the plan was built.
    /// </summary>
    /// <remarks>
    /// Resolved here rather than at the point of use for <see cref="CompensationRetry"/>'s
    /// reason, applied to the other path: this one is read on the <em>success</em> path, once
    /// per step of every policed flow, and walking an array of policy descriptors there would
    /// put the cost of a declaration on the flow that made it rather than on the phase that
    /// implements it. A step that declares nothing at stage 4 holds
    /// <see cref="StepPolicy.None"/> and answers one comparison.
    /// </remarks>
    public StepPolicy StepPolicy { get; private init; } = StepPolicy.None;

    /// <summary>
    /// The step's authorisation stance, resolved from its capability when the plan was built.
    /// </summary>
    /// <remarks>
    /// Resolved here for <see cref="StepPolicy"/>'s reason and read the same way: a step whose
    /// stance admits every caller holds the shared <see cref="StepAuthorization.None"/> and
    /// answers one comparison. Gated by <see cref="ExecutionPlan.HasAuthorizedSteps"/>, so an
    /// unstanced or wholly permissive plan never reaches it —
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0027-authorisation-runs-in-the-step-loop.md">ADR-0027</a>.
    /// </remarks>
    public StepAuthorization StepAuthorization { get; private init; } = StepAuthorization.None;

    /// <summary>
    /// The step's <c>Audit</c>, resolved from its declared chain when the plan was built.
    /// </summary>
    /// <remarks>
    /// Resolved here for <see cref="StepPolicy"/>'s reason and read the same way, and gated by
    /// <see cref="ExecutionPlan.HasAuditedSteps"/> — so a flow that audits nothing reads no
    /// principal, asks the dispatcher for no payload and touches no sink. It is read on the
    /// success path after the step's commit, which is where stage 7 is.
    /// </remarks>
    public StepAudit StepAudit { get; private init; } = StepAudit.None;

    /// <summary>The event published by an <see cref="StepKind.Emit"/> step.</summary>
    public string? EventType { get; private init; }

    /// <summary>
    /// The signal awaited by an <see cref="StepKind.AwaitSignal"/> step, or the one a
    /// <see cref="StepKind.Poll"/> step will also end on.
    /// </summary>
    /// <remarks>
    /// One property for both kinds because it is one fact — the identity a transport addresses a
    /// delivery to — and because the engine reads it the same way in both places: one
    /// <c>DurableExecution.TakeSignal</c> call on the arrival path. A second property would be a
    /// second thing for the manifest, the generated route and <c>flowx diff</c> to learn about,
    /// for a string that means exactly what this one means. <c>null</c> on a poll that declared
    /// no <c>.OrSignal&lt;T&gt;()</c>, and then nothing is taken and the wait has one ending.
    /// </remarks>
    public string? SignalType { get; private init; }

    /// <summary>How long an <see cref="StepKind.AwaitSignal"/> step waits before timing out.</summary>
    public TimeSpan? SignalTimeout { get; private init; }

    /// <summary>How long a <see cref="StepKind.Delay"/> step waits.</summary>
    /// <remarks>
    /// Separate from <see cref="SignalTimeout"/> even though both are a duration a step waits,
    /// because they are answers to different questions and one property would make
    /// <see cref="ToString"/>, the engine's dispatch and every reader of a plan ambiguous about
    /// which it was looking at — the distinction <see cref="CaseTargets"/> and
    /// <see cref="BranchTargets"/> already draw. A signal timeout is a bound on something else
    /// happening; this is the whole of what the step does.
    /// </remarks>
    public TimeSpan? Delay { get; private init; }

    /// <summary>How a <see cref="StepKind.Poll"/> step spaces its attempts.</summary>
    /// <remarks>
    /// <c>null</c> for every other kind, and unread there. A schedule rather than a single
    /// duration because the gap is a function of how many attempts have been made — which is
    /// the difference between polling and a timer, and the reason the two are separate kinds.
    /// The type is the one a <c>Retry</c> policy already declares: two spellings of "how far
    /// apart are the attempts" would be two formulas to keep in step.
    /// </remarks>
    public Backoff? PollInterval { get; private init; }

    /// <summary>
    /// How long a <see cref="StepKind.Poll"/> step keeps polling, measured from the instant
    /// its first attempt committed.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SignalTimeout"/> for that property's own reason: they bound
    /// different things and one property would leave every reader of a plan asking which it
    /// was looking at. A signal timeout bounds an event somebody else causes; this bounds work
    /// this flow is doing.
    /// </remarks>
    public TimeSpan? PollTimeout { get; private init; }

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

    /// <summary>
    /// Business identity of the flow a <see cref="StepKind.SubFlow"/> step runs.
    /// <c>null</c> for every other kind.
    /// </summary>
    /// <remarks>
    /// The child's id, not its plan. The plan is a runtime object the generated dispatcher
    /// hands over; the id is structure, so it is what reaches the manifest and a rendered
    /// diagram — a reader can see <em>which</em> flow is composed without the plan having to
    /// hold a reference to another plan and without the manifest carrying a value.
    /// </remarks>
    public string? SubFlowId { get; private init; }

    /// <summary>
    /// How a <see cref="StepKind.SubFlow"/> step relates to its child.
    /// <see cref="SubFlowMode.Inline"/> for every other kind, and unread there.
    /// </summary>
    public SubFlowMode Mode { get; private init; }

    /// <summary>True when this step declared a compensation, or is a step that may have to undo one.</summary>
    /// <remarks>
    /// <para>
    /// <strong>An inline sub-flow reports <c>true</c> without naming a compensation.</strong>
    /// It has none of its own — what it may have to undo is whatever the <em>child</em>
    /// completed, and the plan cannot know that: the child is compiled separately and may
    /// live in another assembly. So the node says "this step is one the unwind may need to
    /// visit", which is what this property is read for — <see cref="ExecutionPlan"/> uses it
    /// to decide whether the flow needs a compensation stack at all, and a flow that
    /// composes another flow does.
    /// </para>
    /// <para>
    /// A <see cref="SubFlowMode.Detached"/> sub-flow reports <c>false</c>: its lifecycle is
    /// its own, so the parent failing says nothing about it and there is nothing for the
    /// parent's unwind to do.
    /// </para>
    /// </remarks>
    public bool IsCompensable =>
        Compensation is not null || (Kind == StepKind.SubFlow && Mode == SubFlowMode.Inline);

    /// <summary>True when this step moves the instruction pointer rather than doing work.</summary>
    public bool IsControlTransfer => Kind is StepKind.Branch or StepKind.Jump or StepKind.Switch;

    /// <summary>
    /// What this step is, as one string: the capability it invokes, or the one thing it is
    /// about for the kinds that invoke none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read by everything that has to name a running step to somebody — the journal row's
    /// <c>capability_id</c>, <c>ctx.CapabilityId</c>, the identity an unhandled throw is
    /// attributed to. It lives here, on the node, so those readers cannot drift: the
    /// expression used to be written out at each of them, and two of the copies disagreeing
    /// about the same step is exactly the failure <see cref="CompensationIdentity"/> was
    /// added to end.
    /// </para>
    /// <para>
    /// Empty only for the control-transfer kinds, which do no work and are never reported
    /// as having run.
    /// </para>
    /// <para>
    /// <strong>A <see cref="StepKind.Delay"/> names itself.</strong> It invokes nothing, emits
    /// nothing and waits for nothing anybody sends, so there is no business identity to borrow
    /// — but it does commit a row, and a row whose <c>capability_id</c> were empty would be the
    /// one thing in an instance's history an operator could not name.
    /// </para>
    /// <para>
    /// <strong>A <see cref="StepKind.Poll"/> is empty unless it declared an
    /// <c>.OrSignal&lt;T&gt;()</c>, and then it is the signal.</strong> A poll node commits a row
    /// in exactly one case — the wait ended on a delivery — so the only row this can ever name
    /// says which signal ended it, and a poll with one ending commits nothing and needs no name.
    /// </para>
    /// </remarks>
    public string Identity => Capability?.Id ?? EventType ?? SignalType ?? SubFlowId ??
        (Kind == StepKind.Delay ? DelayIdentity : string.Empty);

    /// <summary>What a <see cref="StepKind.Delay"/> step is called in a journal row and a trace.</summary>
    public const string DelayIdentity = "flow.delay";

    /// <summary>What runs when this step is undone, as one string.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The compensating capability, not the step it reverses.</strong> Undoing
    /// <c>inventory.reserve</c> is <c>inventory.release</c> running, and everything that
    /// reports on it — the journal row, the alert raised when it is given up on, and
    /// <c>ctx.CapabilityId</c> inside the compensating capability itself — has to say so. A
    /// compensator deriving an idempotency key from its own identity would otherwise key its
    /// contra write exactly as the forward write was keyed, and any store honouring that key
    /// would deduplicate the undo away and report success over an effect that never
    /// happened.
    /// </para>
    /// <para>
    /// <strong>An inline sub-flow falls back to <see cref="Identity"/>.</strong> It declares
    /// no compensation of its own — see <see cref="IsCompensable"/> — because what its undo
    /// runs is the <em>child's</em> unwind, against the child's own steps and the child's own
    /// context, each of which names itself. There is no third name for the composition's
    /// undo, so it keeps its own.
    /// </para>
    /// </remarks>
    public string CompensationIdentity => Compensation?.Id ?? Identity;

    /// <summary>Creates a capability step.</summary>
    /// <param name="index">Position in the graph.</param>
    /// <param name="capability">The capability to invoke.</param>
    /// <param name="compensation">The business inverse, if this step is compensable.</param>
    /// <param name="policies">Policies wrapping the step.</param>
    /// <param name="compensationPolicies">
    /// Policies wrapping the step's compensation. Build it against
    /// <paramref name="compensation"/> — <see cref="PolicyChain.Create"/> checks a retry
    /// against the capability it is handed, and the capability being retried here is the undo.
    /// </param>
    /// <exception cref="InvalidFlowPlanException">
    /// The compensation is the capability itself, or a compensation policy is declared on a
    /// step that has no compensation.
    /// </exception>
    public static StepNode ForCapability(
        int index,
        CapabilityDescriptor capability,
        CapabilityDescriptor? compensation = null,
        PolicyChain? policies = null,
        PolicyChain? compensationPolicies = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentNullException.ThrowIfNull(capability);

        if (compensation is not null && compensation.Id == capability.Id)
        {
            throw new InvalidFlowPlanException(
                $"Step {index} declares '{capability.Id}' as its own compensation. A " +
                "compensation must undo the step, not repeat it.");
        }

        var stepChain = policies ?? PolicyChain.Empty;
        var undoChain = compensationPolicies ?? PolicyChain.Empty;

        if (compensation is null && !undoChain.IsEmpty)
        {
            throw new InvalidFlowPlanException(
                $"Step {index} declares a compensation policy set and no compensation. A " +
                "policy chain that wraps nothing is a promise the unwind cannot keep, and it " +
                "would read as though the step were recoverable when nothing would ever run.");
        }

        return new StepNode(index, StepKind.Capability)
        {
            Capability = capability,
            Compensation = compensation,
            Policies = stepChain,
            CompensationPolicies = undoChain,
            CompensationRetry = CompensationPolicy.From(undoChain),
            StepPolicy = StepPolicy.From(stepChain),

            // Stage 7's other half, off the step's own chain rather than the undo's. An audit
            // wraps the step, so an applied set is where it lives — only the compensation
            // retry is moved across onto the undo's chain.
            StepAudit = StepAudit.From(stepChain),

            // The forward capability's stance, and deliberately not the compensation's. An
            // undo runs on the failure path to reverse work this principal has already
            // caused; refusing it there would leave the inconsistent state the compensation
            // exists to remove, and the caller has already been authorised for the step that
            // made the mess. ADR-0027 records the trade.
            StepAuthorization = StepAuthorization.From(capability),
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
    /// <param name="signalTarget">
    /// Where control continues when the signal <em>does</em> arrive, when the author declared
    /// an <c>.OnTimeout(...)</c> block. Must point forward. <c>null</c> when they declared
    /// none, and then the signal path is the ordinary next index.
    /// </param>
    /// <exception cref="InvalidFlowPlanException">The target does not point forward.</exception>
    /// <remarks>
    /// <para>
    /// Whether this step is <em>permitted</em> depends on the flow's execution
    /// profile, which the node cannot see. <see cref="ExecutionPlan"/> enforces it.
    /// </para>
    /// <para>
    /// <strong>The target names the signal path, not the timeout path, and that is the
    /// <see cref="ForBranch"/> layout read from the other side.</strong> A branch lays its
    /// <c>then</c> block immediately after itself and carries its <em>false</em> target,
    /// because only one of the two blocks can be contiguous. Here the block that has to be
    /// contiguous is the timeout block — the steps after the wait are the rest of the flow and
    /// have no end to jump over — so the timeout path is <c>Index + 1</c> and the target is
    /// where the signal path lands, one past the block. The two rejoin there, so an author
    /// whose escalation should end the flow writes <c>.Fail(...)</c> inside it exactly as they
    /// would inside an <c>.Otherwise(...)</c>.
    /// </para>
    /// <para>
    /// With no block there is nowhere for a timeout to go, and continuing as though the signal
    /// had arrived would run steps that bind a payload nothing delivered. So a timeout with no
    /// target ends the flow with <c>flow.signal_not_received</c>, and the completed compensable
    /// steps unwind behind it.
    /// </para>
    /// </remarks>
    public static StepNode ForAwaitSignal(
        int index,
        string signalType,
        TimeSpan timeout,
        int? signalTarget = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        return new StepNode(index, StepKind.AwaitSignal)
        {
            SignalType = Identifiers.RequireIdentity(signalType, nameof(signalType)),
            SignalTimeout = timeout,
            Target = signalTarget is { } target
                ? RequireForwardTarget(index, target, "await signal")
                : null,
        };
    }

    /// <summary>Creates a durable timer.</summary>
    /// <param name="index">Position in the graph.</param>
    /// <param name="duration">How long to wait. Must be positive.</param>
    /// <remarks>
    /// <para>
    /// No target, for the reason a capability has none: control resumes at the next index. A
    /// timer is not a decision, it is a step that takes a while — the difference being that
    /// the while is spent as a row rather than as a process.
    /// </para>
    /// <para>
    /// Whether it is <em>permitted</em> depends on the flow's execution profile, which the
    /// node cannot see. <see cref="ExecutionPlan"/> enforces it, on the same grounds it
    /// enforces <see cref="StepKind.AwaitSignal"/>: an in-memory wait does not survive a
    /// deployment, and one that is only in memory is a <c>Task.Delay</c> wearing a plan node.
    /// </para>
    /// </remarks>
    public static StepNode ForDelay(int index, TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);

        return new StepNode(index, StepKind.Delay)
        {
            Delay = duration,
        };
    }

    /// <summary>Creates a poll: one capability, re-invoked until a predicate holds.</summary>
    /// <param name="index">Position in the graph.</param>
    /// <param name="interval">How the gap between attempts grows.</param>
    /// <param name="timeout">How long the polling may go on. Must be positive.</param>
    /// <param name="satisfiedTarget">
    /// Where control continues when the predicate holds, when the author declared an
    /// <c>.OnTimeout(...)</c> block. Must point past the body. <c>null</c> when they declared
    /// none, and then the satisfied path is <c>index + 2</c> — the index after the one-step
    /// body.
    /// </param>
    /// <param name="signalType">
    /// The identity of a signal that also ends this wait, from <c>.OrSignal&lt;T&gt;()</c>, or
    /// <c>null</c> when the predicate and the budget are the only two endings.
    /// </param>
    /// <exception cref="InvalidFlowPlanException">The target does not lie past the body.</exception>
    /// <remarks>
    /// <para>
    /// <strong>The body is one capability at <c>index + 1</c>, implied rather than
    /// stored</strong> — <see cref="ForEach"/>'s reason for not storing its body's start,
    /// applied to a block whose length is fixed as well as whose start is. A multi-step poll
    /// body would need each attempt to be journaled as a range and would let an author put a
    /// compensable step inside a loop that runs an unbounded number of times, which is a
    /// compensation stack of unbounded depth for one declaration.
    /// </para>
    /// <para>
    /// <strong>The predicate is not here</strong>, for <see cref="ForBranch"/>'s reason: the
    /// engine cannot invoke a delegate it has no types for, so it lives with the generated
    /// dispatcher and is reached by this node's index through <c>IStepDispatcher.Evaluate</c>.
    /// A poll and a conditional therefore ask the dispatcher the same question, which is why
    /// no second seam was opened for one.
    /// </para>
    /// <para>
    /// Whether this step is <em>permitted</em> depends on the flow's execution profile, which
    /// the node cannot see. <see cref="ExecutionPlan"/> enforces it, on the grounds it enforces
    /// the other two waits: an in-memory poll is a held thread with a plan node on it.
    /// </para>
    /// <para>
    /// <strong><paramref name="signalType"/> adds a second way for this one wait to end, not a
    /// second wait.</strong> It is still one node, one <see cref="PollTimeout"/> and one parked
    /// row carrying one wake instant; what the identity buys is that the arrival path asks
    /// <c>TakeSignal</c> as well as the predicate, and a delivery ends the wait where the next
    /// attempt would otherwise have been scheduled. A fork over a signal branch and a poll
    /// branch would be two branches sharing one <c>wake_at</c>, which is what
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0058-a-poll-is-one-wait-not-a-race-between-two.md">ADR-0058</a>
    /// refused; this is what it named instead.
    /// </para>
    /// </remarks>
    public static StepNode ForPoll(
        int index,
        Backoff interval,
        TimeSpan timeout,
        int? satisfiedTarget = null,
        string? signalType = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentNullException.ThrowIfNull(interval);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        if (interval.BaseDelay <= TimeSpan.Zero)
        {
            throw new InvalidFlowPlanException(
                $"Step {index} polls with no gap between attempts. A zero interval parks the " +
                "instance on an instant already in the past, so every sweep finds it due and " +
                "the flow spends its whole timeout hot-looping against somebody else's " +
                "service — which is the shape polling exists to replace.");
        }

        // Past the body, not merely past the node. A target of index + 1 would be a satisfied
        // path landing *on* the step it is satisfied by, which is the loop the forward-target
        // rule exists to make unrepresentable.
        if (satisfiedTarget is { } target && target <= index + 1)
        {
            throw new InvalidFlowPlanException(
                $"Step {index} is a poll whose satisfied path targets step {target}, which is " +
                "not past the attempt it polls with. The body occupies index " + (index + 1) +
                ", so a target at or before it re-enters the poll rather than leaving it.");
        }

        return new StepNode(index, StepKind.Poll)
        {
            PollInterval = interval,
            PollTimeout = timeout,
            Target = satisfiedTarget,
            SignalType = signalType is null
                ? null
                : Identifiers.RequireIdentity(signalType, nameof(signalType)),
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

    /// <summary>Creates a step that runs another flow.</summary>
    /// <param name="index">Position in the graph.</param>
    /// <param name="subFlowId">
    /// Business identity of the child flow, <c>&lt;domain&gt;.&lt;verb&gt;</c>. Structure,
    /// so it may safely reach the manifest.
    /// </param>
    /// <param name="mode">How the child relates to this flow.</param>
    /// <exception cref="InvalidFlowPlanException">
    /// <paramref name="mode"/> is <see cref="SubFlowMode.AwaitCompletion"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <strong>No target, and that is the whole of the layout.</strong> Unlike every other
    /// composite kind, a sub-flow occupies exactly one index and the steps it runs are in
    /// another graph. Nothing in the parent's array moves, nothing is spliced, and the
    /// manifest of a flow that composes a hundred-step child is the size of the flow the
    /// author wrote.
    /// </para>
    /// <para>
    /// <strong><see cref="SubFlowMode.AwaitCompletion"/> is unrepresentable.</strong> It
    /// means "suspend the parent until the child finishes", which needs a durable
    /// suspension point, which needs a journal that does not exist. The honest degenerate
    /// forms are both wrong: running it inline instead silently changes the parent's
    /// deadline and failure semantics, and skipping it silently drops business logic.
    /// <c>FLOWX1026</c> refuses it at build time; this refuses it in the one place a plan
    /// can be built by hand.
    /// </para>
    /// </remarks>
    public static StepNode ForSubFlow(int index, string subFlowId, SubFlowMode mode = SubFlowMode.Inline)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        if (mode == SubFlowMode.AwaitCompletion)
        {
            throw new InvalidFlowPlanException(
                $"Step {index} composes flow '{subFlowId}' with AwaitCompletion, which " +
                "suspends the parent until the child finishes. A suspension point needs a " +
                "journal to suspend into and there is not one, so the step cannot be " +
                "executed — and neither of the two ways to pretend otherwise is honest: " +
                "running it inline changes the parent's deadline and failure semantics, " +
                "and skipping it drops business logic. Use SubFlow<T>() or " +
                "SubFlow<T>(Detached).");
        }

        return new StepNode(index, StepKind.SubFlow)
        {
            SubFlowId = Identifiers.RequireIdentity(subFlowId, nameof(subFlowId)),
            Mode = mode,
        };
    }

    /// <summary>Creates a terminal step that ends the flow with a business error.</summary>
    /// <param name="index">Position in the graph.</param>
    /// <remarks>
    /// <para>
    /// <strong>No target and no payload, and both are the point.</strong> No target,
    /// because control never leaves this node — the forward-target rule that proves the
    /// step loop terminates is untouched, and a <c>Fail</c> simply ends the range it is in.
    /// No payload, because the <c>Error</c> is a business value: it lives in the generated
    /// dispatcher beside the case values, and is delivered through the same
    /// <c>IStepDispatcher.ExecuteAsync</c> that delivers a capability's own failure. That
    /// is what lets the engine treat "this arm rejects the request" and "payment declined"
    /// as the same event, which is what a saga needs them to be.
    /// </para>
    /// </remarks>
    public static StepNode ForFail(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        return new StepNode(index, StepKind.Fail);
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
        StepKind.AwaitSignal => Target is { } signalled
            ? $"[{Index}] await {SignalType} ({SignalTimeout}), else {Index + 1}, signalled {signalled}"
            : $"[{Index}] await {SignalType} ({SignalTimeout})",
        StepKind.Delay => $"[{Index}] delay {Delay}",
        StepKind.Poll => (Target is { } satisfied
            ? $"[{Index}] poll {Index + 1} every {PollInterval?.BaseDelay}..{PollInterval?.MaxDelay} " +
              $"for {PollTimeout}, else {Index + 2}, satisfied {satisfied}"
            : $"[{Index}] poll {Index + 1} every {PollInterval?.BaseDelay}..{PollInterval?.MaxDelay} " +
              $"for {PollTimeout}") + (SignalType is null ? string.Empty : $", or {SignalType}"),
        StepKind.Branch => $"[{Index}] branch, else {Target}",
        StepKind.Jump => $"[{Index}] jump {Target}",
        StepKind.Switch => $"[{Index}] switch {string.Join(", ", CaseTargets)}, else {Target}",
        StepKind.Parallel => $"[{Index}] parallel {string.Join(", ", BranchTargets)} ({Merge}), join {Target}",
        StepKind.ForEach =>
            $"[{Index}] foreach {Index + 1}..{Target} (max {MaxDegreeOfParallelism}" +
            (ContinueOnError ? ", continue on error)" : ")"),
        StepKind.SubFlow => $"[{Index}] subflow {SubFlowId} ({Mode})",
        StepKind.Fail => $"[{Index}] fail",
        _ => $"[{Index}] {Kind}",
    };
}
