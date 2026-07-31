using FlowX.Runtime;

namespace FlowX.Runtime.Tests;

/// <summary>
/// A hand-written <see cref="IStepDispatcher"/> that records what the engine asked
/// it to do.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately shaped like the code <c>FlowPlanGenerator</c> will emit at
/// WP-5: a switch on the step index, with the step's typed state held in the
/// dispatcher's own fields rather than in a state bag. Writing it by hand first
/// proves the shape works before a generator has to produce it — and if this is
/// awkward to write, the generated version would have been awkward to debug.
/// </para>
/// <para>
/// The type erasure matters. The engine's loop cannot know each step's input and
/// output types; if it boxed them into <c>object</c> it would allocate per step and
/// budget B2 would be lost on the first commit. So the dispatcher keeps the types
/// and the engine sees only success or failure.
/// </para>
/// </remarks>
internal sealed class RecordingDispatcher : IStepDispatcher
{
    /// <summary>
    /// Guards the recording collections, because a parallel flow calls this from several
    /// threads at once.
    /// </summary>
    /// <remarks>
    /// A test double, not the engine, and the lock is here for the same reason a test
    /// double is written by hand at all: an unguarded <c>List&lt;int&gt;.Add</c> under a
    /// fork drops entries at random, and the test that then fails one run in twenty
    /// teaches the team that the suite is flaky rather than that the engine is wrong.
    /// </remarks>
    private readonly Lock _recording = new();

    private readonly Dictionary<int, Error> _failures = [];
    private readonly Dictionary<int, Error> _compensationFailures = [];
    private readonly Dictionary<int, bool> _predicates = [];
    private readonly Dictionary<int, int> _cases = [];
    private readonly HashSet<int> _yieldingSteps = [];

    // Shaped exactly like the two methods the generator emits: a delegate that knows the
    // element type, so the engine sees only an opaque handle and a FlowContext. Writing
    // them by hand first is what proves the contract is implementable at all.
    private readonly Dictionary<int, IterationSource> _collections = [];
    private readonly Dictionary<int, Func<IterationSource, int, FlowContext, FlowContext>> _scopes = [];

    private readonly Dictionary<(int Index, int Visit), Error> _visitFailures = [];
    private readonly Dictionary<int, int> _visits = [];

    /// <summary>Step indices executed, in the order the engine invoked them.</summary>
    public List<int> Executed { get; } = [];

    /// <summary>Step indices compensated, in the order the engine unwound them.</summary>
    public List<int> Compensated { get; } = [];

    /// <summary>Branch indices the engine asked about, in the order it asked.</summary>
    /// <remarks>
    /// Recorded so a test can assert the engine consulted the branch <em>once</em>. A
    /// predicate evaluated twice would be free here and expensive in a real flow, where
    /// it reads the context and, in a durable flow, has to answer the same way on replay.
    /// </remarks>
    public List<int> Evaluated { get; } = [];

    /// <summary>Switch indices the engine asked about, in the order it asked.</summary>
    /// <remarks>
    /// Recorded for the same reason as <see cref="Evaluated"/>: a selector consulted
    /// twice would be free here and, in a durable flow, has to answer the same way on
    /// replay.
    /// </remarks>
    public List<int> Selected { get; } = [];

    /// <summary>Set to make a branch throw rather than answer.</summary>
    public int? ThrowAtBranch { get; set; }

    /// <summary>Set to make a switch selector throw rather than answer.</summary>
    public int? ThrowAtSwitch { get; set; }

    /// <summary>Set to make a collection selector throw rather than produce a collection.</summary>
    public int? ThrowAtIteration { get; set; }

    /// <summary>Iteration indices the engine asked for a collection, in the order it asked.</summary>
    /// <remarks>
    /// Recorded so a test can assert the selector ran <em>once</em> for a loop of ten
    /// elements. Running it per element would be free here and, in a durable flow, would
    /// have to produce the same collection every time.
    /// </remarks>
    public List<int> Iterated { get; } = [];

    /// <summary>
    /// The contexts each compensation ran under, in unwind order.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ContextsSeen"/> these <em>can</em> be read after the flow: inside a
    /// loop the entry is the iteration's scope, which is not pooled and still holds its
    /// element. That is what lets a test check that the undo of the third line undid the
    /// third line.
    /// </remarks>
    public List<FlowContext> CompensationScopes { get; } = [];

    /// <summary>The context instances seen, for reference-identity assertions only.</summary>
    /// <remarks>
    /// Do not read values off these after the engine returns: the context is reset the
    /// moment it goes back to the pool, so every field reads as empty. That the naive
    /// version of this test failed is the clearest evidence the reset works — see
    /// <see cref="Snapshots"/> for values captured while the step was running.
    /// </remarks>
    public List<FlowContext> ContextsSeen { get; } = [];

    /// <summary>Context values captured <em>during</em> each step, while they are still live.</summary>
    public List<ContextSnapshot> Snapshots { get; } = [];

    /// <summary>Set to have a step observe cancellation instead of completing.</summary>
    public int? CancelAtStep { get; set; }

    /// <summary>Invoked before each step runs, so a test can advance a fake clock.</summary>
    public Action<int>? BeforeStep { get; set; }

    /// <summary>Makes step <paramref name="index"/> fail with <paramref name="error"/>.</summary>
    public RecordingDispatcher FailAt(int index, Error error)
    {
        _failures[index] = error;
        return this;
    }

    /// <summary>
    /// Makes the <paramref name="visit"/>-th execution of step <paramref name="index"/>
    /// fail, counting from one.
    /// </summary>
    /// <remarks>
    /// <see cref="FailAt"/> is not enough inside a loop: one index runs once per element,
    /// so "fail step 3" means "fail every element". Pinning the failure to a visit is what
    /// lets a test say that the <em>third</em> line was the one that was declined.
    /// </remarks>
    public RecordingDispatcher FailAtNthVisit(int index, int visit, Error error)
    {
        _visitFailures[(index, visit)] = error;
        return this;
    }

    /// <summary>Makes the compensation for step <paramref name="index"/> fail.</summary>
    public RecordingDispatcher FailCompensationAt(int index, Error error)
    {
        _compensationFailures[index] = error;
        return this;
    }

    /// <summary>Makes the branch at <paramref name="index"/> answer <paramref name="answer"/>.</summary>
    /// <remarks>An unlisted branch answers <c>true</c>, so a test states only what it cares about.</remarks>
    public RecordingDispatcher AnswerAt(int index, bool answer)
    {
        _predicates[index] = answer;
        return this;
    }

    /// <summary>Makes the switch at <paramref name="index"/> select case <paramref name="arm"/>.</summary>
    /// <remarks>
    /// An unlisted switch selects nothing — arm <c>-1</c> — so a test that says nothing
    /// exercises the default path, which is the arm most likely to be forgotten.
    /// </remarks>
    public RecordingDispatcher SelectAt(int index, int arm)
    {
        _cases[index] = arm;
        return this;
    }

    /// <summary>
    /// Makes step <paramref name="index"/> complete asynchronously rather than
    /// synchronously.
    /// </summary>
    /// <remarks>
    /// The only way to make a fork's branches genuinely overlap. The engine starts each
    /// branch eagerly on the calling thread, so a branch whose every step completes
    /// synchronously runs to the end before its sibling starts — correct, and useless for
    /// proving concurrency. A step that yields lets the sibling in.
    /// </remarks>
    public RecordingDispatcher YieldAt(int index)
    {
        _yieldingSteps.Add(index);
        return this;
    }

    /// <summary>Makes the iteration at <paramref name="index"/> walk <paramref name="items"/>.</summary>
    /// <remarks>
    /// The two delegates are the hand-written version of what the generator emits: the
    /// collection is captured behind an <see cref="IterationSource"/> the engine treats as
    /// opaque, and only this method — which knows <typeparamref name="TItem"/> — ever turns
    /// it back into an element.
    /// </remarks>
    public RecordingDispatcher IterateOver<TItem>(int index, IReadOnlyList<TItem> items)
    {
        _collections[index] = new IterationSource(items, items.Count);
        _scopes[index] = (source, element, ctx) =>
            IterationScope.For(ctx, ((IReadOnlyList<TItem>)source.Items!)[element]);

        return this;
    }

    /// <summary>Highest number of steps observed running at once. 1 means nothing overlapped.</summary>
    public int PeakConcurrency { get; private set; }

    /// <summary>The last <see cref="ForEachOutcome"/> any step could see, or <c>null</c>.</summary>
    public ForEachOutcome? OutcomeAfterTheLoop { get; private set; }

    private int _running;

    /// <inheritdoc />
    public async ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
    {
        BeforeStep?.Invoke(stepIndex);

        if (CancelAtStep == stepIndex)
        {
            ct.ThrowIfCancellationRequested();
        }

        int visit;

        lock (_recording)
        {
            _running++;
            PeakConcurrency = Math.Max(PeakConcurrency, _running);
            Executed.Add(stepIndex);
            ContextsSeen.Add(ctx);
            Snapshots.Add(ContextSnapshot.Of(ctx));

            visit = _visits.TryGetValue(stepIndex, out var seen) ? seen + 1 : 1;
            _visits[stepIndex] = visit;

            // Captured whenever it is there, so a test can read what a loop published
            // without the double having to know which step follows the loop.
            if (ctx.TryGet<ForEachOutcome>(out var outcome))
            {
                OutcomeAfterTheLoop = outcome;
            }
        }

        try
        {
            if (_yieldingSteps.Contains(stepIndex))
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
            }

            if (_visitFailures.TryGetValue((stepIndex, visit), out var visitError))
            {
                return StepOutcome.Failed(visitError);
            }

            return _failures.TryGetValue(stepIndex, out var error)
                ? StepOutcome.Failed(error)
                : StepOutcome.Success;
        }
        finally
        {
            lock (_recording)
            {
                _running--;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
    {
        lock (_recording)
        {
            Compensated.Add(stepIndex);
            CompensationScopes.Add(ctx);
        }

        return _compensationFailures.TryGetValue(stepIndex, out var error)
            ? ValueTask.FromResult(StepOutcome.Failed(error))
            : ValueTask.FromResult(StepOutcome.Success);
    }

    /// <inheritdoc />
    public bool Evaluate(int stepIndex, FlowContext ctx)
    {
        lock (_recording)
        {
            Evaluated.Add(stepIndex);
        }

        if (ThrowAtBranch == stepIndex)
        {
            // The realistic failure: a predicate reading a value no step on the path so
            // far produced. FlowContext.Get<T> throws exactly this.
            throw new InvalidOperationException("The predicate read a value no step produced.");
        }

        return !_predicates.TryGetValue(stepIndex, out var answer) || answer;
    }

    /// <inheritdoc />
    public int Select(int stepIndex, FlowContext ctx)
    {
        lock (_recording)
        {
            Selected.Add(stepIndex);
        }

        if (ThrowAtSwitch == stepIndex)
        {
            // The realistic failure: a selector reading a value no step on the path so
            // far produced. FlowContext.Get<T> throws exactly this.
            throw new InvalidOperationException("The selector read a value no step produced.");
        }

        return _cases.TryGetValue(stepIndex, out var arm) ? arm : -1;
    }

    /// <inheritdoc />
    public IterationSource BeginIteration(int stepIndex, FlowContext ctx)
    {
        lock (_recording)
        {
            Iterated.Add(stepIndex);
        }

        if (ThrowAtIteration == stepIndex)
        {
            // The realistic failure: a selector reading a value no step on the path so
            // far produced. FlowContext.Get<T> throws exactly this.
            throw new InvalidOperationException("The selector read a collection no step produced.");
        }

        // An unlisted iteration walks nothing, so a test that says nothing exercises the
        // empty-collection path — which is the one most likely to be forgotten.
        return _collections.TryGetValue(stepIndex, out var source) ? source : IterationSource.Empty;
    }

    /// <inheritdoc />
    public FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
        _scopes[stepIndex](source, iteration, ctx);
}

/// <summary>
/// The values a context carried at the instant a step ran.
/// </summary>
/// <remarks>
/// Needed because the context is pooled and reset. Asserting on the live object after
/// the flow finished would assert on a cleared instance — which is correct behaviour
/// and a useless test.
/// </remarks>
internal readonly record struct ContextSnapshot(
    string FlowId,
    string FlowVersion,
    string CorrelationId,
    string IdempotencyKey,
    string? TenantId,
    string CapabilityId,
    DateTimeOffset UtcNow,
    TimeSpan TimeRemaining,
    Error? Error,
    Guid NewId,
    bool HasRandom)
{
    public static ContextSnapshot Of(FlowContext ctx) => new(
        ctx.FlowId,
        ctx.FlowVersion,
        ctx.CorrelationId,
        ctx.IdempotencyKey,
        ctx.TenantId,
        ctx.CapabilityId,
        ctx.UtcNow,
        ctx.TimeRemaining,
        ctx.Error,
        ctx.NewId(),
        ctx.Random is not null);
}

/// <summary>A clock a test can move, so deadline behaviour is deterministic.</summary>
internal sealed class FakeClock(DateTimeOffset start) : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; } = start;

    /// <summary>Moves the clock forward.</summary>
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Plans the engine tests execute.</summary>
internal static class Plans
{
    public static CapabilityDescriptor Validate { get; } =
        CapabilityDescriptor.Create("order.validate", "1.0.0", isIdempotent: true);

    public static CapabilityDescriptor Reserve { get; } =
        CapabilityDescriptor.Create("inventory.reserve", "1.0.0", isIdempotent: true, "inventory-ledger");

    public static CapabilityDescriptor Release { get; } =
        CapabilityDescriptor.Create("inventory.release", "1.0.0", isIdempotent: true, "inventory-ledger");

    public static CapabilityDescriptor Capture { get; } =
        CapabilityDescriptor.Create("payment.capture", "2.1.0", isIdempotent: false, "payment-gateway");

    public static CapabilityDescriptor Refund { get; } =
        CapabilityDescriptor.Create("payment.refund", "2.1.0", isIdempotent: true, "payment-gateway");

    /// <summary>Four steps; steps 1 and 2 are compensable; step 3 emits.</summary>
    public static ExecutionPlan FourStepSaga(TimeSpan? deadline = null) => ExecutionPlan.Create(
        FlowDescriptor.Create(
            "order.place", "1.0.0", ExecutionProfile.Ephemeral, deadline ?? TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForCapability(1, Reserve, Release),
            StepNode.ForCapability(2, Capture, Refund),
            StepNode.ForEmit(3, "order.placed"),
        ]));

    /// <summary>
    /// A conditional, written out as the flat layout the compiler produces:
    /// <c>0 validate · 1 branch(else→5) · 2 reserve · 3 capture · 4 jump→6 · 5 validate · 6 emit</c>.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than built by a helper, because the layout <em>is</em> what
    /// these tests are about. A helper that computed the targets would compute them the
    /// same way the emitter does, and a shared bug would then pass on both sides.
    /// Step 2 is compensable so the unwind can be checked to cover only the branch that
    /// actually ran.
    /// </remarks>
    public static ExecutionPlan Conditional() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.review", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForBranch(1, falseTarget: 5),
            StepNode.ForCapability(2, Reserve, Release),
            StepNode.ForCapability(3, Capture),
            StepNode.ForJump(4, target: 6),
            StepNode.ForCapability(5, Validate),
            StepNode.ForEmit(6, "order.reviewed"),
        ]));

    /// <summary>
    /// A <c>When</c> with no <c>Otherwise</c> and nothing after it, so the false path
    /// targets one past the last step and ends the flow.
    /// </summary>
    public static ExecutionPlan ConditionalWithoutOtherwise() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.maybe", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForBranch(1, falseTarget: 3),
            StepNode.ForCapability(2, Capture),
        ]));

    /// <summary>
    /// A three-case switch with a default, written out as the flat layout the compiler
    /// produces:
    /// <c>0 validate · 1 switch(→2,4,6 else 8) · 2 capture · 3 jump→9 · 4 reserve ·
    /// 5 jump→9 · 6 capture · 7 jump→9 · 8 validate · 9 emit</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Spelled out rather than built by a helper, for the same reason
    /// <see cref="Conditional"/> is: the layout <em>is</em> what these tests are about,
    /// and a helper computing the targets the way the emitter does would let a shared bug
    /// pass on both sides.
    /// </para>
    /// <para>
    /// Every case block but the last is closed by a jump to the join, and the default
    /// block is not, because nothing follows it to skip. The step in case 1 is
    /// compensable, so an unwind can be checked to cover only the arm that actually ran.
    /// </para>
    /// </remarks>
    public static ExecutionPlan Switching() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.price", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForSwitch(1, [2, 4, 6], defaultTarget: 8),
            StepNode.ForCapability(2, Capture),
            StepNode.ForJump(3, target: 9),
            StepNode.ForCapability(4, Reserve, Release),
            StepNode.ForJump(5, target: 9),
            StepNode.ForCapability(6, Capture),
            StepNode.ForJump(7, target: 9),
            StepNode.ForCapability(8, Validate),
            StepNode.ForEmit(9, "order.priced"),
        ]));

    /// <summary>
    /// A switch with no <c>Default</c> and nothing after it, so a value that matches no
    /// case lands one past the last step and ends the flow.
    /// </summary>
    public static ExecutionPlan SwitchWithoutDefault() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.route", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForSwitch(1, [2, 4], defaultTarget: 5),
            StepNode.ForCapability(2, Reserve),
            StepNode.ForJump(3, target: 5),
            StepNode.ForCapability(4, Capture),
        ]));

    /// <summary>
    /// A three-branch fork, written out as the flat layout the compiler produces:
    /// <c>0 validate · 1 parallel(→2,3,4 join 5) · 2 reserve · 3 capture · 4 validate · 5 emit</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Spelled out rather than built by a helper, for the same reason
    /// <see cref="Conditional"/> and <see cref="Switching"/> are.
    /// </para>
    /// <para>
    /// <strong>No closing jumps, unlike a switch.</strong> A branch's range already ends
    /// where the next branch begins, so a jump to the join would be a step that exists only
    /// to say what the range bound already says. A switch needs them because its arms fall
    /// through into one another; a fork's do not, because nothing runs an arm it did not
    /// start.
    /// </para>
    /// <para>
    /// Step 2 is compensable, so an unwind can be checked to cover work a cancelled sibling
    /// had already completed.
    /// </para>
    /// </remarks>
    public static ExecutionPlan Parallel(MergeStrategy merge = default) => ExecutionPlan.Create(
        FlowDescriptor.Create("order.screen", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForParallel(1, [2, 3, 4], joinTarget: 5, merge),
            StepNode.ForCapability(2, Reserve, Release),
            StepNode.ForCapability(3, Capture),
            StepNode.ForCapability(4, Validate),
            StepNode.ForEmit(5, "order.screened"),
        ]));

    /// <summary>
    /// A fork whose branches are two steps each, so a branch is a range rather than a
    /// single index: <c>0 parallel(→1,3 join 5) · 1,2 · 3,4 · 5 emit</c>.
    /// </summary>
    /// <remarks>
    /// The <em>first</em> step of each branch is the compensable one, deliberately. That is
    /// what makes the cancellation test possible: branch 0 completes step 1 before it
    /// reaches anything that can yield, so when a sibling's failure cancels its step 2 there
    /// is provably already work on the unwind stack.
    /// </remarks>
    public static ExecutionPlan ParallelWithMultiStepBranches(MergeStrategy merge = default) => ExecutionPlan.Create(
        FlowDescriptor.Create("order.enrich", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForParallel(0, [1, 3], joinTarget: 5, merge),
            StepNode.ForCapability(1, Reserve, Release),
            StepNode.ForCapability(2, Validate),
            StepNode.ForCapability(3, Capture, Refund),
            StepNode.ForCapability(4, Validate),
            StepNode.ForEmit(5, "order.enriched"),
        ]));

    /// <summary>
    /// A loop, written out as the flat layout the compiler produces:
    /// <c>0 validate · 1 foreach(body 2..4, join 4) · 2 reserve · 3 capture · 4 emit</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Spelled out rather than built by a helper, for the same reason
    /// <see cref="Conditional"/>, <see cref="Switching"/> and <see cref="Parallel"/> are.
    /// </para>
    /// <para>
    /// <strong>The body appears once</strong>, however many elements the collection turns
    /// out to hold — that is the whole shape. It has no target of its own and no closing
    /// jump: it is the span between the loop node and its join, and the engine re-enters
    /// that span per element.
    /// </para>
    /// <para>
    /// Step 2 is compensable, so an unwind can be checked to cover every element's work in
    /// strict reverse.
    /// </para>
    /// </remarks>
    public static ExecutionPlan ForEach(int maxDegreeOfParallelism = 1, bool continueOnError = false) =>
        ExecutionPlan.Create(
            FlowDescriptor.Create("order.reserve", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([
                StepNode.ForCapability(0, Validate),
                StepNode.ForEach(1, joinTarget: 4, new ForEachOptions
                {
                    MaxDegreeOfParallelism = maxDegreeOfParallelism,
                    ContinueOnError = continueOnError,
                }),
                StepNode.ForCapability(2, Reserve, Release),
                StepNode.ForCapability(3, Capture),
                StepNode.ForEmit(4, "order.reserved"),
            ]));

    /// <summary>
    /// A loop with a single-step body, so the per-element cost is not diluted by the work
    /// inside it: <c>0 foreach(body 1..2, join 2) · 1 reserve · 2 emit</c>.
    /// </summary>
    public static ExecutionPlan ForEachWithOneStepBody(int maxDegreeOfParallelism = 1) => ExecutionPlan.Create(
        FlowDescriptor.Create("order.count", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForEach(0, joinTarget: 2, new ForEachOptions
            {
                MaxDegreeOfParallelism = maxDegreeOfParallelism,
            }),
            StepNode.ForCapability(1, Validate),
            StepNode.ForEmit(2, "order.counted"),
        ]));

    /// <summary>
    /// A loop inside a loop: <c>0 foreach(body 1..4) · 1 foreach(body 2..3) · 2 reserve ·
    /// 3 capture · 4 emit</c>.
    /// </summary>
    /// <remarks>
    /// The inner loop is an ordinary step of the outer's body, numbered from the same flat
    /// counter, and step 3 is what follows it inside that body. Nothing about the layout is
    /// special-cased for nesting — which is the claim worth testing, because the alternative
    /// is a scope chain that resolves to the wrong element.
    /// </remarks>
    public static ExecutionPlan NestedForEach() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.explode", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForEach(0, joinTarget: 4, new ForEachOptions { MaxDegreeOfParallelism = 1 }),
            StepNode.ForEach(1, joinTarget: 3, new ForEachOptions { MaxDegreeOfParallelism = 1 }),
            StepNode.ForCapability(2, Reserve),
            StepNode.ForCapability(3, Capture),
            StepNode.ForEmit(4, "order.exploded"),
        ]));

    /// <summary>Two steps, neither compensable.</summary>
    public static ExecutionPlan TwoStepQuery() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.get", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(5)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForCapability(1, Validate),
        ]));

    public static FlowInvocation Invocation { get; } = new("corr-1", "idem-1", TenantId: "acme");
}
