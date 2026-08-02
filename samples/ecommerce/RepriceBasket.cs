using System.Text.Json;
using FlowX;

namespace Ecommerce;

/// <summary>Turns a delivered <c>order.placed</c> event into a repriced basket.</summary>
/// <remarks>
/// <para>
/// <strong>This is the step that deserialises, and that is not an accident of layering.</strong>
/// <see cref="RepriceOrderFlow"/> receives a <see cref="BusMessage"/> whose body is JSON the host
/// deliberately did not deserialise: doing so needs a <c>JsonTypeInfo</c> only generated code can
/// name, and reflecting for one is what constraint C2 forbids in a NativeAOT assembly. A
/// capability is where a serialiser context is in scope, and it is where a malformed body is a
/// <c>Result</c> failure rather than an exception out of a background service.
/// </para>
/// <para>
/// <strong><c>Authorization.Internal</c> is the honest stance for a broker-started step.</strong>
/// Nobody called: a delivery carries no principal, so <c>Authenticated</c> or <c>Permission</c>
/// would refuse every message the moment authorisation runs. <c>Internal</c> says this capability
/// is reachable only from inside the platform, which is exactly what a subscription is.
/// </para>
/// <para>
/// <strong><c>Idempotent = true</c> is a promise this keeps trivially</strong> — it computes and
/// writes nothing. That matters because a bus-triggered flow is the one place at-least-once
/// delivery meets a step: the delivery is deduplicated above, by the journal, but a step retried
/// inside one run is retried by the policy engine and must be safe either way.
/// </para>
/// </remarks>
[Capability("order.reprice", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true)]
public sealed class RepriceBasket : ICapability<BusMessage, RepricedOrder>
{
    /// <summary>Reprices the order the event names.</summary>
    /// <param name="input">The delivered message.</param>
    /// <param name="ctx">Correlation, deadline and identity.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The repriced basket, or why the body could not be read.</returns>
    public ValueTask<Result<RepricedOrder>> ExecuteAsync(
        BusMessage input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Payload is not { Length: > 0 } body)
        {
            return ValueTask.FromResult(
                Result.Fail<RepricedOrder>(OrderErrors.EventHasNoBody(input.EventId)));
        }

        var placed = JsonSerializer.Deserialize(body, EcommerceJournalJsonContext.Default.OrderPlaced);

        return ValueTask.FromResult(placed is null
            ? Result.Fail<RepricedOrder>(OrderErrors.EventHasNoBody(input.EventId))

            // A real price list lives behind a port, like IInventoryStore. A constant keeps this
            // sample about the trigger.
            : Result.Ok(new RepricedOrder(placed.OrderId, placed.Sku, placed.Quantity * 9.99m)));
    }
}
