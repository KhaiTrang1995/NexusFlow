using FlowX;

namespace Workflow;

/// <summary>
/// Closes the offers whose window ran out, every night, <strong>started by nothing but a cron
/// expression</strong>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the sample's second bound transport, and the first one nobody calls.</strong>
/// <c>offer.accept</c> and <c>employee.onboard</c> are started by a request; this flow is
/// started by <c>FlowScheduleScan</c>, on whichever node happens to sweep after the occurrence
/// passes, from a registration the compiler wrote out of the <c>[CronTrigger]</c> below. There
/// is no <c>Program.cs</c> line that names 02:00, and there is no hosted service in this project
/// at all.
/// </para>
/// <para>
/// <strong>Its input is the occurrence, and that is not a convention.</strong>
/// <see cref="ScheduledFire"/> carries the instant the expression named — which may be minutes
/// or hours before the sweep that noticed it — and the flow needs that instant rather than "now"
/// for two separate reasons. The business one: a run at 02:41 is still closing the window that
/// ended at 02:00, and a late run that used its own start time would close a different set of
/// offers from an on-time one. The platform one: <c>FLOWX1007</c> and <c>FLOWX1011</c> forbid a
/// durable flow reading an ambient clock at all, because a value taken that way is in none of
/// the fields a replay reconstructs — so a resumed instance would compute a different answer
/// from the one it committed
/// (<c>docs/adr/ADR-0033-a-scheduled-flows-input-is-its-occurrence.md</c>).
/// </para>
/// <para>
/// <strong><c>Durable</c> is load-bearing here in a way it is not on <c>offer.accept</c>.</strong>
/// That flow declares it because it suspends. This one declares it because the id every node
/// derives for a firing is only exclusive if something refuses the second start — and what
/// refuses it is <c>flow_instance</c>'s primary key. An ephemeral flow with this attribute would
/// run once per replica, every night, with nothing anywhere recording that it had;
/// <c>FLOWX1038</c> is what stops that being writable
/// (<c>docs/adr/ADR-0031-an-occurrence-names-the-instance-it-starts.md</c>).
/// </para>
/// <para>
/// <strong>The declared window is the business number, and the demonstration overrides it.</strong>
/// <c>0 2 * * *</c> in <c>Europe/Berlin</c> is what the manifest publishes and what
/// <c>flowx diff</c> compares. A sample cannot wait until two in the morning for the thing it
/// exists to demonstrate, so <c>Program.cs</c> can register a second, denser schedule from the
/// environment — a <em>second</em> registration rather than an override of this one, so that the
/// declaration a reader sees is the declaration the manifest carries.
/// </para>
/// </remarks>
[Flow("offer.window.close", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "people-ops")]
[CronTrigger("0 2 * * *", TimeZone = "Europe/Berlin", MissedFire = MissedFirePolicy.RunOnce)]
[FlowDeadline("PT10M")]
public sealed partial class CloseOfferWindowFlow : Flow<ScheduledFire, OfferWindowClosed>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ScheduledFire, OfferWindowClosed> flow)
    {
        // CA1062, answered the way every other flow in this repository answers it.
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<CloseExpiredOffers>()

            .Return(ctx => ctx.Get<OfferWindowClosed>());
    }
}
