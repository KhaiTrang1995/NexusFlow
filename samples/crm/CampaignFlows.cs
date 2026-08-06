using FlowX;

namespace Crm;

/// <summary>Declares a campaign.</summary>
[Flow("crm.campaign.define", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/campaigns", Idempotent = true)]
public sealed partial class DefineCampaignFlow : Flow<DefineCampaign, CampaignDefined>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<DefineCampaign, CampaignDefined> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<DefineCrmCampaign, CampaignBy>(
                ctx => new CampaignBy(ctx.Input, null, null, null, null, Caller.Subject(ctx.Principal)))
            .Return(ctx => ctx.Get<CampaignDefined>());
    }
}

/// <summary>Records that a campaign reached somebody.</summary>
[Flow("crm.campaign.touch", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/campaigns/touches", Idempotent = true)]
public sealed partial class RecordTouchFlow : Flow<RecordTouch, TouchRecorded>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<RecordTouch, TouchRecorded> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<RecordCrmCampaignTouch, CampaignBy>(
                ctx => new CampaignBy(null, ctx.Input, null, null, null, Caller.Subject(ctx.Principal)))
            .Return(ctx => ctx.Get<TouchRecorded>());
    }
}

/// <summary>Records what a campaign actually cost.</summary>
/// <remarks>
/// <c>Durable</c>: a lost spend is a return reported against money that went out of the door and
/// was never written down.
/// </remarks>
[Flow("crm.campaign.cost", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/campaigns/costs", Idempotent = true)]
public sealed partial class RecordCampaignCostFlow : Flow<RecordCampaignCost, CampaignCostRecorded>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<RecordCampaignCost, CampaignCostRecorded> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<RecordCrmCampaignCost, CampaignBy>(
                ctx => new CampaignBy(null, null, ctx.Input, null, null, Caller.Subject(ctx.Principal)))
            .Return(ctx => ctx.Get<CampaignCostRecorded>());
    }
}

/// <summary>How the campaigns did.</summary>
[Flow("crm.campaign.performance", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "crm-platform")]
[FlowDeadline("PT30S")]
[HttpTrigger("POST", "/api/v1/crm/campaigns/performance")]
public sealed partial class CampaignPerformanceFlow : Flow<ReadCampaignPerformance, CampaignReport>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ReadCampaignPerformance, CampaignReport> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ReadCrmCampaignPerformance, CampaignBy>(
                ctx => new CampaignBy(null, null, null, ctx.Input, null, Caller.Subject(ctx.Principal)))
            .Return(ctx => ctx.Get<CampaignReport>());
    }
}

/// <summary>Who influenced one deal.</summary>
[Flow("crm.campaign.attribution", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "crm-platform")]
[FlowDeadline("PT30S")]
[HttpTrigger("POST", "/api/v1/crm/campaigns/attribution")]
public sealed partial class DealAttributionFlow : Flow<ReadDealAttribution, DealAttribution>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ReadDealAttribution, DealAttribution> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ReadCrmDealAttribution, CampaignBy>(
                ctx => new CampaignBy(null, null, null, null, ctx.Input, Caller.Subject(ctx.Principal)))
            .Return(ctx => ctx.Get<DealAttribution>());
    }
}
