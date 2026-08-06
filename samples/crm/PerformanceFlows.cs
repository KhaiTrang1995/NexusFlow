using FlowX;

namespace Crm;

/// <summary>The plan tree of a period.</summary>
[Flow("crm.planning.tree", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "crm-platform")]
[FlowDeadline("PT30S")]
[HttpTrigger("POST", "/api/v1/crm/planning/tree")]
public sealed partial class PlanTreeFlow : Flow<ReadPlanTree, PlanTree>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ReadPlanTree, PlanTree> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ReadCrmPlanTree>()
            .Return(ctx => ctx.Get<PlanTree>());
    }
}

/// <summary>How the people in the caller's organisation are doing.</summary>
[Flow("crm.performance.sales", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "crm-platform")]
[FlowDeadline("PT30S")]
[HttpTrigger("POST", "/api/v1/crm/performance/sales")]
public sealed partial class SalesPerformanceFlow : Flow<ReadSalesPerformance, SalesPerformance>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ReadSalesPerformance, SalesPerformance> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ReadCrmSalesPerformance, ForPerformance>(
                ctx => new ForPerformance(ctx.Input.Period, Caller.Subject(ctx.Principal)))
            .Return(ctx => ctx.Get<SalesPerformance>());
    }
}

/// <summary>How the deals themselves are doing.</summary>
[Flow("crm.performance.deals", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "crm-platform")]
[FlowDeadline("PT30S")]
[HttpTrigger("POST", "/api/v1/crm/performance/deals")]
public sealed partial class DealPerformanceFlow : Flow<ReadDealPerformance, DealPerformance>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ReadDealPerformance, DealPerformance> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ReadCrmDealPerformance>()
            .Return(ctx => ctx.Get<DealPerformance>());
    }
}

/// <summary>Everything a board looks at, in one request.</summary>
[Flow("crm.board", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "crm-platform")]
[FlowDeadline("PT60S")]
[HttpTrigger("POST", "/api/v1/crm/board")]
public sealed partial class BoardFlow : Flow<ReadBoard, ExecutiveBoard>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ReadBoard, ExecutiveBoard> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ReadExecutiveBoard, ForPerformance>(
                ctx => new ForPerformance(ctx.Input.Period, Caller.Subject(ctx.Principal)))
            .Return(ctx => ctx.Get<ExecutiveBoard>());
    }
}
