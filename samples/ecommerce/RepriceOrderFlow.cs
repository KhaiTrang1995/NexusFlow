using FlowX;

namespace Ecommerce;

/// <summary>
/// Reprices a basket when an order is placed: read the event, apply the price list.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This flow is started by a broker, and it is the other end of
/// <see cref="PlaceOrderFlow"/>'s <c>.Emit&lt;OrderPlaced&gt;</c>.</strong> One flow's emitted
/// event starts another flow, with no code between them naming either: the emitter stages
/// <c>order.placed</c> in its step's own transaction, the outbox drains it to a broker, and the
/// <c>[BusTrigger]</c> below is the whole of what makes this flow the consumer.
/// </para>
/// <para>
/// <strong>Two declarations here are not preferences, and <c>FLOWX1039</c> refuses either
/// without the other.</strong>
/// </para>
/// <list type="number">
/// <item><description>
/// <strong>The input is <see cref="BusMessage"/>, not <c>OrderPlaced</c>.</strong> A delivery
/// hands the body over undeserialised, because turning it into a typed contract needs a
/// <c>JsonTypeInfo</c> only generated code can name — and this is the repository's only
/// NativeAOT-published assembly, so "the host could reflect for one" is not available even in
/// principle (constraint C2). <see cref="RepriceBasket"/> is where the JSON becomes an
/// <see cref="OrderPlaced"/>, in a capability, with a serialiser context in scope and a
/// <c>Result</c> for the failure.
/// </description></item>
/// <item><description>
/// <strong>The profile is <c>Durable</c>.</strong> A broker delivers at least once — that is its
/// contract, not its defect — and the answer is that the delivery <em>derives</em> the instance
/// id it starts
/// (<a href="../../docs/adr/ADR-0035-a-delivery-names-the-instance-it-starts.md">ADR-0035</a>),
/// so the journal's primary key refuses the second one. On an <c>Ephemeral</c> flow that id is
/// inert and every redelivery would reprice the basket again, with nothing anywhere recording
/// that it had.
/// </description></item>
/// </list>
/// <para>
/// <strong>It therefore needs a journal and a broker, and this sample wires neither by
/// default.</strong> <c>dotnet run</c> still serves <c>POST /api/v1/orders</c> against an
/// in-memory inventory and a gateway that always approves, exactly as before — and this
/// subscription consumes nothing, because no <c>IBusConsumer</c> is registered. What it is not is
/// declaration-only: the subscription registration is generated into
/// <c>FlowXSubscriptions.g.cs</c> and called from <c>Program.cs</c>, so wiring a broker is one
/// line rather than a feature. <c>tests/Ecommerce.Tests/EmitStartsAFlowTests</c> is that line,
/// against a real PostgreSQL and a real Redis, and it is where the journal rows in the README
/// come from.
/// </para>
/// </remarks>
[Flow("order.reprice", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "pricing")]
[FlowDeadline("PT30S")]
[BusTrigger("order.placed", Group = "pricing")]
public sealed partial class RepriceOrderFlow : Flow<BusMessage, RepricedOrder>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<BusMessage, RepricedOrder> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<RepriceBasket>()
            .Return(ctx => ctx.Get<RepricedOrder>());
    }
}
