using FlowX;

namespace Ecommerce;

/// <summary>
/// Places an order: validate, reserve stock, take payment, announce it.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole application's control flow. It expresses <em>order, condition and
/// recovery</em> and nothing else — there is no business rule here, and no mention of
/// HTTP, which is why the same flow would run behind Kafka or a cron schedule by
/// changing only the attribute above it.
/// </para>
/// <para>
/// The compiled plan and the step dispatcher are generated into the other half of this
/// partial class at build time. They are on disk, under <c>obj/generated</c>, with line
/// directives back to this file — set a breakpoint on a step and it lands here.
/// </para>
/// <para>
/// <strong>The <c>[HttpTrigger]</c> below is read, not yet acted on.</strong> It reaches
/// <c>flowx.manifest.json</c>, so the published contract states the address this flow
/// answers on; the registration that actually serves it is still written by hand in
/// <c>Program.cs</c> until the endpoint generator lands. The two are deliberately
/// identical — a declared address that nothing serves would be exactly the kind of
/// documented-but-not-produced claim the manifest exists to eliminate.
/// </para>
/// </remarks>
[Flow("order.place", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "orders")]
[FlowDeadline("PT30S")]
[HttpTrigger("POST", "/api/v1/orders", Idempotent = true)]
public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ValidateOrder>()
            .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()
            .Step<CapturePayment>()
#pragma warning disable FLOWX1024 // FLOWX-DEBT: id=DEBT-0001 owner=orders expires=2026-12-31
            //   The step is in the plan and in the manifest, but nothing
            //   publishes it until the outbox lands. Kept, and kept visible,
            //   because this is the reference sample.
            //   See docs/diagnostics/FLOWX1024.md and docs/DEBT.md.
            .Emit<OrderPlaced>(ctx => new OrderPlaced(
                ctx.Get<Reservation>().ReservationId,
                ctx.Get<ValidatedOrder>().Sku,
                ctx.Get<ValidatedOrder>().Quantity))
#pragma warning restore FLOWX1024
            .Return(ctx => new OrderPlacedResult(
                ctx.Get<Reservation>().ReservationId,
                ctx.Get<Payment>().ReceiptId));
    }
}
