using FlowX;

namespace EventDriven;

/// <summary>
/// Issues an invoice for a request observed in the outbox, with no broker in the path.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This flow and <see cref="IssueInvoiceOverBusFlow"/> differ by one attribute and
/// nothing else</strong> — same input contract, same profile, same chain, same emitted event.
/// One reaches the request through <c>PostgresOutboxPublisher</c> and a broker; this one reads
/// the same staged rows straight out of <c>outbox_event</c>. It is the narrowest form the
/// portability claim takes, and the one that needs no infrastructure the journal did not already
/// require.
/// </para>
/// <para>
/// <strong>It observes <c>invoice.requested</c> and emits <c>invoice.issued</c>, which is not an
/// arbitrary choice.</strong> A flow that staged the type it observes would be offered its own
/// event for ever, with every instance legitimately distinct so nothing refuses it;
/// <c>FlowChangeCatalog.Add</c> throws at registration rather than letting the pod become ready.
/// </para>
/// </remarks>
[Flow("invoice.issue.change", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "billing")]
[FlowDeadline("PT30S")]
[ChangeTrigger("invoice.requested", Group = "billing-projection")]
public sealed partial class IssueInvoiceOverChangeFlow : Flow<BusMessage, Invoice>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<BusMessage, Invoice> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ReadInvoiceRequest>()
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
