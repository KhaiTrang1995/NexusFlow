using FlowX;

namespace Ecommerce;

/// <summary>What a caller asks for.</summary>
/// <param name="Sku">The item to order.</param>
/// <param name="Quantity">How many.</param>
/// <param name="PaymentToken">A tokenised payment instrument, never a card number.</param>
public sealed record PlaceOrder(string Sku, int Quantity, [property: Sensitive] string PaymentToken);

/// <summary>An order that passed validation.</summary>
public sealed record ValidatedOrder(string Sku, int Quantity, decimal Total);

/// <summary>Stock held for an order.</summary>
public sealed record Reservation(string Sku, int Quantity, string ReservationId);

/// <summary>Money taken.</summary>
public sealed record Payment(string ReservationId, decimal Amount, string ReceiptId);

/// <summary>What the caller gets back.</summary>
public sealed record OrderPlacedResult(string OrderId, string ReceiptId);

/// <summary>Published once the order is placed.</summary>
/// <remarks>
/// <para>
/// <strong>The one contract in this repository that declares its own schema version.</strong>
/// Absent <c>[EventSchema]</c> an event publishes <c>1.0.0</c>, which is what every event here
/// published while the value was a compiler constant. This one declares a major above the
/// default so the declaration is visible in all three places it lands: <c>event.schemaVersion</c>
/// in <c>flowx.manifest.baseline.json</c>, the <c>schema_version</c> column of the outbox rows
/// <c>EmitStartsAFlowTests</c> reads, and <c>FLOWX-DIFF-020</c>'s key when the baseline is
/// compared against a build that does not declare it.
/// </para>
/// <para>
/// The version is a property of this record, not of either <c>.Emit</c> call site — both
/// <see cref="PlaceOrderFlow"/> and <see cref="ConfirmOrderFlow"/> emit it and the catalogue
/// carries one entry, because the schema evolves with the type.
/// </para>
/// </remarks>
[EventSchema("2.0.0")]
public sealed record OrderPlaced(string OrderId, string Sku, int Quantity);

/// <summary>What repricing an order produced.</summary>
/// <remarks>
/// The output of <see cref="RepriceOrderFlow"/>, which is started by a broker rather than by a
/// caller — so nobody is waiting for this value. It is journalled on the instance and is what a
/// replay or <c>flowx replay --mode inspect</c> shows, which is the whole audience a
/// bus-triggered flow's return has.
/// </remarks>
public sealed record RepricedOrder(string OrderId, string Sku, decimal Total);

/// <summary>The read-model row an observed order produced.</summary>
/// <remarks>
/// The output of <see cref="ProjectOrderFlow"/>, which is started by the outbox rather than by a
/// caller — so <see cref="RepricedOrder"/>'s note applies unchanged: nobody is waiting for this
/// value, and the journal row is its whole audience.
/// </remarks>
public sealed record OrderProjection(string OrderId, string Sku, int Quantity);
