using System;
using System.Linq;
using System.Text.Json;
using FlowX.Compiler.Emit;
using FlowX.Compiler.Model;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// The manifest is the artifact everything else derives from — OpenAPI, the agent tool
/// surface, <c>flowx graph</c>, impact analysis, <c>flowx diff</c>. These tests defend
/// the two properties that make it usable at all: it validates, and it is byte-stable.
/// </summary>
public sealed class ManifestWriterTests
{
    private static string Write(params FlowModel[] flows) =>
        ManifestWriter.Write("Sample.App", "1.0.0", flows);

    private static JsonDocument Parse(string manifest) => JsonDocument.Parse(manifest);

    [Fact]
    public void EmitsWellFormedJson()
    {
        using var document = Parse(Write(Models.PlaceOrder()));

        document.RootElement.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public void CarriesEveryFieldTheSchemaRequiresAtTheTopLevel()
    {
        using var document = Parse(Write(Models.PlaceOrder()));
        var root = document.RootElement;

        foreach (var required in new[] { "schemaVersion", "application", "flows", "capabilities" })
        {
            root.TryGetProperty(required, out _).ShouldBeTrue(
                $"'{required}' is required by schemas/flowx.manifest.schema.json.");
        }

        root.GetProperty("schemaVersion").GetString().ShouldBe("0.1.0");
        root.GetProperty("application").GetProperty("name").GetString().ShouldBe("Sample.App");
    }

    [Fact]
    public void EachCapabilityCarriesEveryFieldTheSchemaRequires()
    {
        using var document = Parse(Write(Models.PlaceOrder()));

        foreach (var capability in document.RootElement.GetProperty("capabilities").EnumerateArray())
        {
            foreach (var required in new[] { "id", "version", "input", "output", "authorization", "idempotent" })
            {
                capability.TryGetProperty(required, out _).ShouldBeTrue(
                    $"Capability entry is missing required field '{required}'.");
            }

            capability.GetProperty("authorization").TryGetProperty("mode", out _).ShouldBeTrue();
        }
    }

    [Fact]
    public void FlowIdsAndCapabilityIdsMatchTheSchemasIdentityPattern()
    {
        using var document = Parse(Write(Models.PlaceOrder(), Models.Minimal()));
        var pattern = new System.Text.RegularExpressions.Regex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$");

        foreach (var flow in document.RootElement.GetProperty("flows").EnumerateArray())
        {
            pattern.IsMatch(flow.GetProperty("id").GetString()!).ShouldBeTrue(
                $"Flow id '{flow.GetProperty("id").GetString()}' does not match the schema's identity pattern.");
        }

        foreach (var capability in document.RootElement.GetProperty("capabilities").EnumerateArray())
        {
            pattern.IsMatch(capability.GetProperty("id").GetString()!).ShouldBeTrue();
        }
    }

    [Fact]
    public void IsByteIdenticalAcrossRuns()
    {
        Write(Models.PlaceOrder()).ShouldBe(Write(Models.PlaceOrder()),
            "Two builds of identical source must produce identical bytes, or `flowx diff` " +
            "reports changes nobody made and people stop reading it.");
    }

    [Fact]
    public void IsIndependentOfTheOrderFlowsAreDiscoveredIn()
    {
        var forwards = Write(Models.PlaceOrder(), Models.Minimal());
        var backwards = Write(Models.Minimal(), Models.PlaceOrder());

        backwards.ShouldBe(forwards,
            "Roslyn does not promise a stable discovery order, so the writer sorts. " +
            "Without that, the manifest would differ between builds for no reason.");
    }

    [Fact]
    public void ContainsNoTimestampOrCommitHash()
    {
        var manifest = Write(Models.PlaceOrder());

        manifest.ShouldNotContainText("builtAt",
            "A build timestamp makes every manifest differ, which destroys `flowx diff`. " +
            "It belongs in the manifest, injected at publish time by the CLI — not baked " +
            "in by the generator.");
        manifest.ShouldNotContainText("commit", "Same reason as builtAt.");
    }

    [Fact]
    public void ContainsStructureButNoValues()
    {
        // ManifestContainsNoSecrets. The manifest says a capability accepts a
        // CaptureRequest; it must never say what was in one. This is what makes the
        // file safe to publish, feed to an agent, or attach to a build.
        var manifest = Write(Models.PlaceOrder());

        foreach (var forbidden in new[]
        {
            "password", "secret", "apikey", "api_key", "token",
            "connectionstring", "bearer", "private_key",
        })
        {
            manifest.ToUpperInvariant().Contains(forbidden.ToUpperInvariant(), StringComparison.Ordinal)
                .ShouldBeFalse($"The manifest contains '{forbidden}'. It describes structure, never values.");
        }
    }

    [Fact]
    public void DeduplicatesACapabilityUsedByTwoFlows()
    {
        var first = new FlowModel(
            "order.place", "1.0.0", "Ephemeral", null, "Sample", "A", "In", "Out",
            [Models.Validate(0)]);

        var second = new FlowModel(
            "order.cancel", "1.0.0", "Ephemeral", null, "Sample", "B", "In", "Out",
            [Models.Validate(0)]);

        using var document = Parse(Write(first, second));

        document.RootElement.GetProperty("capabilities").GetArrayLength().ShouldBe(1,
            "One capability invoked by two flows is one manifest entry.");
        document.RootElement.GetProperty("flows").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public void KeepsTwoVersionsOfTheSameCapabilityApart()
    {
        var v1 = StepModel.Capability(0, "T", "payment.capture", "1.0.0", false);
        var v2 = StepModel.Capability(0, "T", "payment.capture", "2.0.0", false);

        var flow1 = new FlowModel("a.one", "1.0.0", "Ephemeral", null, "S", "A", "In", "Out", [v1]);
        var flow2 = new FlowModel("a.two", "1.0.0", "Ephemeral", null, "S", "B", "In", "Out", [v2]);

        using var document = Parse(Write(flow1, flow2));

        document.RootElement.GetProperty("capabilities").GetArrayLength().ShouldBe(2,
            "Deduplication is by id AND version — a contract change is a different entry.");
    }

    [Fact]
    public void RecordsCompensationAgainstTheStepItUndoes()
    {
        using var document = Parse(Write(Models.PlaceOrder()));

        var steps = document.RootElement.GetProperty("flows")[0].GetProperty("steps");
        var compensable = steps.EnumerateArray().Single(s => s.TryGetProperty("compensation", out _));

        compensable.GetProperty("id").GetInt32().ShouldBe(1);
        compensable.GetProperty("compensation").GetString().ShouldBe("inventory.release@1.0.0");
    }

    [Fact]
    public void ListsTheEventsAFlowEmits()
    {
        using var document = Parse(Write(Models.PlaceOrder()));

        document.RootElement.GetProperty("flows")[0].GetProperty("emits")
            .EnumerateArray().Select(e => e.GetString()).ShouldBe(["order.placed"]);

        document.RootElement.GetProperty("events")[0].GetProperty("type").GetString()
            .ShouldBe("order.placed");
    }

    [Fact]
    public void EscapesCharactersThatWouldBreakTheJson()
    {
        var awkward = new FlowModel(
            "a.b", "1.0.0", "Ephemeral", null, "N", "T",
            "Sample.Generic<Sample.Item>", "Out",
            [StepModel.Capability(0, @"Ns.With\Backslash", "a.b", "1.0.0", true)],
            declarationLocation: @"C:\src\Flows\Order.cs:12");

        // Parsing is the assertion: a broken escape produces invalid JSON.
        using var document = Parse(Write(awkward));

        document.RootElement.GetProperty("flows")[0].GetProperty("source").GetString()
            .ShouldBe(@"C:\src\Flows\Order.cs:12");
    }

    [Fact]
    public void HandlesAnApplicationWithNoFlows()
    {
        using var document = Parse(ManifestWriter.Write("Empty.App", "1.0.0", []));

        document.RootElement.GetProperty("flows").GetArrayLength().ShouldBe(0);
        document.RootElement.GetProperty("capabilities").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void RejectsANullFlowList()
        => Should.Throw<ArgumentNullException>(() => ManifestWriter.Write("A", "1.0.0", null!));
}
