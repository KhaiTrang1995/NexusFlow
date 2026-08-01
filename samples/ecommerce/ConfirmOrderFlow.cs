using FlowX;

namespace Ecommerce;

/// <summary>
/// Confirms an order and announces it: validate, then emit <c>order.placed</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The emitting half of this sample's event chain.</strong> <see cref="PlaceOrderFlow"/>
/// declares the same <c>.Emit&lt;OrderPlaced&gt;</c> and stages nothing, because it is
/// deliberately <c>Ephemeral</c> and so keeps no transaction to stage into — the trade
/// <c>docs/diagnostics/FLOWX1012.md</c> argues, and the reason that flow suppresses
/// <c>FLOWX1024</c>. This flow is the same announcement with the trade taken the other way: it
/// declares <c>Durable</c>, so the event is staged in the step's own transaction, drained by
/// <c>PostgresOutboxPublisher</c>, and consumed by <see cref="RepriceOrderFlow"/>.
/// </para>
/// <para>
/// <strong>The two flows name each other nowhere.</strong> The emitter names a contract and the
/// consumer names a topic; the topic is the contract's identity — <c>OrderPlaced</c> becomes
/// <c>order.placed</c> — and everything between them is the outbox, the broker and the manifest.
/// That is what makes this a demonstration rather than a wiring diagram: deleting the
/// <c>[BusTrigger]</c> leaves the emitter untouched and the manifest honest about it.
/// </para>
/// <para>
/// <strong>Why this exists rather than <see cref="PlaceOrderFlow"/> being changed.</strong> That
/// flow is the reference endpoint and its whole value is that <c>dotnet run</c> serves an order
/// with nothing behind it. Making it <c>Durable</c> would make the reference application refuse
/// every request without a database. This one refuses without a journal — which is correct, and
/// which is why <c>Program.cs</c> registers it and wires no journal by default, so the refusal
/// is a documented state rather than a broken sample.
/// </para>
/// </remarks>
[Flow("order.confirm", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "orders")]
[FlowDeadline("PT30S")]
[HttpTrigger("POST", "/api/v1/confirmations", Idempotent = true)]
public sealed partial class ConfirmOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ValidateOrder>()

            // Staged in the step's own transaction, keyed on this instance so the broker offers
            // one instance's events in the order they were staged and offers nothing across
            // instances (ADR-0018 decision 3, ADR-0037).
            .Emit<OrderPlaced>(ctx => new OrderPlaced(
                ctx.FlowInstanceId ?? ctx.CorrelationId,
                ctx.Get<ValidatedOrder>().Sku,
                ctx.Get<ValidatedOrder>().Quantity))
            .Return(ctx => new OrderPlacedResult(
                ctx.FlowInstanceId ?? ctx.CorrelationId,
                ctx.Get<ValidatedOrder>().Sku));
    }
}
