using FlowX;
using Shouldly;
using Xunit;

namespace Workflow.Tests;

/// <summary>
/// What the old README promised, what the platform did with it, and which half is left.
/// </summary>
/// <remarks>
/// <para>
/// <c>samples/workflow/README.md</c> used to claim "a multi-day process with human approvals,
/// escalations, timers and reversible steps". Three of those four were <c>AwaitSignal</c>,
/// <c>Delay</c> and <c>OnTimeout</c>, and none of them worked. This class measured the one of
/// the three whose failure was observable from a test project: an <c>AwaitSignal</c> step in a
/// <c>Durable</c> flow did not wait.
/// </para>
/// <para>
/// <strong>That test is gone, because the behaviour it measured is gone.</strong> It was
/// written to go red on the day WP-63 landed and it did; the assertion that replaces it is
/// <see cref="SuspensionTests"/>, which runs <c>offer.accept</c> — a flow of this sample's
/// own, with a real wait in the middle of it — through the real host and the real journal.
/// A degenerate behaviour that no longer exists is not something to keep a test for.
/// </para>
/// <para>
/// <strong>What is left is the timer half, and it is still absent.</strong> <c>Delay</c> and
/// <c>OnTimeout</c> compile to nothing and say so as <c>FLOWX1031</c>. They fail at compile
/// time, so demonstrating them needs a flow that declares them and the whole point is that no
/// flow in this repository should — the README carries the generated output from a throwaway
/// project, with the command to reproduce it. What this class can still assert is that no flow
/// here declares one, which is the guard on that claim.
/// </para>
/// </remarks>
public sealed class TheAbsentHalfTests
{
    /// <summary>
    /// No flow in this sample declares a construct the compiler still cannot honour.
    /// </summary>
    /// <remarks>
    /// A <c>Delay</c> or an <c>OnTimeout</c> would be a warning in this project's build, and
    /// this repository builds with <c>TreatWarningsAsErrors</c> — so this assertion is
    /// belt-and-braces against the day someone relaxes that. The failure it guards is a flow
    /// that says it waits a day and does not.
    /// </remarks>
    [Fact]
    public void NoFlowInThisSampleDeclaresAConstructTheCompilerCannotHonour()
    {
        // A Delay would occupy an index of its own and an OnTimeout would contribute steps;
        // neither reaches a plan, so the only observable form of "this sample declares one"
        // is the build, which is green. What is assertable here is the shape that *does*
        // reach the plan, and that it is the one the engine has a case for.
        AllSteps()
            .Where(step => step.Kind == StepKind.AwaitSignal)
            .ShouldAllBe(
                step => step.SignalTimeout != null,
                "a suspension point carries the author's declared duration. A plan with none " +
                "is the state that made FlowEmitter fabricate one hour.");
    }

    /// <summary>
    /// The two flows that were written before suspension existed still declare none.
    /// </summary>
    /// <remarks>
    /// <c>employee.onboard</c> is <c>[HttpTrigger]</c>-mapped, and the generated endpoint
    /// answers <c>200</c> with the flow's projected output — a shape a suspended flow has no
    /// answer for, because its <c>.Return(...)</c> reads values the steps after the wait were
    /// going to produce. Adding a wait to it would publish a route that fails on the request
    /// that suspends, which is why <c>offer.accept</c> is a separate flow with hand-written
    /// routes and this assertion keeps the two apart.
    /// </remarks>
    [Fact]
    public void TheHttpTriggeredFlowsDeclareNoSuspensionPoint()
    {
        OnboardEmployeeFlow.Plan.Graph.Steps
            .ShouldNotContain(step => step.Kind == StepKind.AwaitSignal);

        ProvisionWorkspaceFlow.Plan.Graph.Steps
            .ShouldNotContain(step => step.Kind == StepKind.AwaitSignal);
    }

    private static IEnumerable<StepNode> AllSteps() =>
    [
        .. OnboardEmployeeFlow.Plan.Graph.Steps,
        .. ProvisionWorkspaceFlow.Plan.Graph.Steps,
        .. AcceptOfferFlow.Plan.Graph.Steps,
    ];
}
