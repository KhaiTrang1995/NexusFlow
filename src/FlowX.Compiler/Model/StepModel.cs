using System.Collections.Generic;

namespace FlowX.Compiler.Model;

/// <summary>What one call in a <c>Define</c> chain declared.</summary>
/// <remarks>
/// The remaining branching kind — sub-flow — arrives with the DSL surface that can express
/// it. Modelling it now would be a shape nothing can produce and no test can exercise.
/// </remarks>
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

    /// <summary>The capability's input contract, fully qualified. Required by the manifest schema.</summary>
    public string? CapabilityInput { get; private init; }

    /// <summary>The capability's output contract, fully qualified.</summary>
    public string? CapabilityOutput { get; private init; }

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

    /// <summary>Fully-qualified compensation type, or <c>null</c>.</summary>
    public string? CompensationTypeName => Compensation?.CapabilityTypeName;

    /// <summary>Business identity of the compensation.</summary>
    public string? CompensationId => Compensation?.CapabilityId;

    /// <summary>Contract version of the compensation.</summary>
    public string? CompensationVersion => Compensation?.CapabilityVersion;

    /// <summary>Event identity for an <see cref="StepKindModel.Emit"/> step.</summary>
    public string? EventType { get; private init; }

    /// <summary>Signal identity for an <see cref="StepKindModel.AwaitSignal"/> step.</summary>
    public string? SignalType { get; private init; }

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
            or StepKindModel.ForEach
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

            foreach (var nested in Body)
            {
                foreach (var step in nested.SelfAndNested)
                {
                    yield return step;
                }
            }
        }
    }

    /// <summary>Models a <c>.Step&lt;TCapability&gt;()</c> call.</summary>
    public static StepModel Capability(
        int index,
        string capabilityTypeName,
        string capabilityId,
        string capabilityVersion,
        bool isIdempotent,
        string[]? sideEffects = null,
        string? location = null,
        string? authorizationMode = null,
        string? capabilityInput = null,
        string? capabilityOutput = null)
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
            CapabilityInput = capabilityInput,
            CapabilityOutput = capabilityOutput,
        };
    }

    /// <summary>Models an <c>.Emit&lt;TEvent&gt;(...)</c> call.</summary>
    public static StepModel Emit(int index, string eventType, string? location = null)
    {
        return new StepModel(index, StepKindModel.Emit)
        {
            EventType = eventType,
            Location = location,
        };
    }

    /// <summary>Models an <c>.AwaitSignal&lt;TSignal&gt;(...)</c> call.</summary>
    public static StepModel AwaitSignal(int index, string signalType, string? location = null)
    {
        return new StepModel(index, StepKindModel.AwaitSignal)
        {
            SignalType = signalType,
            Location = location,
        };
    }

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

    /// <summary>Returns a copy carrying a compensation.</summary>
    /// <param name="compensation">
    /// The compensating capability, modelled exactly as a step is — build it with
    /// <see cref="Capability"/> so it reaches the manifest with its full metadata.
    /// </param>
    public StepModel WithCompensation(StepModel compensation) => this with
    {
        Compensation = compensation,
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
