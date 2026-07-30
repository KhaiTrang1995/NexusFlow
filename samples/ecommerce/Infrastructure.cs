using System.Text.Json.Serialization;

namespace Ecommerce;

/// <summary>Source-generated serialisation, so the sample publishes with NativeAOT.</summary>
/// <remarks>
/// <para>
/// The two contracts on the wire are the flow's own input and output types. There is no
/// separate request or response DTO, and nothing to keep in step with the flow — the
/// endpoint serialises exactly what <c>PlaceOrderFlow</c> declared.
/// </para>
/// <para>
/// The camelCase policy is not decoration. Without it the wire names are the C# ones, and
/// a client sending the conventional <c>"quantity"</c> gets a <c>Quantity</c> of zero
/// rather than an error — a missing member deserialises to <c>default</c>. Here
/// <c>ValidateOrder</c> rejects it, which is the point of validating at the first step,
/// but a field whose default is plausible would have gone straight through.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OrderPlacedResult))]
[JsonSerializable(typeof(PlaceOrder))]
internal sealed partial class EcommerceJsonContext : JsonSerializerContext;

/// <summary>Stock, in memory.</summary>
/// <remarks>
/// The capabilities depend on <see cref="IInventoryStore"/>, not on this. Swapping in a
/// database changes this file and nothing else — which is the point of keeping
/// infrastructure out of the capability.
/// </remarks>
internal sealed class InMemoryInventoryStore : IInventoryStore
{
    private readonly Dictionary<string, int> _stock = new(StringComparer.Ordinal) { ["SKU-1"] = 10 };
    private readonly Dictionary<string, (string Sku, int Quantity)> _holds = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    public ValueTask<int> AvailableAsync(string sku, CancellationToken ct)
    {
        lock (_sync)
        {
            return ValueTask.FromResult(_stock.TryGetValue(sku, out var available) ? available : 0);
        }
    }

    public ValueTask ReserveAsync(string sku, int quantity, string idempotencyKey, CancellationToken ct)
    {
        lock (_sync)
        {
            // Keyed on the idempotency key, so the same request replayed reserves once.
            // This is the deduplication the capability's Idempotent = true promises.
            if (_holds.ContainsKey(idempotencyKey))
            {
                return ValueTask.CompletedTask;
            }

            _stock[sku] = _stock.GetValueOrDefault(sku) - quantity;
            _holds[idempotencyKey] = (sku, quantity);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ReleaseAsync(string idempotencyKey, CancellationToken ct)
    {
        lock (_sync)
        {
            if (_holds.Remove(idempotencyKey, out var hold))
            {
                _stock[hold.Sku] = _stock.GetValueOrDefault(hold.Sku) + hold.Quantity;
            }
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>A payment provider that always approves. Enough for a sample; nothing more.</summary>
internal sealed class AlwaysApprovesGateway : IPaymentGateway
{
    public ValueTask<string?> CaptureAsync(string reservationId, string idempotencyKey, CancellationToken ct)
        => ValueTask.FromResult<string?>("receipt-" + idempotencyKey);
}
