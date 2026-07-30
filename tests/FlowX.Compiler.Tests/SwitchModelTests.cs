using System.Linq;
using FlowX.Compiler.Model;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// The layout arithmetic of a value branch, in the model layer where it is decided.
/// </summary>
/// <remarks>
/// A <c>Switch</c> is the general form of the conditional layout: instead of two blocks
/// and one jump there are <em>n</em> blocks and <em>n − 1</em> jumps, and every case
/// needs a target of its own. Each of those numbers is a chance to be off by one, and
/// being off by one here compiles, runs, and silently executes the wrong arm — the same
/// class of defect <see cref="ConditionModelTests"/> exists to prevent, with more places
/// to hide.
/// </remarks>
public sealed class SwitchModelTests
{
    private static StepModel Step(int index) =>
        StepModel.Capability(index, "Sample.C" + index, "sample.c" + index, "1.0.0", isIdempotent: true);

    private static SwitchCaseModel Case(string value, params StepModel[] steps) =>
        new(value, steps);

    private static StepModel Switch(
        int index,
        IReadOnlyList<SwitchCaseModel> cases,
        IReadOnlyList<StepModel>? @default = null) =>
        StepModel.Switch(index, "ctx => ctx.Get<O>().Channel", "Sample.Channel", cases, @default);

    [Fact]
    public void EachCaseTargetsTheFirstStepOfItsOwnBlock()
    {
        // 1 switch · 2 case0 · 3 jump · 4 case1 · 5 jump · 6 default · 7 join
        var node = Switch(
            1,
            [Case("Retail", Step(2)), Case("Wholesale", Step(4))],
            @default: [Step(6)]);

        node.Cases.Select(c => c.Target).ShouldBe([2, 4],
            "A case target that pointed at the preceding jump would run the wrong arm " +
            "and still produce a plausible-looking trace.");

        node.DefaultTarget.ShouldBe(6);
        node.JoinIndex.ShouldBe(7);
        node.NextIndex.ShouldBe(7, "A switch occupies everything up to its join.");
    }

    [Fact]
    public void EveryBlockButTheLastIsClosedByAJump()
    {
        var node = Switch(
            1,
            [Case("Retail", Step(2)), Case("Wholesale", Step(4))],
            @default: [Step(6)]);

        node.Cases.Select(c => c.JumpIndex).ShouldBe([3, 5]);
        // The default block is last, so nothing follows it to skip. A jump there would be
        // a step whose only effect is to cost an index the graph then has to account for.
    }

    [Fact]
    public void TheLastCaseNeedsNoJumpWhenThereIsNoDefault()
    {
        var node = Switch(1, [Case("Retail", Step(2)), Case("Wholesale", Step(4))]);

        node.Cases.Select(c => c.JumpIndex).ShouldBe([3, null]);
        node.JoinIndex.ShouldBe(5);
    }

    [Fact]
    public void WithNoDefaultTheMissTargetsTheJoin()
    {
        // The documented fall-through rule, expressed as arithmetic: a value matching no
        // case continues after the switch, exactly where a `When` with no `Otherwise`
        // sends its false path.
        var node = Switch(1, [Case("Retail", Step(2))]);

        node.DefaultTarget.ShouldBe(node.JoinIndex);
        node.DefaultTarget.ShouldBe(3);
        node.Default.ShouldBeEmpty();
    }

    [Fact]
    public void AnEmptyCaseBlockOccupiesNoIndicesAndTargetsTheJoin()
    {
        // `.Case(v, b => { })` is legal and means "match this, do nothing". Reserving a
        // slot for a jump it does not need would leave a gap, and StepGraph rejects a gap
        // at type initialisation.
        var node = Switch(1, [Case("Retail"), Case("Wholesale", Step(2))]);

        node.Cases[0].Target.ShouldBe(3, "The join.");
        node.Cases[0].JumpIndex.ShouldBeNull();
        node.Cases[1].Target.ShouldBe(2);
        node.JoinIndex.ShouldBe(3);
    }

    [Fact]
    public void AnEmptyBlockBetweenTwoFullOnesDoesNotConsumeAJump()
    {
        // 1 switch · 2 case0 · 3 jump · (case1 empty) · 4 case2 · 5 join
        var node = Switch(1, [Case("A", Step(2)), Case("B"), Case("C", Step(4))]);

        node.Cases.Select(c => c.Target).ShouldBe([2, 5, 4]);
        node.Cases.Select(c => c.JumpIndex).ShouldBe([3, null, null]);
        node.JoinIndex.ShouldBe(5);
    }

    [Fact]
    public void ASwitchWhereNothingDeclaredAnythingJoinsImmediatelyAfterItself()
    {
        var node = Switch(1, [Case("A"), Case("B")]);

        node.JoinIndex.ShouldBe(2);
        node.DefaultTarget.ShouldBe(2);
        node.Cases.Select(c => c.Target).ShouldBe([2, 2]);
    }

    [Fact]
    public void ANestedBranchIsAccountedForByItsWholeSpanNotItsOwnIndex()
    {
        // The inner conditional occupies 2..5; the case block therefore ends at 6, not at
        // 3. Using the inner step's own index here is the mistake that makes the closing
        // jump land inside the nested branch.
        var inner = StepModel.Condition(2, "ctx => false", then: [Step(3)], otherwise: [Step(5)]);
        var node = Switch(1, [Case("A", inner), Case("B", Step(7))]);

        inner.JoinIndex.ShouldBe(6);
        node.Cases[0].JumpIndex.ShouldBe(6);
        node.Cases[1].Target.ShouldBe(7);
        node.JoinIndex.ShouldBe(8);
    }

    [Fact]
    public void StepsInsideCasesAndTheDefaultAreVisibleThroughAllSteps()
    {
        // Everything that treats `Steps` as the whole flow — descriptors, the dispatcher's
        // switch, the manifest's capability list — reads this instead. A capability
        // invoked only inside a case would otherwise never be injected, and the flow would
        // fail the first time that case was selected.
        var flow = Models.Switching();

        flow.Steps.Count.ShouldBe(3, "Three top-level declarations.");
        flow.AllSteps.Select(s => s.Index).ShouldBe([0, 1, 2, 4, 6, 7],
            "Flat-layout order, with 3 and 5 absent because a jump has no model of its own.");
    }

    [Fact]
    public void ACompensationInsideACaseStillCountsAsCompensation()
        => Models.Switching().HasCompensation.ShouldBeTrue(
            "Only the reserve step is compensable, and it lives inside a case block.");

    [Fact]
    public void CapabilitiesInsideCasesAndTheDefaultAreReferenced()
    {
        var referenced = Models.Switching().ReferencedCapabilities;

        referenced.ShouldContain("Sample.Capabilities.CapturePayment", "declared in a case");
        referenced.ShouldContain("Sample.Capabilities.ValidateOrder", "declared in the default");
    }

    [Fact]
    public void TheSelectorAndItsTypeAreCarriedForTheEmitter()
    {
        var node = Models.Switching().Steps[1];

        node.Kind.ShouldBe(StepKindModel.Switch);
        node.Selector.ShouldBe("ctx => ctx.Get<ValidatedOrder>().Channel");
        node.SelectorTypeName.ShouldBe("Sample.Contracts.Channel",
            "Without the real type the emitted comparison would go through object and " +
            "box the value on every switch the flow takes.");
    }
}
