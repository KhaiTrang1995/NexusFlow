using System.Text.Json.Serialization;
using FlowX;

namespace Functions;

/// <summary>An order somebody placed, as the HTTP entry point receives it.</summary>
/// <param name="OrderId">The caller's id for the order, which is also the partition key.</param>
/// <param name="Sku">What was bought.</param>
/// <param name="Quantity">How many.</param>
public sealed record PlaceOrder(string OrderId, string Sku, int Quantity);

/// <summary>What the caller is told.</summary>
/// <param name="OrderId">The order.</param>
/// <param name="Status">What became of it.</param>
public sealed record OrderAccepted(string OrderId, string Status);

/// <summary>An order that was accepted, as it reaches the broker.</summary>
/// <param name="OrderId">The order.</param>
/// <param name="Sku">What was bought.</param>
/// <param name="Quantity">How many.</param>
public sealed record OrderPlaced(string OrderId, string Sku, int Quantity);

/// <summary>What the reservation flow answers with.</summary>
/// <param name="OrderId">The order.</param>
/// <param name="Reserved">How many units were taken out of stock.</param>
public sealed record StockReserved(string OrderId, int Reserved);

/// <summary>What the nightly sweep answers with.</summary>
/// <param name="Counted">How many orders it looked at.</param>
public sealed record ReconciliationReport(int Counted);

/// <summary>
/// Every contract this worker puts on a wire, declared once.
/// </summary>
/// <remarks>
/// <strong>Source-generated, and a worker is where that stops being a preference.</strong> An
/// Azure Functions worker is published trimmed; a reflection-based serialiser is exactly what a
/// trimmed publish removes, and it removes it silently — the failure is a run-time
/// <c>NotSupportedException</c> in the cloud rather than a build error here. The generated HTTP
/// entry points name this context, and <c>FlowPushSeams.HttpAsync</c> fails at start-up naming
/// the missing type if a contract is not declared on it.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PlaceOrder))]
[JsonSerializable(typeof(OrderAccepted))]
[JsonSerializable(typeof(OrderPlaced))]
[JsonSerializable(typeof(StockReserved))]
[JsonSerializable(typeof(ReconciliationReport))]
[JsonSerializable(typeof(ScheduledFire))]

// The bus-triggered flow binds BusMessage, so the journal records one — FLOWX1006 refuses a
// durable flow whose state bag holds a type no context declares, rather than falling back to
// reflection and failing after a trimmed publish.
[JsonSerializable(typeof(BusMessage))]
public sealed partial class FunctionsJson : JsonSerializerContext
{
}
