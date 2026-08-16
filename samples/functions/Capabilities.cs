using System.Collections.Concurrent;
using FlowX;

namespace Functions;

/// <summary>The order book this sample keeps, in memory.</summary>
/// <remarks>
/// The fixture, not the story. A serverless deployment would reach a real store here and every
/// capability below would be unchanged — which is the point <c>samples/event-driven</c> makes
/// about transports and this one makes about hosts.
/// </remarks>
public sealed class InMemoryOrderBook
{
    private readonly ConcurrentDictionary<string, int> _orders = new(StringComparer.Ordinal);

    /// <summary>Records an accepted order.</summary>
    /// <param name="orderId">The order.</param>
    /// <param name="quantity">How many units.</param>
    public void Accept(string orderId, int quantity) => _orders[orderId] = quantity;

    /// <summary>How many orders are on the book.</summary>
    public int Count => _orders.Count;

    /// <summary>Every order on the book, for a test to read.</summary>
    public IReadOnlyDictionary<string, int> Orders => _orders;
}

/// <summary>
/// Accepts an order and puts it on the book.
/// </summary>
/// <remarks>
/// Reached by the generated <c>[HttpTrigger]</c> entry point, which hands it the deserialised
/// body and nothing else. The capability has no idea it is running in a worker.
/// </remarks>
[Capability("orders.accept", Version = "1.0.0",
    Authorization = Authorization.Internal, Idempotent = true)]
public sealed class AcceptOrder : ICapability<PlaceOrder, OrderAccepted>
{
    private readonly InMemoryOrderBook _book;

    /// <summary>Creates the capability over the book.</summary>
    /// <param name="book">Where an accepted order is recorded.</param>
    public AcceptOrder(InMemoryOrderBook book) => _book = book;

    /// <inheritdoc />
    public ValueTask<Result<OrderAccepted>> ExecuteAsync(
        PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Quantity <= 0)
        {
            // A business refusal is a Result and not an exception (ADR-0007), and the category
            // is what the generated entry point answers 400 with — through
            // ErrorCategory.ToHttpStatusCode, the same map the ASP.NET host uses.
            return ValueTask.FromResult(Result.Fail<OrderAccepted>(new Error(
                "orders.quantity_invalid",
                $"Quantity must be greater than zero, and '{input.OrderId}' asked for " +
                $"{input.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture)}.",
                ErrorCategory.Validation)));
        }

        _book.Accept(input.OrderId, input.Quantity);

        return ValueTask.FromResult(Result.Ok(new OrderAccepted(input.OrderId, "accepted")));
    }
}

/// <summary>
/// Takes the ordered units out of stock.
/// </summary>
/// <remarks>
/// Reached by the generated <c>[ServiceBusTrigger]</c> entry point, through
/// <c>FlowBusScan.AdmitAsync</c> — the same seam the pull sweep goes through, so a redelivery
/// is refused by the journal's primary key here exactly as it is on a host that polls
/// (ADR-0035).
/// </remarks>
[Capability("stock.reserve", Version = "1.0.0",
    Authorization = Authorization.Internal, Idempotent = true)]
public sealed class ReserveStock : ICapability<BusMessage, StockReserved>
{
    /// <inheritdoc />
    public ValueTask<Result<StockReserved>> ExecuteAsync(
        BusMessage input, CapabilityContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        // The payload is deserialised here rather than bound by the trigger, which is what
        // FLOWX1039 requires: a delivery has only the message to give a flow. A body that is
        // not this contract is a business refusal and not an exception (ADR-0007) — and the
        // generated entry point turns that into a completed message rather than a poison one,
        // because the flow ran and decided (ADR-0036).
        var placed = input.Payload is null
            ? null
            : System.Text.Json.JsonSerializer.Deserialize(
                input.Payload, FunctionsJson.Default.OrderPlaced);

        if (placed is null)
        {
            return ValueTask.FromResult(Result.Fail<StockReserved>(new Error(
                "stock.payload_unreadable",
                $"Message '{input.EventId}' on '{input.Topic}' carried no readable " +
                $"{nameof(OrderPlaced)} body.",
                ErrorCategory.Validation)));
        }

        return ValueTask.FromResult(Result.Ok(new StockReserved(placed.OrderId, placed.Quantity)));
    }
}

/// <summary>
/// Counts what is on the book, once a night.
/// </summary>
/// <remarks>
/// <strong>It binds <see cref="ScheduledFire"/> and never a clock</strong> — <c>FLOWX1007</c>
/// refuses the alternative. The instant arrives as data, journalled on
/// <c>flow_instance.input</c>, which is what makes a firing the platform's timer released at
/// 02:00:41 read the same occurrence a punctual one would have.
/// </remarks>
[Capability("orders.reconcile", Version = "1.0.0",
    Authorization = Authorization.Internal, Idempotent = true)]
public sealed class ReconcileOrders : ICapability<ScheduledFire, ReconciliationReport>
{
    private readonly InMemoryOrderBook _book;

    /// <summary>Creates the capability over the book.</summary>
    /// <param name="book">What is counted.</param>
    public ReconcileOrders(InMemoryOrderBook book) => _book = book;

    /// <inheritdoc />
    public ValueTask<Result<ReconciliationReport>> ExecuteAsync(
        ScheduledFire input, CapabilityContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(Result.Ok(new ReconciliationReport(_book.Count)));
}
