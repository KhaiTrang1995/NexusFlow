using FlowX;

namespace Ecommerce;

/// <summary>Errors this application can produce.</summary>
/// <remarks>
/// Declared in one place so the codes are greppable and so two capabilities cannot
/// invent two spellings of the same condition. Each one reaches the manifest, the
/// generated OpenAPI responses and the RFC 7807 <c>type</c> URI.
/// </remarks>
public static class OrderErrors
{
    /// <summary>The requested quantity is not positive.</summary>
    public static Error InvalidQuantity(int quantity) =>
        new Error("order.invalid_quantity", $"Quantity must be positive; got {quantity}.", ErrorCategory.Validation)
            .With("quantity", quantity);

    /// <summary>There is not enough stock.</summary>
    public static Error OutOfStock(string sku, int available) =>
        new Error("inventory.out_of_stock", $"'{sku}' has {available} in stock.", ErrorCategory.Conflict)
            .With("sku", sku)
            .With("available", available);

    /// <summary>A delivered event carried no body to reprice from.</summary>
    /// <remarks>
    /// A <c>Result</c> failure and not an exception, which is what decides what the broker is
    /// told: a flow that ran and failed is <em>acknowledged</em>
    /// (<a href="../../docs/adr/ADR-0036-a-message-is-acknowledged-when-its-flow-is-journalled.md">ADR-0036</a>),
    /// because it happened. Throwing instead would leave the message pending and redeliver a body
    /// that will never be any different.
    /// </remarks>
    public static Error EventHasNoBody(Guid eventId) =>
        new Error("order.event_has_no_body", "The delivered event carried no body.", ErrorCategory.Validation)
            .With("eventId", eventId.ToString("d"));

    /// <summary>The payment instrument was declined.</summary>
    public static Error PaymentDeclined(string reason) =>
        new Error("payment.declined", $"The payment was declined: {reason}.", ErrorCategory.Conflict)
            .With("reason", reason);
}

/// <summary>Checks the order is well formed and prices it.</summary>
/// <remarks>
/// A read. No side effects, safe to retry, and — because the class references no
/// transport type and calls no other capability — testable by constructing it and
/// calling the method.
/// </remarks>
[Capability("order.validate", Version = "1.0.0",
    Authorization = Authorization.Authenticated,
    Idempotent = true)]
public sealed class ValidateOrder : ICapability<PlaceOrder, ValidatedOrder>
{
    private const decimal UnitPrice = 19.99m;

    /// <inheritdoc />
    public ValueTask<Result<ValidatedOrder>> ExecuteAsync(
        PlaceOrder input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Quantity <= 0)
        {
            return ValueTask.FromResult(
                Result.Fail<ValidatedOrder>(OrderErrors.InvalidQuantity(input.Quantity)));
        }

        return ValueTask.FromResult(Result.Ok(
            new ValidatedOrder(input.Sku, input.Quantity, UnitPrice * input.Quantity)));
    }
}

/// <summary>Holds stock for the order.</summary>
/// <remarks>
/// Idempotent and effectful: retrying with the same idempotency key is safe, which is
/// why a retry policy may be attached — but caching it is a build error (FLOWX1018),
/// because a cache hit would report a reservation that never happened.
/// </remarks>
[Capability("inventory.reserve", Version = "1.0.0",
    Authorization = Authorization.Authenticated,
    Idempotent = true,
    SideEffects = ["inventory-ledger"])]
public sealed class ReserveInventory : ICapability<ValidatedOrder, Reservation>
{
    private readonly IInventoryStore _store;

    /// <summary>Creates the capability.</summary>
    public ReserveInventory(IInventoryStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<Reservation>> ExecuteAsync(
        ValidatedOrder input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var available = await _store.AvailableAsync(input.Sku, ct).ConfigureAwait(false);

        if (available < input.Quantity)
        {
            return OrderErrors.OutOfStock(input.Sku, available);
        }

        // The idempotency key comes from the context, so a retry reserves once. Reading
        // it from anywhere else — a new Guid, the clock — would break replay.
        await _store.ReserveAsync(input.Sku, input.Quantity, ctx.IdempotencyKey, ct).ConfigureAwait(false);

        return new Reservation(input.Sku, input.Quantity, ctx.IdempotencyKey);
    }
}

/// <summary>Undoes a reservation. The business inverse of <see cref="ReserveInventory"/>.</summary>
[Capability("inventory.release", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["inventory-ledger"])]
public sealed class ReleaseInventory : ICapability<ValidatedOrder, Reservation>
{
    private readonly IInventoryStore _store;

    /// <summary>Creates the capability.</summary>
    public ReleaseInventory(IInventoryStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<Reservation>> ExecuteAsync(
        ValidatedOrder input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        await _store.ReleaseAsync(ctx.IdempotencyKey, ct).ConfigureAwait(false);

        return new Reservation(input.Sku, input.Quantity, ctx.IdempotencyKey);
    }
}

/// <summary>Takes the money.</summary>
/// <remarks>
/// <strong>Not idempotent</strong>, and it says so. That single declaration is what
/// makes attaching a retry policy a build error (FLOWX1014) — retrying a capture is a
/// duplicate charge, and the compiler refusing is cheaper than the refund process.
/// </remarks>
[Capability("payment.capture", Version = "2.1.0",
    Authorization = Authorization.Permission, Permission = "payment.write",
    Idempotent = false,
    SideEffects = ["payment-gateway", "ledger"])]
public sealed class CapturePayment : ICapability<Reservation, Payment>
{
    private readonly IPaymentGateway _gateway;

    /// <summary>Creates the capability.</summary>
    public CapturePayment(IPaymentGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        _gateway = gateway;
    }

    /// <inheritdoc />
    public async ValueTask<Result<Payment>> ExecuteAsync(
        Reservation input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var receipt = await _gateway
            .CaptureAsync(input.ReservationId, ctx.IdempotencyKey, ct)
            .ConfigureAwait(false);

        return receipt is null
            ? OrderErrors.PaymentDeclined("insufficient funds")
            : new Payment(input.ReservationId, 0m, receipt);
    }
}

/// <summary>Stock the application reads and writes.</summary>
public interface IInventoryStore
{
    /// <summary>How many units are available.</summary>
    ValueTask<int> AvailableAsync(string sku, CancellationToken ct);

    /// <summary>Holds stock under an idempotency key.</summary>
    ValueTask ReserveAsync(string sku, int quantity, string idempotencyKey, CancellationToken ct);

    /// <summary>Releases a hold.</summary>
    ValueTask ReleaseAsync(string idempotencyKey, CancellationToken ct);
}

/// <summary>The payment provider.</summary>
public interface IPaymentGateway
{
    /// <summary>Captures a payment, returning a receipt id or <c>null</c> when declined.</summary>
    ValueTask<string?> CaptureAsync(string reservationId, string idempotencyKey, CancellationToken ct);
}
