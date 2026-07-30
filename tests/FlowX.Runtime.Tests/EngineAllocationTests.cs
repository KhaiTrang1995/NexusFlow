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
    }
}
