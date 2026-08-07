using FlowX;

namespace Crm;

/// <summary>One plan, whole.</summary>
/// <remarks>
/// <c>Ephemeral</c>: a read that journals nothing. Six statements on one connection, so the
/// steps and the risks are read at the same instant — a plan drawn from six requests is a plan
/// that can disagree with itself.
/// </remarks>
[Flow("crm.plan.read", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "crm-platform")]
[FlowDeadline("PT30S")]
[HttpTrigger("POST", "/api/v1/crm/planning/plan")]
public sealed partial class PlanDetailFlow : Flow<ReadPlan, PlanDetail>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ReadPlan, PlanDetail> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ReadCrmPlan, ReadPlan>(ctx => ctx.Input)
            .Return(ctx => ctx.Get<PlanDetail>());
    }
}
