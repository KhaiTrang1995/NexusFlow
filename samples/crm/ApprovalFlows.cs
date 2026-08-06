using FlowX;

namespace Crm;

/// <summary>Declares an approval process.</summary>
[Flow("crm.approval.process", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/approvals/processes", Idempotent = true)]
public sealed partial class DefineApprovalProcessFlow
    : Flow<DefineApprovalProcess, ApprovalProcessDefined>
{
    /// <inheritdoc />
    protected override void Define(
        IFlowBuilder<DefineApprovalProcess, ApprovalProcessDefined> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<DefineCrmApprovalProcess>()
            .Return(ctx => ctx.Get<ApprovalProcessDefined>());
    }
}

/// <summary>Submits something for approval, if any process says it needs one.</summary>
[Flow("crm.approval.submit", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/approvals/requests", Idempotent = true)]
public sealed partial class SubmitApprovalFlow : Flow<SubmitForApproval, ApprovalSubmitted>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<SubmitForApproval, ApprovalSubmitted> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<SubmitCrmApproval, ApprovalBy>(
                ctx => new ApprovalBy(ctx.Input, null, Caller.Subject(ctx.Principal)))
            .Return(ctx => ctx.Get<ApprovalSubmitted>());
    }
}

/// <summary>Records what somebody said about a request.</summary>
/// <remarks>
/// <c>Durable</c>: a lost decision is an approval somebody gave that nothing recorded, which is
/// the one thing an audit cannot recover from.
/// </remarks>
[Flow("crm.approval.decide", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/approvals/decisions", Idempotent = true)]
public sealed partial class DecideApprovalFlow : Flow<DecideApproval, ApprovalDecided>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<DecideApproval, ApprovalDecided> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<DecideCrmApproval, ApprovalBy>(
                ctx => new ApprovalBy(null, ctx.Input, Caller.Subject(ctx.Principal)))
            .Return(ctx => ctx.Get<ApprovalDecided>());
    }
}

/// <summary>What is waiting on the caller.</summary>
[Flow("crm.approval.inbox", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "crm-platform")]
[FlowDeadline("PT30S")]
[HttpTrigger("POST", "/api/v1/crm/approvals/inbox")]
public sealed partial class ApprovalInboxFlow : Flow<ReadApprovalInbox, ApprovalInbox>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ReadApprovalInbox, ApprovalInbox> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ReadCrmApprovalInbox, ApprovalBy>(
                ctx => new ApprovalBy(null, null, Caller.Subject(ctx.Principal)))
            .Return(ctx => ctx.Get<ApprovalInbox>());
    }
}
