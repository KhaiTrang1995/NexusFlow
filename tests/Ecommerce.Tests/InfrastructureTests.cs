using Ecommerce;
using Shouldly;
using Xunit;

namespace Ecommerce.Tests;

/// <summary>
/// The sample's in-memory adapters.
/// </summary>
/// <remarks>
/// These are the sample's infrastructure, not its story — but they carry the behaviour
/// that makes the idempotency claim in the README true, and a claim demonstrated only
/// by a <c>curl</c> in a terminal is a claim nothing protects.
/// </remarks>
public sealed class InfrastructureTests
{
    [Fact]
    public async Task ReservingHoldsStock()
    {
        var store = new InMemoryInventoryStore();

        await store.ReserveAsync("SKU-1", 3, "key-1", TestContext.Current.CancellationToken);

        (await store.AvailableAsync("SKU-1", TestContext.Current.CancellationToken)).ShouldBe(7);
    }

    [Fact]
    public async Task ReservingTwiceUnderOneKeyHoldsOnce()
    {
        // This is what the capability's Idempotent = true promises, and what makes a
        // retried request safe. Without it, a client that retries on a timeout is
        // charged twice for stock it asked for once.
        var store = new InMemoryInventoryStore();

        await store.ReserveAsync("SKU-1", 3, "key-1", TestContext.Current.CancellationToken);
        await store.ReserveAsync("SKU-1", 3, "key-1", TestContext.Current.CancellationToken);

        (await store.AvailableAsync("SKU-1", TestContext.Current.CancellationToken)).ShouldBe(7);
    }

    [Fact]
    public async Task DifferentKeysHoldSeparately()
    {
        var store = new InMemoryInventoryStore();

        await store.ReserveAsync("SKU-1", 3, "key-1", TestContext.Current.CancellationToken);
        await store.ReserveAsync("SKU-1", 2, "key-2", TestContext.Current.CancellationToken);

        (await store.AvailableAsync("SKU-1", TestContext.Current.CancellationToken)).ShouldBe(5);
    }

    [Fact]
    public async Task ReleasingGivesTheStockBack()
    {
        var store = new InMemoryInventoryStore();
        await store.ReserveAsync("SKU-1", 4, "key-1", TestContext.Current.CancellationToken);

        await store.ReleaseAsync("key-1", TestContext.Current.CancellationToken);

        (await store.AvailableAsync("SKU-1", TestContext.Current.CancellationToken)).ShouldBe(10);
    }

    [Fact]
    public async Task ReleasingAnUnknownKeyIsHarmless()
    {
        // Compensation is best-effort and may run against a step that never took hold.
        // Releasing something that was never reserved must not invent stock.
        var store = new InMemoryInventoryStore();

        await store.ReleaseAsync("never-seen", TestContext.Current.CancellationToken);

        (await store.AvailableAsync("SKU-1", TestContext.Current.CancellationToken)).ShouldBe(10);
    }

    [Fact]
    public async Task ReleasingTwiceOnlyRestoresOnce()
    {
        var store = new InMemoryInventoryStore();
        await store.ReserveAsync("SKU-1", 4, "key-1", TestContext.Current.CancellationToken);

        await store.ReleaseAsync("key-1", TestContext.Current.CancellationToken);
        await store.ReleaseAsync("key-1", TestContext.Current.CancellationToken);

        (await store.AvailableAsync("SKU-1", TestContext.Current.CancellationToken)).ShouldBe(10);
    }

    [Fact]
    public async Task AnUnknownSkuHasNoStock()
    {
        var store = new InMemoryInventoryStore();

        (await store.AvailableAsync("SKU-NOPE", TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task TheGatewayReturnsAReceiptDerivedFromTheKey()
    {
        var receipt = await new AlwaysApprovesGateway()
            .CaptureAsync("res-1", "key-1", TestContext.Current.CancellationToken);

        // Derived rather than random, so a replayed capture reports the same receipt.
        receipt.ShouldBe("receipt-key-1");
    }
}
