using FlowX;

namespace EventDriven;

/// <summary>
/// Issues an invoice for a request that arrived over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This flow's <c>Define</c> body below <c>.Step&lt;ValidateInvoice&gt;()</c> is the
/// subject of this sample's claim</strong>, and it appears character for character in
/// <see cref="IssueInvoiceOverBusFlow"/>, <see cref="IssueInvoiceOverChangeFlow"/> and
/// <see cref="IssueInvoiceOverScheduleFlow"/>. Nothing enforces that by convention:
/// <c>TransportPortabilityTests</c> compares the four compiled plans and then runs one billing
/// reference through all four transports against a real broker, a real outbox feed, a real cron
/// occurrence and a real journal, and requires one invoice.
/// </para>
/// <para>
/// <strong>What differs between the four is the first step and the attribute above the
/// class, and the reason it is not only the attribute is worth stating exactly.</strong> The
/// vision's illustration puts three triggers on one class; the platform cannot serve that,
/// because each transport fixes the flow's input contract — <c>FLOWX1038</c> requires
/// <see cref="ScheduledFire"/>, <c>FLOWX1039</c> and <c>FLOWX1041</c> require
/// <see cref="BusMessage"/>, and an HTTP route binds the flow's own request type. Two of them on
/// one class is unsatisfiable, and <c>FLOWX1048</c> is the rule that says so rather than leaving
/// an author to discover it by alternating between two errors
/// (<a href="../../docs/adr/ADR-0062-transport-portability-is-a-property-of-the-capability-chain.md">ADR-0062</a>).
/// So portability holds over the chain, one adapter step in.
/// </para>
/// <para>
/// <strong><c>Durable</c> is not a preference on any of the four.</strong> Three of them are
/// refused without it — a delivery, a change and an occurrence each derive the instance id they
/// start, and on an ephemeral flow that id is inert — and this one declares it so that the four
/// are the same flow rather than three durable ones and a fourth that is measured differently.
/// It therefore needs a journal; see the README.
/// </para>
/// </remarks>
[Flow("invoice.issue.http", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "billing")]
[FlowDeadline("PT30S")]
[HttpTrigger("POST", "/api/v1/invoices", Idempotent = true)]
public sealed partial class IssueInvoiceOverHttpFlow : Flow<IssueInvoice, Invoice>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<IssueInvoice, Invoice> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
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
