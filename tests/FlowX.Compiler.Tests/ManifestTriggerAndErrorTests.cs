using System.Linq;
using System.Text.Json;
using FlowX.Compiler.Analysis;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// The manifest fields the schema declared and nothing produced: a flow's
/// <c>triggers</c>, a capability's <c>errors</c>, and its
/// <c>authorization.value</c>.
/// </summary>
/// <remarks>
/// <para>
/// End to end through a real compilation, because none of them can be tested any other
/// way. A trigger is an attribute on the flow's class, an error catalogue is read by
/// following expressions of type <c>Error</c> back to the literals that produced them,
/// and the named permission is a property of the <c>[Capability]</c> attribute — all
/// questions about symbols, and a model built by hand would only prove that the writer
/// renders what it is given.
/// </para>
/// <para>
/// The negative cases matter more than the positive ones here. A field that is emitted
/// when it should not be, or emitted complete when it is not, is worse than one that was
/// never emitted at all: the first is an absence a consumer can see, and the second is a
/// wrong answer it cannot.
/// </para>
/// </remarks>
public sealed class ManifestTriggerAndErrorTests
{
    private const string Preamble = """
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record PlaceOrder(string Sku, int Quantity);
        public sealed record OrderResult(string Id);

        public static class OrderErrors
        {
            public static Error OutOfStock(string sku, int available) =>
                new Error("inventory.out_of_stock", $"'{sku}' has {available} in stock.", ErrorCategory.Conflict)
                    .With("sku", sku);

            public static Error Declined(string reason) =>
                new("payment.declined", $"Declined: {reason}.", ErrorCategory.Unavailable);
        }
        """;

    private static string Source(string body) => Preamble + "\n\n" + body;

    private static JsonDocument ManifestOf(string body)
    {
        GeneratorHarness.CompileErrorsIn(Source(body)).ShouldBeEmpty();

        var run = GeneratorHarness.Run(Source(body));

        run.ManifestJson.ShouldNotBeNull(run.Describe());

        return JsonDocument.Parse(run.ManifestJson!);
    }

    private static JsonElement Flow(JsonDocument manifest) =>
        manifest.RootElement.GetProperty("flows")[0];

    private static JsonElement Capability(JsonDocument manifest, string id) =>
        manifest.RootElement.GetProperty("capabilities").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == id);

    // ----------------------------------------------------------------- triggers

    private const string OneCapability = """
        [Capability("inventory.reserve", Version = "1.0.0", Authorization = Authorization.Authenticated, Idempotent = true)]
        public sealed class ReserveInventory : ICapability<PlaceOrder, OrderResult>
        {
            public ValueTask<Result<OrderResult>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new OrderResult(input.Sku)));
        }
        """;

    private static string FlowWith(string attributes) => OneCapability + "\n\n" + $$"""
        [Flow("order.place")]
        {{attributes}}
        public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
        {
            protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                .Step<ReserveInventory>()
                .Return(ctx => new OrderResult("id"));
        }
        """;

    [Fact]
    public void AnHttpTriggerReachesTheManifestAsItsAddress()
    {
        using var manifest = ManifestOf(FlowWith("""[HttpTrigger("POST", "/api/v1/orders", Idempotent = true)]"""));

        var trigger = Flow(manifest).GetProperty("triggers")[0];

        trigger.GetProperty("kind").GetString().ShouldBe("Http");
        trigger.GetProperty("method").GetString().ShouldBe("POST");
        trigger.GetProperty("route").GetString().ShouldBe("/api/v1/orders");
        trigger.GetProperty("idempotent").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void EveryTriggerKindTheAbstractionShipsIsRecognised()
    {
        using var manifest = ManifestOf(FlowWith(
            """
            [HttpTrigger("POST", "/api/v1/orders")]
            [KafkaTrigger("orders.requested", Group = "order-placement")]
            [CronTrigger("0 2 * * *", TimeZone = "Europe/Berlin")]
            [StreamTrigger("orders.stream", Window = "tumbling:1m")]
            [AgentTrigger(Description = "Place a customer order", Confirmation = ConfirmationMode.Always)]
            """));

        var triggers = Flow(manifest).GetProperty("triggers").EnumerateArray().ToList();

        triggers.Select(t => t.GetProperty("kind").GetString())
            .ShouldBe(["Agent", "Bus", "Http", "Schedule", "Stream"],
                "Ordinally sorted, because Roslyn promises no attribute order and the manifest " +
                "must be byte-identical between builds.");

        var bus = triggers.Single(t => t.GetProperty("kind").GetString() == "Bus");
        bus.GetProperty("transport").GetString().ShouldBe("kafka");
        bus.GetProperty("topic").GetString().ShouldBe("orders.requested");
        bus.GetProperty("group").GetString().ShouldBe("order-placement");

        var schedule = triggers.Single(t => t.GetProperty("kind").GetString() == "Schedule");
        schedule.GetProperty("cron").GetString().ShouldBe("0 2 * * *");
        schedule.GetProperty("timeZone").GetString().ShouldBe("Europe/Berlin");

        var agent = triggers.Single(t => t.GetProperty("kind").GetString() == "Agent");
        agent.GetProperty("confirmation").GetString().ShouldBe("Always");
        agent.GetProperty("description").GetString().ShouldBe("Place a customer order");
    }

    /// <summary>
    /// Omitted, not empty — and for the opposite reason to the error catalogue.
    /// </summary>
    /// <remarks>
    /// An empty <c>triggers</c> array would read as "this flow cannot be started from
    /// outside the process", and the compiler is not entitled to say that. Nothing yet
    /// turns a trigger attribute into a registration, so a flow with no attribute may
    /// still be serving a hand-written route. What the compiler knows is what was
    /// declared, and the positive statement is the only sound one.
    /// </remarks>
    [Fact]
    public void AFlowThatDeclaresNoTriggerPublishesNoTriggersArray()
    {
        using var manifest = ManifestOf(FlowWith(string.Empty));

        Flow(manifest).TryGetProperty("triggers", out _).ShouldBeFalse();
    }

    /// <summary>
    /// A trigger attribute this compiler does not know is skipped, not guessed at — and
    /// the skip is now reported.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A trigger's <c>Kind</c> is an overridden property returning an enum value — that is
    /// executable code, not attribute data, so there is no way to ask an arbitrary
    /// <c>TriggerAttribute</c> subclass what family it belongs to, nor what its
    /// constructor arguments mean. Emitting a plausible kind for one would be inventing
    /// the fact the manifest exists to publish, so the manifest half of this assertion is
    /// unchanged and must stay that way.
    /// </para>
    /// <para>
    /// What changed is that the gap is no longer silent. The absence asserted here is
    /// indistinguishable to <c>flowx diff</c> from a flow that declares no trigger, which
    /// is why <c>FLOWX1025</c> now tells the author. Both facts are asserted together
    /// deliberately: an omission nobody is told about was the defect, and either half
    /// alone would let it return.
    /// </para>
    /// </remarks>
    [Fact]
    public void ATriggerAttributeFromOutsideTheAbstractionIsNotGuessedAt()
    {
        const string Body =
            """
            public sealed class MqttTriggerAttribute : TriggerAttribute
            {
                public override TriggerKind Kind => TriggerKind.Bus;
            }
            """;

        using var manifest = ManifestOf(Body + "\n\n" + FlowWith("[MqttTrigger]"));

        Flow(manifest).TryGetProperty("triggers", out _).ShouldBeFalse(
            "An unrecognised trigger attribute produces nothing rather than a guessed kind.");

        GeneratorHarness
            .Analyze(Source(Body + "\n\n" + FlowWith("[MqttTrigger]")), new TriggerDeclarationAnalyzer())
            .ShouldBe(
                ["FLOWX1025"],
                "Skipping it is right; skipping it in silence was the defect FLOWX1025 closes.");
    }

    // ------------------------------------------------------------------- errors

    private static string CapabilityThatFails(string body) => $$"""
        [Capability("inventory.reserve", Version = "1.0.0", Authorization = Authorization.Authenticated, Idempotent = true)]
        public sealed class ReserveInventory : ICapability<PlaceOrder, OrderResult>
        {
            public ValueTask<Result<OrderResult>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
            {
                {{body}}
            }
        }

        [Flow("order.place")]
        public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
        {
            protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                .Step<ReserveInventory>()
                .Return(ctx => new OrderResult("id"));
        }
        """;

    [Fact]
    public void AnErrorBuiltInlineReachesTheCatalogue()
    {
        using var manifest = ManifestOf(CapabilityThatFails(
            """
            if (input.Quantity <= 0)
            {
                return ValueTask.FromResult(Result.Fail<OrderResult>(
                    new Error("order.invalid_quantity", $"Got {input.Quantity}.", ErrorCategory.Validation)));
            }

            return ValueTask.FromResult(Result.Ok(new OrderResult(input.Sku)));
            """));

        var errors = Capability(manifest, "inventory.reserve").GetProperty("errors");

        errors.GetArrayLength().ShouldBe(1);
        errors[0].GetProperty("code").GetString().ShouldBe("order.invalid_quantity");
        errors[0].GetProperty("category").GetString().ShouldBe("Validation");
    }

    /// <summary>
    /// The documented declaration mechanism: a static factory class per domain.
    /// </summary>
    /// <remarks>
    /// <c>docs/07-Capability-Model.md §7</c> requires errors to be declared this way "so
    /// error codes are enumerable — they appear in the manifest and in generated OpenAPI".
    /// Following the factory call, and the <c>.With(...)</c> chain that decorates its
    /// result, is what turns that requirement into something that actually happens.
    /// </remarks>
    [Fact]
    public void AnErrorReturnedThroughAFactoryAndDecoratedWithDetailIsStillOneError()
    {
        using var manifest = ManifestOf(CapabilityThatFails(
            """
            return ValueTask.FromResult(Result.Fail<OrderResult>(OrderErrors.OutOfStock(input.Sku, 0)));
            """));

        var errors = Capability(manifest, "inventory.reserve").GetProperty("errors");

        errors.GetArrayLength().ShouldBe(1, "`.With(...)` decorates an error; it does not create a second one.");
        errors[0].GetProperty("code").GetString().ShouldBe("inventory.out_of_stock");
        errors[0].GetProperty("category").GetString().ShouldBe("Conflict");
    }

    [Fact]
    public void BothArmsOfAConditionalFailureAreCatalogued()
    {
        using var manifest = ManifestOf(CapabilityThatFails(
            """
            return ValueTask.FromResult(Result.Fail<OrderResult>(
                input.Quantity <= 0 ? OrderErrors.Declined("none") : OrderErrors.OutOfStock(input.Sku, 0)));
            """));

        Capability(manifest, "inventory.reserve").GetProperty("errors").EnumerateArray()
            .Select(e => e.GetProperty("code").GetString())
            .ShouldBe(["inventory.out_of_stock", "payment.declined"]);
    }

    /// <summary>
    /// The empty array is a statement, and this is the case that makes it one.
    /// </summary>
    /// <remarks>
    /// A capability that returns no declared error publishes <c>errors: []</c>, which a
    /// consumer is entitled to read as "this never fails with a code". That reading is only
    /// safe because an unreadable capability publishes nothing at all — the two states are
    /// rendered differently on purpose, and collapsing them is the ambiguity this field
    /// exists to remove.
    /// </remarks>
    [Fact]
    public void ACapabilityThatCannotFailPublishesAnEmptyCatalogueRatherThanNone()
    {
        using var manifest = ManifestOf(CapabilityThatFails(
            "return ValueTask.FromResult(Result.Ok(new OrderResult(input.Sku)));"));

        Capability(manifest, "inventory.reserve").GetProperty("errors").GetArrayLength().ShouldBe(0);
    }

    /// <summary>
    /// A failure path that cannot be resolved withholds the whole catalogue.
    /// </summary>
    /// <remarks>
    /// A code composed at run time is not an identifier anybody can branch on, and a
    /// catalogue that quietly dropped it would be short by exactly the case nobody thought
    /// about — while looking identical to a complete one. Absent is a state a consumer can
    /// see; short is not.
    /// </remarks>
    [Fact]
    public void AnErrorCodeThatIsNotALiteralWithholdsTheCatalogue()
    {
        using var manifest = ManifestOf(CapabilityThatFails(
            """
            return ValueTask.FromResult(Result.Fail<OrderResult>(
                new Error("inventory." + input.Sku, "Bad sku.", ErrorCategory.Validation)));
            """));

        Capability(manifest, "inventory.reserve").TryGetProperty("errors", out _).ShouldBeFalse();
    }

    [Fact]
    public void AnErrorArrivingFromOutsideTheCapabilityWithholdsTheCatalogue()
    {
        using var manifest = ManifestOf(CapabilityThatFails(
            """
            return ValueTask.FromResult(Result.Fail<OrderResult>(Failure(input)));
            """).Replace(
            "public ValueTask<Result<OrderResult>> ExecuteAsync",
            "private static Error Failure(PlaceOrder order) => Whatever(order);\n"
            + "    private static Error Whatever(PlaceOrder order) => throw new System.NotImplementedException();\n"
            + "    public ValueTask<Result<OrderResult>> ExecuteAsync",
            System.StringComparison.Ordinal));

        Capability(manifest, "inventory.reserve").TryGetProperty("errors", out _).ShouldBeFalse();
    }

    /// <summary>
    /// A message is never published, however it was written.
    /// </summary>
    /// <remarks>
    /// It is the one field of an <c>Error</c> that routinely interpolates business values,
    /// and the manifest is publishable to consumers who are not entitled to them. Codes and
    /// categories are structure; <c>"'SKU-1' has 3 in stock."</c> is not.
    /// </remarks>
    [Fact]
    public void NeitherTheMessageNorTheStructuredDetailReachesTheManifest()
    {
        var run = GeneratorHarness.Run(Source(CapabilityThatFails(
            """
            return ValueTask.FromResult(Result.Fail<OrderResult>(
                new Error("inventory.out_of_stock", "The secret threshold is 80.", ErrorCategory.Conflict)
                    .With("threshold", 80)));
            """)));

        run.ManifestJson.ShouldNotBeNull();
        run.ManifestJson!.ShouldNotContain("secret threshold");
        run.ManifestJson!.ShouldNotContain("threshold");
        run.ManifestJson!.ShouldContain("inventory.out_of_stock");
    }

    [Fact]
    public void AFlowsErrorsAreTheUnionOfItsCapabilitiesCodes()
    {
        using var manifest = ManifestOf(CapabilityThatFails(
            """
            return ValueTask.FromResult(Result.Fail<OrderResult>(OrderErrors.OutOfStock(input.Sku, 0)));
            """));

        Flow(manifest).GetProperty("errors").EnumerateArray()
            .Select(e => e.GetString())
            .ShouldBe(["inventory.out_of_stock"]);
    }

    [Fact]
    public void AFlowWithAnUnreadableCapabilityPublishesNoAggregateEither()
    {
        using var manifest = ManifestOf(CapabilityThatFails(
            """
            return ValueTask.FromResult(Result.Fail<OrderResult>(
                new Error("inventory." + input.Sku, "Bad sku.", ErrorCategory.Validation)));
            """));

        Flow(manifest).TryGetProperty("errors", out _).ShouldBeFalse(
            "A union short by one capability's codes reads exactly like a complete one, and " +
            "an OpenAPI document generated from it would omit responses the endpoint returns.");
    }

    // ------------------------------------------------------ authorisation value

    private static string FlowOver(string capability) => capability + "\n\n" + """
        [Flow("order.place")]
        public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
        {
            protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                .Step<Guarded>()
                .Return(ctx => new OrderResult("id"));
        }
        """;

    private static string Guarded(string attribute) => FlowOver($$"""
        {{attribute}}
        public sealed class Guarded : ICapability<PlaceOrder, OrderResult>
        {
            public ValueTask<Result<OrderResult>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new OrderResult(input.Sku)));
        }
        """);

    /// <summary>
    /// The permission the stance names reaches the manifest.
    /// </summary>
    /// <remarks>
    /// <c>FLOWX-DIFF-015</c> is <em>"authorisation tightened, or the named permission
    /// changed"</em>, and its second half compared <c>authorization.value</c> on both
    /// sides while nothing wrote that field. On every manifest FlowX produced, that
    /// comparison was null against null: half of a Breaking rule that could not fire. The
    /// value was on the attribute the whole time and was dropped between the reader and
    /// the writer.
    /// </remarks>
    [Fact]
    public void ThePermissionAStanceNamesReachesTheManifest()
    {
        using var manifest = ManifestOf(Guarded(
            """[Capability("payment.capture", Version = "2.1.0", Authorization = Authorization.Permission, Permission = "payment.write")]"""));

        var authorization = Capability(manifest, "payment.capture").GetProperty("authorization");

        authorization.GetProperty("mode").GetString().ShouldBe("Permission");
        authorization.GetProperty("value").GetString().ShouldBe("payment.write");
    }

    [Fact]
    public void ThePolicyAStanceNamesReachesTheSameField()
    {
        // One field for both, because a capability has one stance. The schema declares
        // `value`, not `permission` and `policy`, and the mode says which it came from.
        using var manifest = ManifestOf(Guarded(
            """[Capability("payment.capture", Version = "2.1.0", Authorization = Authorization.Policy, Policy = "eu-residents-only")]"""));

        var authorization = Capability(manifest, "payment.capture").GetProperty("authorization");

        authorization.GetProperty("mode").GetString().ShouldBe("Policy");
        authorization.GetProperty("value").GetString().ShouldBe("eu-residents-only");
    }

    /// <summary>
    /// A stance that needs no name publishes no value, rather than an empty one.
    /// </summary>
    /// <remarks>
    /// The distinction the schema's optional field exists for: an empty string reads as
    /// "a permission whose name is blank", and absence reads as "this stance names
    /// nothing" — which is the truth for <c>Public</c>, <c>Authenticated</c> and
    /// <c>Internal</c>.
    /// </remarks>
    [Fact]
    public void AStanceThatNamesNothingPublishesNoValueAtAll()
    {
        using var manifest = ManifestOf(Guarded(
            """[Capability("payment.capture", Version = "2.1.0", Authorization = Authorization.Internal)]"""));

        var authorization = Capability(manifest, "payment.capture").GetProperty("authorization");

        authorization.GetProperty("mode").GetString().ShouldBe("Internal");
        authorization.TryGetProperty("value", out _).ShouldBeFalse();
    }

    /// <summary>
    /// A name declared beside a stance that does not read it is not published.
    /// </summary>
    /// <remarks>
    /// <c>Permission = "payment.write"</c> under <c>Authorization.Internal</c> names
    /// nothing the stance consults. Publishing it would put a string in
    /// <c>authorization.value</c> that no principal is ever checked against, and
    /// <c>FLOWX-DIFF-015</c> would then report a Breaking change to a grant that was never
    /// required — the over-reporting that gets a gate routed around.
    /// </remarks>
    [Fact]
    public void ANameDeclaredBesideAStanceThatDoesNotUseItIsNotPublished()
    {
        using var manifest = ManifestOf(Guarded(
            """[Capability("payment.capture", Version = "2.1.0", Authorization = Authorization.Internal, Permission = "payment.write")]"""));

        Capability(manifest, "payment.capture").GetProperty("authorization")
            .TryGetProperty("value", out _).ShouldBeFalse();
    }

    // ------------------------------------------------------------- determinism

    [Fact]
    public void TheManifestIsByteIdenticalAcrossRunsWithTriggersAndErrors()
    {
        var body = FlowWith(
            """
            [HttpTrigger("POST", "/api/v1/orders", Idempotent = true)]
            [KafkaTrigger("orders.requested", Group = "order-placement")]
            [CronTrigger("0 2 * * *")]
            """);

        GeneratorHarness.Run(Source(body)).ManifestJson
            .ShouldBe(GeneratorHarness.Run(Source(body)).ManifestJson);
    }
}
