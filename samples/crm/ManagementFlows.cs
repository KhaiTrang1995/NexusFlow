using FlowX;

namespace Crm;

/// <summary>Places a person in the organisation.</summary>
[Flow("crm.org.member", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/org/members", Idempotent = true)]
public sealed partial class SetOrgMemberFlow : Flow<SetOrgMember, OrgMemberSet>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<SetOrgMember, OrgMemberSet> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<PlaceOrgMember>()
            .Return(ctx => ctx.Get<OrgMemberSet>());
    }
}

/// <summary>Writes an objective of an account plan.</summary>
[Flow("crm.planning.objective", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/planning/objectives", Idempotent = true)]
public sealed partial class SetObjectiveFlow : Flow<SetObjective, ObjectiveSet>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<SetObjective, ObjectiveSet> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<SetPlanObjective>()
            .Return(ctx => ctx.Get<ObjectiveSet>());
    }
}

/// <summary>Puts a person on a plan's relationship map.</summary>
[Flow("crm.planning.stakeholder", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/planning/stakeholders", Idempotent = true)]
public sealed partial class SetStakeholderFlow : Flow<SetStakeholder, StakeholderSet>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<SetStakeholder, StakeholderSet> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<SetPlanStakeholder>()
            .Return(ctx => ctx.Get<StakeholderSet>());
    }
}

/// <summary>Raises a risk against a plan, or closes one.</summary>
[Flow("crm.planning.risk", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/planning/risks", Idempotent = true)]
public sealed partial class SetRiskFlow : Flow<SetRisk, RiskSet>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<SetRisk, RiskSet> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<SetPlanRisk>()
            .Return(ctx => ctx.Get<RiskSet>());
    }
}

/// <summary>Declares a number the leadership team reviews.</summary>
[Flow("crm.kpi.define", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/kpis", Idempotent = true)]
public sealed partial class DefineKpiFlow : Flow<DefineKpi, KpiDefined>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<DefineKpi, KpiDefined> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<DefineCrmKpi>()
            .Return(ctx => ctx.Get<KpiDefined>());
    }
}

/// <summary>Runs every KPI for a period.</summary>
/// <remarks><c>Ephemeral</c>: it writes nothing, and a review screen polls it.</remarks>
[Flow("crm.kpi.scorecard", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "crm-platform")]
[FlowDeadline("PT30S")]
[HttpTrigger("POST", "/api/v1/crm/kpis/scorecards")]
public sealed partial class ScorecardFlow : Flow<ReadScorecard, Scorecard>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ReadScorecard, Scorecard> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ReadCrmScorecard>()
            .Return(ctx => ctx.Get<Scorecard>());
    }
}

/// <summary>Records what was said about a number.</summary>
/// <remarks>
/// <c>Durable</c>: a minute of a meeting that was lost is a decision nobody can show was taken.
/// </remarks>
[Flow("crm.kpi.review", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/kpis/reviews", Idempotent = true)]
public sealed partial class ReviewKpiFlow : Flow<ReviewKpi, KpiReviewed>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ReviewKpi, KpiReviewed> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<RecordKpiReview, ForReview>(
                ctx => new ForReview(ctx.Input, Caller.Subject(ctx.Principal)))
            .Return(ctx => ctx.Get<KpiReviewed>());
    }
}

/// <summary>Reads the reporting line.</summary>
/// <remarks><c>Ephemeral</c>: it writes nothing, and a settings screen polls it.</remarks>
[Flow("crm.org.chart", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/org/chart")]
public sealed partial class OrgChartFlow : Flow<ReadOrgChart, OrgChart>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ReadOrgChart, OrgChart> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ReadCrmOrgChart>()
            .Return(ctx => ctx.Get<OrgChart>());
    }
}
