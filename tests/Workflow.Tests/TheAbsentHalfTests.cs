using FlowX;
using Shouldly;
using Xunit;

namespace Workflow.Tests;

/// <summary>
/// What the old README promised, what the platform did with it, and what is left.
/// </summary>
/// <remarks>
/// <para>
/// <c>samples/workflow/README.md</c> used to claim "a multi-day process with human approvals,
/// escalations, timers and reversible steps". Three of those four were <c>AwaitSignal</c>,
/// <c>Delay</c> and <c>OnTimeout</c>, and none of them worked. This class measured the one
/// whose failure was observable from a test project: an <c>AwaitSignal</c> step in a
/// <c>Durable</c> flow did not wait.
/// </para>
/// <para>
/// <strong>Nothing it measured exists any more, and the class is kept for one narrower
/// job.</strong> Its first test was written to go red on the day suspension landed and it did;
/// its second guarded the claim that no flow here declared a construct the compiler could not
/// honour, and there is no such construct left. <see cref="SuspensionTests"/> is what replaces
/// both — it runs <c>offer.accept</c>, with two real waits in it, through the real host, the
/// real journal and the real sweep.
/// </para>
/// <para>
/// What is left is the one claim that is still a claim rather than a behaviour: which flows in
/// this sample are allowed to wait at all. That is not about the timer half; it is about the
/// transport, and it is stated below.
/// </para>
/// </remarks>
public sealed class TheAbsentHalfTests
{
    /// <summary>
    /// The two flows that are reached over HTTP declare no wait of either kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>employee.onboard</c> is <c>[HttpTrigger]</c>-mapped, and the generated endpoint
    /// answers <c>200</c> with the flow's projected output — a shape a suspended flow has no
    /// answer for, because its <c>.Return(...)</c> reads values the steps after the wait were
    /// going to produce. Adding a wait to it would publish a route that fails on the request
    /// that suspends, which is why <c>offer.accept</c> is a separate flow with hand-written
    /// routes and this assertion keeps the two apart.
    /// </para>
    /// <para>
    /// <strong>Both kinds, not just the signal.</strong> A <c>.Delay</c> suspends exactly as a
    /// suspension point does — one row, no thread, no lease — so it breaks a <c>200</c>-shaped
    /// endpoint in precisely the same way, and a rule that only excluded <c>AwaitSignal</c>
    /// would let the same defect in through the other door.
    /// </para>
    /// <para>
    /// This is a gap in <c>plugins/FlowX.Http</c> rather than in the flows: <c>202 Accepted</c>
    /// with the instance id is the answer the shape needs, and the plugin has no path for it.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheHttpTriggeredFlowsDeclareNoWaitOfEitherKind()
    {
        OnboardEmployeeFlow.Plan.Graph.Steps.ShouldNotContain(step => Waits(step));
        ProvisionWorkspaceFlow.Plan.Graph.Steps.ShouldNotContain(step => Waits(step));

        OnboardEmployeeFlow.Plan.HasTimers.ShouldBeFalse(
            "and the plan says so in one flag, which is what a host reads rather than " +
            "walking the graph.");

        ProvisionWorkspaceFlow.Plan.HasTimers.ShouldBeFalse();
    }

    /// <summary>Every wait this sample declares carries the duration its author wrote.</summary>
    /// <remarks>
    /// The state that made <c>FlowEmitter</c> fabricate <c>TimeSpan.FromHours(1)</c> was a plan
    /// node with no duration on it. Neither factory can produce one now — both demand a
    /// positive <c>TimeSpan</c> — so this asserts a shape that is unrepresentable rather than
    /// merely absent, which is worth one line against the sample's own compiled plans.
    /// </remarks>
    [Fact]
    public void EveryWaitInThisSampleCarriesTheDurationItsAuthorWrote() =>
        AllSteps().Where(step => Waits(step)).ShouldAllBe(
            step => step.SignalTimeout > TimeSpan.Zero || step.Delay > TimeSpan.Zero,
            "a wait with no duration is one nothing can arm and one an emitter would have " +
            "to invent a number for.");

    /// <summary>Either kind of wait: a suspension point, or a timer.</summary>
    private static bool Waits(StepNode step) =>
        step.Kind is StepKind.AwaitSignal or StepKind.Delay;

    private static IEnumerable<StepNode> AllSteps() =>
    [
        .. OnboardEmployeeFlow.Plan.Graph.Steps,
        .. ProvisionWorkspaceFlow.Plan.Graph.Steps,
        .. AcceptOfferFlow.Plan.Graph.Steps,
    ];
}
