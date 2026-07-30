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
public sealed record OrderPlaced(string OrderId, string Sku, int Quantity);
