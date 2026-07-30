using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// <c>When</c> / <c>Otherwise</c> as the engine sees it: a <see cref="StepKind.Branch"/>
/// carrying a false target, and a <see cref="StepKind.Jump"/> closing the <c>then</c>
/// block.
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
}
