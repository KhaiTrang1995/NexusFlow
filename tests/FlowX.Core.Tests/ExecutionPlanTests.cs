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

    /// <summary>And so does polling, on the same argument twice over.</summary>
    /// <remarks>
    /// A poll parks between attempts and reads which attempt it is on out of the journal that
    /// parked it. Outside one it has neither anywhere to record when the next call is due nor
    /// any way to count the ones already made, which leaves a hot loop against somebody else's
    /// service. FLOWX1017 says the same thing at build time.
    /// </remarks>
    [Fact]
    public void PollingRequiresTheDurableProfile()
    {
        var graph = StepGraph.Create([
            StepNode.ForPoll(0, Backoff.Exponential("PT5S", "PT5M"), TimeSpan.FromHours(4)),
            StepNode.ForCapability(1, Fixtures.ValidateOrder),
        ]);

        Should.Throw<InvalidFlowPlanException>(() => ExecutionPlan.Create(Fixtures.PlaceOrder, graph))
            .Message.ShouldContain("Durable");
    }

    /// <summary>And on a <c>Streaming</c> flow, which journals for <c>Durable</c>'s reason.</summary>
    /// <remarks>
    /// The refusals above all name a journal as the thing that is missing, and a window's flow
    /// has one: the engine reads the profile once, through
    /// <c>ExecutionProfiles.IsJournaled</c>, and everything that parks or wakes an instance
    /// hangs off the cursor that question opens. <c>FlowStreamScan</c> already counts a
    /// suspended window's flow as started and checkpoints past it, so a wait here is a
    /// parked row rather than a held pump.
    /// </remarks>
    [Fact]
    public void AWaitIsAllowedOnAStreamingFlowBecauseAWindowsFlowIsJournaled()
    {
        var streaming = FlowDescriptor.Create(
            "telemetry.aggregate", "1.0.0", ExecutionProfile.Streaming, TimeSpan.FromHours(1));

        var plan = ExecutionPlan.Create(streaming, StepGraph.Create([
            StepNode.ForCapability(0, Fixtures.ValidateOrder),
            StepNode.ForDelay(1, TimeSpan.FromMinutes(5)),
            StepNode.ForAwaitSignal(2, "payment.confirmed", TimeSpan.FromHours(1)),
        ]));

        plan.HasTimers.ShouldBeTrue();
    }

    /// <summary>A poll counts towards the flag that decides whether a timer sweep matters.</summary>
    /// <remarks>
    /// The flag answers "can an instance of this flow be waiting on a clock", and a parked poll
    /// is one — so a deployment that registered no <c>ITimerIndex</c> is told about a flow that
    /// polls for the same reason it is told about one that delays.
    /// </remarks>
    [Fact]
    public void APollIsAWaitTheHostHasToKnowAbout()
    {
        var durable = FlowDescriptor.Create(
            "order.place", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromDays(30));

        ExecutionPlan.Create(durable, StepGraph.Create([
            StepNode.ForPoll(0, Backoff.Exponential("PT5S", "PT5M"), TimeSpan.FromHours(4)),
            StepNode.ForCapability(1, Fixtures.ValidateOrder),
        ])).HasTimers.ShouldBeTrue();
    }
}
