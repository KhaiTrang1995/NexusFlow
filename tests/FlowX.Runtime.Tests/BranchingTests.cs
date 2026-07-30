using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// Branching as the engine sees it: a <see cref="StepKind.Branch"/> carrying a false
/// target or a <see cref="StepKind.Switch"/> carrying one target per case, and a
/// <see cref="StepKind.Jump"/> closing each block.
/// </summary>
/// <remarks>
/// Every test here asserts what ran <em>and</em> what did not. Asserting only that the
/// taken branch ran would pass against an engine that ran both, which is the mistake
/// this shape is most likely to make: the `then` block sits immediately after the
/// branch, so falling through it costs nothing and looks correct until the untaken
/// branch's side effects show up in production.
/// </remarks>
public sealed class BranchingTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static FlowEngine Engine() => new(new FakeClock(T0));

    [Fact]
    public async Task TheTruePathRunsTheThenBlockAndSkipsTheOtherwiseBlock()
    {
        var dispatcher = new RecordingDispatcher().AnswerAt(1, true);

        var result = await Engine().ExecuteAsync(
            Plans.Conditional(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        dispatcher.Executed.ShouldBe([0, 2, 3, 6],
            "The `then` block is steps 2 and 3; step 5 is the `Otherwise` and must not run.");
    }

    [Fact]
    public async Task TheFalsePathRunsTheOtherwiseBlockAndSkipsTheThenBlock()
    {
        var dispatcher = new RecordingDispatcher().AnswerAt(1, false);

        var result = await Engine().ExecuteAsync(
            Plans.Conditional(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        dispatcher.Executed.ShouldBe([0, 5, 6],
            "The branch jumps straight to the `Otherwise` block, over both `then` steps " +
            "and over the jump that closes them.");
    }

    [Fact]
    public async Task TheJumpEndsTheThenBlockRatherThanFallingIntoTheOtherwiseBlock()
    {
        // The specific defect: emitting the `then` block and the `Otherwise` block
        // adjacently and forgetting the jump. Every step still runs, in order, and a
        // test that only checked the `then` steps ran would not notice.
        var dispatcher = new RecordingDispatcher().AnswerAt(1, true);

        await Engine().ExecuteAsync(
            Plans.Conditional(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        dispatcher.Executed.ShouldNotContain(5);
    }

    [Fact]
    public async Task ABranchIsEvaluatedExactlyOnce()
    {
        var dispatcher = new RecordingDispatcher().AnswerAt(1, false);

        await Engine().ExecuteAsync(
            Plans.Conditional(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        dispatcher.Evaluated.ShouldBe([1]);
    }

    [Fact]
    public async Task OnlyStepsThatActuallyRanCountAsCompleted()
    {
        var dispatcher = new RecordingDispatcher().AnswerAt(1, false);

        var result = await Engine().ExecuteAsync(
            Plans.Conditional(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.CompletedSteps.ShouldBe(3,
            "Three capabilities ran. The branch and the jump did no work, and counting " +
            "them would make a two-step conditional look like a four-step flow in every " +
            "metric derived from this number.");
    }

    [Fact]
    public async Task AFalseBranchWithNoOtherwiseEndsTheFlow()
    {
        // The target is one past the last step, which is the layout of a `When` written
        // at the tail of a chain. StepGraph allows exactly that and nothing beyond it.
        var dispatcher = new RecordingDispatcher().AnswerAt(1, false);

        var result = await Engine().ExecuteAsync(
            Plans.ConditionalWithoutOtherwise(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        dispatcher.Executed.ShouldBe([0]);
    }

    [Fact]
    public async Task ATrueBranchWithNoOtherwiseRunsTheThenBlock()
    {
        var dispatcher = new RecordingDispatcher().AnswerAt(1, true);

        await Engine().ExecuteAsync(
            Plans.ConditionalWithoutOtherwise(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        dispatcher.Executed.ShouldBe([0, 2]);
    }

    [Fact]
    public async Task OnlyTheBranchThatRanIsCompensated()
    {
        // Step 2 is compensable and lives in the `then` block. Taking the `Otherwise`
        // path must leave it off the compensation stack — undoing work that never
        // happened is how a saga turns one incident into two.
        var dispatcher = new RecordingDispatcher()
            .AnswerAt(1, false)
            .FailAt(6, new Error("emit.failed", "no", ErrorCategory.Internal));

        var result = await Engine().ExecuteAsync(
            Plans.Conditional(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        dispatcher.Compensated.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheCompensationOfATakenBranchStillUnwinds()
    {
        var dispatcher = new RecordingDispatcher()
            .AnswerAt(1, true)
            .FailAt(6, new Error("emit.failed", "no", ErrorCategory.Internal));

        var result = await Engine().ExecuteAsync(
            Plans.Conditional(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.Compensation.ShouldBe(CompensationOutcome.Succeeded);
        dispatcher.Compensated.ShouldBe([2]);
    }

    [Fact]
    public async Task APredicateThatThrowsFailsTheFlowAndStillUnwinds()
    {
        // A predicate is pure by construction, so throwing is a defect — but letting it
        // escape would skip the compensation the steps already completed need, which is
        // strictly worse than reporting it.
        var dispatcher = new RecordingDispatcher { ThrowAtBranch = 1 };

        var result = await Engine().ExecuteAsync(
            Plans.Conditional(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("flow.predicate_failed");
        result.Error.Data!["stepIndex"].ShouldBe(1);
        dispatcher.Executed.ShouldBe([0]);
    }

    [Fact]
    public async Task AStepInsideABranchThatFailsStopsTheFlow()
    {
        var dispatcher = new RecordingDispatcher()
            .AnswerAt(1, true)
            .FailAt(2, new Error("inventory.out_of_stock", "none left", ErrorCategory.Conflict));

        var result = await Engine().ExecuteAsync(
            Plans.Conditional(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("inventory.out_of_stock");
        dispatcher.Executed.ShouldBe([0, 2], "Step 3 is in the same branch and must not run.");
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 4)]
    [InlineData(2, 6)]
    public async Task EachCaseOfASwitchRunsItsOwnBlockAndNoOther(int arm, int expected)
    {
        // Every arm, because the failure this shape invites is a case target that is off
        // by one — pointing at the previous block's jump instead of at the block. That
        // still runs *a* block, in a plausible order, and only one arm reveals it.
        var dispatcher = new RecordingDispatcher().SelectAt(1, arm);

        var result = await Engine().ExecuteAsync(
            Plans.Switching(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        dispatcher.Executed.ShouldBe([0, expected, 9],
            "Exactly one case block, then the join. A missing jump would run the arms " +
            "after it as well, in order, and look almost right.");
    }

    [Fact]
    public async Task AValueMatchingNoCaseRunsTheDefaultBlock()
    {
        var dispatcher = new RecordingDispatcher().SelectAt(1, -1);

        var result = await Engine().ExecuteAsync(
            Plans.Switching(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        dispatcher.Executed.ShouldBe([0, 8, 9]);
    }

    [Fact]
    public async Task AnArmOutsideThePlanTakesTheDefaultRatherThanThrowing()
    {
        // A dispatcher and a plan from different builds. Indexing the case array with it
        // would throw IndexOutOfRangeException from the middle of a flow, after some of
        // its steps had already run; the default is the one destination known to be safe.
        var dispatcher = new RecordingDispatcher().SelectAt(1, 99);

        var result = await Engine().ExecuteAsync(
            Plans.Switching(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        dispatcher.Executed.ShouldBe([0, 8, 9]);
    }

    [Fact]
    public async Task ASwitchIsSelectedExactlyOnce()
    {
        var dispatcher = new RecordingDispatcher().SelectAt(1, 1);

        await Engine().ExecuteAsync(
            Plans.Switching(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        dispatcher.Selected.ShouldBe([1]);
        dispatcher.Evaluated.ShouldBeEmpty("A switch is not a branch; Evaluate must not be consulted.");
    }

    [Fact]
    public async Task OnlyStepsThatActuallyRanCountAsCompletedAcrossASwitch()
    {
        var dispatcher = new RecordingDispatcher().SelectAt(1, 2);

        var result = await Engine().ExecuteAsync(
            Plans.Switching(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.CompletedSteps.ShouldBe(3,
            "Validate, the selected case, and the emit. The switch and the three jumps " +
            "did no work, and counting them would make a one-case flow look like a " +
            "seven-step one in every metric derived from this number.");
    }

    [Fact]
    public async Task OnlyTheCaseThatRanIsCompensated()
    {
        // Step 4 is compensable and lives in case 1. Selecting case 0 must leave it off
        // the compensation stack — undoing work that never happened is how a saga turns
        // one incident into two.
        var dispatcher = new RecordingDispatcher()
            .SelectAt(1, 0)
            .FailAt(9, new Error("emit.failed", "no", ErrorCategory.Internal));

        var result = await Engine().ExecuteAsync(
            Plans.Switching(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        dispatcher.Compensated.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheCompensationOfASelectedCaseStillUnwinds()
    {
        var dispatcher = new RecordingDispatcher()
            .SelectAt(1, 1)
            .FailAt(9, new Error("emit.failed", "no", ErrorCategory.Internal));

        var result = await Engine().ExecuteAsync(
            Plans.Switching(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.Compensation.ShouldBe(CompensationOutcome.Succeeded);
        dispatcher.Compensated.ShouldBe([4]);
    }

    [Fact]
    public async Task ASelectorThatThrowsFailsTheFlowAndStillUnwinds()
    {
        var dispatcher = new RecordingDispatcher { ThrowAtSwitch = 1 };

        var result = await Engine().ExecuteAsync(
            Plans.Switching(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("flow.selector_failed");
        result.Error.Data!["stepIndex"].ShouldBe(1);
        dispatcher.Executed.ShouldBe([0]);
    }

    [Theory]
    [InlineData(0, new[] { 0, 2 })]
    [InlineData(1, new[] { 0, 4 })]
    [InlineData(-1, new[] { 0 })]
    public async Task ASwitchWithNoDefaultFallsThroughWhenNothingMatches(int arm, int[] expected)
    {
        // The documented answer to "what happens when nothing matches and there is no
        // Default": control continues after the switch, exactly as a `When` with no
        // `Otherwise` does. Here the switch is the tail of the flow, so continuing after
        // it ends the flow — the target is one past the last step, which is the only
        // out-of-range value StepGraph deliberately permits.
        var dispatcher = new RecordingDispatcher().SelectAt(1, arm);

        var result = await Engine().ExecuteAsync(
            Plans.SwitchWithoutDefault(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        dispatcher.Executed.ShouldBe(expected);
    }
}
