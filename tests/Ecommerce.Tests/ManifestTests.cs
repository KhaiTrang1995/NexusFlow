using System.Text.Json;
using FlowX.Generated;
using Shouldly;
using Xunit;

namespace Ecommerce.Tests;

/// <summary>
/// The manifest the sample's build produced.
/// </summary>
/// <remarks>
/// Asserted against the generated constant rather than a file, because that is what the
/// build actually emits — <c>flowx manifest</c> only copies it to disk. If the two ever
/// disagree, the CLI is wrong, not this.
/// </remarks>
public sealed class ManifestTests
{
    private static readonly JsonDocument Manifest = JsonDocument.Parse(FlowXManifest.Json);

    private static JsonElement Flow => Manifest.RootElement.GetProperty("flows")[0];

    [Fact]
    public void NamesTheFlowAndItsProfile()
    {
        Flow.GetProperty("id").GetString().ShouldBe("order.place");
        Flow.GetProperty("version").GetString().ShouldBe("1.0.0");
        Flow.GetProperty("profile").GetString().ShouldBe("Ephemeral");
        Flow.GetProperty("deadline").GetString().ShouldBe("PT30S");
    }

    [Fact]
    public void RecordsThePaymentTokenAsSensitive()
    {
        // `PlaceOrder.PaymentToken` carries [property: Sensitive]. Before this, the
        // compiler never read the attribute: it reached nothing, and a reviewer looking
        // at the manifest had no way to know the field carried a secret.
        Flow.GetProperty("input").GetProperty("sensitive").EnumerateArray()
            .Select(e => e.GetString())
            .ShouldBe(["PaymentToken"]);
    }

    [Fact]
    public void TheOutputContractHasNoSecrets()
        => Flow.GetProperty("output").TryGetProperty("sensitive", out _).ShouldBeFalse();

    [Fact]
    public void TheSourcePointerIsRelativeToTheProject()
    {
        // An absolute path would differ between two machines that compiled identical
        // source, breaking the determinism ADR-0005 requires of this document — and
        // shipping the build agent's directory layout to whoever reads it.
        var source = Flow.GetProperty("source").GetString().ShouldNotBeNull();

        source.ShouldBe("PlaceOrderFlow.cs:23");
        source.ShouldNotStartWith("/");
        source.ShouldNotContain(":\\");
    }

    [Fact]
    public void RecordsEveryStepIncludingTheCompensation()
    {
        var steps = Flow.GetProperty("steps").EnumerateArray().ToList();

        // The Emit step has no capability, so it is filtered rather than dereferenced.
        steps.Where(s => s.TryGetProperty("capability", out _))
            .Select(s => s.GetProperty("capability").GetString())
            .ShouldBe(["order.validate@1.0.0", "inventory.reserve@1.0.0", "payment.capture@2.1.0"]);

        steps[1].GetProperty("compensation").GetString().ShouldBe("inventory.release@1.0.0");
    }

    [Fact]
    public void RecordsTheEventTheFlowDeclares()
    {
        // Recorded even though nothing publishes it — which is the entire reason
        // FLOWX1024 exists. A consumer reading this would expect the event.
        Flow.GetProperty("emits").EnumerateArray()
            .Select(e => e.GetString())
            .ShouldBe(["order.placed"]);
    }

    [Fact]
    public void RecordsEachCapabilitysAuthorisationStance()
    {
        var capabilities = Manifest.RootElement.GetProperty("capabilities")
            .EnumerateArray()
            .ToDictionary(c => c.GetProperty("id").GetString()!, c => c);

        capabilities["payment.capture"].GetProperty("authorization").GetProperty("mode")
            .GetString().ShouldBe("Permission");

        // The compensation is a capability in its own right. It used to appear only as a
        // name on the step it undoes, so its stance and side effects reached nothing —
        // and `flowx diff` could not see a breaking change to one.
        capabilities["inventory.release"].GetProperty("authorization").GetProperty("mode")
            .GetString().ShouldBe("Internal");

        capabilities["inventory.release"].GetProperty("sideEffects").EnumerateArray()
            .Select(e => e.GetString())
            .ShouldBe(["inventory-ledger"]);

        // The declaration that makes a retry policy on this capability a build error.
        capabilities["payment.capture"].GetProperty("idempotent").GetBoolean().ShouldBeFalse();
        capabilities["inventory.reserve"].GetProperty("idempotent").GetBoolean().ShouldBeTrue();
    }
}
