namespace FlowX.Core.Tests;

/// <summary>
/// Shared test data. Named for what each value <em>means</em> rather than for its
/// shape, so a failing assertion reads as a statement about the domain.
/// </summary>
internal static class Fixtures
{
    /// <summary>A read. Safe to retry, safe to cache, no external effect.</summary>
    public static CapabilityDescriptor ValidateOrder { get; } =
        CapabilityDescriptor.Create("order.validate", "1.0.0", isIdempotent: true);

    /// <summary>Idempotent but effectful: retrying is safe, caching is not.</summary>
    public static CapabilityDescriptor ReserveInventory { get; } =
        CapabilityDescriptor.Create("inventory.reserve", "1.0.0", isIdempotent: true, "inventory-ledger");

    /// <summary>The compensating inverse of <see cref="ReserveInventory"/>.</summary>
    public static CapabilityDescriptor ReleaseInventory { get; } =
        CapabilityDescriptor.Create("inventory.release", "1.0.0", isIdempotent: true, "inventory-ledger");

    /// <summary>
    /// The dangerous one. Not idempotent, and money moves — retrying it twice is a
    /// duplicate charge, which is why several invariants below single it out.
    /// </summary>
    public static CapabilityDescriptor CapturePayment { get; } =
        CapabilityDescriptor.Create("payment.capture", "2.1.0", isIdempotent: false, "payment-gateway", "ledger");

    /// <summary>A 30-second ephemeral flow — the shape P0 targets.</summary>
    public static FlowDescriptor PlaceOrder { get; } =
        FlowDescriptor.Create("order.place", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30));
}
