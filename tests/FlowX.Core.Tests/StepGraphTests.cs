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
    public void TheGraphIsImmutableOnceBuilt()
    {
        var steps = new List<StepNode> { Step(0, Fixtures.ValidateOrder) };
        var graph = StepGraph.Create(steps);

        steps.Add(Step(1, Fixtures.CapturePayment));

        graph.Count.ShouldBe(1, "The graph must copy its input, not alias it.");
    }
}
