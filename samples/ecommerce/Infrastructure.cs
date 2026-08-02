using System.Text.Json.Serialization;
using FlowX;

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

/// <summary>Serialisation for the durable half of the sample.</summary>
/// <remarks>
/// <para>
/// <strong>Separate from <see cref="EcommerceJsonContext"/> because it is not on the wire.</strong>
/// That context is the HTTP endpoint's and carries the camelCase policy a client expects. These
/// three are journal payloads: <c>OrderPlaced</c> is the event <c>PlaceOrderFlow</c> emits and
/// the outbox stages, <c>BusMessage</c> is what a delivery journals as
/// <c>flow_instance.input</c>, and <c>RepricedOrder</c> is what the consuming flow's step
/// commits. A durable flow cannot record any of them without a context that declares them, and
/// <c>FLOWX1006</c> is the rule that says so rather than the journal discovering it at run time.
/// </para>
/// <para>
/// Public, unlike the endpoint's, because <c>tests/Ecommerce.Tests</c> hands it to the host it
/// builds against a real PostgreSQL — the sample declares the contracts and the test supplies
/// the infrastructure, which is the split constraint C2 forces on the only NativeAOT-published
/// assembly here.
/// </para>
/// <para>
/// <strong>No contract appears in both contexts, and the compiler enforces it.</strong>
/// <c>FLOWX1006</c> asks for a <em>single</em> context declaring a journalled contract, and
/// <c>EndpointEmitter</c> emits the no-argument <c>MapFlowX()</c> only where one context declares
/// both of a flow's wire contracts. Declaring <c>PlaceOrder</c> in both was the first draft of
/// this file and it failed twice over — a durable flow that could not be journalled, and an
/// endpoint registration that would not compile.
/// </para>
/// </remarks>
[JsonSerializable(typeof(OrderPlaced))]
[JsonSerializable(typeof(BusMessage))]
[JsonSerializable(typeof(RepricedOrder))]
[JsonSerializable(typeof(OrderProjection))]
[JsonSerializable(typeof(ValidatedOrder))]
public sealed partial class EcommerceJournalJsonContext : JsonSerializerContext;

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
