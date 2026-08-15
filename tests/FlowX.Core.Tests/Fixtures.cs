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
    /// A second read, and the one a degraded step falls back to. No side effects, which is
    /// what FLOWX1053 requires of a fallback capability as well as of the step it answers for.
    /// </summary>
    public static CapabilityDescriptor CachedRating { get; } =
        CapabilityDescriptor.Create("rating.cached", "1.0.0", isIdempotent: true);

    /// <summary>
    /// The dangerous one. Not idempotent, and money moves — retrying it twice is a
    /// duplicate charge, which is why several invariants below single it out.
    /// </summary>
    public static CapabilityDescriptor CapturePayment { get; } =
        CapabilityDescriptor.Create("payment.capture", "2.1.0", isIdempotent: false, "payment-gateway", "ledger");

    /// <summary>
    /// The inverse of <see cref="CapturePayment"/>, and idempotent where the capture is not.
    /// </summary>
    /// <remarks>
    /// The pairing is the whole reason a step's chain and its compensation's chain are
    /// separate: one set may legitimately say "never retry the capture" and "always retry the
    /// refund", and a single chain validated against a single capability could not.
    /// </remarks>
    public static CapabilityDescriptor RefundPayment { get; } =
        CapabilityDescriptor.Create("payment.refund", "2.1.0", isIdempotent: true, "payment-gateway", "ledger");

    /// <summary>A 30-second ephemeral flow — the shape P0 targets.</summary>
    public static FlowDescriptor PlaceOrder { get; } =
        FlowDescriptor.Create("order.place", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30));
}
