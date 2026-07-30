using Shouldly;
using Xunit;

namespace FlowX.Core.Tests;

/// <summary>
/// A capability descriptor is the compile-time projection of <c>[Capability]</c>.
/// Its validation exists because the descriptor also reaches the manifest, and a
/// malformed identity there is a broken contract for every downstream consumer.
/// </summary>
public sealed class CapabilityDescriptorTests
{
    [Theory]
    [InlineData("inventory.reserve")]
    [InlineData("payment.capture")]
    [InlineData("order.line_item.validate")]
    public void AcceptsWellFormedIdentity(string id)
        => CapabilityDescriptor.Create(id, "1.0.0", isIdempotent: true).Id.ShouldBe(id);

    [Theory]
    [InlineData("reserve")]              // no domain
    [InlineData("Inventory.Reserve")]    // not lower snake
    [InlineData("inventory.")]           // trailing separator
    [InlineData(".reserve")]             // leading separator
    [InlineData("inventory reserve")]    // space
    [InlineData("")]
    public void RejectsMalformedIdentity(string id)
        => Should.Throw<ArgumentException>(() => CapabilityDescriptor.Create(id, "1.0.0", isIdempotent: true));

    [Theory]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    [InlineData("")]
    public void RejectsNonSemanticVersion(string version)
        => Should.Throw<ArgumentException>(() => CapabilityDescriptor.Create("a.b", version, isIdempotent: true));

    [Fact]
    public void SideEffectsAreReportedForBlastRadiusAnalysis()
    {
        Fixtures.CapturePayment.HasSideEffects.ShouldBeTrue();
        Fixtures.CapturePayment.SideEffects.ShouldBe(["payment-gateway", "ledger"]);

        Fixtures.ValidateOrder.HasSideEffects.ShouldBeFalse();
    }

    [Fact]
    public void SideEffectsAreImmutable()
    {
        var effects = new[] { "payment-gateway" };
        var descriptor = CapabilityDescriptor.Create("payment.capture", "1.0.0", false, effects);

        effects[0] = "mutated";

        descriptor.SideEffects[0].ShouldBe(
            "payment-gateway",
            "A descriptor must copy its inputs. Sharing the caller's array would let a " +
            "declared side effect change after the plan was validated against it.");
    }
}
