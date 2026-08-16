using FlowX;

namespace Functions;

/// <summary>
/// Accepts an order over HTTP and stages the event that reserves its stock.
/// </summary>
/// <remarks>
/// <strong>Nothing in this file mentions Azure Functions, and that is the sample.</strong> The
/// three attributes below are the same three any FlowX flow carries; what
/// <c>samples/ecommerce</c> turns into an ASP.NET route, this project turns into a
/// <c>[Function]</c> with an <c>HttpTrigger</c> — from the same reading of the same attribute.
/// The deployment shape changed and the flow did not.
/// </remarks>
[Flow("orders.place", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "orders")]
[FlowDeadline("PT30S")]
// Idempotent, and the generated entry point enforces it: a POST that starts a Durable flow
// without a deduplication key repeats its effects on any retry, and a serverless platform
// retries. FlowPushSeams.HttpAsync refuses a request with no `Idempotency-Key` header, which is
// what makes this declaration true here as well as on the ASP.NET host.
[HttpTrigger("POST", "/api/v1/orders", Idempotent = true)]
public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderAccepted>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<PlaceOrder, OrderAccepted> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<AcceptOrder>()

            // Staged in the step's own transaction, keyed on this instance, so the broker
            // offers one instance's events in the order they were staged (ADR-0018).
            .Emit<OrderPlaced>(ctx => new OrderPlaced(
                ctx.Get<OrderAccepted>().OrderId,
                ctx.Input.Sku,
                ctx.Input.Quantity))
            .Return(ctx => ctx.Get<OrderAccepted>());
    }
}

/// <summary>
/// Reserves stock for an order that was placed.
/// </summary>
/// <remarks>
/// <para>
/// <strong><c>Durable</c> is load-bearing on a push host, and more so than on a pull one.</strong>
/// The platform delivers at least once and settles from what the generated entry point returns.
/// What makes the second delivery of one message inert is the instance id derived from it and
/// the journal's primary key refusing it (ADR-0035) — an ephemeral flow would reserve the stock
/// again, and the entry point would complete the message both times with nothing recording
/// either.
/// </para>
/// <para>
/// <strong>Its input is <see cref="BusMessage"/> and <c>FLOWX1039</c> refuses anything else.</strong>
/// A delivery has only the message to give a flow, so the payload is deserialised in the
/// capability rather than bound by the trigger — and the generated <c>[ServiceBusTrigger]</c>
/// entry point is what fills that message in, from the platform's own fields.
/// </para>
/// </remarks>
[Flow("stock.reserve", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "stock")]
[BusTrigger("order.placed", Group = "stock")]
public sealed partial class ReserveStockFlow : Flow<BusMessage, StockReserved>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<BusMessage, StockReserved> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow.Step<ReserveStock>().Return(ctx => ctx.Get<StockReserved>());
    }
}

/// <summary>
/// Counts the order book, nightly.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The declared expression is fired by the sweep, not by the platform's timer.</strong>
/// The generated <c>FlowXSchedulePass</c> wakes this worker every minute and asks
/// <c>FlowScheduleScan</c> whether anything is due; this expression is what that question is
/// answered from. Putting <c>0 2 * * *</c> into the <c>TimerTrigger</c> instead would have
/// dropped the zone — the platform's timer has no field for one — and would have made a firing
/// at the wrong minute a <em>different</em> occurrence rather than a late one, because
/// ADR-0031 derives the instance id from the expression.
/// </para>
/// <para>
/// Its input is <see cref="ScheduledFire"/> and has to be: a firing carries no body, and
/// <c>FLOWX1038</c> refuses a scheduled flow that declares any other input.
/// </para>
/// </remarks>
[Flow("orders.reconcile", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "orders")]
[CronTrigger("0 2 * * *", TimeZone = "Europe/Berlin")]
public sealed partial class ReconcileOrdersFlow : Flow<ScheduledFire, ReconciliationReport>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ScheduledFire, ReconciliationReport> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow.Step<ReconcileOrders>().Return(ctx => ctx.Get<ReconciliationReport>());
    }
}
