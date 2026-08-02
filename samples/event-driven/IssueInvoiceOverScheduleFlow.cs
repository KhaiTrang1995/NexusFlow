using FlowX;

namespace EventDriven;

/// <summary>
/// Issues the invoice that falls due at an occurrence, started by nothing but a cron expression.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="IssueInvoiceOverHttpFlow"/>'s chain, one adapter step in, with nobody
/// calling.</strong> There is no line in <c>Program.cs</c> that names 03:00 and no hosted service
/// in this project: <c>FlowScheduleScan</c> sweeps, every node computes the same occurrence, every
/// node derives the same instance id from it, and the lease store and the journal's primary key
/// settle the race — which is why this flow is <c>Durable</c> and why <c>FLOWX1038</c> refuses one
/// that is not.
/// </para>
/// <para>
/// <strong>The declared expression is the business number and the demonstration overrides
/// nothing.</strong> <c>0 3 * * *</c> in UTC is what the manifest publishes and what
/// <c>flowx diff</c> compares; a denser schedule for watching it fire is registered beside it from
/// the environment, so the declaration a reader sees is the declaration that runs.
/// </para>
/// </remarks>
[Flow("invoice.issue.schedule", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "billing")]
[FlowDeadline("PT30S")]
[CronTrigger("0 3 * * *", TimeZone = "UTC", MissedFire = MissedFirePolicy.RunOnce)]
public sealed partial class IssueInvoiceOverScheduleFlow : Flow<ScheduledFire, Invoice>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ScheduledFire, Invoice> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<DueInvoice>()
            .Step<ValidateInvoice>()
            .Step<CalculateTax>()
            .Step<PersistInvoice>().CompensateWith<VoidInvoice>()
            .Emit<InvoiceIssued>(ctx => new InvoiceIssued(
                ctx.Get<Invoice>().InvoiceId,
                ctx.Get<Invoice>().Account,
                ctx.Get<Invoice>().Gross,
                ctx.Get<Invoice>().Currency))
            .Return(ctx => ctx.Get<Invoice>());
    }
}
