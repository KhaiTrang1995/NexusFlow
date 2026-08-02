using FlowX;

namespace EventDriven;

/// <summary>
/// Accepts a request to invoice and announces it, so the two event-driven transports have
/// something to consume.
/// </summary>
/// <remarks>
/// It stages <c>invoice.requested</c> in its step's own transaction; the outbox drains it to the
/// broker for <see cref="IssueInvoiceOverBusFlow"/> and offers the same rows directly to
/// <see cref="IssueInvoiceOverChangeFlow"/>. This flow names neither consumer, and neither
/// consumer names it.
/// </remarks>
[Flow("invoice.request", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "billing")]
[FlowDeadline("PT30S")]
[HttpTrigger("POST", "/api/v1/invoice-requests", Idempotent = true)]
public sealed partial class RequestInvoiceFlow : Flow<IssueInvoice, InvoiceRequestAccepted>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<IssueInvoice, InvoiceRequestAccepted> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ValidateInvoice>()
            .Emit<InvoiceRequested>(ctx => new InvoiceRequested(
                ctx.Get<ValidatedInvoice>().Reference,
                ctx.Get<ValidatedInvoice>().Account,
                ctx.Get<ValidatedInvoice>().Net,
                ctx.Get<ValidatedInvoice>().Currency))
            .Return(ctx => new InvoiceRequestAccepted(
                ctx.Get<ValidatedInvoice>().Reference,
                ctx.Get<ValidatedInvoice>().Account));
    }
}
