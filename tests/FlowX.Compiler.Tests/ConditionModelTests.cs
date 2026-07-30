using System.Linq;
using FlowX.Compiler.Model;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// The layout arithmetic of a conditional, in the model layer where it is decided.
/// </summary>
/// <remarks>
/// A conditional is nested in the model and flat in the plan, and these three numbers —
/// false target, jump index, join index — are the whole of the translation between the
/// two. Getting one of them wrong produces a graph that either skips a step or runs a
/// branch that should not have run, and both are the kind of defect that compiles,
/// passes on the path the author happened to test, and is wrong the other way round.
/// </remarks>
public sealed class ConditionModelTests
{
    private static StepModel Step(int index) =>
        StepModel.Capability(index, "Sample.C" + index, "sample.c" + index, "1.0.0", isIdempotent: true);

    [Fact]
    public void AConditionalWithBothBlocksLaysOutBranchThenJumpOtherwise()
    {
        // 1 branch · 2 then · 3 jump · 4 otherwise · 5 join
        var condition = StepModel.Condition(1, "ctx => true", then: [Step(2)], otherwise: [Step(4)]);

        condition.JumpIndex.ShouldBe(3, "The jump closes the `then` block.");
        condition.FalseTarget.ShouldBe(4, "The false path lands on the alternative, skipping the jump.");
        condition.JoinIndex.ShouldBe(5);
        condition.NextIndex.ShouldBe(5, "A conditional occupies everything up to its join.");
    }

    [Fact]
    public void AConditionalWithNoOtherwiseEmitsNoJump()
    {
        // Nothing to skip, so a jump would be a step whose only effect is to cost an
        // index — and an index the graph would then have to account for.
        var condition = StepModel.Condition(1, "ctx => true", then: [Step(2), Step(3)]);

        condition.JumpIndex.ShouldBeNull();
        condition.FalseTarget.ShouldBe(4, "Straight past the `then` block.");
        condition.JoinIndex.ShouldBe(4);
    }

    [Fact]
    public void AnOtherwiseThatDeclaredNothingIsTreatedAsAbsent()
    {
        var condition = StepModel.Condition(1, "ctx => true", then: [Step(2)], otherwise: []);

        condition.JumpIndex.ShouldBeNull();
        condition.FalseTarget.ShouldBe(3);
    }

    [Fact]
    public void ANestedConditionalIsAccountedForByItsWholeSpanNotItsOwnIndex()
    {
        // The inner conditional occupies 2..5; the outer `then` block therefore ends at 6,
        // not at 3. Using the inner step's own index here is the mistake that makes the
        // outer jump land inside the inner branch.
        var inner = StepModel.Condition(2, "ctx => false", then: [Step(3)], otherwise: [Step(5)]);
        var outer = StepModel.Condition(1, "ctx => true", then: [inner], otherwise: [Step(8)]);

        inner.JoinIndex.ShouldBe(6);
        outer.JumpIndex.ShouldBe(6);
        outer.FalseTarget.ShouldBe(7);
        outer.JoinIndex.ShouldBe(9);
    }

    [Fact]
    public void NestedStepsAreVisibleThroughAllSteps()
    {
        // Everything that treats `Steps` as the whole flow — descriptors, the dispatcher's
        // switch, the manifest's capability list — reads this instead. A capability invoked
        // only inside a branch would otherwise never be injected, and the flow would fail
        // at the first branch it took.
        var flow = Models.Conditional();

        flow.Steps.Count.ShouldBe(3, "Three top-level declarations.");
        flow.AllSteps.Select(s => s.Index).ShouldBe([0, 1, 2, 4, 5],
            "Flat-layout order, with 3 absent because the jump has no model of its own.");
    }

    [Fact]
    public void ACompensationInsideABranchStillCountsAsCompensation()
    {
        Models.Conditional().HasCompensation.ShouldBeTrue(
            "Only the reserve step is compensable, and it lives inside the `then` block.");
    }

    [Fact]
    public void CapabilitiesInsideBranchesAreReferenced()
    {
        Models.Conditional().ReferencedCapabilities.ShouldContain("Sample.Capabilities.CapturePayment");
    }
}
