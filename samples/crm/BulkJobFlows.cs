using FlowX;

namespace Crm;

/// <summary>Submits rows to be imported.</summary>
/// <remarks>
/// <strong>Durable, because a submission that is lost is work a caller believes is queued.</strong>
/// The routes that read a job are ephemeral for the opposite reason: a client polls them.
/// </remarks>
[Flow("crm.bulk.import", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT30S")]
[HttpTrigger("POST", "/api/v1/crm/bulk/imports", Idempotent = true)]
public sealed partial class SubmitImportFlow : Flow<SubmitImport, JobSubmitted>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<SubmitImport, JobSubmitted> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<SubmitBulkJob, SubmitJob>(
                ctx => new SubmitJob(ctx.Input, null, CustomFieldPolicy.Scopes(ctx.Principal)))
            .Return(ctx => ctx.Get<JobSubmitted>());
    }
}

/// <summary>Submits a request for an object's records as a document.</summary>
[Flow("crm.bulk.export", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT30S")]
[HttpTrigger("POST", "/api/v1/crm/bulk/exports", Idempotent = true)]
public sealed partial class SubmitExportFlow : Flow<SubmitExport, JobSubmitted>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<SubmitExport, JobSubmitted> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<SubmitBulkJob, SubmitJob>(
                ctx => new SubmitJob(null, ctx.Input, CustomFieldPolicy.Scopes(ctx.Principal)))
            .Return(ctx => ctx.Get<JobSubmitted>());
    }
}

/// <summary>Tells a caller how their job is getting on.</summary>
[Flow("crm.bulk.status", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "crm-platform")]
[FlowDeadline("PT15S")]
[HttpTrigger("POST", "/api/v1/crm/bulk/jobs")]
public sealed partial class ReadJobFlow : Flow<ReadJob, JobStatus>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ReadJob, JobStatus> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ReadBulkJob, ReadJobFor>(
                ctx => new ReadJobFor(ctx.Input, CustomFieldPolicy.Scopes(ctx.Principal)))
            .Return(ctx => ctx.Get<JobStatus>());
    }
}

/// <summary>Advances one job by one chunk, per tenant, every minute.</summary>
[Flow("crm.bulk.sweep", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "crm-platform")]
[FlowDeadline("PT60S")]
[CronTrigger("* * * * *", PerTenant = true)]
public sealed partial class SweepJobsFlow : Flow<ScheduledFire, JobsSwept>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ScheduledFire, JobsSwept> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<SweepBulkJobs>()
            .Return(ctx => ctx.Get<JobsSwept>());
    }
}
