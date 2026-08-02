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
/// <strong>The <c>[HttpTrigger]</c> below <em>is</em> the endpoint.</strong> One reading of
/// it produces both the <c>triggers</c> block of <c>flowx.manifest.json</c> and the route
/// <c>app.MapFlowX()</c> registers, so the address this flow publishes and the address it
/// answers on are one string rather than two kept in step. A declared address that nothing
/// serves would be exactly the documented-but-not-produced claim the manifest exists to
/// eliminate; there is now no way to write one.
/// </para>
/// <para>
/// <strong>This flow is compensable and ephemeral, and <c>FLOWX1012</c> is right to say
/// so.</strong> The unwind stack lives in the memory of the process that took the request,
/// so a node that dies between the reservation and the end of the flow leaves the hold
/// standing with nothing left to release it. The rule is suppressed below because the
/// answer is <c>Ephemeral</c>, not because the finding is wrong: <c>docs/DEBT.md</c> draws
/// exactly this line — a trade recorded in an ADR is a decision, not debt — and the ADR is
/// <a href="../../docs/adr/ADR-0003-execution-profiles.md">ADR-0003</a>.
/// </para>
/// <para>
/// The reason is this sample's whole value. <c>dotnet run</c> serves an order with nothing
/// behind it, and the only journal FlowX ships is PostgreSQL. Declaring <c>Durable</c> here
/// would buy a crash-safe unwind for an inventory store that is a dictionary and a payment
/// gateway that always approves, at the price of a reference application that cannot place
/// an order without a database. What the choice costs is in the README's known gaps rather
/// than left to be inferred, and the argument in full is on
/// <a href="../../docs/diagnostics/FLOWX1012.md">the diagnostic's page</a>.
/// </para>
/// <para>
/// <strong>The <c>[AgentTrigger]</c> is the second transport, and it is one line for the same
/// reason the first one is.</strong> An agent reaches this flow as the MCP tool
/// <c>order_place</c> over <c>POST /mcp</c>; the name, the description, the declared side
/// effects and the confirmation requirement are projected out of <c>flowx.manifest.json</c>,
/// so what a model is told and what the build published are one document. Neither the flow
/// body nor any capability changes — including <c>payment.capture</c>'s
/// <c>payment.write</c> stance, which refuses an under-privileged agent at the same step and
/// with the same unwind it refuses an under-privileged HTTP caller.
/// </para>
/// <para>
/// <strong>And it is what proves <c>plugins/FlowX.Mcp</c> publishes under NativeAOT.</strong>
/// Every test project sets <c>IsAotCompatible=false</c> (<c>Directory.Build.props</c>), so no
/// test can make that statement; this project is the repository's only AOT-published assembly
/// (constraint C2) and CI publishes it, which makes the agent surface's AOT-cleanliness a
/// property of the build rather than a claim.
/// </para>
/// </remarks>
#pragma warning disable FLOWX1012 // Deliberate: an ephemeral saga, argued in docs/diagnostics/FLOWX1012.md
[Flow("order.place", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "orders")]
#pragma warning restore FLOWX1012
[FlowDeadline("PT30S")]
[HttpTrigger("POST", "/api/v1/orders", Idempotent = true)]
[AgentTrigger(
    Description =
        "Place an order for a quantity of a stock-keeping unit and capture payment for it. " +
        "Reserves inventory before charging, and releases the reservation if the charge fails.")]
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
            //   The step is in the plan and in the manifest, and this flow does
            //   not publish it. The whole chain exists now — the generated
            //   dispatcher builds the body, the engine stages it in the step's
            //   own transaction, PostgresOutboxPublisher drains it — and none of
            //   it applies here, because this flow is deliberately Ephemeral and
            //   so keeps no transaction to stage into. That choice is argued on
            //   docs/diagnostics/FLOWX1012.md; this suppression is its price, and
            //   it stays visible because this is the reference sample.
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
