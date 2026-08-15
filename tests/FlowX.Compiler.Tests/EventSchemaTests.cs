using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FlowX.Compiler.Analysis;
using FlowX.Compiler.Emit;
using FlowX.Compiler.Model;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// <c>[EventSchema("…")]</c>: the declaration that made <c>event.schemaVersion</c> stop being
/// a constant.
/// </summary>
/// <remarks>
/// <para>
/// <strong>ADR-0017 F2's instrument, which could not be written until now.</strong> The
/// criterion asks for "a test in which two events declared at different versions produce
/// different <c>schemaVersion</c> values — one that cannot pass while the value is a literal",
/// and the record's own note was that the check "cannot be written, because two events cannot
/// be declared at different versions". <see cref="TwoEventsDeclaredAtDifferentVersionsPublishDifferentVersions"/>
/// is that test.
/// </para>
/// <para>
/// <strong>The other half is that there is one reading.</strong> The version reaches the
/// manifest's <c>events</c> array and the outbox row's <c>SchemaVersion</c> from the same
/// field on the same step, so a document promising <c>2.0.0</c> over rows stamped
/// <c>1.0.0</c> is not a state this compiler can produce —
/// <see cref="TheManifestAndTheOutboxRowCarryTheSameNumber"/> is asserted over the two real
/// writers rather than over one of them twice.
/// </para>
/// </remarks>
public sealed class EventSchemaTests
{
    /// <summary>Two contracts, one declaring a major and one declaring a patch.</summary>
    private const string TwoDeclaredEvents = """
        using System.Text.Json.Serialization;
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record PlaceOrder(string Sku);
        public sealed record Confirmation(string OrderId);

        [EventSchema("2.0.0")]
        public sealed record OrderPlaced(string OrderId);

        [EventSchema("1.4.2")]
        public sealed record OrderConfirmed(string OrderId);

        [JsonSerializable(typeof(PlaceOrder))]
        [JsonSerializable(typeof(Confirmation))]
        [JsonSerializable(typeof(OrderPlaced))]
        [JsonSerializable(typeof(OrderConfirmed))]
        public sealed partial class SampleJson : JsonSerializerContext;

        [Capability("order.confirm", Version = "1.0.0", Authorization = Authorization.Internal)]
        public sealed class ConfirmOrder : ICapability<PlaceOrder, Confirmation>
        {
            public ValueTask<Result<Confirmation>> ExecuteAsync(
                PlaceOrder input, CapabilityContext ctx, CancellationToken ct) =>
                ValueTask.FromResult(Result.Ok(new Confirmation("o")));
        }

        [Flow("order.place", Version = "1.0.0", Profile = ExecutionProfile.Durable)]
        public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, Confirmation>
        {
            protected override void Define(IFlowBuilder<PlaceOrder, Confirmation> flow) =>
                flow
                    .Step<ConfirmOrder>()
                    .Emit<OrderPlaced>(ctx => new OrderPlaced(ctx.Get<Confirmation>().OrderId))
                    .Emit<OrderConfirmed>(ctx => new OrderConfirmed(ctx.Get<Confirmation>().OrderId))
                    .Return(ctx => ctx.Get<Confirmation>());
        }
        """;

    /// <summary>The same flow with neither contract declaring anything.</summary>
    private static string UndeclaredEvents => TwoDeclaredEvents
        .Replace("[EventSchema(\"2.0.0\")]\n", string.Empty, StringComparison.Ordinal)
        .Replace("[EventSchema(\"1.4.2\")]\n", string.Empty, StringComparison.Ordinal);

    // ------------------------------------------------------------------ the criterion

    /// <summary>
    /// ADR-0017 F2: two events at two versions produce two values, in one manifest.
    /// </summary>
    /// <remarks>
    /// It cannot pass while the value is a literal, which is the property the criterion asked
    /// for and the reason this file exists.
    /// </remarks>
    [Fact]
    public void TwoEventsDeclaredAtDifferentVersionsPublishDifferentVersions()
    {
        var manifest = GeneratorHarness.Run(TwoDeclaredEvents).ManifestJson;

        Versions(manifest).ShouldBe(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["order.confirmed"] = "1.4.2",
            ["order.placed"] = "2.0.0",
        });
    }

    /// <summary>
    /// A contract that declares nothing publishes what it published before the attribute
    /// existed.
    /// </summary>
    /// <remarks>
    /// The half that keeps every manifest in this repository byte-identical across the
    /// change: absence is <c>1.0.0</c>, so an author who declares nothing is not affected by
    /// a feature they did not ask for. It is also what makes the assertion above mean
    /// something — the two versions differ because they were declared, not because the writer
    /// varies on its own.
    /// </remarks>
    [Fact]
    public void AnEventThatDeclaresNothingPublishesTheDefault()
    {
        var manifest = GeneratorHarness.Run(UndeclaredEvents).ManifestJson;

        Versions(manifest).ShouldBe(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["order.confirmed"] = "1.0.0",
            ["order.placed"] = "1.0.0",
        });

        EventSchemaReader.Default.ShouldBe("1.0.0");
    }

    // ------------------------------------------------------------------ one reading

    /// <summary>
    /// The manifest entry and the outbox row this build stages carry one number each, and it
    /// is the declared one.
    /// </summary>
    /// <remarks>
    /// <strong>The two-copies defect this repository exists to kill, asserted over the two
    /// real writers.</strong> A manifest saying <c>2.0.0</c> while the <c>schema_version</c>
    /// column says <c>1.0.0</c> would be a consumer reading a promise the wire does not keep,
    /// and it is the state that existed by construction while <c>ManifestWriter</c> and
    /// <c>FlowEmitter</c> shared a constant rather than a reading. Both sides are taken from
    /// one generator run, so nothing here can pass by asserting the same writer twice.
    /// </remarks>
    [Fact]
    public void TheManifestAndTheOutboxRowCarryTheSameNumber()
    {
        var run = GeneratorHarness.Run(TwoDeclaredEvents);

        Versions(run.ManifestJson)["order.placed"].ShouldBe("2.0.0");
        Versions(run.ManifestJson)["order.confirmed"].ShouldBe("1.4.2");

        run.Plan.ShouldContain("Type = \"order.placed\",");
        run.Plan.ShouldContain("SchemaVersion = \"2.0.0\",");
        run.Plan.ShouldContain("Type = \"order.confirmed\",");
        run.Plan.ShouldContain("SchemaVersion = \"1.4.2\",");
        run.Plan.Contains("SchemaVersion = \"1.0.0\",", StringComparison.Ordinal).ShouldBeFalse(
            "Both contracts declare a version, so no row may be stamped with the default.");
    }

    /// <summary>The outbox row of an undeclared contract keeps the default.</summary>
    [Fact]
    public void AnUndeclaredContractStagesTheDefaultVersion()
    {
        GeneratorHarness.Run(UndeclaredEvents).Plan.ShouldContain("SchemaVersion = \"1.0.0\",");
    }

    // --------------------------------------------------- what the writers do with rubble

    /// <summary>
    /// A value that is not a version is not published, in either artifact.
    /// </summary>
    /// <remarks>
    /// FLOWX1055 is the sentence that tells the author; this is what happens meanwhile. The
    /// alternative — writing the rubble through — would put a string in the manifest that
    /// <c>flowx diff</c>'s <c>id@major</c> key cannot read, which is F2's objection to an
    /// invented value arriving from the other direction.
    /// </remarks>
    [Fact]
    public void AMalformedVersionIsDroppedRatherThanPublished()
    {
        var run = GeneratorHarness.Run(TwoDeclaredEvents.Replace(
            "[EventSchema(\"2.0.0\")]", "[EventSchema(\"v2\")]", StringComparison.Ordinal));

        Versions(run.ManifestJson)["order.placed"].ShouldBe("1.0.0");
        run.ManifestJson!.Contains("v2", StringComparison.Ordinal).ShouldBeFalse();
        run.Plan.Contains("SchemaVersion = \"v2\",", StringComparison.Ordinal).ShouldBeFalse();
    }

    // ------------------------------------------------------------- the model, directly

    /// <summary>
    /// The catalogue entry is the contract's, not the call site's: two flows emitting one
    /// contract publish one entry at one version.
    /// </summary>
    [Fact]
    public void TwoFlowsEmittingOneContractPublishOneEntry()
    {
        var manifest = ManifestWriter.Write(
            "Sample.App",
            "1.0.0",
            [Emitting("order.place", "PlaceOrderFlow"), Emitting("order.confirm", "ConfirmOrderFlow")]);

        var events = JsonDocument.Parse(manifest).RootElement.GetProperty("events");

        events.GetArrayLength().ShouldBe(1, "one entry, not one per emitter.");
        events[0].GetProperty("schemaVersion").GetString().ShouldBe("2.0.0");
        events[0].GetProperty("producedBy").EnumerateArray()
            .Select(static p => p.GetString())
            .ShouldBe(["order.confirm", "order.place"]);
    }

    /// <summary>Every event the manifest publishes, by identity, with the version it carries.</summary>
    private static Dictionary<string, string> Versions(string? manifest)
    {
        manifest.ShouldNotBeNull();

        return JsonDocument.Parse(manifest).RootElement.GetProperty("events")
            .EnumerateArray()
            .ToDictionary(
                static e => e.GetProperty("type").GetString()!,
                static e => e.GetProperty("schemaVersion").GetString()!,
                StringComparer.Ordinal);
    }

    private static FlowModel Emitting(string flowId, string typeName) => new(
        flowId: flowId,
        version: "1.0.0",
        profile: "Durable",
        deadline: "PT30S",
        containingNamespace: "Sample.Flows",
        typeName: typeName,
        inputTypeName: "Sample.Contracts.PlaceOrder",
        outputTypeName: "Sample.Contracts.OrderPlacedResult",
        steps:
        [
            StepModel.Emit(
                0,
                "order.placed",
                contractTypeName: "Sample.Contracts.OrderPlaced",
                schemaVersion: "2.0.0"),
        ]);
}
