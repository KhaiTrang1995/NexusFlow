using Shouldly;
using Xunit;

namespace FlowX.Core.Tests;

/// <summary>
/// The step graph is the compiled shape of a flow body. Its invariants exist so the
/// engine's step loop can be a plain indexed walk with no bounds checking and no
/// null handling — the cost of a malformed graph is paid once, at construction.
/// </summary>
public sealed class StepGraphTests
{
    private static StepNode Step(int index, CapabilityDescriptor capability, CapabilityDescriptor? compensation = null)
        => StepNode.ForCapability(index, capability, compensation);

    [Fact]
    public void BuildsTheThreeStepPlanWithOneCompensation()
    {
        // This is the P0 target shape, and the exit criterion of WP-2.
        var graph = StepGraph.Create([
            Step(0, Fixtures.ValidateOrder),
            Step(1, Fixtures.ReserveInventory, Fixtures.ReleaseInventory),
            Step(2, Fixtures.CapturePayment),
        ]);

        graph.Count.ShouldBe(3);
        graph[1].IsCompensable.ShouldBeTrue();
        graph[0].IsCompensable.ShouldBeFalse();
        graph[2].Capability.ShouldBe(Fixtures.CapturePayment);
    }

    [Fact]
    public void RejectsAnEmptyGraph()
        => Should.Throw<InvalidFlowPlanException>(() => StepGraph.Create([]));

    [Fact]
    public void RejectsDuplicateIndices()
    {
        var error = Should.Throw<InvalidFlowPlanException>(() => StepGraph.Create([
            Step(0, Fixtures.ValidateOrder),
            Step(0, Fixtures.ReserveInventory),
        ]));

        error.Message.ShouldContain("0");
    }

    [Fact]
    public void RejectsNonContiguousIndices()
    {
        // A gap means the generator emitted a step it then dropped. Catching it here
        // turns a silent skipped-step bug into a build failure.
        Should.Throw<InvalidFlowPlanException>(() => StepGraph.Create([
            Step(0, Fixtures.ValidateOrder),
            Step(2, Fixtures.ReserveInventory),
        ]));
    }

    [Fact]
    public void RejectsIndicesNotStartingAtZero()
        => Should.Throw<InvalidFlowPlanException>(() => StepGraph.Create([Step(1, Fixtures.ValidateOrder)]));

    [Fact]
    public void AcceptsStepsSuppliedOutOfOrderAndNormalisesThem()
    {
        var graph = StepGraph.Create([
            Step(2, Fixtures.CapturePayment),
            Step(0, Fixtures.ValidateOrder),
            Step(1, Fixtures.ReserveInventory),
        ]);

        graph.Steps.Select(static s => s.Index).ShouldBe([0, 1, 2]);
        graph[0].Capability.ShouldBe(Fixtures.ValidateOrder);
    }

    [Fact]
    public void ACompensationMustDifferFromTheStepItCompensates()
    {
        var error = Should.Throw<InvalidFlowPlanException>(
            () => Step(0, Fixtures.ReserveInventory, Fixtures.ReserveInventory));

        error.Message.ShouldContain("inventory.reserve");
        // A step that compensates itself would run the same effect twice on the
        // failure path — the opposite of an undo.
    }

    [Fact]
    public void EmitStepsCarryAnEventTypeAndNoCapability()
    {
        var step = StepNode.ForEmit(0, "order.placed");

        step.Kind.ShouldBe(StepKind.Emit);
        step.EventType.ShouldBe("order.placed");
        step.Capability.ShouldBeNull();
        step.IsCompensable.ShouldBeFalse();
    }

    [Fact]
    public void ABranchCarriesOnlyItsFalseTarget()
    {
        // The true path needs no target: the `then` block is laid out immediately after
        // the branch, so taking it is the ordinary next index.
        var branch = StepNode.ForBranch(0, falseTarget: 3);

        branch.Kind.ShouldBe(StepKind.Branch);
        branch.Target.ShouldBe(3);
        branch.IsControlTransfer.ShouldBeTrue();
        branch.Capability.ShouldBeNull();
        branch.IsCompensable.ShouldBeFalse();
    }

    [Fact]
    public void AnOrdinaryStepHasNoTarget()
    {
        Step(0, Fixtures.ValidateOrder).Target.ShouldBeNull();
        Step(0, Fixtures.ValidateOrder).IsControlTransfer.ShouldBeFalse();
        StepNode.ForEmit(0, "order.placed").Target.ShouldBeNull();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    [InlineData(0)]
    public void ATargetMustPointForward(int target)
    {
        // 2 is backwards, 1 is a self-loop, 0 is further backwards. None of the three is
        // something `When` can express, so all three are layout bugs — and each would
        // make the engine's step loop run forever rather than fail.
        Should.Throw<InvalidFlowPlanException>(() => StepNode.ForBranch(2, target))
            .Message.ShouldContain("forward");

        Should.Throw<InvalidFlowPlanException>(() => StepNode.ForJump(2, target));
    }

    [Fact]
    public void ATargetMayBeOnePastTheLastStepBecauseThatEndsTheFlow()
    {
        // The layout of a `When` written at the tail of a chain: the false path has
        // nowhere to go but out.
        var graph = StepGraph.Create([
            Step(0, Fixtures.ValidateOrder),
            StepNode.ForBranch(1, falseTarget: 3),
            Step(2, Fixtures.CapturePayment),
        ]);

        graph[1].Target.ShouldBe(3);
    }

    [Fact]
    public void RejectsATargetPastTheEndOfTheGraph()
    {
        // The factory cannot catch this — it does not know how many steps there will be.
        // Left unchecked it surfaces as an IndexOutOfRangeException from the middle of a
        // flow, after some of its steps have already run.
        var error = Should.Throw<InvalidFlowPlanException>(() => StepGraph.Create([
            Step(0, Fixtures.ValidateOrder),
            StepNode.ForBranch(1, falseTarget: 4),
            Step(2, Fixtures.CapturePayment),
        ]));

        error.Message.ShouldContain("4");
        error.Message.ShouldContain("3");
    }

    [Fact]
    public void RejectsAJumpTargetPastTheEndOfTheGraph()
    {
        Should.Throw<InvalidFlowPlanException>(() => StepGraph.Create([
            Step(0, Fixtures.ValidateOrder),
            StepNode.ForJump(1, target: 9),
        ]));
    }

    [Fact]
    public void AcceptsTheFullConditionalLayout()
    {
        // The shape the emitter produces for
        // `.Step<A>().When(p, t => t.Step<B>()).Otherwise(o => o.Step<C>()).Step<D>()`.
        var graph = StepGraph.Create([
            Step(0, Fixtures.ValidateOrder),
            StepNode.ForBranch(1, falseTarget: 4),
            Step(2, Fixtures.ReserveInventory),
            StepNode.ForJump(3, target: 5),
            Step(4, Fixtures.CapturePayment),
            Step(5, Fixtures.ValidateOrder),
        ]);

        graph.Count.ShouldBe(6);
        graph[1].Target.ShouldBe(4);
        graph[3].Target.ShouldBe(5);
    }

    [Fact]
    public void TheGraphIsImmutableOnceBuilt()
    {
        var steps = new List<StepNode> { Step(0, Fixtures.ValidateOrder) };
        var graph = StepGraph.Create(steps);

        steps.Add(Step(1, Fixtures.CapturePayment));

        graph.Count.ShouldBe(1, "The graph must copy its input, not alias it.");
    }
}
