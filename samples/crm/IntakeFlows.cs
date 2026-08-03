using FlowX;

namespace Crm;

/// <summary>Takes an arriving lead and announces it.</summary>
/// <remarks>
/// <para>
/// <strong>One write and one event, and the event is staged in the write's own
/// transaction.</strong> A lead that exists and a <c>lead.created</c> nobody published are the
/// same failure from a consumer's side, and the outbox is what makes them impossible: the row
/// and the staged event commit together or neither does.
/// </para>
/// <para>
/// <strong><c>Durable</c> because it emits.</strong> An <c>Ephemeral</c> flow keeps no
/// transaction to stage into, which is <c>FLOWX1024</c> — the reference sample suppresses it
/// with an argument and this one does not need to.
/// </para>
/// </remarks>
[Flow("crm.lead.capture", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-sales")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/leads", Idempotent = true)]
public sealed partial class CaptureLeadFlow : Flow<CaptureLead, LeadCaptured>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<CaptureLead, LeadCaptured> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<CaptureNewLead>()
            .Emit<LeadCreated>(ctx => new LeadCreated(
                ctx.Get<LeadCaptured>().LeadId,
                ctx.Input.Company,
                ctx.Input.Source))
            .Return(ctx => ctx.Get<LeadCaptured>());
    }
}

/// <summary>Scores a lead when one is captured.</summary>
/// <remarks>
/// <para>
/// <strong>One of two flows on the same event, and neither knows the other exists.</strong>
/// That is §8.5: the publisher publishes once, the broker routes to every queue bound to the
/// routing key, and each subscription acknowledges on its own. A redelivery to this one does
/// not re-run <see cref="AssignLeadFlow"/>, because they are different queues holding different
/// deliveries of the same event.
/// </para>
/// <para>
/// <strong>The input is <see cref="BusMessage"/> and the profile is <c>Durable</c>, and
/// <c>FLOWX1039</c> refuses either without the other.</strong> A delivery hands its body over
/// undeserialised — turning it into a typed contract needs a <c>JsonTypeInfo</c> only generated
/// code can name — and a consumer that is not journaled cannot say whether it has already
/// handled a redelivery.
/// </para>
/// <para>
/// <strong><c>Group</c> is what makes this a subscription rather than a competing
/// consumer.</strong> Two flows on one event with the same group would share the deliveries
/// between them; with different groups each gets every one.
/// </para>
/// </remarks>
[Flow("crm.lead.scoring", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-sales")]
[FlowDeadline("PT30S")]
[BusTrigger("lead.created", Group = "scoring")]
public sealed partial class ScoreLeadFlow : Flow<BusMessage, LeadScored>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<BusMessage, LeadScored> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ScoreLead>()
            .Return(ctx => ctx.Get<LeadScored>());
    }
}

/// <summary>Gives a captured lead an owner.</summary>
/// <remarks>
/// <strong>The second subscription on <c>lead.created</c>, and the one a redelivery could
/// hurt.</strong> Scoring twice writes the same number; assigning twice would move a lead off
/// the representative already working it. The guard is in the statement —
/// <c>AND owner_id IS NULL</c> — rather than in this flow, because a broker's redelivery and a
/// second capture of the same lead are the same problem and one guard answers both.
/// </remarks>
[Flow("crm.lead.assignment", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-sales")]
[FlowDeadline("PT30S")]
[BusTrigger("lead.created", Group = "assignment")]
public sealed partial class AssignLeadFlow : Flow<BusMessage, LeadAssigned>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<BusMessage, LeadAssigned> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<AssignLead>()
            .Return(ctx => ctx.Get<LeadAssigned>());
    }
}
