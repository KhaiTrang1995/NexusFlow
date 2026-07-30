using Shouldly;
using Xunit;

namespace FlowX.Core.Tests;

/// <summary>
/// The execution plan is what the generator emits and the engine walks. It is the
/// artifact <a href="../../docs/adr/ADR-0002-compile-time-orchestration.md">ADR-0002</a>
/// is a bet on, so its properties are asserted rather than assumed.
/// </summary>
public sealed class ExecutionPlanTests
{
    private static StepGraph ThreeStepGraph() => StepGraph.Create([
        StepNode.ForCapability(0, Fixtures.ValidateOrder),
        StepNode.ForCapability(1, Fixtures.ReserveInventory, Fixtures.ReleaseInventory),
        StepNode.ForCapability(2, Fixtures.CapturePayment),
    ]);

    [Fact]
    public void ExposesTheCompensableStepsWithoutWalkingTheGraph()
    {
        var plan = ExecutionPlan.Create(Fixtures.PlaceOrder, ThreeStepGraph());

        plan.CompensableStepIndices.ShouldBe([1]);
        plan.HasCompensation.ShouldBeTrue();
        // Precomputed at build time. The engine must not scan for compensable steps
        // on the failure path, where latency matters most.
    }

    [Fact]
    public void ReportsNoCompensationWhenNoStepDeclaresAny()
    {
        var plan = ExecutionPlan.Create(
            Fixtures.PlaceOrder,
            StepGraph.Create([StepNode.ForCapability(0, Fixtures.ValidateOrder)]));

        plan.HasCompensation.ShouldBeFalse();
        plan.CompensableStepIndices.ShouldBeEmpty();
    }

    [Fact]
    public void AggregatesEveryDeclaredSideEffectForBlastRadiusAnalysis()
    {
        var plan = ExecutionPlan.Create(Fixtures.PlaceOrder, ThreeStepGraph());

        // This set is what an agent confirmation prompt states, and what impact
        // analysis reads. Deduplicated: inventory-ledger appears on both the
        // reserve step and its compensation, and must appear once here.
        plan.SideEffects.ShouldBe(["inventory-ledger", "ledger", "payment-gateway"]);
    }

    [Fact]
    public void SideEffectsAreSortedSoTheManifestIsDeterministic()
    {
        var plan = ExecutionPlan.Create(Fixtures.PlaceOrder, ThreeStepGraph());

        plan.SideEffects
            .SequenceEqual(plan.SideEffects.OrderBy(static s => s, StringComparer.Ordinal), StringComparer.Ordinal)
            .ShouldBeTrue(
                "Two builds of identical source must emit a byte-identical manifest, or " +
                "`flowx diff` reports phantom changes and everyone stops reading it.");
    }

    [Fact]
    public void ADeadlineMustBePositive()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => FlowDescriptor.Create("a.b", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.Zero));

        Should.Throw<ArgumentOutOfRangeException>(
            () => FlowDescriptor.Create("a.b", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void AwaitingASignalRequiresTheDurableProfile()
    {
        var graph = StepGraph.Create([
            StepNode.ForCapability(0, Fixtures.ValidateOrder),
            StepNode.ForAwaitSignal(1, "payment.confirmed", TimeSpan.FromHours(1)),
        ]);

        var error = Should.Throw<InvalidFlowPlanException>(
            () => ExecutionPlan.Create(Fixtures.PlaceOrder, graph));

        error.Message.ShouldContain("Durable");
        // An in-memory wait cannot survive a deployment. FLOWX1017.
    }

    [Fact]
    public void AwaitingASignalIsAllowedOnADurableFlow()
    {
        var durable = FlowDescriptor.Create(
            "order.place", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromDays(30));

        var plan = ExecutionPlan.Create(durable, StepGraph.Create([
            StepNode.ForCapability(0, Fixtures.ValidateOrder),
            StepNode.ForAwaitSignal(1, "payment.confirmed", TimeSpan.FromHours(1)),
        ]));

        plan.Graph.Count.ShouldBe(2);
    }
}
