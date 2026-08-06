using FlowX;

namespace Crm;

/// <summary>Declares the week the desk is open.</summary>
[Flow("crm.service.hours", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/service/hours", Idempotent = true)]
public sealed partial class SetBusinessHoursFlow : Flow<SetBusinessHours, BusinessHoursSet>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<SetBusinessHours, BusinessHoursSet> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<SetCrmBusinessHours, ServiceBy>(
                ctx => new ServiceBy(ctx.Input, null, null, null, null, Caller.Subject(ctx.Principal)))
            .Return(ctx => ctx.Get<BusinessHoursSet>());
    }
}

/// <summary>Declares what the desk promises for a priority.</summary>
[Flow("crm.service.sla", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/service/policies", Idempotent = true)]
public sealed partial class DefineSlaPolicyFlow : Flow<DefineSlaPolicy, SlaPolicyDefined>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<DefineSlaPolicy, SlaPolicyDefined> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<DefineCrmSlaPolicy, ServiceBy>(
                ctx => new ServiceBy(null, ctx.Input, null, null, null, Caller.Subject(ctx.Principal)))
            .Return(ctx => ctx.Get<SlaPolicyDefined>());
    }
}

/// <summary>Raises a case.</summary>
/// <remarks>
/// <c>Durable</c>: a lost case is a customer who telephoned and was not written down, which is the
/// failure a service desk exists to make impossible.
/// </remarks>
[Flow("crm.service.open", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/service/cases", Idempotent = true)]
public sealed partial class OpenCaseFlow : Flow<OpenCase, CaseOpened>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<OpenCase, CaseOpened> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<OpenCrmCase, ServiceBy>(
                ctx => new ServiceBy(null, null, ctx.Input, null, null, Caller.Subject(ctx.Principal)))
            .Return(ctx => ctx.Get<CaseOpened>());
    }
}

/// <summary>Says something on a case.</summary>
[Flow("crm.service.comment", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/service/comments", Idempotent = true)]
public sealed partial class CommentOnCaseFlow : Flow<CommentOnCase, CaseCommented>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<CommentOnCase, CaseCommented> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<CommentOnCrmCase, ServiceBy>(
                ctx => new ServiceBy(null, null, null, ctx.Input, null, Caller.Subject(ctx.Principal)))
            .Return(ctx => ctx.Get<CaseCommented>());
    }
}

/// <summary>The live queue.</summary>
[Flow("crm.service.queue", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "crm-platform")]
[FlowDeadline("PT30S")]
[HttpTrigger("POST", "/api/v1/crm/service/queue")]
public sealed partial class CaseWorklistFlow : Flow<ReadCaseWorklist, CaseWorklist>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ReadCaseWorklist, CaseWorklist> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ReadCrmCaseWorklist, ServiceBy>(
                ctx => new ServiceBy(null, null, null, null, ctx.Input, Caller.Subject(ctx.Principal)))
            .Return(ctx => ctx.Get<CaseWorklist>());
    }
}
