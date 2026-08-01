using FlowX;

namespace Workflow;

/// <summary>
/// Sends an offer out for countersignature, <strong>waits for a person</strong>, and starts
/// the joiner's onboarding once they sign.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the flow this sample's README said for two phases it could not
/// write.</strong> The wait in the middle is a real one: the invocation that starts this flow
/// returns at <c>.AwaitSignal</c>, the instance is left <see cref="FlowInstanceState.Suspended"/>
/// in the journal at its resume frontier, and it holds no thread, no pooled context and no
/// lease while it waits. Days later a signal arrives, and the same step loop that started the
/// flow — the one <c>FlowRecoveryScan</c> re-enters when a node dies — picks it up, steps over
/// every boundary that committed, and runs the rest.
/// </para>
/// <para>
/// <strong>The unwind spans the wait, which is the property a saga is for.</strong>
/// <c>offer.send</c> declares an inverse, and it is on the compensation stack when the flow
/// suspends. It is <em>not</em> in this node's memory when the flow resumes — that node has
/// gone — so it is rebuilt from the journal's committed rows by the same frontier scan that
/// decides which steps to skip. A failure in <c>onboarding.start</c>, three days after the
/// offer went out, withdraws the offer.
/// </para>
/// <para>
/// <strong>Three things about the shape are deliberate.</strong>
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <c>onboarding.start</c> binds <see cref="OfferCountersigned"/> — the signal's own payload.
/// The engine seeds a delivered signal into the state bag under the contract
/// <c>.AwaitSignal&lt;T&gt;</c> named, and the commit that records the suspension point
/// journals it with the rest of the bag, so the value survives a second crash exactly as a
/// step's output does. A wait that woke a flow up and told it nothing would need the flow to
/// go and look, which is the design the wait exists to replace.
/// </description>
/// </item>
/// <item>
/// <description>
/// <strong>An ordinary <c>[HttpTrigger]</c>, and two generated routes.</strong> <em>This
/// entry read "No <c>[HttpTrigger]</c>" until WP-64.</em> The generated endpoint answered
/// <c>200</c> with the flow's projected output, and a suspended flow has none — its
/// <c>.Return(...)</c> reads values the steps after the wait were going to produce — so the
/// attribute was left off and <c>Program.cs</c> mapped two routes by hand. The plugin has the
/// <c>202</c> path now (<c>docs/adr/ADR-0022-http-shape-of-a-suspending-flow.md</c>), so this
/// flow declares its address like any other and the generator publishes both routes it needs:
/// <c>POST /api/v1/offers</c>, which answers <c>202</c> with the instance and where to
/// continue it, and
/// <c>POST /api/v1/offers/{instanceId}/signals/offer.countersigned</c>, which delivers the
/// countersignature. The identity in the second route is read off the <c>.AwaitSignal</c>
/// below — there is no second declaration of it to disagree with the plan.
/// </description>
/// </item>
/// <item>
/// <description>
/// <strong>The declared timeout is carried, not armed.</strong> <c>Waits.Countersignature</c>
/// reaches <c>StepNode.SignalTimeout</c> — that is the fabricated <c>TimeSpan.FromHours(1)</c>
/// gone, and it is what <c>FLOWX1031</c> was raised over — but nothing fires when it expires,
/// because there is no timer. What bounds this flow is <c>[FlowDeadline("P30D")]</c>, checked
/// at every step boundary including the one that decides whether to wait. The same is true of
/// <c>.OnTimeout(...)</c>, which is why this flow does not declare one: it would compile to
/// nothing and say so as <c>FLOWX1031</c>.
/// </description>
/// </item>
/// </list>
/// </remarks>
[Flow("offer.accept", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "people-ops")]
[HttpTrigger("POST", "/api/v1/offers")]
[FlowDeadline("P30D")]
public sealed partial class AcceptOfferFlow : Flow<OfferToAccept, AcceptedOffer>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<OfferToAccept, AcceptedOffer> flow)
    {
        // CA1062, answered the way every other flow in this repository answers it: `Define`
        // is a protected override the framework calls, so the parameter cannot be null in
        // practice, and a guard is cheaper than an exemption to justify again next time.
        ArgumentNullException.ThrowIfNull(flow);

        flow
            // Compensable, and the compensation is what makes the wait interesting: it is
            // registered before the flow suspends and has to survive the node that
            // registered it.
            .Step<SendOfferForSignature>().CompensateWith<WithdrawOffer>()

            // The suspension point. The invocation returns here.
            .AwaitSignal<OfferCountersigned>(Waits.Countersignature)

            // And this binds what the signal carried.
            .Step<StartOnboarding>()

            .Return(ctx => new AcceptedOffer(
                ctx.Input.CandidateId,
                ctx.Get<OnboardingStarted>().OnboardingId));
    }
}
