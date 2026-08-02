using FlowX;

namespace EventDriven;

/// <summary>
/// Issues an invoice for a request that arrived over a broker.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="IssueInvoiceOverHttpFlow"/>'s chain, one adapter step in.</strong> The
/// subscription is the whole of what makes this flow the consumer: nothing in the emitting flow
/// names it, and nothing here names the emitter. <c>[BusTrigger]</c> rather than
/// <c>[KafkaTrigger]</c> because a flow says what it consumes and not on what — which bus serves
/// it is the host's <c>IBusConsumer</c> registration, and this repository's is Redis Streams.
/// </para>
/// <para>
/// <strong>Naming a broker family would be a claim this deployment cannot keep.</strong>
/// <c>[KafkaTrigger]</c> publishes <c>"transport": "kafka"</c> into the manifest and is checked
/// against the registered consumer at startup, so declaring it here would be a pod that never
/// becomes ready. That refusal is correct, and it is why the README's Kafka commit is a design
/// rather than a step of this sample.
/// </para>
/// </remarks>
[Flow("invoice.issue.bus", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "billing")]
[FlowDeadline("PT30S")]
[BusTrigger("invoice.requested", Group = "billing")]
public sealed partial class IssueInvoiceOverBusFlow : Flow<BusMessage, Invoice>
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
