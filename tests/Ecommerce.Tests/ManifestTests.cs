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

        source.ShouldBe("PlaceOrderFlow.cs:52");
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
        // Recorded even though this flow does not publish it: the sample is deliberately
        // Ephemeral, so it keeps no transaction to stage the event in. That is what
        // FLOWX1024 reports here, and DEBT-0001 is the dated suppression of it. A consumer
        // reading this manifest would expect the event.
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

        // And the permission it names. `CapturePayment` has declared
        // Permission = "payment.write" since the sample was written; the manifest
        // published {"mode": "Permission"} and dropped it, so `flowx diff`'s
        // FLOWX-DIFF-015 — "authorisation tightened, *or the named permission changed*" —
        // compared null against null here and could not fire. A reader of this document
        // could not tell "this capability requires no named grant" from "the compiler
        // never carried the one it declares".
        capabilities["payment.capture"].GetProperty("authorization").GetProperty("value")
            .GetString().ShouldBe("payment.write");

        // A stance that names nothing publishes no value, rather than an empty one.
        capabilities["inventory.reserve"].GetProperty("authorization")
            .TryGetProperty("value", out _).ShouldBeFalse();

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

    /// <summary>
    /// The address the flow declares, which is also the one <c>Program.cs</c> serves.
    /// </summary>
    /// <remarks>
    /// The registration is still hand-written, so these two agree because somebody kept
    /// them in step rather than because anything enforces it. The manifest publishes the
    /// declaration; the endpoint generator is what will make the declaration the only copy.
    /// </remarks>
    [Fact]
    public void PublishesTheAddressTheFlowDeclares()
    {
        var trigger = Flow.GetProperty("triggers")[0];

        trigger.GetProperty("kind").GetString().ShouldBe("Http");
        trigger.GetProperty("method").GetString().ShouldBe("POST");
        trigger.GetProperty("route").GetString().ShouldBe("/api/v1/orders");
        trigger.GetProperty("idempotent").GetBoolean().ShouldBeTrue();
    }

    /// <summary>
    /// Every failure the sample can return, read out of the capabilities themselves.
    /// </summary>
    /// <remarks>
    /// ADR-0007 chose <c>Result&lt;T&gt;</c> over exceptions so that failure paths would be
    /// "enumerable in the manifest, so error catalogues, OpenAPI responses and client SDKs
    /// are generated". These are the three <c>OrderErrors</c> factories, reached by
    /// following the expressions the capabilities actually return — the codes and the
    /// categories, and none of the messages, which interpolate the sku and the quantity.
    /// </remarks>
    [Fact]
    public void PublishesEachCapabilitysErrorCatalogue()
    {
        var capabilities = Manifest.RootElement.GetProperty("capabilities")
            .EnumerateArray()
            .ToDictionary(c => c.GetProperty("id").GetString()!, c => c);

        Codes(capabilities["order.validate"]).ShouldBe(["order.invalid_quantity"]);
        Codes(capabilities["inventory.reserve"]).ShouldBe(["inventory.out_of_stock"]);
        Codes(capabilities["payment.capture"]).ShouldBe(["payment.declined"]);

        // Present and empty, which is a statement: ReleaseInventory has no failure path,
        // and that is different from "nothing could be read about it".
        Codes(capabilities["inventory.release"]).ShouldBeEmpty();

        capabilities["payment.capture"].GetProperty("errors")[0]
            .GetProperty("category").GetString().ShouldBe("Conflict");
    }

    [Fact]
    public void TheFlowsErrorsAreTheUnionOfItsCapabilities()
        => Flow.GetProperty("errors").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(["inventory.out_of_stock", "order.invalid_quantity", "payment.declined"]);

    /// <summary>
    /// The message on an <c>Error</c> never reaches the manifest.
    /// </summary>
    /// <remarks>
    /// <c>OrderErrors.OutOfStock</c> builds <c>"'{sku}' has {available} in stock."</c> and
    /// attaches the sku and the count as structured detail. Both are business data, and the
    /// manifest is publishable to consumers not entitled to it — the codes and categories
    /// are structure, and they are all that crosses the line.
    /// </remarks>
    [Fact]
    public void NoErrorMessageOrStructuredDetailReachesTheManifest()
    {
        foreach (var fragment in new[] { "in stock", "Quantity must be positive", "available", "declined:" })
        {
            FlowXManifest.Json.ShouldNotContain(fragment, Case.Insensitive);
        }
    }

    private static string[] Codes(JsonElement capability) => [.. capability.GetProperty("errors")
        .EnumerateArray()
        .Select(e => e.GetProperty("code").GetString()!)];
}
