using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// The step loop. Every test here pins down behaviour that a saga cannot get wrong
/// without losing money or leaving two systems disagreeing.
/// </summary>
public sealed class FlowEngineTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static (FlowEngine Engine, FakeClock Clock) NewEngine()
    {
        var clock = new FakeClock(T0);
        return (new FlowEngine(clock), clock);
    }

    [Fact]
    public async Task RunsEveryStepInGraphOrder()
    {
        var (engine, _) = NewEngine();
        var dispatcher = new RecordingDispatcher();

        var result = await engine.ExecuteAsync(
            Plans.FourStepSaga(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.CompletedSteps.ShouldBe(4);
        dispatcher.Executed.ShouldBe([0, 1, 2, 3]);
        dispatcher.Compensated.ShouldBeEmpty();
        result.Compensation.ShouldBe(CompensationOutcome.NotRequired);
    }

    [Fact]
    public async Task StopsAtTheFirstFailingStep()
    {
        var (engine, _) = NewEngine();
        var outOfStock = new Error("inventory.out_of_stock", "SKU-1 unavailable", ErrorCategory.Conflict);
        var dispatcher = new RecordingDispatcher().FailAt(1, outOfStock);

        var result = await engine.ExecuteAsync(
            Plans.FourStepSaga(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(outOfStock);
        dispatcher.Executed.ShouldBe([0, 1], "Steps after the failure must not run.");
        result.CompletedSteps.ShouldBe(1, "Step 1 failed, so only step 0 completed.");
    }

    [Fact]
    public async Task CompensatesCompletedStepsInStrictReverseOrder()
    {
        var (engine, _) = NewEngine();
        var declined = new Error("payment.declined", "issuer declined", ErrorCategory.Conflict);

        // Steps 1 and 2 are compensable; step 2 fails, so only step 1 completed.
        var dispatcher = new RecordingDispatcher().FailAt(2, declined);

        var result = await engine.ExecuteAsync(
            Plans.FourStepSaga(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.Error.ShouldBe(declined);
        dispatcher.Compensated.ShouldBe([1],
            "Only steps that completed are compensated. Step 2 failed, so its own " +
            "compensation must not run — there is nothing to undo.");
        result.Compensation.ShouldBe(CompensationOutcome.Succeeded);
    }

    [Fact]
    public async Task CompensationUnwindsNewestFirstAcrossSeveralSteps()
    {
        var (engine, _) = NewEngine();
        var plan = ExecutionPlan.Create(
            FlowDescriptor.Create("saga.long", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromMinutes(1)),
            StepGraph.Create([
                StepNode.ForCapability(0, Plans.Reserve, Plans.Release),
                StepNode.ForCapability(1, Plans.Capture, Plans.Refund),
                StepNode.ForCapability(2, Plans.Reserve, Plans.Release),
                StepNode.ForCapability(3, Plans.Validate),
            ]));

        var dispatcher = new RecordingDispatcher()
            .FailAt(3, new Error("order.invalid", "bad order", ErrorCategory.Validation));

        await engine.ExecuteAsync(plan, dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        dispatcher.Compensated.ShouldBe([2, 1, 0],
            "Newest first. Releasing the first reservation before refunding the payment " +
            "that followed it leaves the two systems disagreeing about the same order.");
    }

    [Fact]
    public async Task ReportsPartialFailureWhenACompensationItselfFails()
    {
        var (engine, _) = NewEngine();
        var dispatcher = new RecordingDispatcher()
            .FailAt(2, new Error("payment.declined", "declined", ErrorCategory.Conflict))
            .FailCompensationAt(1, new Error("inventory.release_failed", "broker down", ErrorCategory.Unavailable));

        var result = await engine.ExecuteAsync(
            Plans.FourStepSaga(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.Compensation.ShouldBe(CompensationOutcome.PartiallyFailed);
        result.Error!.Code.ShouldBe("payment.declined",
            "The original failure is what the caller needs. A compensation failure is " +
            "an operational problem, not a replacement for the business error.");
    }

    [Fact]
    public async Task KeepsCompensatingAfterOneCompensationFails()
    {
        var (engine, _) = NewEngine();
        var plan = ExecutionPlan.Create(
            FlowDescriptor.Create("saga.long", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromMinutes(1)),
            StepGraph.Create([
                StepNode.ForCapability(0, Plans.Reserve, Plans.Release),
                StepNode.ForCapability(1, Plans.Capture, Plans.Refund),
                StepNode.ForCapability(2, Plans.Validate),
            ]));

        var dispatcher = new RecordingDispatcher()
            .FailAt(2, new Error("order.invalid", "bad", ErrorCategory.Validation))
            .FailCompensationAt(1, new Error("payment.refund_failed", "gateway down", ErrorCategory.Unavailable));

        await engine.ExecuteAsync(plan, dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        dispatcher.Compensated.ShouldBe([1, 0],
            "A failed refund must not abandon the inventory reservation. Best-effort " +
            "unwind continues, and the partial failure is reported.");
    }

    [Fact]
    public async Task DoesNotCompensateAFlowWithNoCompensableSteps()
    {
        var (engine, _) = NewEngine();
        var dispatcher = new RecordingDispatcher()
            .FailAt(1, new Error("order.not_found", "missing", ErrorCategory.NotFound));

        var result = await engine.ExecuteAsync(
            Plans.TwoStepQuery(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.Compensation.ShouldBe(CompensationOutcome.NotRequired);
        dispatcher.Compensated.ShouldBeEmpty();
    }

    [Fact]
    public async Task FailsWhenTheDeadlineHasPassedBeforeAStepStarts()
    {
        var (engine, clock) = NewEngine();

        // A 10-second budget, and step 1 takes 11 seconds of clock.
        var dispatcher = new RecordingDispatcher
        {
            BeforeStep = index =>
            {
                if (index == 1)
                {
                    clock.Advance(TimeSpan.FromSeconds(11));
                }
            },
        };

        var result = await engine.ExecuteAsync(
            Plans.FourStepSaga(TimeSpan.FromSeconds(10)),
            dispatcher,
            Plans.Invocation,
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("flow.deadline_exceeded");
        result.Error.Category.ShouldBe(ErrorCategory.Unavailable);
        dispatcher.Executed.ShouldBe([0, 1],
            "The deadline is checked at step boundaries. Step 1 had already started " +
            "when the clock moved, so it ran; step 2 never began.");
    }

    [Fact]
    public async Task CompensatesWhenTheDeadlineExpires()
    {
        var (engine, clock) = NewEngine();
        var dispatcher = new RecordingDispatcher
        {
            BeforeStep = index =>
            {
                if (index == 2)
                {
                    clock.Advance(TimeSpan.FromSeconds(11));
                }
            },
        };

        var result = await engine.ExecuteAsync(
            Plans.FourStepSaga(TimeSpan.FromSeconds(10)),
            dispatcher,
            Plans.Invocation,
            TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("flow.deadline_exceeded");
        dispatcher.Compensated.ShouldBe([2, 1],
            "A flow that ran out of budget still has to undo what it already did.");
    }

    [Fact]
    public async Task AnInvocationDeadlineOverridesTheFlowDefault()
    {
        var (engine, clock) = NewEngine();
        var dispatcher = new RecordingDispatcher
        {
            BeforeStep = _ => clock.Advance(TimeSpan.FromSeconds(2)),
        };

        // The flow declares 30 s; the trigger says the caller only has 3 s left.
        var invocation = Plans.Invocation with { Deadline = T0.AddSeconds(3) };

        var result = await engine.ExecuteAsync(
            Plans.FourStepSaga(), dispatcher, invocation, TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("flow.deadline_exceeded");
        dispatcher.Executed.Count.ShouldBeLessThan(4,
            "A caller's remaining budget must shorten the flow's, never lengthen it.");
    }

    [Fact]
    public async Task PropagatesCancellationAsAFailureAndStillCompensates()
    {
        var (engine, _) = NewEngine();
        using var cts = new CancellationTokenSource();

        var dispatcher = new RecordingDispatcher { CancelAtStep = 2 };
        dispatcher.BeforeStep = index =>
        {
            if (index == 2)
            {
                cts.Cancel();
            }
        };

        var result = await engine.ExecuteAsync(
            Plans.FourStepSaga(), dispatcher, Plans.Invocation, cts.Token);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("flow.cancelled");
        dispatcher.Compensated.ShouldBe([1],
            "Cancellation is not an excuse to leave a reservation dangling.");
    }

    [Fact]
    public async Task SurfacesAnUnhandledExceptionAsAnInternalError()
    {
        var (engine, _) = NewEngine();
        var dispatcher = new ThrowingDispatcher();

        var result = await engine.ExecuteAsync(
            Plans.FourStepSaga(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Category.ShouldBe(ErrorCategory.Internal);
        result.Error.Code.ShouldBe("capability.unhandled");
        // A capability that throws has a defect. The engine reports it as one rather
        // than letting it escape and kill the trigger's consumer loop.
    }

    [Fact]
    public async Task RejectsNullArguments()
    {
        var (engine, _) = NewEngine();
        var dispatcher = new RecordingDispatcher();
        var ct = TestContext.Current.CancellationToken;

        await Should.ThrowAsync<ArgumentNullException>(
            async () => await engine.ExecuteAsync(null!, dispatcher, Plans.Invocation, ct));

        await Should.ThrowAsync<ArgumentNullException>(
            async () => await engine.ExecuteAsync(Plans.TwoStepQuery(), null!, Plans.Invocation, ct));

        Should.Throw<ArgumentNullException>(() => new FlowEngine(null!));
    }

    private sealed class ThrowingDispatcher : IStepDispatcher
    {
        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => throw new InvalidOperationException("a capability blew up");

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This double runs plans with no branch step.");

        /// <inheritdoc />
        /// <remarks>This double declares no iteration, so the engine never asks it for one.</remarks>
        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to begin.");

        /// <inheritdoc />
        public FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to enter.");

        public int Select(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This double runs plans with no switch step.");
    }
}
