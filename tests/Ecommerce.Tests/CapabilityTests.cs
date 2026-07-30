using Ecommerce;
using FlowX;
using Shouldly;
using Xunit;

namespace Ecommerce.Tests;

/// <summary>
/// Quality goal Q2: a capability is testable by constructing it and calling it.
/// </summary>
/// <remarks>
/// <para>
/// There is no host in this file. No <c>WebApplicationFactory</c>, no service provider,
/// no in-memory server, no attribute that a runner has to understand — a capability is a
/// class with a method, and these tests treat it as one. That is the claim Q2 makes, and
/// this file is what makes the claim checkable rather than aspirational.
/// </para>
/// <para>
/// What that buys is not tidiness. It is that the business rules of this application can
/// be exercised at the speed of a method call, so there is no incentive to skip testing
/// them — which is the actual reason untested business logic ships.
/// </para>
/// </remarks>
public sealed class CapabilityTests
{
    private static readonly CapabilityContext Context = new FixedContext("test-key");

    [Fact]
    public async Task ValidateOrderPricesAWellFormedOrder()
    {
        var result = await new ValidateOrder()
            .ExecuteAsync(new PlaceOrder("SKU-1", 3, "tok"), Context, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Sku.ShouldBe("SKU-1");
        result.Value.Quantity.ShouldBe(3);
        result.Value.Total.ShouldBe(59.97m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ValidateOrderRejectsANonPositiveQuantity(int quantity)
    {
        var result = await new ValidateOrder()
            .ExecuteAsync(new PlaceOrder("SKU-1", quantity, "tok"), Context, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("order.invalid_quantity");
        result.Error.Category.ShouldBe(ErrorCategory.Validation);
    }

    [Fact]
    public async Task ReserveInventoryHoldsStock()
    {
        var store = new FakeInventory(available: 10);

        var result = await new ReserveInventory(store)
            .ExecuteAsync(new ValidatedOrder("SKU-1", 4, 40m), Context, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ReservationId.ShouldBe("test-key");
        store.Reserved.ShouldBe(4);
    }

    [Fact]
    public async Task ReserveInventoryRefusesWhenStockIsShort()
    {
        var store = new FakeInventory(available: 2);

        var result = await new ReserveInventory(store)
            .ExecuteAsync(new ValidatedOrder("SKU-1", 4, 40m), Context, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("inventory.out_of_stock");
        result.Error.Category.ShouldBe(ErrorCategory.Conflict);

        // Nothing was held, so there is nothing for the compensation to undo. A failed
        // reservation that still took stock is the bug this asserts against.
        store.Reserved.ShouldBe(0);
    }

    [Fact]
    public async Task ReserveInventoryUsesTheContextIdempotencyKey()
    {
        // The key is what makes a retry safe. A capability that generated its own would
        // reserve twice on the second attempt, and nothing else in the system would notice.
        var store = new FakeInventory(available: 10);
        var capability = new ReserveInventory(store);
        var order = new ValidatedOrder("SKU-1", 1, 10m);

        await capability.ExecuteAsync(order, Context, TestContext.Current.CancellationToken);
        await capability.ExecuteAsync(order, Context, TestContext.Current.CancellationToken);

        store.Keys.ShouldBe(["test-key", "test-key"]);
    }

    [Fact]
    public async Task ReleaseInventoryUndoesTheHold()
    {
        var store = new FakeInventory(available: 10);
        await new ReserveInventory(store)
            .ExecuteAsync(new ValidatedOrder("SKU-1", 4, 40m), Context, TestContext.Current.CancellationToken);

        var result = await new ReleaseInventory(store)
            .ExecuteAsync(new ValidatedOrder("SKU-1", 4, 40m), Context, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        store.Released.ShouldBe("test-key");
    }

    [Fact]
    public async Task CapturePaymentReturnsAReceipt()
    {
        var result = await new CapturePayment(new FakeGateway("receipt-9"))
            .ExecuteAsync(new Reservation("SKU-1", 1, "res-1"), Context, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ReceiptId.ShouldBe("receipt-9");
    }

    [Fact]
    public async Task CapturePaymentReportsADecline()
    {
        var result = await new CapturePayment(new FakeGateway(null))
            .ExecuteAsync(new Reservation("SKU-1", 1, "res-1"), Context, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("payment.declined");
    }

    [Fact]
    public void EveryCapabilityRejectsANullDependency()
    {
        Should.Throw<ArgumentNullException>(() => new ReserveInventory(null!));
        Should.Throw<ArgumentNullException>(() => new ReleaseInventory(null!));
        Should.Throw<ArgumentNullException>(() => new CapturePayment(null!));
    }

    /// <summary>
    /// A context, written out by hand. Not a mock, and not a framework.
    /// </summary>
    /// <remarks>
    /// Fixed values throughout, deliberately: the clock, the identifiers and the
    /// randomness a capability is allowed to use all come from here, so pinning them is
    /// what makes these tests deterministic. That is the same property durable replay
    /// depends on, which is why the abstraction exists at all.
    /// <para>
    /// Nine members is more ceremony than Q2 should cost. A supported test context
    /// belongs in the platform — see PLAN.md, WP-12.
    /// </para>
    /// </remarks>
    private sealed class FixedContext(string idempotencyKey) : CapabilityContext
    {
        public override string IdempotencyKey { get; } = idempotencyKey;

        public override string CorrelationId => "test-correlation";

        public override string? FlowInstanceId => null;

        public override string CapabilityId => "test.capability";

        public override string? TenantId => null;

        public override DateTimeOffset Deadline => DateTimeOffset.UnixEpoch.AddMinutes(1);

        public override DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;

        public override Guid NewId() => Guid.Empty;

        public override Random Random { get; } = new Random(Seed: 0);
    }

    private sealed class FakeInventory(int available) : IInventoryStore
    {
        public int Reserved { get; private set; }

        public string? Released { get; private set; }

        public List<string> Keys { get; } = [];

        public ValueTask<int> AvailableAsync(string sku, CancellationToken ct)
            => ValueTask.FromResult(available);

        public ValueTask ReserveAsync(string sku, int quantity, string idempotencyKey, CancellationToken ct)
        {
            Reserved += quantity;
            Keys.Add(idempotencyKey);
            return ValueTask.CompletedTask;
        }

        public ValueTask ReleaseAsync(string idempotencyKey, CancellationToken ct)
        {
            Released = idempotencyKey;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeGateway(string? receipt) : IPaymentGateway
    {
        public ValueTask<string?> CaptureAsync(string reservationId, string idempotencyKey, CancellationToken ct)
            => ValueTask.FromResult(receipt);
    }
}
