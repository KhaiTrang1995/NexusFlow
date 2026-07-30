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
/// This is WP-4's exit criterion, and it is asserted rather than benchmarked because
/// allocation counts are deterministic while timings are not. A benchmark would tell
/// us nightly; this tells us on every pull request.
/// </remarks>
public sealed class EngineAllocationTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

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

    /// <summary>A dispatcher that allocates nothing itself, so the measurement is the engine's.</summary>
    private sealed class NullDispatcher : IStepDispatcher
    {
        public int? FailAtStep { get; init; }

        public Error? Failure { get; init; }

        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => stepIndex == FailAtStep && Failure is not null
                ? ValueTask.FromResult(StepOutcome.Failed(Failure))
                : ValueTask.FromResult(StepOutcome.Success);

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);
    }
}
