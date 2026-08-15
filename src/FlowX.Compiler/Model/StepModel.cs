using System.Collections.Generic;

namespace FlowX.Compiler.Model;

/// <summary>What one call in a <c>Define</c> chain declared.</summary>
public enum StepKindModel
{
    /// <summary><c>.Step&lt;TCapability&gt;()</c></summary>
    Capability = 0,

    /// <summary><c>.Emit&lt;TEvent&gt;(...)</c></summary>
    Emit = 1,

    /// <summary><c>.AwaitSignal&lt;TSignal&gt;(...)</c></summary>
    AwaitSignal = 2,

    /// <summary><c>.When(predicate, then)</c>, with the <c>.Otherwise(...)</c> that may follow it.</summary>
    Condition = 3,

    /// <summary><c>.Switch(selector)</c>, with the <c>.Case(...)</c> and <c>.Default(...)</c> that follow it.</summary>
    Switch = 4,

    /// <summary><c>.Parallel(p =&gt; p.Branch...(), merge)</c>.</summary>
    Parallel = 5,

    /// <summary><c>.ForEach(selector, body, options)</c>.</summary>
    ForEach = 6,

    /// <summary><c>.SubFlow&lt;TFlow, TSubIn&gt;(map, mode)</c>.</summary>
    /// <remarks>
    /// The one kind with no nested block of its own. A conditional, a switch, a fork and a
    /// loop all carry their steps; this carries a <em>name</em> — the child's flow id — and
    /// the child's steps are modelled by the child's own <c>FlowModel</c>, compiled
    /// separately and possibly in another assembly.
    /// </remarks>
    SubFlow = 7,

    /// <summary><c>.Fail(error)</c>.</summary>
    /// <remarks>
    /// The one kind that is <em>terminal</em>: control never leaves it, so the steps a
    /// block declares after one are unreachable and <c>FLOWX1027</c> says so. Everything
    /// else here is a statement about what happens next.
    /// </remarks>
    Fail = 8,

    /// <summary><c>.Delay(duration)</c>.</summary>
    /// <remarks>
    /// Appended rather than inserted beside <see cref="AwaitSignal"/>, because the members
    /// are explicitly numbered and <c>flowx diff</c> compares manifests written by two builds
    /// of the same source.
    /// </remarks>
    Delay = 9,

    /// <summary><c>.PollUntil&lt;TCapability&gt;(until, interval, timeout)</c>.</summary>
    /// <remarks>
    /// Appended for <see cref="Delay"/>'s reason, and it is the one kind that carries
    /// <em>another kind</em> in its block: the polled capability is an ordinary
    /// <see cref="Capability"/> step at <c>Index + 1</c>, so the descriptors, the dispatcher's
    /// switch and the manifest's capability list see it without learning anything new.
    /// </remarks>
    Poll = 10,
}

/// <summary>One branch of a <c>Parallel</c>: a block of steps that runs concurrently with its siblings.</summary>
/// <remarks>
/// Thinner than <see cref="SwitchCaseModel"/> because there is nothing to match on. A case
/// answers "was it this value?" and so carries one; a branch answers nothing, it simply
/// runs — which is also why a branch has no closing jump where a case does.
/// </remarks>
public sealed class ParallelBranchModel
{
    /// <summary>Models one <c>.Branch(...)</c> call.</summary>
    /// <param name="steps">The branch's steps, already carrying their flat indices.</param>
    /// <param name="location"><c>file:line</c> of the <c>.Branch</c> call.</param>
    public ParallelBranchModel(IReadOnlyList<StepModel>? steps = null, string? location = null)
    {
        Steps = steps ?? System.Array.Empty<StepModel>();
        Location = location;
    }

    /// <summary>Steps declared in this branch, in declaration order.</summary>
    public IReadOnlyList<StepModel> Steps { get; }

    /// <summary><c>file:line</c> of the <c>.Branch</c> call, so a diagnostic names one branch.</summary>
    public string? Location { get; }

    /// <summary>
    /// Where this branch's range begins: the index of its first step.
    /// </summary>
    /// <remarks>
    /// Derived by <see cref="StepModel.Parallel"/> from the block itself, never supplied,
    /// for the reason given on <see cref="SwitchCaseModel.Target"/>. A branch also has no
    /// stored <em>end</em>: it runs up to the next branch's target, or to the join for the
    /// last one, so storing an end would be a second copy of a fact the ordering already
    /// carries and a chance for the two to disagree.
    /// </remarks>
    public int Target { get; internal set; }
}

/// <summary>One arm of a <c>Switch</c>: a value to match, and the block to run when it does.</summary>
/// <remarks>
/// The value is the author's source text, copied verbatim, for the same reason the
/// predicate and the <c>.Return(...)</c> projection are — see
/// <see cref="StepModel.Predicate"/>. It reaches the generated dispatcher and nothing
/// else; in particular it never reaches the manifest, whose rule is structure only,
/// never values.
/// </remarks>
public sealed record SwitchCaseModel
{
    /// <summary>Models one <c>.Case(value, body)</c> call.</summary>
    /// <param name="value">The case value's source text, copied verbatim.</param>
    /// <param name="steps">The block's steps, already carrying their flat indices.</param>
    /// <param name="valueLocation"><c>file:line</c> of the value expression.</param>
    public SwitchCaseModel(string value, IReadOnlyList<StepModel>? steps = null, string? valueLocation = null)
    {
        Value = value;
        Steps = steps ?? System.Array.Empty<StepModel>();
        ValueLocation = valueLocation;
    }

    /// <summary>The case value, as it was written.</summary>
    public string Value { get; }

    /// <summary><c>file:line</c> of the value expression.</summary>
    public string? ValueLocation { get; }

    /// <summary>Steps declared in this case's block, in declaration order.</summary>
    public IReadOnlyList<StepModel> Steps { get; }

    /// <summary>
    /// Where control goes when this case matches: the first step of its block, or the
    /// join index when the block declared nothing.
    /// </summary>
    /// <remarks>
    /// Derived by <see cref="StepModel.Switch"/> from the blocks themselves, never
    /// supplied — a caller able to state a target that disagreed with the block it also
    /// supplied could produce a plan that runs the wrong arm.
    /// </remarks>
    public int Target { get; internal init; }

    /// <summary>
    /// Index of the jump that closes this case's block, or <c>null</c> when there is
    /// nothing after it to skip.
    /// </summary>
    public int? JumpIndex { get; internal init; }
}

/// <summary>One step of a declared flow, expressed without Roslyn types.</summary>
/// <remarks>
/// <para>
/// A <c>record</c> specifically so <see cref="WithCompensation"/> and
/// <see cref="WithPolicy"/> can use <c>with</c> instead of copying every property by
/// hand. The hand-written versions listed a dozen properties each, and adding a field
/// meant remembering all three places — which failed the first time it was tried, in
/// exactly the way that kind of duplication always fails.
/// </para>
/// <para>
/// Immutable from the emitter's point of view: the analysis layer builds a step, then
/// <c>.CompensateWith</c> produces a new one carrying the compensation. That is what
/// makes emitter output reproducible from a model alone.
/// </para>
/// </remarks>
public sealed record StepModel
{
    private StepModel(int index, StepKindModel kind)
    {
        Index = index;
        Kind = kind;
    }

    /// <summary>Position in the chain, contiguous from zero.</summary>
    public int Index { get; }

    /// <summary>What the step does.</summary>
    public StepKindModel Kind { get; }

    /// <summary>Fully-qualified capability type, or <c>null</c> for non-capability kinds.</summary>
    public string? CapabilityTypeName { get; private init; }

    /// <summary>Business identity read from the capability's <c>[Capability]</c> attribute.</summary>
    public string? CapabilityId { get; private init; }

    /// <summary>Contract version from <c>[Capability]</c>.</summary>
    public string? CapabilityVersion { get; private init; }

    /// <summary>Whether the capability declared itself idempotent. Gates retry policies.</summary>
    public bool IsIdempotent { get; private init; }

    /// <summary>Declared side effects, in declaration order.</summary>
    public string[] SideEffects { get; private init; } = System.Array.Empty<string>();

    /// <summary>The capability's declared authorisation stance. Reaches the manifest.</summary>
    public string? AuthorizationMode { get; private init; }

    /// <summary>
    /// The permission or policy the stance names, or <c>null</c> when it names none.
    /// Reaches the manifest as <c>authorization.value</c>.
    /// </summary>
    /// <remarks>
    /// One field for both, because a capability has one stance and the mode already says
    /// which of <c>Permission</c> or <c>Policy</c> it was read from. It travels beside
    /// <see cref="AuthorizationMode"/> because <c>FLOWX-DIFF-015</c> compares the pair:
    /// the mode alone catches a move between stances, and the value is what catches a
    /// capability that stayed on <c>Permission</c> while the permission it demands moved.
    /// </remarks>
    public string? AuthorizationValue { get; private init; }

    /// <summary>The capability's input contract, fully qualified. Required by the manifest schema.</summary>
    public string? CapabilityInput { get; private init; }

    /// <summary>The capability's output contract, fully qualified.</summary>
    public string? CapabilityOutput { get; private init; }

    /// <summary>
    /// Source text of the <c>.Step&lt;TCapability, TStepIn&gt;(map)</c> mapping, copied
    /// verbatim, or <c>null</c> when the step binds its input from the state bag.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Verbatim for the reason the predicate, the selector and the sub-flow mapping are —
    /// see <see cref="Predicate"/>. It reaches the generated dispatcher and nothing else;
    /// in particular it never reaches the manifest, whose rule is structure only, never
    /// values.
    /// </para>
    /// <para>
    /// <strong>Its presence is what makes the step's input a local rather than a bag
    /// entry.</strong> A mapping exists precisely because the bag holds no
    /// <c>TStepIn</c>, so the emitted call passes the mapping's result straight to the
    /// capability and writes nothing back. Two mapped steps of the same type in one flow
    /// therefore cannot collide: each has its own delegate and its own local.
    /// </para>
    /// </remarks>
    public string? StepInputMap { get; private init; }

    /// <summary><c>file:line</c> of the mapping expression, for its <c>#line</c> directive.</summary>
    public string? StepInputMapLocation { get; private init; }

    /// <summary>
    /// Fully-qualified type the mapping produces — the <c>TStepIn</c> C# inferred.
    /// </summary>
    /// <remarks>
    /// Needed for the same reason <see cref="SelectorTypeName"/> is: the emitted mapping
    /// is a <c>static readonly Func&lt;FlowContext&lt;TIn&gt;, TStepIn&gt;</c> field and a
    /// field needs a type. Kept separate from <see cref="CapabilityInput"/> even though
    /// FLOWX1029 requires one to be assignable to the other, because the two are different
    /// facts: what the author's lambda returns, and what the capability declares.
    /// </remarks>
    public string? StepInputTypeName { get; private init; }

    /// <summary>True when the step supplies its own input from a mapping.</summary>
    public bool HasInputMapping => StepInputMap != null;

    /// <summary>
    /// The compensation declared by <c>.CompensateWith&lt;T&gt;()</c>, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// A whole <see cref="StepModel"/>, not three loose strings. It used to be the loose
    /// strings — type, id, version — and the consequence was that the manifest listed the
    /// compensation as a reference on the step and never as a capability in its own right.
    /// Its authorisation stance, side effects and idempotency were invisible, so
    /// <c>flowx diff</c> could not see a breaking change to one. Carrying the full model
    /// makes the compensation the same kind of thing as any other capability, which is
    /// what it always was.
    /// </remarks>
    public StepModel? Compensation { get; private init; }

    /// <summary>
    /// The capability a declared <c>Fallback&lt;TCapability&gt;()</c> names, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A whole <see cref="StepModel"/>, for <see cref="Compensation"/>'s reason and with the
    /// same consequence: the fallback reaches the manifest's capability inventory carrying its
    /// own version, side effects, authorisation stance and error catalogue, rather than as a
    /// name on the step that may call it. A dependency a build can invoke and the manifest does
    /// not list is a dependency <c>flowx diff</c> and the impact analysis cannot see, which is
    /// the fourth of the four things
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0078-stage-four-nests-six-kinds.md">ADR-0078</a>
    /// §3 named as missing.
    /// </para>
    /// <para>
    /// It is <em>not</em> a step: it has no index in the graph, no <c>case</c> in the step
    /// switch and no place in the layout. The generated dispatcher reaches it through
    /// <c>ExecuteFallbackAsync</c> under the index of the step it answers for, which is why the
    /// index copied into this model is that step's.
    /// </para>
    /// </remarks>
    public StepModel? FallbackCapability { get; private init; }

    /// <summary>Business identity of the fallback capability.</summary>
    public string? FallbackId => FallbackCapability?.CapabilityId;

    /// <summary>Contract version of the fallback capability.</summary>
    public string? FallbackVersion => FallbackCapability?.CapabilityVersion;

    /// <summary>True when the step's degraded answer takes a dispatch rather than a constant.</summary>
    public bool HasFallbackCapability => FallbackCapability != null;

    /// <summary>Fully-qualified compensation type, or <c>null</c>.</summary>
    public string? CompensationTypeName => Compensation?.CapabilityTypeName;

    /// <summary>Business identity of the compensation.</summary>
    public string? CompensationId => Compensation?.CapabilityId;

    /// <summary>Contract version of the compensation.</summary>
    public string? CompensationVersion => Compensation?.CapabilityVersion;

    /// <summary>
    /// Source text of the <c>.Fail(...)</c> argument, copied verbatim, or <c>null</c> for
    /// every other kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Verbatim for the reason the predicate and the case values are: an author writes
    /// <c>OrderErrors.UnsupportedChannel</c>, or a factory call carrying structured detail,
    /// and reconstructing an arbitrary C# expression means re-rendering every form the
    /// language has and being wrong on the first one nobody thought of.
    /// </para>
    /// <para>
    /// <strong>It reaches the generated dispatcher and nothing else.</strong> An
    /// <c>Error</c> carries a message, and the messages in this codebase interpolate
    /// business values — the same reason <c>CapabilityErrorModel</c> publishes a code and a
    /// category and never a message. The manifest records that the arm <em>fails</em>,
    /// which is structure; what it fails with stays in compiled code.
    /// </para>
    /// </remarks>
    public string? FailureExpression { get; private init; }

    /// <summary><c>file:line</c> of the error expression, for its <c>#line</c> directive.</summary>
    public string? FailureLocation { get; private init; }

    /// <summary>Event identity for an <see cref="StepKindModel.Emit"/> step.</summary>
    public string? EventType { get; private init; }

    /// <summary>
    /// Fully-qualified type of the contract an <see cref="StepKindModel.Emit"/> step
    /// publishes — the <c>TEvent</c> of <c>.Emit&lt;TEvent&gt;(...)</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="EventType"/> is the identity a consumer subscribes to (<c>order.placed</c>);
    /// this is the C# type whose <c>JsonTypeInfo</c> serialises the body. They are different
    /// facts and the manifest publishes only the first.
    /// </remarks>
    public string? EventContractTypeName { get; private init; }

    /// <summary>
    /// Source text of the <c>.Emit(...)</c> factory, copied verbatim, or <c>null</c> for
    /// every other kind.
    /// </summary>
    /// <remarks>
    /// Verbatim for the reason the predicate, the selector and the projection are:
    /// reconstructing an arbitrary C# expression means re-rendering every form the language
    /// has and being wrong on the first one nobody thought of. It reaches the generated
    /// dispatcher and nothing else — the manifest publishes that the flow emits the event,
    /// which is structure, and never how the body is built.
    /// </remarks>
    public string? EventFactory { get; private init; }

    /// <summary><c>file:line</c> of the factory expression, for its <c>#line</c> directive.</summary>
    public string? EventFactoryLocation { get; private init; }

    /// <summary>
    /// Signal identity for an <see cref="StepKindModel.AwaitSignal"/> step, and for a
    /// <see cref="StepKindModel.Poll"/> that declared an <c>.OrSignal&lt;TSignal&gt;()</c>.
    /// </summary>
    public string? SignalType { get; private init; }

    /// <summary>
    /// The author's declared wait, copied verbatim from the <c>.AwaitSignal</c> call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This field is what ended a fabrication.</strong>
    /// <c>StepNode.ForAwaitSignal</c> demands a duration and the model had none, so the
    /// emitter wrote <c>TimeSpan.FromHours(1)</c> for every suspension point whatever the
    /// author declared. Between publishing a value nobody wrote and publishing nothing, the
    /// compiler published nothing — and this is the third option, which is the one that was
    /// always right and needed a field.
    /// </para>
    /// <para>
    /// The expression, not an evaluated <c>TimeSpan</c>, for the reason every other copied
    /// expression here is verbatim: the generator does not constant-fold, so a duration
    /// written as <c>Policies.OfferWindow</c> reaches the plan as that and the plan means
    /// what the source means.
    /// </para>
    /// </remarks>
    public string? SignalTimeout { get; private init; }

    /// <summary>
    /// The same wait, evaluated to an ISO-8601 duration, or <c>null</c> when it could not be.
    /// Reaches the manifest as an <c>AwaitSignal</c> step's <c>timeout</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Two fields for one declaration, because two consumers need different things
    /// from it</strong> (ADR-0021 §2.2). <see cref="SignalTimeout"/> is what the plan carries,
    /// and it is verbatim because generated C# can evaluate <c>Waits.Countersignature</c>
    /// itself. This is what the manifest carries, and it cannot be verbatim, because a
    /// consumer reading JSON has never seen the assembly the symbol lives in — and a
    /// <c>flowx diff</c> rule over a symbol name would fire on a rename and stay silent on a
    /// change of value, which is the exact inversion of what the rule is for.
    /// </para>
    /// <para>
    /// <c>null</c> whenever <c>DeclaredDuration</c> could not evaluate the expression, and the
    /// manifest then omits the field. That is <c>merge</c>'s stance
    /// (<see cref="MergeKindName"/>): an absent field is a consumer asking, a guessed one is a
    /// consumer misled.
    /// </para>
    /// </remarks>
    public string? SignalTimeoutIso { get; private init; }

    /// <summary>
    /// Fully-qualified contract of the signal an <see cref="StepKindModel.AwaitSignal"/> step
    /// waits for — or a <see cref="StepKindModel.Poll"/> also ends on — or <c>null</c> when
    /// there is none or it could not be resolved.
    /// </summary>
    /// <remarks>
    /// The signal's payload is seeded into the state bag under this type, so it is a journaled
    /// contract in exactly the sense a capability step's output is: <c>FLOWX1006</c> checks it
    /// for membership of a generated JSON context, and the emitted <c>DescribeStep</c> and
    /// <c>RestoreState</c> carry it. Without that, an instance resumed by a signal and then
    /// crashed would come back having satisfied the wait and lost what it delivered.
    /// </remarks>
    public string? SignalContractTypeName { get; private init; }

    /// <summary>
    /// The author's declared wait, copied verbatim from the <c>.Delay</c> call.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SignalTimeout"/> even though both are a duration a step waits
    /// for, because they answer different questions — one bounds something else happening, the
    /// other <em>is</em> what the step does — and one field would make the emitter's two arms
    /// indistinguishable from each other. The expression, not a folded <c>TimeSpan</c>, for
    /// the reason every other copied expression here is verbatim.
    /// </remarks>
    public string? DelayDuration { get; private init; }

    /// <summary>
    /// The <c>interval:</c> argument's source text, copied verbatim, or <c>null</c> for every
    /// other kind.
    /// </summary>
    /// <remarks>
    /// Verbatim for <see cref="MergeExpression"/>'s reason: an author may write
    /// <c>Waits.OcrSchedule</c> or a schedule built from a constant on the flow, and a
    /// generator that rebuilt the call would disagree with the source for every expression it
    /// could not fold. It reaches the generated plan and nothing else — the manifest publishes
    /// structure and has no field for a tuning number, which is
    /// <c>MaxDegreeOfParallelism</c>'s stance and this is the same kind of number.
    /// </remarks>
    public string? PollInterval { get; private init; }

    /// <summary>
    /// The <c>timeout:</c> argument's source text, copied verbatim, or <c>null</c> for every
    /// other kind.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="SignalTimeout"/> even though both are a bound on a wait, for
    /// that property's own reason: one field would make the emitter's two arms
    /// indistinguishable and would leave every reader of a model asking which construct it was
    /// looking at.
    /// </remarks>
    public string? PollTimeout { get; private init; }

    /// <summary>
    /// The same timeout, evaluated to an ISO-8601 duration, or <c>null</c> when it could not
    /// be. Reaches the manifest as a <c>Poll</c> step's <c>timeout</c>.
    /// </summary>
    /// <remarks>
    /// Two fields for one declaration, for <see cref="SignalTimeoutIso"/>'s reason and by
    /// exactly its route: the plan carries the expression because generated C# can evaluate it,
    /// and the manifest cannot, because a consumer reading JSON has never seen the assembly the
    /// symbol lives in.
    /// </remarks>
    public string? PollTimeoutIso { get; private init; }

    /// <summary>
    /// Source text of the <c>until:</c> predicate, copied verbatim, or <c>null</c> for every
    /// other kind.
    /// </summary>
    /// <remarks>
    /// Held apart from <see cref="Predicate"/> so that a poll and a conditional stay two things
    /// in the model, and emitted into the same <c>Conditions</c> class the conditional's goes
    /// into — because the engine asks both through <c>IStepDispatcher.Evaluate</c>, and a
    /// second seam for the same question is a second thing to keep in step.
    /// </remarks>
    public string? PollPredicate { get; private init; }

    /// <summary><c>file:line</c> of the <c>until:</c> expression, for its <c>#line</c> directive.</summary>
    public string? PollPredicateLocation { get; private init; }

    /// <summary>Named policy set applied via <c>.WithPolicy(...)</c>.</summary>
    public string? PolicySetName { get; private init; }

    /// <summary>
    /// The policy kinds that set declares — <c>Retry</c>, <c>Cache</c>, and so on —
    /// ordinally sorted.
    /// </summary>
    /// <remarks>
    /// The name alone said nothing about the contents, which is why FLOWX1014 and
    /// FLOWX1018 could not fire: both ask a question about what is in the set, not what
    /// it is called. Empty when the set could not be resolved — one built at run time
    /// cannot be inspected at compile time, and a guess would produce a diagnostic
    /// nobody could act on.
    /// </remarks>
    public string[] PolicyKinds { get; private init; } = System.Array.Empty<string>();

    /// <summary><c>file:line</c> of the call, so a diagnostic points at the right chain link.</summary>
    public string? Location { get; private init; }

    /// <summary>
    /// Source text of the <c>.When(...)</c> predicate, copied verbatim, or <c>null</c> for
    /// every other kind.
    /// </summary>
    /// <remarks>
    /// Verbatim for the same reason the <c>.Return(...)</c> projection is — see
    /// <c>FlowModel.ReturnProjection</c>. Reconstructing an arbitrary C# expression means
    /// re-rendering every form the language has, and being wrong on the first one nobody
    /// thought of.
    /// </remarks>
    public string? Predicate { get; private init; }

    /// <summary><c>file:line</c> of the predicate expression, for its <c>#line</c> directive.</summary>
    public string? PredicateLocation { get; private init; }

    /// <summary>
    /// Source text of the <c>.Switch(...)</c> selector, copied verbatim, or <c>null</c>
    /// for every other kind.
    /// </summary>
    public string? Selector { get; private init; }

    /// <summary><c>file:line</c> of the selector expression, for its <c>#line</c> directive.</summary>
    public string? SelectorLocation { get; private init; }

    /// <summary>
    /// Fully-qualified type of the value the selector produces.
    /// </summary>
    /// <remarks>
    /// Needed because the emitted selector is a <c>static readonly Func&lt;FlowContext,
    /// T&gt;</c> field and a field needs a type. It is also what makes the case
    /// comparison allocation-free: <c>EqualityComparer&lt;T&gt;.Default</c> at the real
    /// type boxes nothing, where a comparison through <c>object</c> would box an
    /// <c>enum</c> on every switch a flow takes.
    /// </remarks>
    public string? SelectorTypeName { get; private init; }

    /// <summary>The <c>.Case(...)</c> arms, in declaration order. Empty for every other kind.</summary>
    public IReadOnlyList<SwitchCaseModel> Cases { get; private init; } = System.Array.Empty<SwitchCaseModel>();

    /// <summary>Steps declared in the <c>.Default(...)</c> block. Empty when there is none.</summary>
    public IReadOnlyList<StepModel> Default { get; private init; } = System.Array.Empty<StepModel>();

    /// <summary>
    /// Where control goes when no case matched: the first step of the <c>Default</c>
    /// block, or the join index when there is none.
    /// </summary>
    /// <remarks>
    /// Equal to <see cref="JoinIndex"/> without a <c>Default</c>, which is the whole of
    /// the documented fall-through rule: a value nothing matched continues after the
    /// switch. See <c>ISwitchBuilder</c> for why that is the chosen answer.
    /// </remarks>
    public int DefaultTarget { get; private init; }

    /// <summary>The <c>.Branch(...)</c> blocks of a <c>Parallel</c>, in declaration order. Empty for every other kind.</summary>
    public IReadOnlyList<ParallelBranchModel> Branches { get; private init; } = System.Array.Empty<ParallelBranchModel>();

    /// <summary>
    /// The <c>merge:</c> argument's source text, copied verbatim, or <c>null</c> for every
    /// other kind.
    /// </summary>
    /// <remarks>
    /// Verbatim for the same reason the predicate and the case values are: an author may
    /// write <c>MergeStrategy.Quorum(RequiredChecks)</c>, and reconstructing an arbitrary
    /// C# expression means re-rendering every form the language has and being wrong on the
    /// first one nobody thought of. It reaches the generated plan and nothing else.
    /// </remarks>
    public string? MergeExpression { get; private init; }

    /// <summary>
    /// Which <c>MergeKind</c> the expression names — <c>AllMustSucceed</c>, <c>Quorum</c>
    /// and so on — or <c>null</c> when it could not be read statically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the half that reaches the manifest, and it is a <em>name</em> rather than
    /// the expression precisely because the manifest publishes structure and never values.
    /// "This fork waits for a quorum" is structure; how large the quorum is comes from an
    /// expression that may be a constant on the flow, and the difference is not worth
    /// arguing about at a schema boundary.
    /// </para>
    /// <para>
    /// <c>null</c> when the argument is something this reader cannot name — a variable, a
    /// method call, a strategy chosen by a helper. The compiled plan is still exactly right
    /// in that case, because the plan copies the expression; only the manifest loses the
    /// label, and omitting a field is more honest than guessing one.
    /// </para>
    /// </remarks>
    public string? MergeKindName { get; private init; }

    /// <summary>Steps declared in a <c>ForEach</c> body, in declaration order. Empty for every other kind.</summary>
    /// <remarks>
    /// A single block, unlike <see cref="Branches"/> and <see cref="Cases"/>, because a
    /// loop has exactly one body — and it is laid out immediately after the loop's own
    /// node, so it needs no stored target either. What makes it different from every other
    /// block in this model is that the engine runs it more than once.
    /// </remarks>
    public IReadOnlyList<StepModel> Body { get; private init; } = System.Array.Empty<StepModel>();

    /// <summary>
    /// Fully-qualified type of the element a <c>ForEach</c> iterates.
    /// </summary>
    /// <remarks>
    /// Needed for the same reason <see cref="SelectorTypeName"/> is: the emitted selector
    /// is a <c>static readonly Func&lt;FlowContext, IReadOnlyList&lt;T&gt;&gt;</c> field and
    /// a field needs a type. It is also what the generated dispatcher casts the engine's
    /// opaque collection handle back to, which is the one place the element type is needed
    /// at run time.
    /// </remarks>
    public string? ItemTypeName { get; private init; }

    /// <summary>
    /// The <c>options:</c> argument's source text, copied verbatim, or <c>null</c> for
    /// every other kind.
    /// </summary>
    /// <remarks>
    /// Verbatim for the reason <see cref="MergeExpression"/> is: an author may write
    /// <c>ForEachOptions.Default</c> or a set built from a constant on the flow, and
    /// reconstructing an arbitrary C# expression means re-rendering every form the language
    /// has and being wrong on the first one nobody thought of. It reaches the generated plan
    /// and nothing else — in particular the concurrency bound never reaches the manifest,
    /// which publishes structure and has no field for a tuning number.
    /// </remarks>
    public string? OptionsExpression { get; private init; }

    /// <summary>
    /// Business identity of the flow a <see cref="StepKindModel.SubFlow"/> composes, read
    /// from the child's <c>[Flow]</c> attribute. <c>null</c> for every other kind.
    /// </summary>
    /// <remarks>
    /// The id rather than the CLR type, because this is what reaches the manifest and a
    /// rendered diagram: a reader comparing two versions of an application cares that
    /// <c>order.place</c> composes <c>order.fulfil</c>, not what the classes are called.
    /// <see cref="SubFlowTypeName"/> carries the type for the generated code, which needs
    /// something it can name.
    /// </remarks>
    public string? SubFlowId { get; private init; }

    /// <summary>Fully-qualified type of the composed flow, for the generated dispatcher.</summary>
    public string? SubFlowTypeName { get; private init; }

    /// <summary>Fully-qualified input contract of the composed flow.</summary>
    /// <remarks>
    /// Needed for the same reason <see cref="SelectorTypeName"/> is: the emitted mapping is
    /// a <c>static readonly Func&lt;FlowContext, TSubIn&gt;</c> field and a field needs a
    /// type. It is also what the generated <c>EnterSubFlow</c> casts the engine's opaque
    /// input handle back to.
    /// </remarks>
    public string? SubFlowInputTypeName { get; private init; }

    /// <summary>
    /// Which <c>SubFlowMode</c> the call names — <c>Inline</c> or <c>Detached</c>.
    /// </summary>
    /// <remarks>
    /// A name rather than the expression, unlike <see cref="MergeExpression"/> and
    /// <see cref="OptionsExpression"/>, and the asymmetry is deliberate. Those two carry
    /// arbitrary numbers an author may compute; this is a two-member choice that changes
    /// the flow's <em>semantics</em> — whether the parent waits, whether the child's failure
    /// is the parent's, whether the deadline is shared. A mode the compiler could not read
    /// would be a mode the manifest could not publish and FLOWX1022 could not check, so an
    /// unreadable one is refused rather than copied through.
    /// </remarks>
    public string? SubFlowMode { get; private init; }

    /// <summary>Source text of the <c>.SubFlow(...)</c> input mapping, copied verbatim.</summary>
    public string? SubFlowMap { get; private init; }

    /// <summary><c>file:line</c> of the mapping expression, for its <c>#line</c> directive.</summary>
    public string? SubFlowMapLocation { get; private init; }

    /// <summary>Steps declared in the <c>then</c> block, in declaration order.</summary>
    public IReadOnlyList<StepModel> Then { get; private init; } = System.Array.Empty<StepModel>();

    /// <summary>Steps declared in the <c>.Otherwise(...)</c> block. Empty when there is none.</summary>
    public IReadOnlyList<StepModel> Otherwise { get; private init; } = System.Array.Empty<StepModel>();

    /// <summary>
    /// Where control continues when the predicate does not hold.
    /// </summary>
    /// <remarks>
    /// The flat layout a conditional compiles to is
    /// <c>branch · then… · [jump] · otherwise…</c>, so the true path is the branch's own
    /// index plus one and only the false path needs to be recorded. See
    /// <c>StepNode.Target</c>, which is what this becomes.
    /// </remarks>
    public int FalseTarget { get; private init; }

    /// <summary>
    /// Index of the jump that closes the <c>then</c> block, or <c>null</c> when there is
    /// no <c>Otherwise</c> to skip over.
    /// </summary>
    public int? JumpIndex { get; private init; }

    /// <summary>
    /// Index the whole conditional joins at: the first step after both blocks, which is
    /// also the jump's target.
    /// </summary>
    /// <remarks>
    /// May be one past the last step of the flow, when the conditional is the last thing
    /// the chain declares. That is the layout <c>StepGraph</c> deliberately permits.
    /// </remarks>
    public int JoinIndex { get; private init; }

    /// <summary>
    /// The flat index immediately after everything this step occupies.
    /// </summary>
    /// <remarks>
    /// One past its own index for a plain step; the join index for a conditional or a
    /// switch, which occupies its own node, every block, and the jumps between them. This
    /// is what lets a nested conditional be laid out without the enclosing block having
    /// to know how deep it goes.
    /// </remarks>
    public int NextIndex =>
        Kind is StepKindModel.Condition or StepKindModel.Switch or StepKindModel.Parallel
            or StepKindModel.ForEach or StepKindModel.AwaitSignal or StepKindModel.Poll
            ? JoinIndex
            : Index + 1;

    /// <summary>True when the step declared a compensation.</summary>
    public bool IsCompensable => CompensationTypeName != null;

    /// <summary>
    /// This step and every step nested inside it, in flat-layout order.
    /// </summary>
    /// <remarks>
    /// Pre-order, which for this shape <em>is</em> the order the flat step array runs in:
    /// a conditional comes before its <c>then</c> block, which comes before its
    /// <c>Otherwise</c> block. Everything that used to read <c>flow.Steps</c> as the whole
    /// flow — the descriptors, the dispatcher's switch, the manifest's capability list —
    /// has to read this instead, or a capability invoked inside a branch is invisible to it.
    /// </remarks>
    public IEnumerable<StepModel> SelfAndNested
    {
        get
        {
            yield return this;

            // Before `Then`, because a poll is the one kind that carries both and its layout is
            // `poll · attempt · escalation`: the attempt is the block the engine re-enters and
            // the escalation is the block it may jump into, in that order. Every other kind
            // fills one of the two collections and leaves the other empty, so the move is
            // invisible to them and the enumeration stays the flat array's own order — which is
            // what this property promises and what the descriptor list relies on.
            foreach (var nested in Body)
            {
                foreach (var step in nested.SelfAndNested)
                {
                    yield return step;
                }
            }

            foreach (var nested in Then)
            {
                foreach (var step in nested.SelfAndNested)
                {
                    yield return step;
                }
            }

            foreach (var nested in Otherwise)
            {
                foreach (var step in nested.SelfAndNested)
                {
                    yield return step;
                }
            }

            foreach (var arm in Cases)
            {
                foreach (var nested in arm.Steps)
                {
                    foreach (var step in nested.SelfAndNested)
                    {
                        yield return step;
                    }
                }
            }

            foreach (var nested in Default)
            {
                foreach (var step in nested.SelfAndNested)
                {
                    yield return step;
                }
            }

            foreach (var branch in Branches)
            {
                foreach (var nested in branch.Steps)
                {
                    foreach (var step in nested.SelfAndNested)
                    {
                        yield return step;
                    }
                }
            }
        }
    }

    /// <summary>Models a <c>.Step&lt;TCapability&gt;()</c> call, or the mapped overload.</summary>
    /// <param name="index">Flat index of the step.</param>
    /// <param name="capabilityTypeName">Fully-qualified capability type.</param>
    /// <param name="capabilityId">Business identity from <c>[Capability]</c>.</param>
    /// <param name="capabilityVersion">Contract version from <c>[Capability]</c>.</param>
    /// <param name="isIdempotent">Whether the capability declared itself idempotent.</param>
    /// <param name="sideEffects">Declared side effects, in declaration order.</param>
    /// <param name="location"><c>file:line</c> of the <c>.Step</c> call.</param>
    /// <param name="authorizationMode">The capability's declared authorisation stance.</param>
    /// <param name="authorizationValue">
    /// The permission or policy that stance names, or <c>null</c> when it names none.
    /// </param>
    /// <param name="capabilityInput">The capability's input contract, fully qualified.</param>
    /// <param name="capabilityOutput">The capability's output contract, fully qualified.</param>
    /// <param name="stepInputMap">
    /// The mapping's source text for <c>.Step&lt;TCapability, TStepIn&gt;(map)</c>, copied
    /// verbatim, or <c>null</c> for the one-type-argument overload that binds from the bag.
    /// </param>
    /// <param name="stepInputTypeName">Fully-qualified type the mapping produces.</param>
    /// <param name="stepInputMapLocation"><c>file:line</c> of the mapping expression.</param>
    /// <remarks>
    /// One factory for both overloads rather than two, because they produce the same
    /// <em>kind</em> of step: the capability, the descriptor, the compensation and the
    /// manifest entry are identical, and only where the input comes from differs. A second
    /// factory would have meant every reader of a capability step asking which one it came
    /// from.
    /// </remarks>
    public static StepModel Capability(
        int index,
        string capabilityTypeName,
        string capabilityId,
        string capabilityVersion,
        bool isIdempotent,
        string[]? sideEffects = null,
        string? location = null,
        string? authorizationMode = null,
        string? authorizationValue = null,
        string? capabilityInput = null,
        string? capabilityOutput = null,
        string? stepInputMap = null,
        string? stepInputTypeName = null,
        string? stepInputMapLocation = null)
    {
        return new StepModel(index, StepKindModel.Capability)
        {
            CapabilityTypeName = capabilityTypeName,
            CapabilityId = capabilityId,
            CapabilityVersion = capabilityVersion,
            IsIdempotent = isIdempotent,
            SideEffects = sideEffects ?? System.Array.Empty<string>(),
            Location = location,
            AuthorizationMode = authorizationMode,
            AuthorizationValue = authorizationValue,
            CapabilityInput = capabilityInput,
            CapabilityOutput = capabilityOutput,
            StepInputMap = stepInputMap,
            StepInputTypeName = stepInputTypeName,
            StepInputMapLocation = stepInputMapLocation,
        };
    }

    /// <summary>Models an <c>.Emit&lt;TEvent&gt;(...)</c> call.</summary>
    /// <param name="index">Flat index of the step.</param>
    /// <param name="eventType">The event identity, e.g. <c>order.placed</c>.</param>
    /// <param name="location"><c>file:line</c> of the <c>.Emit</c> call.</param>
    /// <param name="contractTypeName">Fully-qualified <c>TEvent</c>, or <c>null</c> when unresolved.</param>
    /// <param name="factory">The factory expression's source text, copied verbatim.</param>
    /// <param name="factoryLocation"><c>file:line</c> of the factory expression.</param>
    public static StepModel Emit(
        int index,
        string eventType,
        string? location = null,
        string? contractTypeName = null,
        string? factory = null,
        string? factoryLocation = null)
    {
        return new StepModel(index, StepKindModel.Emit)
        {
            EventType = eventType,
            EventContractTypeName = contractTypeName,
            EventFactory = factory,
            EventFactoryLocation = factoryLocation,
            Location = location,
        };
    }

    /// <summary>Models a <c>.Fail(error)</c> call.</summary>
    /// <param name="index">Flat index of the terminal step.</param>
    /// <param name="error">The error expression's source text, copied verbatim.</param>
    /// <param name="errorLocation"><c>file:line</c> of the error expression.</param>
    /// <param name="location"><c>file:line</c> of the <c>.Fail</c> call.</param>
    /// <remarks>
    /// One index and no layout, like <see cref="SubFlow"/> — but for the opposite reason.
    /// A sub-flow's steps are somewhere else; a <c>Fail</c> has none, because it is where
    /// the flow stops.
    /// </remarks>
    public static StepModel Fail(int index, string error, string? errorLocation = null, string? location = null)
    {
        return new StepModel(index, StepKindModel.Fail)
        {
            FailureExpression = error,
            FailureLocation = errorLocation,
            Location = location,
        };
    }

    /// <summary>Models an <c>.AwaitSignal&lt;TSignal&gt;(timeout)</c> call.</summary>
    /// <param name="index">Flat index of the suspension point.</param>
    /// <param name="signalType">The signal's identity, <c>&lt;domain&gt;.&lt;signal&gt;</c>.</param>
    /// <param name="timeoutExpression">
    /// The author's declared wait, copied verbatim. Null only from a half-typed buffer — the
    /// DSL has no <c>AwaitSignal</c> overload without a timeout — and the emitter refuses such
    /// a model rather than inventing a duration for it.
    /// </param>
    /// <param name="contractTypeName">Fully-qualified <c>TSignal</c>, or null when unresolved.</param>
    /// <param name="location"><c>file:line</c> of the call.</param>
    /// <param name="timeout">
    /// The same wait as an ISO-8601 duration, for the manifest, or null when the compiler
    /// could not evaluate the expression. Supplied rather than folded here, because following
    /// a named constant to its declaration needs a semantic model and this layer has none.
    /// </param>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="timeout"/> is not an ISO-8601 duration the manifest schema accepts.
    /// </exception>
    /// <param name="onTimeout">
    /// Steps of the <c>.OnTimeout(...)</c> block, already carrying their flat indices, or
    /// empty when the author declared none.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>The block is carried in <see cref="Then"/>, and that is reuse rather than
    /// overloading.</strong> It is a block laid out contiguously after the node that owns it,
    /// closed by nothing, whose steps have to appear in <see cref="SelfAndNested"/> so that
    /// the descriptors, the dispatcher's switch and the manifest's capability list see them —
    /// which is exactly what a conditional's <c>then</c> block is. A second collection meaning
    /// the same thing would need every one of those readers to learn about it.
    /// </para>
    /// <para>
    /// <see cref="JoinIndex"/> is derived here rather than passed in, for the reason
    /// <see cref="Condition"/> derives its three layout numbers: it is a consequence of the
    /// block, and a caller able to supply one that disagreed with the block it also supplied
    /// could produce a plan that skips or repeats real steps.
    /// </para>
    /// </remarks>
    public static StepModel AwaitSignal(
        int index,
        string signalType,
        string? timeoutExpression = null,
        string? contractTypeName = null,
        string? location = null,
        string? timeout = null,
        IReadOnlyList<StepModel>? onTimeout = null)
    {
        // Refused here rather than left to the schema, because the schema is validated by
        // this repository's tests and never by an application's build — a folder that started
        // producing `7.00:00:00` would ship into somebody's manifest and fail in their parser.
        if (timeout != null && !Iso8601.IsMatch(timeout))
        {
            throw new System.ArgumentException(
                $"'{timeout}' is not an ISO-8601 duration. The manifest's `timeout` field is " +
                "`#/$defs/duration`, the same shape a flow's deadline uses.",
                nameof(timeout));
        }

        var block = onTimeout ?? (IReadOnlyList<StepModel>)System.Array.Empty<StepModel>();

        return new StepModel(index, StepKindModel.AwaitSignal)
        {
            SignalType = signalType,
            SignalTimeout = timeoutExpression,
            SignalTimeoutIso = timeout,
            SignalContractTypeName = contractTypeName,
            Then = block,

            // One past the block, which is where a delivered signal carries on — and where the
            // block falls through to, because the two paths rejoin. With no block it is the
            // ordinary next index, and the emitter writes no target at all: a target equal to
            // the next index would read as an escalation that runs nothing.
            JoinIndex = block.Count == 0 ? index + 1 : block[block.Count - 1].NextIndex,
            Location = location,
        };
    }

    /// <summary>Models a <c>.Delay(duration)</c> call.</summary>
    /// <param name="index">Flat index of the timer.</param>
    /// <param name="durationExpression">
    /// The author's declared wait, copied verbatim. Null only from a half-typed buffer — the
    /// DSL has no <c>Delay</c> overload without a duration — and the emitter refuses such a
    /// model rather than inventing one for it.
    /// </param>
    /// <param name="location"><c>file:line</c> of the call.</param>
    /// <remarks>
    /// One index and no block, like a capability. A timer is not a decision, it is a step that
    /// takes a while — the difference being that the while is spent as a row rather than as a
    /// process.
    /// </remarks>
    public static StepModel Delay(int index, string? durationExpression = null, string? location = null)
    {
        return new StepModel(index, StepKindModel.Delay)
        {
            DelayDuration = durationExpression,
            Location = location,
        };
    }

    /// <summary>Models a <c>.PollUntil&lt;TCapability&gt;(until, interval, timeout)</c> call.</summary>
    /// <param name="index">Flat index of the poll node itself.</param>
    /// <param name="attempt">
    /// The polled capability, modelled exactly as a <c>.Step&lt;T&gt;()</c> is and already
    /// carrying flat index <c>index + 1</c>. A whole <see cref="StepModel"/> rather than three
    /// loose strings, for <see cref="Compensation"/>'s reason: the capability a poll calls is
    /// the same kind of thing as any other capability, so its stance, its side effects and its
    /// idempotency reach the manifest and <c>flowx diff</c> unchanged.
    /// </param>
    /// <param name="predicate">The <c>until:</c> expression's source text, copied verbatim.</param>
    /// <param name="interval">The <c>interval:</c> expression's source text, copied verbatim.</param>
    /// <param name="timeoutExpression">The <c>timeout:</c> expression's source text, copied verbatim.</param>
    /// <param name="predicateLocation"><c>file:line</c> of the predicate expression.</param>
    /// <param name="location"><c>file:line</c> of the <c>.PollUntil</c> call.</param>
    /// <param name="timeout">
    /// The same timeout as an ISO-8601 duration, for the manifest, or null when the compiler
    /// could not evaluate the expression.
    /// </param>
    /// <param name="onTimeout">
    /// Steps of the <c>.OnTimeout(...)</c> block, already carrying their flat indices, or empty
    /// when the author declared none.
    /// </param>
    /// <param name="signalType">
    /// Identity of the signal an <c>.OrSignal&lt;TSignal&gt;()</c> declared, or null when the
    /// poll has one ending.
    /// </param>
    /// <param name="signalContractTypeName">
    /// Fully-qualified <c>TSignal</c>, or null when there is none or it could not be resolved.
    /// </param>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="timeout"/> is not an ISO-8601 duration the manifest schema accepts.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <strong>The layout is <c>poll · attempt · escalation…</c>, and it is the only one here
    /// with two contiguous blocks of different meanings.</strong> The attempt is a block the
    /// engine re-enters; the escalation is a block it may jump into. They cannot be
    /// interchanged, which is why the attempt is exactly one step and the join is derived from
    /// the escalation rather than from both.
    /// </para>
    /// <para>
    /// The attempt is carried in <see cref="Body"/> and the escalation in <see cref="Then"/>,
    /// which is reuse rather than overloading, for <see cref="AwaitSignal"/>'s reason: a loop's
    /// body and a wait's escalation are what those two collections already mean, and every
    /// reader that walks <see cref="SelfAndNested"/> sees both without learning anything.
    /// </para>
    /// </remarks>
    public static StepModel Poll(
        int index,
        StepModel attempt,
        string? predicate = null,
        string? interval = null,
        string? timeoutExpression = null,
        string? predicateLocation = null,
        string? location = null,
        string? timeout = null,
        IReadOnlyList<StepModel>? onTimeout = null,
        string? signalType = null,
        string? signalContractTypeName = null)
    {
        // Refused here rather than left to the schema, for AwaitSignal's reason: the schema is
        // validated by this repository's tests and never by an application's build.
        if (timeout != null && !Iso8601.IsMatch(timeout))
        {
            throw new System.ArgumentException(
                $"'{timeout}' is not an ISO-8601 duration. The manifest's `timeout` field is " +
                "`#/$defs/duration`, the same shape a flow's deadline uses.",
                nameof(timeout));
        }

        var body = attempt is null
            ? System.Array.Empty<StepModel>()
            : new[] { attempt };

        var block = onTimeout ?? (IReadOnlyList<StepModel>)System.Array.Empty<StepModel>();

        return new StepModel(index, StepKindModel.Poll)
        {
            Body = body,
            Then = block,
            PollPredicate = predicate,
            PollPredicateLocation = predicateLocation,
            PollInterval = interval,
            PollTimeout = timeoutExpression,
            PollTimeoutIso = timeout,

            // The same two properties an AwaitSignal fills, and deliberately not a second pair.
            // A poll's alternative ending is an inbound address in exactly the sense a
            // suspension point's is, so the manifest's `signal` field, the generated route and
            // the state bag's membership all read it without learning a new name.
            SignalType = signalType,
            SignalContractTypeName = signalContractTypeName,

            // One past the escalation block, which is where a satisfied poll carries on — and
            // where the block falls through to, because the two paths rejoin. With no block it
            // is the index after the attempt, and the emitter writes no target at all: a target
            // equal to that index would read as an escalation that runs nothing.
            JoinIndex = block.Count == 0 ? index + 2 : block[block.Count - 1].NextIndex,
            Location = location,
        };
    }

    /// <summary>The manifest schema's <c>duration</c> pattern, copied so the model can hold to it.</summary>
    /// <remarks>
    /// Duplicated from <c>schemas/flowx.manifest.schema.json</c>'s <c>#/$defs/duration</c> for
    /// the reason <c>ManifestWriter.PolicyStages</c> is duplicated from <c>PolicySet</c>: this
    /// assembly targets netstandard2.0, loads into the compiler process and reads no files.
    /// Exposed rather than private because the copy has to be pinned to survive, and
    /// <c>TheModelsDurationPatternIsTheSchemasOwn</c> is what pins it — it reads the committed
    /// schema and fails if the two ever disagree.
    /// </remarks>
    public const string Iso8601DurationPattern =
        @"^P(?!$)(\d+Y)?(\d+M)?(\d+D)?(T(?=\d)(\d+H)?(\d+M)?(\d+(\.\d+)?S)?)?$";

    private static readonly System.Text.RegularExpressions.Regex Iso8601 =
        new System.Text.RegularExpressions.Regex(
            Iso8601DurationPattern,
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            System.TimeSpan.FromSeconds(1));

    /// <summary>Models a <c>.When(predicate, then)</c> and the <c>.Otherwise(...)</c> that may follow.</summary>
    /// <param name="index">Flat index of the branch itself.</param>
    /// <param name="predicate">The predicate's source text, copied verbatim.</param>
    /// <param name="then">Steps of the <c>then</c> block, already carrying their flat indices.</param>
    /// <param name="otherwise">Steps of the <c>Otherwise</c> block, or empty when there is none.</param>
    /// <param name="predicateLocation"><c>file:line</c> of the predicate expression.</param>
    /// <param name="location"><c>file:line</c> of the <c>.When</c> call.</param>
    /// <remarks>
    /// The three layout numbers — <see cref="FalseTarget"/>, <see cref="JumpIndex"/>,
    /// <see cref="JoinIndex"/> — are derived here from the blocks rather than passed in.
    /// They are a consequence of the layout, not an independent decision, and a caller
    /// able to supply a target that disagreed with the blocks it also supplied could
    /// produce a plan that skips or repeats real steps.
    /// </remarks>
    public static StepModel Condition(
        int index,
        string predicate,
        IReadOnlyList<StepModel> then,
        IReadOnlyList<StepModel>? otherwise = null,
        string? predicateLocation = null,
        string? location = null)
    {
        var thenSteps = then ?? (IReadOnlyList<StepModel>)System.Array.Empty<StepModel>();
        var otherwiseSteps = otherwise ?? (IReadOnlyList<StepModel>)System.Array.Empty<StepModel>();

        var thenEnd = thenSteps.Count == 0 ? index + 1 : thenSteps[thenSteps.Count - 1].NextIndex;

        // With no alternative there is nothing to skip, so no jump is emitted and the
        // false path lands exactly where the `then` block ended. Emitting a jump anyway
        // would leave a step in the graph that does nothing but cost an index.
        var hasOtherwise = otherwiseSteps.Count > 0;

        var join = hasOtherwise
            ? otherwiseSteps[otherwiseSteps.Count - 1].NextIndex
            : thenEnd;

        return new StepModel(index, StepKindModel.Condition)
        {
            Predicate = predicate,
            PredicateLocation = predicateLocation,
            Then = thenSteps,
            Otherwise = otherwiseSteps,
            JumpIndex = hasOtherwise ? thenEnd : (int?)null,
            FalseTarget = hasOtherwise ? thenEnd + 1 : thenEnd,
            JoinIndex = join,
            Location = location,
        };
    }

    /// <summary>Models a <c>.Switch(selector)</c> and the <c>.Case</c>/<c>.Default</c> blocks that follow.</summary>
    /// <param name="index">Flat index of the switch itself.</param>
    /// <param name="selector">The selector's source text, copied verbatim.</param>
    /// <param name="selectorTypeName">Fully-qualified type of the value it produces.</param>
    /// <param name="cases">The arms, already carrying their blocks' flat indices.</param>
    /// <param name="default">Steps of the <c>Default</c> block, or empty when there is none.</param>
    /// <param name="selectorLocation"><c>file:line</c> of the selector expression.</param>
    /// <param name="location"><c>file:line</c> of the <c>.Switch</c> call.</param>
    /// <remarks>
    /// <para>
    /// The layout is <c>switch · case₀… · jump · case₁… · jump · … · default…</c>, and
    /// every number in it — each case's target, each closing jump, the default target and
    /// the join — is derived here from the blocks rather than passed in, for the reason
    /// given on <see cref="Condition"/>.
    /// </para>
    /// <para>
    /// A block that declared nothing occupies no indices and needs no jump: its target is
    /// the join, so matching it simply continues after the switch. Only a block with a
    /// non-empty block <em>after</em> it gets a closing jump, which is why the last one
    /// never has one.
    /// </para>
    /// </remarks>
    public static StepModel Switch(
        int index,
        string selector,
        string selectorTypeName,
        IReadOnlyList<SwitchCaseModel> cases,
        IReadOnlyList<StepModel>? @default = null,
        string? selectorLocation = null,
        string? location = null)
    {
        var arms = cases ?? (IReadOnlyList<SwitchCaseModel>)System.Array.Empty<SwitchCaseModel>();
        var defaultSteps = @default ?? (IReadOnlyList<StepModel>)System.Array.Empty<StepModel>();

        // Every block in layout order — the cases, then the default — so the jump
        // arithmetic is written once and cannot disagree between the two.
        var blocks = new List<IReadOnlyList<StepModel>>(arms.Count + 1);

        foreach (var arm in arms)
        {
            blocks.Add(arm.Steps);
        }

        blocks.Add(defaultSteps);

        var ends = new int[blocks.Count];
        var join = index + 1;

        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];

            ends[i] = block.Count == 0 ? -1 : block[block.Count - 1].NextIndex;

            if (ends[i] > join)
            {
                join = ends[i];
            }
        }

        // A block's jump exists only to skip what follows it, so it is emitted when some
        // later block is non-empty and omitted otherwise.
        var jumps = new int?[blocks.Count];

        for (var i = 0; i < blocks.Count; i++)
        {
            if (ends[i] < 0)
            {
                continue;
            }

            for (var later = i + 1; later < blocks.Count; later++)
            {
                if (ends[later] >= 0)
                {
                    jumps[i] = ends[i];
                    break;
                }
            }
        }

        var resolved = new List<SwitchCaseModel>(arms.Count);

        for (var i = 0; i < arms.Count; i++)
        {
            resolved.Add(arms[i] with
            {
                Target = arms[i].Steps.Count == 0 ? join : arms[i].Steps[0].Index,
                JumpIndex = jumps[i],
            });
        }

        return new StepModel(index, StepKindModel.Switch)
        {
            Selector = selector,
            SelectorLocation = selectorLocation,
            SelectorTypeName = selectorTypeName,
            Cases = resolved,
            Default = defaultSteps,
            DefaultTarget = defaultSteps.Count == 0 ? join : defaultSteps[0].Index,
            JoinIndex = join,
            Location = location,
        };
    }

    /// <summary>Models a <c>.Parallel(p =&gt; p.Branch…(), merge)</c> and lays its branches out in the flat index space.</summary>
    /// <param name="index">Flat index of the fork itself.</param>
    /// <param name="branches">The branches, already carrying their blocks' flat indices. Each must be non-empty.</param>
    /// <param name="mergeExpression">The <c>merge:</c> argument's source text, copied verbatim.</param>
    /// <param name="mergeKindName">Which <c>MergeKind</c> the expression names, or <c>null</c> when unreadable.</param>
    /// <param name="location"><c>file:line</c> of the <c>.Parallel</c> call.</param>
    /// <remarks>
    /// <para>
    /// The layout is <c>parallel · branch₀… · branch₁… · …</c>, and — unlike
    /// <see cref="Switch"/> — <strong>there are no closing jumps</strong>. A case block
    /// needs one because the arms are laid out adjacently and a taken arm would otherwise
    /// fall into the next; a branch does not, because the engine runs each branch as a
    /// bounded range and the next branch's target <em>is</em> the bound. Emitting jumps
    /// anyway would put steps in the graph whose only job is to restate a number the
    /// layout already carries.
    /// </para>
    /// <para>
    /// Every number here is derived from the blocks rather than passed in, for the reason
    /// given on <see cref="Condition"/>: a caller able to state a target that disagreed
    /// with the block it also supplied could produce a plan that runs the wrong steps
    /// concurrently, which is a race rather than merely a wrong answer.
    /// </para>
    /// <para>
    /// An empty branch is not laid out and not modelled — <c>FlowAnalyzer</c> drops it
    /// before it gets here, and <c>StepNode.ForParallel</c> rejects the layout it would
    /// produce. A branch that runs no steps would still count towards a quorum, which is a
    /// silent way to make <c>Quorum(2)</c> mean <c>Quorum(1)</c>.
    /// </para>
    /// </remarks>
    public static StepModel Parallel(
        int index,
        IReadOnlyList<ParallelBranchModel> branches,
        string mergeExpression,
        string? mergeKindName = null,
        string? location = null)
    {
        var blocks = branches ?? (IReadOnlyList<ParallelBranchModel>)System.Array.Empty<ParallelBranchModel>();
        var join = index + 1;

        foreach (var branch in blocks)
        {
            if (branch.Steps.Count == 0)
            {
                continue;
            }

            branch.Target = branch.Steps[0].Index;

            var end = branch.Steps[branch.Steps.Count - 1].NextIndex;

            if (end > join)
            {
                join = end;
            }
        }

        return new StepModel(index, StepKindModel.Parallel)
        {
            Branches = blocks,
            MergeExpression = mergeExpression,
            MergeKindName = mergeKindName,
            JoinIndex = join,
            Location = location,
        };
    }

    /// <summary>Models a <c>.ForEach(selector, body, options)</c> and lays its body out in the flat index space.</summary>
    /// <param name="index">Flat index of the loop itself.</param>
    /// <param name="selector">The collection selector's source text, copied verbatim.</param>
    /// <param name="itemTypeName">Fully-qualified type of the element it yields.</param>
    /// <param name="body">The body's steps, already carrying their flat indices. Must be non-empty.</param>
    /// <param name="optionsExpression">The <c>options:</c> argument's source text, copied verbatim.</param>
    /// <param name="selectorLocation"><c>file:line</c> of the selector expression.</param>
    /// <param name="location"><c>file:line</c> of the <c>.ForEach</c> call.</param>
    /// <remarks>
    /// <para>
    /// The layout is <c>foreach · body…</c> — the simplest of the four branching shapes,
    /// because there is one block, it starts at the very next index, and there is nothing
    /// after it to skip. So there is no closing jump, no per-block target and no default:
    /// the only number is the join, and it is derived here from the body rather than
    /// passed in, for the reason given on <see cref="Condition"/>.
    /// </para>
    /// <para>
    /// <strong>What is not in the layout is the interesting part.</strong> The body appears
    /// once in the step array however many elements the collection holds, because the
    /// engine re-runs the same span rather than the generator unrolling it. That is what
    /// keeps the compiled plan, the manifest and a rendered diagram independent of the size
    /// of the data — a flow that reserves one line and one that reserves a thousand compile
    /// to the same graph.
    /// </para>
    /// <para>
    /// An empty body is not laid out and not modelled — <c>FlowAnalyzer</c> drops it before
    /// it gets here, and <c>StepNode.ForEach</c> rejects the layout it would produce.
    /// </para>
    /// </remarks>
    public static StepModel ForEach(
        int index,
        string selector,
        string itemTypeName,
        IReadOnlyList<StepModel> body,
        string optionsExpression,
        string? selectorLocation = null,
        string? location = null)
    {
        var steps = body ?? (IReadOnlyList<StepModel>)System.Array.Empty<StepModel>();

        return new StepModel(index, StepKindModel.ForEach)
        {
            Selector = selector,
            SelectorLocation = selectorLocation,
            ItemTypeName = itemTypeName,
            Body = steps,
            OptionsExpression = optionsExpression,
            JoinIndex = steps.Count == 0 ? index + 1 : steps[steps.Count - 1].NextIndex,
            Location = location,
        };
    }

    /// <summary>Models a <c>.SubFlow&lt;TFlow, TSubIn&gt;(map, mode)</c> call.</summary>
    /// <param name="index">Flat index of the composition itself.</param>
    /// <param name="subFlowId">The child's business identity, from its <c>[Flow]</c> attribute.</param>
    /// <param name="subFlowTypeName">Fully-qualified type of the child flow.</param>
    /// <param name="subFlowInputTypeName">Fully-qualified input contract of the child.</param>
    /// <param name="map">The input mapping's source text, copied verbatim.</param>
    /// <param name="mode"><c>Inline</c> or <c>Detached</c>.</param>
    /// <param name="mapLocation"><c>file:line</c> of the mapping expression.</param>
    /// <param name="location"><c>file:line</c> of the <c>.SubFlow</c> call.</param>
    /// <remarks>
    /// <para>
    /// <strong>There is no layout.</strong> Every other composite factory here derives
    /// targets, jumps and a join from blocks it was handed; this one has no blocks. A
    /// sub-flow occupies exactly one index and the steps it runs are in another
    /// <c>FlowModel</c> — which is why it is also the only kind whose
    /// <see cref="NextIndex"/> is the plain <c>Index + 1</c> that a capability step uses.
    /// </para>
    /// <para>
    /// The consequence worth stating: a flow that composes a hundred-step child produces a
    /// manifest the size of the flow the author wrote, and <c>flowx diff</c> sees a change
    /// to the child as a change to the child. Splicing would have made every parent's
    /// document grow with every child's, and every child's edit a diff in every parent.
    /// </para>
    /// </remarks>
    public static StepModel SubFlow(
        int index,
        string subFlowId,
        string subFlowTypeName,
        string subFlowInputTypeName,
        string map,
        string mode,
        string? mapLocation = null,
        string? location = null)
    {
        return new StepModel(index, StepKindModel.SubFlow)
        {
            SubFlowId = subFlowId,
            SubFlowTypeName = subFlowTypeName,
            SubFlowInputTypeName = subFlowInputTypeName,
            SubFlowMap = map,
            SubFlowMode = mode,
            SubFlowMapLocation = mapLocation,
            Location = location,
        };
    }

    /// <summary>Returns a copy carrying a compensation.</summary>
    /// <param name="compensation">
    /// The compensating capability, modelled exactly as a step is — build it with
    /// <see cref="Capability"/> so it reaches the manifest with its full metadata.
    /// </param>
    public StepModel WithCompensation(StepModel compensation) => this with
    {
        Compensation = compensation,
    };

    /// <summary>Returns a copy carrying the capability its fallback would ask.</summary>
    /// <param name="fallback">
    /// The fallback capability, modelled exactly as a step is — build it with
    /// <see cref="Capability"/> so it reaches the manifest with its full metadata.
    /// </param>
    public StepModel WithFallbackCapability(StepModel fallback) => this with
    {
        FallbackCapability = fallback,
    };

    /// <summary>Returns a copy carrying a named policy set.</summary>
    /// <param name="policySetName">The set as it was written at the call site.</param>
    /// <param name="policyKinds">What the set declares, or empty when it could not be read.</param>
    public StepModel WithPolicy(string policySetName, string[]? policyKinds = null) => this with
    {
        PolicySetName = policySetName,
        PolicyKinds = policyKinds ?? System.Array.Empty<string>(),
    };
}
