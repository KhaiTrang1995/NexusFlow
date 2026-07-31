using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// What a <see cref="StepKind.Fail"/> means to the engine.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The decision these tests exist to pin down is the compensation one.</strong>
/// A <c>Fail</c> is a deliberate rejection rather than something going wrong, and it is
/// tempting to read that as a clean exit with nothing to undo. It is not. A saga's
/// guarantee is about what <em>happened</em>, not about who decided it: by the time an
/// arm rejects a request the flow may already have reserved stock, and the reservation is
/// just as real as it would be after a declined payment.
/// </para>
/// <para>
/// The point is sharper than an analogy. <c>08-Flow-Definition.md §3.2</c>'s workaround
/// while <c>Fail</c> was unimplemented was "spell an unsupported value out as a capability
/// that returns <c>Result.Fail(...)</c>". If <c>Fail</c> did not unwind, replacing that
/// workaround with the feature it stands in for would silently weaken every saga that took
/// the advice — which is the same failure mode <c>FLOWX1005</c>'s "extract the shared steps
/// into a sub-flow" would have had if a successful child never compensated.
/// </para>
/// <para>
/// So the engine needs no special case at all: the generated dispatcher hands back
/// <c>StepOutcome.Failed(error)</c>, and a rejection and a declined payment are literally
/// the same event to the step loop. That is what is asserted here.
/// </para>
/// </remarks>
public sealed class FailStepTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static readonly Error UnsupportedChannel = new(
        "order.unsupported_channel", "That channel is not served.", ErrorCategory.Validation);

    private static FlowEngine NewEngine() => new(new FakeClock(T0));

    /// <summary>
    /// <c>0 reserve(compensable) · 1 capture(compensable) · 2 fail</c>.
    /// </summary>
    private static ExecutionPlan RejectsAfterTwoEffects() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.price", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Plans.Reserve, Plans.Release),
            StepNode.ForCapability(1, Plans.Capture, Plans.Refund),
            StepNode.ForFail(2),
        ]));

    [Fact]
    public async Task AFailEndsTheFlowWithTheErrorTheAuthorDeclared()
    {
        var engine = NewEngine();

        // Exactly what the generated dispatcher does at a Fail index: hand back the static
        // Error from the flow's `Failures` class.
        var dispatcher = new RecordingDispatcher().FailAt(2, UnsupportedChannel);

        var result = await engine.ExecuteAsync(
            RejectsAfterTwoEffects(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(UnsupportedChannel,
            "The flow's error is the one the author wrote, not one the engine invented.");
        result.CompletedSteps.ShouldBe(2, "A Fail completes nothing — it is where the flow stops.");
    }

    [Fact]
    public async Task TheCompletedCompensableStepsUnwindInStrictReverse()
    {
        var engine = NewEngine();
        var dispatcher = new RecordingDispatcher().FailAt(2, UnsupportedChannel);

        var result = await engine.ExecuteAsync(
            RejectsAfterTwoEffects(), dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        dispatcher.Compensated.ShouldBe([1, 0],
            "Rejecting a request after stock has been reserved must release the stock. A " +
            "Fail that ended the flow 'cleanly' would leave it reserved forever, which is " +
            "the dangling state a saga exists to prevent.");

        result.Compensation.ShouldBe(CompensationOutcome.Succeeded);
    }

    [Fact]
    public async Task AFlowThatRejectsBeforeDoingAnythingHasNothingToUndo()
    {
        var engine = NewEngine();

        var plan = ExecutionPlan.Create(
            FlowDescriptor.Create("order.price", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([
                StepNode.ForBranch(0, falseTarget: 2),
                StepNode.ForFail(1),
                StepNode.ForCapability(2, Plans.Reserve, Plans.Release),
            ]));

        var dispatcher = new RecordingDispatcher()
            .AnswerAt(0, true)
            .FailAt(1, UnsupportedChannel);

        var result = await engine.ExecuteAsync(
            plan, dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.Error.ShouldBe(UnsupportedChannel);
        dispatcher.Executed.ShouldBe([1], "The rejecting arm ran and nothing else did.");
        dispatcher.Compensated.ShouldBeEmpty("Nothing completed, so there is nothing to undo.");
        result.Compensation.ShouldBe(CompensationOutcome.NotRequired);
    }

    [Fact]
    public async Task ControlNeverLeavesAFailEvenWhenStepsFollowItInTheArray()
    {
        // The generator drops steps after a Fail and reports FLOWX1027, so this layout is
        // not one an author can produce. The engine is asserted against it anyway: the
        // reason nothing after a Fail runs must be that control stops there, not that the
        // compiler happened to omit it.
        var engine = NewEngine();

        var plan = ExecutionPlan.Create(
            FlowDescriptor.Create("order.price", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([
                StepNode.ForFail(0),
                StepNode.ForCapability(1, Plans.Capture),
            ]));

        var dispatcher = new RecordingDispatcher().FailAt(0, UnsupportedChannel);

        var result = await engine.ExecuteAsync(
            plan, dispatcher, Plans.Invocation, TestContext.Current.CancellationToken);

        result.Error.ShouldBe(UnsupportedChannel);
        dispatcher.Executed.ShouldBe([0]);
    }

    [Fact]
    public void AFailNodeCarriesNoTargetAndNoBusinessValue()
    {
        var node = StepNode.ForFail(4);

        node.Kind.ShouldBe(StepKind.Fail);
        node.Target.ShouldBeNull(
            "Control does not continue, so there is no destination — and the forward-target " +
            "rule that proves the step loop terminates is untouched.");
        node.IsControlTransfer.ShouldBeFalse(
            "A Fail is not a jump: it does work, in the sense that it produces the flow's " +
            "outcome, and it is reached through the dispatcher like any other step.");
        node.Capability.ShouldBeNull();
        node.IsCompensable.ShouldBeFalse();
        node.ToString().ShouldBe("[4] fail");
    }
}
