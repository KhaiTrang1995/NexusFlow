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
/// <strong>No <c>[HttpTrigger]</c>.</strong> The generated endpoint answers <c>200</c> with
/// the flow's projected output, and a suspended flow has no output to project — its
/// <c>.Return(...)</c> reads values the steps after the wait were going to produce.
/// <c>202 Accepted</c> is the answer that shape needs and the HTTP plugin does not have one
/// yet, so this sample maps its own two endpoints in <c>Program.cs</c> rather than publishing
/// a route that would fail on the request that suspends. It is a gap in the transport, named
/// in the README rather than worked around silently.
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
