using FlowX;

namespace Crm;

/// <summary>Declares a period that plans are made for.</summary>
[Flow("crm.planning.period", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/planning/periods", Idempotent = true)]
public sealed partial class DefinePeriodFlow : Flow<DefinePeriod, PeriodDefined>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<DefinePeriod, PeriodDefined> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<DefinePlanPeriod>()
            .Return(ctx => ctx.Get<PeriodDefined>());
    }
}

/// <summary>Sets the number and the words a period is planned against.</summary>
[Flow("crm.planning.strategy", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/planning/strategies", Idempotent = true)]
public sealed partial class SetStrategyFlow : Flow<SetStrategy, StrategySet>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<SetStrategy, StrategySet> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<SetSalesStrategy>()
            .Return(ctx => ctx.Get<StrategySet>());
    }
}

/// <summary>Commits one plan against a period.</summary>
[Flow("crm.planning.plan", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/planning/plans", Idempotent = true)]
public sealed partial class DefinePlanFlow : Flow<DefinePlan, PlanDefined>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<DefinePlan, PlanDefined> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<DefineCrmPlan>()
            .Return(ctx => ctx.Get<PlanDefined>());
    }
}

/// <summary>Records whether one thing about a deal is actually known.</summary>
[Flow("crm.planning.qualification", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/planning/qualifications", Idempotent = true)]
public sealed partial class AnswerQualificationFlow : Flow<AnswerQualification, QualificationRecorded>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<AnswerQualification, QualificationRecorded> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<RecordQualification>()
            .Return(ctx => ctx.Get<QualificationRecorded>());
    }
}

/// <summary>Writes a step of the mutual action plan, or marks one done.</summary>
[Flow("crm.planning.step", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/planning/steps", Idempotent = true)]
public sealed partial class SetPlanStepFlow : Flow<SetPlanStep, PlanStepSet>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<SetPlanStep, PlanStepSet> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<SetCrmPlanStep>()
            .Return(ctx => ctx.Get<PlanStepSet>());
    }
}

/// <summary>Says how a period is looking.</summary>
/// <remarks><c>Ephemeral</c>: it writes nothing, and a management screen polls it.</remarks>
[Flow("crm.planning.rollup", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "crm-platform")]
[FlowDeadline("PT30S")]
[HttpTrigger("POST", "/api/v1/crm/planning/roll-ups")]
public sealed partial class RollUpFlow : Flow<ReadRollUp, PeriodRollUp>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ReadRollUp, PeriodRollUp> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ReadPeriodRollUp>()
            .Return(ctx => ctx.Get<PeriodRollUp>());
    }
}
