using System.Runtime.CompilerServices;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// Budget B2 for the engine itself: <strong>zero allocations per step</strong> on the
/// success path of an ephemeral flow.
/// </summary>
/// <remarks>
/// <para>
/// This is WP-4's exit criterion, and it is asserted rather than benchmarked because
/// allocation counts are deterministic while timings are not. A benchmark would tell
/// us nightly; this tells us on every pull request.
/// </para>
/// <para>
/// <strong>Release only</strong>, and skipped rather than failed in Debug. The C#
/// compiler emits an async state machine as a class in Debug and as a struct in
/// Release, so a Debug run measures 376 B of Edit-and-Continue scaffolding and reports
/// it as an engine allocation. That number is not the engine's, and a gate that fails
/// in the configuration everyone runs locally is a gate people learn to ignore.
/// </para>
/// </remarks>
public sealed class EngineAllocationTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    /// <summary>
    /// Skips the measurement in Debug, where it would measure the compiler rather than
    /// the engine. See the class remarks.
    /// </summary>
    private static void RequireOptimisedBuild()
    {
#if DEBUG
        Assert.Skip(
            "Allocation budgets are measured in Release only. In Debug the compiler emits " +
            "async state machines as classes, which shows up as a few hundred bytes per " +
            "execution that the engine does not allocate. Run: dotnet test -c Release");
#endif
    }

    /// <summary>
    /// Measures one execution after the pool, the JIT and the async state machine have
    /// all warmed up. The warm-up matters: the first execution legitimately allocates
    /// the pooled context, and charging that to the steady state would measure startup
    /// rather than the hot path.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureSteadyState(FlowEngine engine, ExecutionPlan plan, IStepDispatcher dispatcher)
    {
        for (var i = 0; i < 64; i++)
        {
            RunSync(engine.ExecuteAsync(plan, dispatcher, Plans.Invocation));
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        RunSync(engine.ExecuteAsync(plan, dispatcher, Plans.Invocation));
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>
    /// Completes a <see cref="ValueTask{TResult}"/> that must already be finished.
    /// </summary>
    /// <remarks>
    /// The assertion is the point, not a formality. When every step completes
    /// synchronously the engine must too — a flow of synchronous steps that
    /// gratuitously goes async would allocate a state-machine box per execution and
    /// lose budget B2. Measuring across an await would also risk a thread switch,
    /// which would make GetAllocatedBytesForCurrentThread meaningless.
    /// </remarks>
    private static FlowExecutionResult RunSync(ValueTask<FlowExecutionResult> execution)
    {
        execution.IsCompleted.ShouldBeTrue(
            "The engine went asynchronous for a flow whose every step completed " +
            "synchronously. That allocates a state machine on the hot path.");

        return execution.GetAwaiter().GetResult();
    }

    [Fact]
    public void TheMeasurementCanDetectAnAllocationItShouldSee()
    {
        // The positive control, same as in FlowX.Core.Tests. A zero-allocation
        // assertion that cannot fail is worse than none.
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var wasted = new object[4];
        var after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).ShouldBeGreaterThan(0);
        wasted.Length.ShouldBe(4);
    }

    [Fact]
    public void ASuccessfulFourStepFlowAllocatesNothingInSteadyState()
    {
        RequireOptimisedBuild();

        var engine = new FlowEngine(new FakeClock(T0));
        var plan = Plans.FourStepSaga();
        var dispatcher = new NullDispatcher();

        var allocated = MeasureSteadyState(engine, plan, dispatcher);

        allocated.ShouldBe(0,
            $"Measured {allocated} B. Budget B2 is a hard zero, and this is WP-4's exit criterion. The context " +
            "is pooled, StepOutcome and FlowExecutionResult are structs, and the step " +
            "loop indexes an ImmutableArray rather than enumerating an interface.");
    }

    [Fact]
    public void AFlowWithNoCompensableStepsAllocatesNoCompensationStack()
    {
        RequireOptimisedBuild();

        var engine = new FlowEngine(new FakeClock(T0));

        var allocated = MeasureSteadyState(engine, Plans.TwoStepQuery(), new NullDispatcher());

        allocated.ShouldBe(0,
            $"Measured {allocated} B. A query flow must not pay for saga machinery it never uses — the engine " +
            "only builds a CompensationStack when the plan declares one.");
    }

    /// <summary>
    /// The failure path allocates today, and this records how much rather than
    /// asserting a zero that is not true.
    /// </summary>
    /// <remarks>
    /// Two sources: the <c>CompensationStack</c> (a <c>Stack&lt;T&gt;</c> plus an
    /// iterator, already tracked by the tripwire in <c>FlowX.Core.Tests</c>) and the
    /// <c>Error</c> record with its structured data. Both are acceptable — they occur
    /// once per *failed* flow, not per step — but they are measured so a future change
    /// cannot quietly move an allocation from the failure path onto the success path.
    /// </remarks>
    [Fact]
    public void TheFailurePathAllocatesAndTheAmountIsRecorded()
    {
        // Guarded for the same reason as the zero assertions: the ceiling is only
        // meaningful against a Release measurement.
        RequireOptimisedBuild();

        var engine = new FlowEngine(new FakeClock(T0));
        var plan = Plans.FourStepSaga();
        var dispatcher = new NullDispatcher
        {
            FailAtStep = 2,
            Failure = new Error("payment.declined", "declined", ErrorCategory.Conflict),
        };

        var allocated = MeasureSteadyState(engine, plan, dispatcher);

        allocated.ShouldBeGreaterThan(0);
        allocated.ShouldBeLessThan(2048,
            "Compensation is allowed to allocate; it is not allowed to allocate a lot. " +
            "If this ceiling is ever hit, something moved onto the failure path that " +
            "does not belong there.");
    }

    /// <summary>
    /// A flow whose undo carries a retry policy pays nothing for it while it is succeeding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The regression this forbids is the obvious way to build WP-57: resolving the
    /// compensation's policy chain, or building a retry state object, somewhere the forward
    /// loop can reach. The policy is resolved once when the plan is built and hangs off
    /// <see cref="StepNode"/>; the loop that runs the flow never looks at it, and the flag
    /// that says the flow has one is precomputed exactly the way
    /// <see cref="ExecutionPlan.HasParallel"/> is.
    /// </para>
    /// <para><strong>Measured: 0 B, Release, .NET 10, x64.</strong></para>
    /// </remarks>
    [Fact]
    public void ACompensationPolicyCostsTheSuccessPathNothing()
    {
        RequireOptimisedBuild();

        var engine = new FlowEngine(new FakeClock(T0));

        var allocated = MeasureSteadyState(engine, RetryingSaga(), new NullDispatcher());

        allocated.ShouldBe(0,
            $"Measured {allocated} B for a four-step saga whose first undo declares a retry. " +
            "A policy nobody has needed yet is a field on a node the loop does not read.");
    }

    /// <summary>The four-step saga, with a compensation retry declared on step 1's undo.</summary>
    private static ExecutionPlan RetryingSaga() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.place", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Plans.Validate),
            StepNode.ForCapability(
                1,
                Plans.Reserve,
                Plans.Release,
                compensationPolicies: PolicyChain.Create(
                    PolicySet.Named("undo").CompensationRetry(3), Plans.Release)),
            StepNode.ForCapability(2, Plans.Capture, Plans.Refund),
            StepNode.ForEmit(3, "order.placed"),
        ]));

    /// <summary>
    /// Budget B2 has to survive branching, or the DSL's most-used shape quietly buys
    /// back the allocation the engine was built to avoid.
    /// </summary>
    /// <remarks>
    /// Both directions, because they cost differently in principle: the true path falls
    /// through to the next index, and the false path takes the target. Neither may
    /// allocate — the predicate is a cached static delegate, <see cref="StepNode.Target"/>
    /// is an <c>int?</c> read off a node that already exists, and the loop holds no
    /// branch stack.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TakingEitherBranchOfAConditionalAllocatesNothing(bool predicate)
    {
        RequireOptimisedBuild();

        var engine = new FlowEngine(new FakeClock(T0));
        var dispatcher = new NullDispatcher { PredicateAnswer = predicate };

        var allocated = MeasureSteadyState(engine, Plans.Conditional(), dispatcher);

        allocated.ShouldBe(0,
            $"Measured {allocated} B on the {(predicate ? "true" : "false")} path. A branch is an " +
            "index assignment inside the existing step loop; if it costs anything, " +
            "something started boxing, closing over, or enumerating.");
    }

    /// <summary>
    /// Budget B2 has to survive a value branch too — including the arm nobody declared.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every arm, not a representative one. They cost differently in principle: arms 0..2
    /// are an array read out of <see cref="StepNode.CaseTargets"/>, and the miss is the
    /// <see cref="StepNode.Target"/> fallback, which is a different line of the engine.
    /// A theory covering only one of them would pass against an engine that boxed the
    /// answer on the other.
    /// </para>
    /// <para>
    /// <c>-1</c> is the documented "no case matched", and <c>7</c> is out of range — a
    /// dispatcher and a plan from different builds. Both must take the default target
    /// rather than throw, and neither may allocate on the way.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(-1)]
    [InlineData(7)]
    public void TakingAnyCaseOfASwitchAllocatesNothing(int arm)
    {
        RequireOptimisedBuild();

        var engine = new FlowEngine(new FakeClock(T0));
        var dispatcher = new NullDispatcher { CaseAnswer = arm };

        var allocated = MeasureSteadyState(engine, Plans.Switching(), dispatcher);

        allocated.ShouldBe(0,
            $"Measured {allocated} B selecting arm {arm}. Taking a case is one comparison, " +
            "one read out of an ImmutableArray<int> that already exists, and one assignment " +
            "to the loop index. If it costs anything, something started boxing the " +
            "selector's value, looking a case up in a dictionary, or closing over an arm.");
    }

    /// <summary>
    /// A step whose input comes from <c>.Step&lt;TCapability, TStepIn&gt;(map)</c> costs
    /// the same as one that binds from the state bag: nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The shape under test is the emitted one, reproduced by hand.</strong> The
    /// generator writes the mapping into a
    /// <c>static readonly Func&lt;FlowContext&lt;TIn&gt;, TStepIn&gt;</c> field and calls it
    /// as <c>StepInputs.StepN(Typed(ctx))</c>, and <see cref="MappingDispatcher"/> is that,
    /// line for line. The generator's own output is asserted as text in
    /// <c>StepInputMappingTests</c>; what cannot be asserted there is what the shape costs
    /// when it runs, and budget B2 is a hard zero for the linear path a mapped step sits on.
    /// </para>
    /// <para>
    /// Two things have to be free for this to hold, and both are structural. The delegate is
    /// a field built once at type initialisation, so invoking it allocates nothing; and
    /// <c>FlowContext&lt;TIn&gt;</c> is a <c>readonly struct</c> over one reference, so
    /// producing the typed view the mapping is written against allocates nothing either. A
    /// lambda built at the call site would have cost a delegate per step per execution, and
    /// a <c>FlowContext&lt;TIn&gt;</c> that was still a class could not have existed at all.
    /// </para>
    /// <para>
    /// <strong>Measured: 0 B, Release, .NET 10, x64</strong>, over the same four-step saga
    /// the first assertion in this class uses. What the mapping's <em>body</em> allocates is
    /// the author's own — <c>ctx =&gt; new CaptureRequest(…)</c> allocates a
    /// <c>CaptureRequest</c>, exactly as the capability it feeds would have needed one
    /// built somewhere — so the mapping here returns a pre-built value and what is measured
    /// is the plumbing.
    /// </para>
    /// </remarks>
    [Fact]
    public void AMappedStepInputAllocatesNothing()
    {
        RequireOptimisedBuild();

        var engine = new FlowEngine(new FakeClock(T0));

        var allocated = MeasureSteadyState(engine, Plans.FourStepSaga(), new MappingDispatcher());

        allocated.ShouldBe(0,
            $"Measured {allocated} B. An explicit input mapping is a cached static delegate " +
            "invoked through a readonly-struct view of the context, so it costs a call and " +
            "a register. If it costs bytes, either the delegate stopped being a field or " +
            "FlowContext<TIn> stopped being a struct.");
    }

    /// <summary>
    /// A fork allocates, and this records how much rather than asserting a zero that
    /// cannot be honoured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Budget B2 is unchanged and still a hard zero</strong> — for the linear,
    /// conditional and switch paths, which is what it has always covered and what the four
    /// theories above assert. A parallel flow is doing something none of those do: it runs
    /// several branches at once, which needs a linked <c>CancellationTokenSource</c>, a
    /// <c>Task</c> per branch, a list to drain them from, and the awaiters behind all of
    /// that. There is no arrangement of those that costs nothing, so pretending otherwise
    /// would mean either a false assertion or a fake concurrency.
    /// </para>
    /// <para>
    /// The ceiling is what the test is for. It is deliberately close to the measured
    /// figure, so that a change which quietly starts allocating <em>per step</em> inside a
    /// branch — rather than per fork — trips it. The shape being defended is that the cost
    /// is proportional to the number of branches and not to the work they do.
    /// </para>
    /// <para>
    /// <strong>The measured figures, Release, .NET 10, x64.</strong> A three-branch
    /// <c>AllMustSucceed</c> fork over a six-step plan, every step completing
    /// synchronously: <strong>792 B</strong>. The same measurement with two branches over a
    /// six-step plan: <strong>552 B</strong>. So a fork costs roughly <strong>240 B per
    /// branch</strong> — one <c>Task</c> from <c>ValueTask.AsTask()</c>, its list slot and
    /// its share of the awaiter — on top of about <strong>70 B</strong> fixed for the
    /// linked token source and the drain list. Nothing scales with the number of steps a
    /// branch runs, which is the property the second test pins.
    /// </para>
    /// <para>
    /// The ceiling is 2048 B, the same headroom the failure path is given. The figures are
    /// written down here rather than only implied by the assertion so that a future reader
    /// can tell whether a change moved them and by how much.
    /// </para>
    /// </remarks>
    [Fact]
    public void AParallelFlowAllocatesAndTheAmountIsRecorded()
    {
        RequireOptimisedBuild();

        var engine = new FlowEngine(new FakeClock(T0));
        var plan = Plans.Parallel(MergeStrategy.AllMustSucceed);

        var allocated = MeasureSteadyState(engine, plan, new NullDispatcher());

        allocated.ShouldBeGreaterThan(0,
            "A zero here would mean the branches did not actually fork — no linked token, " +
            "no tasks — which is a correctness problem wearing a budget's clothes.");

        allocated.ShouldBeLessThan(2048,
            $"Measured {allocated} B for a three-branch fork. Concurrency is allowed to " +
            "allocate; it is allowed to allocate per fork, not per step. If this ceiling " +
            "is hit, something started allocating inside a branch's step loop.");
    }

    /// <summary>
    /// The steady-state cost of a fork must not grow with the work its branches do.
    /// </summary>
    /// <remarks>
    /// The measurement that actually protects the shape. An absolute ceiling would still
    /// pass if a branch allocated a few bytes per step; comparing a two-branch fork over
    /// five steps against a three-branch fork over six catches that, because the per-step
    /// component would show up as a difference the per-fork component cannot explain.
    /// </remarks>
    [Fact]
    public void TheCostOfAForkTracksItsBranchesRatherThanItsSteps()
    {
        RequireOptimisedBuild();

        var engine = new FlowEngine(new FakeClock(T0));

        var twoBranches = MeasureSteadyState(
            engine, Plans.ParallelWithMultiStepBranches(MergeStrategy.AllMustSucceed), new NullDispatcher());

        var threeBranches = MeasureSteadyState(
            engine, Plans.Parallel(MergeStrategy.AllMustSucceed), new NullDispatcher());

        // Two branches over four capability steps, against three branches over three. If
        // steps cost anything, the four-step plan would be the expensive one.
        threeBranches.ShouldBeGreaterThan(twoBranches,
            $"A three-branch fork ({threeBranches} B) did not cost more than a two-branch " +
            $"one ({twoBranches} B). Either branches are not being started per branch, or " +
            "the per-step cost is drowning the per-branch one.");
    }

    /// <summary>
    /// A loop allocates, and this records how much rather than asserting a zero that
    /// cannot be honoured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Budget B2 is unchanged and still a hard zero</strong> — for the linear,
    /// conditional and switch paths, which is what it has always covered and what the
    /// theories above assert. A <c>ForEach</c> is doing something none of those do: it runs
    /// its body once per element, and each pass needs the element to be visible to the
    /// steps in it under its own type. There is no arrangement of that which costs nothing,
    /// because the shared state bag is keyed by type and would give every pass the same
    /// slot — which is a race above a concurrency of one and a leftover at one.
    /// </para>
    /// <para>
    /// <strong>The measured figures, Release, .NET 10, x64.</strong> A sequential loop over
    /// three elements with a one-step body: <strong>96 B</strong>, which is
    /// <strong>32 B per element</strong> — one <c>IterationScope&lt;T&gt;</c>, and nothing
    /// else: an object header and the two references it holds, the enclosing context and
    /// the element. The same loop over six elements: <strong>192 B</strong>. Nothing at all
    /// is charged per step of the body, which is the property the companion test pins.
    /// </para>
    /// <para>
    /// The ceiling is deliberately close to the figure, so that a change which starts
    /// allocating per <em>step</em> rather than per element trips it. Written down here
    /// rather than only implied by the assertion so a future reader can tell whether a
    /// change moved it and by how much.
    /// </para>
    /// </remarks>
    [Fact]
    public void AForEachAllocatesAndTheAmountIsRecorded()
    {
        RequireOptimisedBuild();

        var engine = new FlowEngine(new FakeClock(T0));

        var allocated = MeasureSteadyState(
            engine, Plans.ForEachWithOneStepBody(), new NullDispatcher { Elements = ThreeElements });

        allocated.ShouldBeGreaterThan(0,
            "A zero here would mean the elements did not get their own scope — which is " +
            "a correctness problem wearing a budget's clothes, because every pass would " +
            "then read the same slot.");

        allocated.ShouldBeLessThan(256,
            $"Measured {allocated} B for a three-element loop with a one-step body. A loop " +
            "is allowed to allocate per element; it is not allowed to allocate per step. " +
            "If this ceiling is hit, something started allocating inside the body's step loop.");
    }

    /// <summary>
    /// The steady-state cost of a loop must track the elements, not the body.
    /// </summary>
    /// <remarks>
    /// The measurement that actually protects the shape. An absolute ceiling would still
    /// pass if each body step allocated a few bytes; comparing three elements over a
    /// one-step body against three elements over a two-step body catches that, because a
    /// per-step component would show up as a difference the per-element component cannot
    /// explain. The second comparison is the other axis: twice the elements really does
    /// cost about twice as much, because a scope per element is what a loop buys.
    /// </remarks>
    [Fact]
    public void TheCostOfALoopTracksItsElementsRatherThanItsSteps()
    {
        RequireOptimisedBuild();

        var engine = new FlowEngine(new FakeClock(T0));

        var oneStepBody = MeasureSteadyState(
            engine, Plans.ForEachWithOneStepBody(), new NullDispatcher { Elements = ThreeElements });

        var twoStepBody = MeasureSteadyState(
            engine, Plans.ForEach(), new NullDispatcher { Elements = ThreeElements });

        var twiceTheElements = MeasureSteadyState(
            engine, Plans.ForEachWithOneStepBody(), new NullDispatcher { Elements = SixElements });

        twoStepBody.ShouldBe(oneStepBody,
            $"Doubling the work per element changed the cost from {oneStepBody} B to " +
            $"{twoStepBody} B. A loop pays for elements, not for the steps inside them.");

        twiceTheElements.ShouldBe(oneStepBody * 2,
            $"Six elements cost {twiceTheElements} B against {oneStepBody} B for three. " +
            "The cost is one scope per element; anything else means a fixed cost crept in " +
            "or a per-element one grew.");
    }

    /// <summary>
    /// A composition allocates, and this records how much rather than asserting a zero it
    /// cannot promise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Budget B2 is unchanged and still a hard zero</strong> for the linear,
    /// conditional and switch paths, which is what it has always covered and what the
    /// theories above assert. A sub-flow is doing something none of those do: it runs a
    /// second, independent flow, with its own context, its own compensation stack and its
    /// own deadline.
    /// </para>
    /// <para>
    /// <strong>The measured figure, Release, .NET 10, x64: 0 B</strong> for a parent whose
    /// child completes synchronously and leaves compensations pending. That is worth
    /// writing down rather than quietly enjoying, because it is not "sub-flows are free" —
    /// it is that every part of a composition was made pooled or a struct on purpose. The
    /// child's context comes from the same <c>ContextPool</c> the parent's does; its
    /// compensation stack is owned by that context and reset rather than rebuilt;
    /// <c>SubFlowSource</c> and <c>FlowInvocation</c> are structs; the engine awaits the
    /// child's range directly rather than through <c>Task</c>, so no state machine is boxed
    /// when the child's steps complete synchronously; and the children still rented at the
    /// end are found through a list the context owns rather than by walking the compensation
    /// stack, which the first version did and which cost <strong>96 B</strong> — one
    /// iterator per nesting level, on the success path.
    /// </para>
    /// <para>
    /// <strong>What a real composition does allocate is the author's own input.</strong>
    /// <c>ctx =&gt; new FulfilOrder(ctx.Get&lt;OrderId&gt;())</c> is a record construction,
    /// and it is charged to the flow that wrote it — the dispatcher here returns a
    /// pre-built value so the measurement is the engine's and not the test's litter, which
    /// is the same convention the <c>ForEach</c> measurement uses for its elements.
    /// </para>
    /// <para>
    /// The ceiling is 512 B, and the assertion is deliberately <em>not</em> "greater than
    /// zero": unlike a fork, where a zero would mean the branches never actually forked,
    /// there is no correctness claim hiding inside a composition costing nothing. What the
    /// ceiling defends is that the cost stays per-composition — if it is ever hit, something
    /// started allocating per step inside the child, or the child's context stopped coming
    /// from the pool.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASubFlowAllocatesAndTheAmountIsRecorded()
    {
        RequireOptimisedBuild();

        var engine = new FlowEngine(new FakeClock(T0));

        var allocated = MeasureSteadyState(
            engine, Plans.Composing(), new ComposingDispatcher(new NullDispatcher()));

        allocated.ShouldBeLessThan(512,
            $"Measured {allocated} B for a flow composing a two-step child that leaves a " +
            "compensation pending. A composition is allowed to cost something per child; " +
            "it is not allowed to cost anything per step of that child, and it must not " +
            "stop renting the child's context from the pool.");
    }

    /// <summary>
    /// The steady-state cost of a composition must not grow with the child's work.
    /// </summary>
    /// <remarks>
    /// The measurement that actually protects the shape, in the same spirit as the fork's
    /// and the loop's. An absolute ceiling would still pass if each of the child's steps
    /// allocated a few bytes; comparing a two-step child against a four-step one catches it,
    /// because a per-step component would show up as a difference the per-composition
    /// component cannot explain.
    /// </remarks>
    [Fact]
    public void TheCostOfACompositionTracksTheChildRatherThanItsSteps()
    {
        RequireOptimisedBuild();

        var engine = new FlowEngine(new FakeClock(T0));

        var twoStepChild = MeasureSteadyState(
            engine, Plans.Composing(), new ComposingDispatcher(new NullDispatcher()));

        var fourStepChild = MeasureSteadyState(
            engine,
            Plans.Composing(),
            new ComposingDispatcher(new NullDispatcher(), Plans.FourStepSaga()));

        fourStepChild.ShouldBe(twoStepChild,
            $"Doubling the child's work changed the cost from {twoStepChild} B to " +
            $"{fourStepChild} B. A composition pays for the child, not for the steps " +
            "inside it.");
    }

    /// <summary>
    /// A dispatcher that composes a child at step 1 and allocates nothing itself.
    /// </summary>
    /// <remarks>
    /// The input is a pre-built object rather than one created per call, for the reason the
    /// loop's elements are pre-boxed: what is being measured is the engine's per-composition
    /// cost, not the mapping the author wrote.
    /// </remarks>
    private sealed class ComposingDispatcher(IStepDispatcher child, ExecutionPlan? childPlan = null)
        : IStepDispatcher
    {
        private static readonly object Input = new();

        private readonly ExecutionPlan _plan = childPlan ?? Plans.Child();

        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx) => true;

        public int Select(int stepIndex, FlowContext ctx) => -1;

        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to begin.");

        public FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to enter.");

        public SubFlowSource BeginSubFlow(int stepIndex, FlowContext ctx) =>
            new(_plan, child, Input);

        public void EnterSubFlow(int stepIndex, in SubFlowSource source, FlowContext child) =>
            child.Set(source.Input!);
    }

    /// <summary>
    /// A durable flow pays for its journal, and this records how much of that is the
    /// engine's rather than the store's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Budget B2 is unchanged and still a hard zero</strong> for the linear,
    /// conditional and switch paths of an <em>ephemeral</em> flow, which is what it has
    /// always covered and what the assertions above pin. B2 was never a budget on durability:
    /// a journaled step boundary is roughly 1–15 ms against ~1 µs in memory, so a handful of
    /// bytes is not the interesting cost of one. What matters is that the ephemeral path pays
    /// none of it — a durable seam that charged every flow would be a second engine wearing
    /// one engine's name.
    /// </para>
    /// <para>
    /// <strong>The store is deliberately not in the measurement.</strong>
    /// <see cref="NullJournal"/> accepts every commit and allocates nothing, so what is left
    /// is what the <em>engine</em> spends to describe a step boundary: the
    /// <c>StepCommit</c> record, the non-determinism envelope, the scope path where there is
    /// one, and the awaiters behind an interface call that could have gone asynchronous. A
    /// real store's row, its transaction and its round trip are its own to measure, and
    /// <c>JournalBenchmarks</c> is where B7 does it.
    /// </para>
    /// <para>
    /// <strong>The measured figures, Release, .NET 10, x64.</strong> The four-step saga
    /// declared <c>Durable</c>: <strong>768 B</strong>, which is <strong>192 B per
    /// step</strong> — a <c>StepCommit</c>, a <c>NondeterminismCapture</c>, and the awaiters
    /// behind an interface call that could have suspended. The identical plan declared
    /// <c>Ephemeral</c>: <strong>0 B</strong>, which the assertion below it pins. The ceiling
    /// is per step rather than per flow so that it keeps its meaning when the plan changes,
    /// and it is close enough to the figure that a new per-step allocation shows up rather
    /// than hiding in headroom.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADurableFlowPaysForItsJournalAndTheAmountIsRecorded()
    {
        RequireOptimisedBuild();

        var engine = new FlowEngine(new FakeClock(T0));
        var plan = Durable(Plans.FourStepSaga());

        var allocated = MeasureDurableSteadyState(engine, plan, new NullDispatcher());

        allocated.ShouldBeGreaterThan(0,
            "A zero here would mean nothing was journaled, which is a correctness problem " +
            "wearing a budget's clothes.");

        (allocated / plan.Graph.Count).ShouldBeLessThan(384,
            $"Measured {allocated} B over {plan.Graph.Count} steps. A journaled boundary is " +
            "allowed to allocate; it is allowed to allocate per boundary, not per anything " +
            "else. If this ceiling is hit, something started building a payload, a list or a " +
            "closure the seam did not need.");
    }

    /// <summary>
    /// The ephemeral path pays nothing for the existence of the durable one.
    /// </summary>
    /// <remarks>
    /// The regression this forbids is the one ADR-0015 names as its accepted cost and its
    /// biggest risk: "the ephemeral hot path grows a branch it does not need". It is allowed
    /// to grow the branch. It is not allowed to grow an allocation — the same bargain
    /// <c>ExecutionPlan.HasParallel</c> struck, where a linear flow pays one predictable,
    /// always-false comparison for a fork lock it never takes.
    /// </remarks>
    [Fact]
    public void TheEphemeralPathPaysNothingForTheExistenceOfTheDurableOne()
    {
        RequireOptimisedBuild();

        var engine = new FlowEngine(new FakeClock(T0));

        var allocated = MeasureSteadyState(engine, Plans.FourStepSaga(), new JournallingDispatcher());

        allocated.ShouldBe(0,
            $"Measured {allocated} B for an ephemeral flow whose dispatcher can describe a " +
            "step for a journal. The seam is gated on the flow's declared profile, so this " +
            "must be exactly what it was before the journal existed.");
    }

    /// <summary>The same plan, re-declared <c>Durable</c>.</summary>
    private static ExecutionPlan Durable(ExecutionPlan plan) => ExecutionPlan.Create(
        FlowDescriptor.Create(
            plan.Flow.Id, plan.Flow.Version, ExecutionProfile.Durable, plan.Flow.Deadline),
        plan.Graph);

    /// <summary>
    /// <see cref="MeasureSteadyState"/> for a journaled flow: a fresh instance per run,
    /// because an append-only journal refuses a key it has already seen.
    /// </summary>
    /// <remarks>
    /// The instance id and the <c>DurableExecution</c> are built outside the measured region,
    /// so what is counted is the execution and not the session that carries it. Opening an
    /// instance is once per flow and a store's cost anyway.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureDurableSteadyState(
        FlowEngine engine, ExecutionPlan plan, IStepDispatcher dispatcher)
    {
        var journal = new NullJournal();

        for (var i = 0; i < 64; i++)
        {
            RunSync(engine.ExecuteAsync(plan, dispatcher, Plans.Invocation, journal.Open()));
        }

        var run = journal.Open();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        RunSync(engine.ExecuteAsync(plan, dispatcher, Plans.Invocation, run));
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>
    /// A journal that accepts everything and allocates nothing, so the measurement is the
    /// engine's.
    /// </summary>
    /// <remarks>
    /// Every answer is a pre-built value returned through an already-completed
    /// <see cref="ValueTask{TResult}"/>, which is the same convention the dispatcher doubles
    /// in this file follow: what a real store spends on a row and a transaction is its own,
    /// and mixing the two would produce a number that means nothing about either.
    /// </remarks>
    private sealed class NullJournal : IFlowJournal
    {
        private static readonly FencingToken Token = new(1);

        private static readonly JournalStep Committed = new()
        {
            Key = StepKey.First(Guid.Empty, 0),
            Sequence = 1,
            CapabilityId = "measured",
            CapabilityVersion = "1.0.0",
            Outcome = JournalOutcome.Success,
        };

        private readonly ValueTask<Result<JournalStep>> _commit = new(Result.Ok(Committed));

        private FlowInstanceRecord _instance = new()
        {
            InstanceId = Guid.Empty,
            FlowId = "measured",
            FlowVersion = "1.0.0",
            State = FlowInstanceState.Running,
            Fence = Token,
        };

        /// <summary>A session over a fresh instance, so no key is ever committed twice.</summary>
        public DurableExecution Open()
        {
            var instanceId = Guid.NewGuid();

            _instance = _instance with { InstanceId = instanceId };

            // Through a Task, not off the ValueTask: reading a ValueTask that has not
            // finished is undefined rather than merely slow, and this runs outside every
            // measured region so the conversion costs the measurement nothing.
            var begun = DurableExecution
                .BeginAsync(this, Plans.FourStepSaga(), Plans.Invocation, instanceId, Token)
                .AsTask();

            return begun.GetAwaiter().GetResult().Value;
        }

        public ValueTask<Result<FlowInstanceRecord>> StartAsync(
            FlowInstanceStart start, CancellationToken cancellationToken) =>
            new(Result.Ok(_instance));

        public ValueTask<Result<FencingToken>> FenceAsync(
            Guid instanceId, FencingToken token, CancellationToken cancellationToken) =>
            new(Result.Ok(token));

        public ValueTask<Result<JournalStep>> CommitAsync(
            StepCommit commit, CancellationToken cancellationToken) => _commit;

        public ValueTask<Result<FlowInstanceRecord>> CompleteAsync(
            Guid instanceId,
            FencingToken token,
            FlowInstanceState state,
            JournalPayload stateBag,
            CancellationToken cancellationToken) =>
            new(Result.Ok(_instance));

        public ValueTask<Result<FlowInstanceRecord>> ReadInstanceAsync(
            Guid instanceId, CancellationToken cancellationToken) =>
            new(Result.Ok(_instance));

        public ValueTask<Result<ResumeFrontier>> ReadResumeFrontierAsync(
            Guid instanceId, CancellationToken cancellationToken) =>
            new(Result.Ok(new ResumeFrontier { Instance = _instance, Committed = [] }));

        public ValueTask<Result<IReadOnlyList<OutboxRecord>>> ReadOutboxAsync(
            Guid instanceId, CancellationToken cancellationToken) =>
            new(Result.Ok<IReadOnlyList<OutboxRecord>>([]));
    }

    /// <summary>
    /// A dispatcher that can describe a step for a journal, and allocates nothing doing it.
    /// </summary>
    /// <remarks>
    /// Present so the ephemeral measurement is taken against a dispatcher that <em>could</em>
    /// have been asked. A double with no <c>DescribeStep</c> at all would prove only that an
    /// absent member costs nothing.
    /// </remarks>
    private sealed class JournallingDispatcher : IStepDispatcher
    {
        private static readonly StepJournalEntry Entry = StepJournalEntry.Nothing;

        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx) => true;

        public int Select(int stepIndex, FlowContext ctx) => -1;

        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to begin.");

        public FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to enter.");

        public StepJournalEntry DescribeStep(int stepIndex, FlowContext ctx) => Entry;
    }

    /// <summary>
    /// The guarded state bag must not cost a flow that never forks anything at all.
    /// </summary>
    /// <remarks>
    /// The specific regression this forbids is the obvious fix for the concurrency problem:
    /// swapping the context's <c>Dictionary</c> for a <c>ConcurrentDictionary</c>. That
    /// allocates a node per entry written, where a pooled <c>Dictionary</c> reuses the
    /// buckets it already has — so it would have cost every linear flow an allocation per
    /// step to solve a problem only parallel flows have.
    /// </remarks>
    [Fact]
    public void AFlowThatDoesNotForkPaysNothingForTheOnesThatDo()
    {
        RequireOptimisedBuild();

        var engine = new FlowEngine(new FakeClock(T0));

        var allocated = MeasureSteadyState(engine, Plans.FourStepSaga(), new WritingDispatcher());

        allocated.ShouldBe(0,
            $"Measured {allocated} B writing four values into the state bag of a flow with " +
            "no fork in it. The guard is one always-false branch on a field the plan " +
            "precomputed; it must not be a concurrent collection.");
    }

    /// <summary>A dispatcher that writes to the context, so the state bag is on the measured path.</summary>
    /// <remarks>
    /// The values are boxed <c>int</c>s taken from a pre-built array rather than created
    /// per call, so what is measured is the dictionary's write path and not the test's own
    /// litter.
    /// </remarks>
    private sealed class WritingDispatcher : IStepDispatcher
    {
        private static readonly object[] Values = [new object(), new object(), new object(), new object()];

        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
        {
            ctx.Set(Values[stepIndex % Values.Length]);
            return ValueTask.FromResult(StepOutcome.Success);
        }

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx) => true;

        /// <inheritdoc />
        /// <remarks>This double declares no iteration, so the engine never asks it for one.</remarks>
        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to begin.");

        /// <inheritdoc />
        public FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to enter.");

        public int Select(int stepIndex, FlowContext ctx) => -1;
    }

    /// <summary>A dispatcher that allocates nothing itself, so the measurement is the engine's.</summary>
    private sealed class NullDispatcher : IStepDispatcher
    {
        public int? FailAtStep { get; init; }

        public Error? Failure { get; init; }

        /// <summary>What every branch answers. Fixed, so the predicate itself allocates nothing.</summary>
        public bool PredicateAnswer { get; init; } = true;

        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => stepIndex == FailAtStep && Failure is not null
                ? ValueTask.FromResult(StepOutcome.Failed(Failure))
                : ValueTask.FromResult(StepOutcome.Success);

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        /// <summary>What every switch answers. Fixed, so the selector itself allocates nothing.</summary>
        public int CaseAnswer { get; init; } = -1;

        public bool Evaluate(int stepIndex, FlowContext ctx) => PredicateAnswer;

        public int Select(int stepIndex, FlowContext ctx) => CaseAnswer;

        /// <summary>
        /// What every iteration walks. A pre-built array of pre-boxed elements, so what the
        /// measurement sees is the engine's per-element cost and not the test's own litter.
        /// </summary>
        public object[] Elements { get; init; } = ThreeElements;

        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            new(Elements, Elements.Length);

        public FlowContext EnterIteration(
            int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            IterationScope.For(ctx, ((IReadOnlyList<object>)source.Items!)[iteration]);
    }

    /// <summary>
    /// A dispatcher shaped exactly like the one the generator emits for
    /// <c>.Step&lt;TCapability, TStepIn&gt;(map)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two things are copied deliberately from <c>FlowEmitter</c> and would make this
    /// measurement meaningless if they drifted: the mapping is a <c>static readonly</c>
    /// field rather than a lambda written at the call site, and the argument it is invoked
    /// with is <c>new FlowContext&lt;TIn&gt;(ctx)</c> — the emitted <c>Typed(ctx)</c>
    /// helper, inlined here because a private helper on a test double would be one
    /// indirection the JIT has to see through before the measurement means anything.
    /// </para>
    /// <para>
    /// The mapping returns a pre-built value. What an author's own lambda allocates is
    /// theirs; what this measures is whether reaching it costs anything.
    /// </para>
    /// </remarks>
    private sealed class MappingDispatcher : IStepDispatcher
    {
        private static readonly object Mapped = new();

        private static readonly Func<FlowContext<object>, object> Map = _ => Mapped;

        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
        {
            // The emitted shape: the mapping's result is an argument to the capability.
            // Handed to a method the JIT cannot see into, so the call survives to be
            // measured rather than being folded away as dead.
            Consume(Map(new FlowContext<object>(ctx)));

            return ValueTask.FromResult(StepOutcome.Success);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Consume(object input) => _ = input;

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx) => true;

        public int Select(int stepIndex, FlowContext ctx) => -1;

        /// <inheritdoc />
        /// <remarks>This double declares no iteration, so the engine never asks it for one.</remarks>
        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to begin.");

        /// <inheritdoc />
        public FlowContext EnterIteration(
            int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to enter.");
    }

    private static readonly object[] ThreeElements = [new object(), new object(), new object()];

    private static readonly object[] SixElements =
        [new object(), new object(), new object(), new object(), new object(), new object()];
}
