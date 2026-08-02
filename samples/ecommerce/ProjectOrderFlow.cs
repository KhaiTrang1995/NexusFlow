using System.Text.Json;
using FlowX;

namespace Ecommerce;

/// <summary>
/// Projects an order into a read model when one is placed — started by the outbox itself,
/// with no broker anywhere in the path.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This flow and <see cref="RepriceOrderFlow"/> observe the same event and differ in one
/// attribute.</strong> That is the point of it being here. <see cref="ConfirmOrderFlow"/> stages
/// <c>order.placed</c> in its step's own transaction; the repricing flow reaches it through
/// <c>PostgresOutboxPublisher</c> and a broker, and this one reads the same staged rows directly
/// from <c>outbox_event</c>
/// (<a href="../../docs/adr/ADR-0047-a-change-trigger-observes-the-outbox.md">ADR-0047</a>). Same
/// input contract, same capability shape, same profile — one attribute, and no broker.
/// </para>
/// <para>
/// <strong>The two declarations <c>FLOWX1041</c> refuses without are the bus trigger's two</strong>
/// — <see cref="BusMessage"/> as the input, because a change is an outbox row and has nothing
/// else to give, and <c>Durable</c>, because the change derives the instance id it starts
/// (<a href="../../docs/adr/ADR-0049-a-change-names-the-instance-it-starts.md">ADR-0049</a>) and
/// on an <c>Ephemeral</c> flow that id is inert. The second matters here for a reason of its own:
/// a change feed commits its cursor <em>after</em> the flows have run
/// (<a href="../../docs/adr/ADR-0048-a-change-feed-advances-a-cursor.md">ADR-0048</a>), which is
/// the only order that cannot lose work, so a crash in between re-offers the change.
/// </para>
/// <para>
/// <strong>It emits nothing, and that is a rule rather than a preference.</strong> A flow that
/// staged <c>order.placed</c> while observing it would be offered its own event, for ever, with
/// every instance legitimately distinct so nothing refuses it — <c>FlowChangeCatalog.Add</c>
/// throws at registration rather than letting the pod become ready.
/// </para>
/// <para>
/// <strong>It needs a journal and a change feed, and this sample wires neither by default</strong>
/// — <see cref="RepriceOrderFlow"/>'s position exactly. <c>dotnet run</c> still serves
/// <c>POST /api/v1/orders</c> with no infrastructure; wiring the feed is one
/// <c>AddFlowXPostgresChangeFeed</c> call.
/// <c>tests/Ecommerce.Tests/ChangeStartsAFlowTests</c> makes it, against a real PostgreSQL and
/// nothing else.
/// </para>
/// </remarks>
[Flow("order.project", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "orders")]
[FlowDeadline("PT30S")]
[ChangeTrigger("order.placed", Group = "projection")]
public sealed partial class ProjectOrderFlow : Flow<BusMessage, OrderProjection>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<BusMessage, OrderProjection> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            .Step<ProjectOrder>()
            .Return(ctx => ctx.Get<OrderProjection>());
    }
}

/// <summary>Turns an observed <c>order.placed</c> change into a read-model row.</summary>
/// <remarks>
/// <see cref="RepriceBasket"/>'s shape and its three reasons, unchanged: the capability is where
/// a serialiser context is in scope, <c>Authorization.Internal</c> is the honest stance for a step
/// nobody called, and <c>Idempotent = true</c> is a promise it keeps by computing and writing
/// nothing.
/// </remarks>
[Capability("order.project", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true)]
public sealed class ProjectOrder : ICapability<BusMessage, OrderProjection>
{
    /// <summary>Projects the order the change names.</summary>
    /// <param name="input">The observed change.</param>
    /// <param name="ctx">Correlation, deadline and identity.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The projected row, or why the body could not be read.</returns>
    public ValueTask<Result<OrderProjection>> ExecuteAsync(
        BusMessage input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Payload is not { Length: > 0 } body)
        {
            return ValueTask.FromResult(
                Result.Fail<OrderProjection>(OrderErrors.EventHasNoBody(input.EventId)));
        }

        var placed = JsonSerializer.Deserialize(body, EcommerceJournalJsonContext.Default.OrderPlaced);

        return ValueTask.FromResult(placed is null
            ? Result.Fail<OrderProjection>(OrderErrors.EventHasNoBody(input.EventId))
            : Result.Ok(new OrderProjection(placed.OrderId, placed.Sku, placed.Quantity)));
    }
}
