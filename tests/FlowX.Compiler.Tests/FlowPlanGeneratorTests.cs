using System;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// The generator, end to end: real source, real compilation, real symbols.
/// </summary>
/// <remarks>
/// These are the slow tests, so there are deliberately few of them. Each one covers
/// something the pure-layer tests structurally cannot: symbol resolution, attribute
/// reading, and whether a diagnostic lands on the right span.
/// </remarks>
public sealed class FlowPlanGeneratorTests
{
    private const string Preamble = """
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record PlaceOrder(string Sku);
        public sealed record OrderPlaced(string Sku);
        public sealed record OrderResult(string Id);
        public sealed record Reservation(string Sku);

        public enum Channel { Retail, Wholesale, Partner }

        [Capability("inventory.reserve", Version = "1.2.0",
            Authorization = Authorization.Authenticated,
            Idempotent = true, SideEffects = new[] { "inventory-ledger" })]
        public sealed class ReserveInventory : ICapability<PlaceOrder, Reservation>
        {
            public ValueTask<Result<Reservation>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Reservation(input.Sku)));
        }

        [Capability("inventory.release", Version = "1.2.0",
            Authorization = Authorization.Internal, Idempotent = true)]
        public sealed class ReleaseInventory : ICapability<PlaceOrder, Reservation>
        {
            public ValueTask<Result<Reservation>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Reservation(input.Sku)));
        }

        [Capability("payment.capture", Version = "2.1.0",
            Authorization = Authorization.Permission, Permission = "payment.write",
            SideEffects = new[] { "payment-gateway" })]
        public sealed class CapturePayment : ICapability<PlaceOrder, OrderResult>
        {
            public ValueTask<Result<OrderResult>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new OrderResult(input.Sku)));
        }
        """;

    private static string WithFlow(string flowDeclaration) => Preamble + "\n\n" + flowDeclaration;

    [Fact]
    public void TheTestPreambleItselfCompiles()
    {
        // Guards the harness. Without it, a typo in the shared source shows up as
        // "the generator found no flows" and the next hour goes into the wrong file.
        GeneratorHarness.CompileErrorsIn(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Return(ctx => new OrderResult("id"));
            }
            """)).ShouldBeEmpty();
    }

    [Fact]
    public void GeneratesAPlanForASingleStepFlow()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldBeEmpty(run.Describe());
        run.Plan.ShouldContainText("partial class PlaceOrderFlow", run.Describe());
        run.Plan.ShouldContainText(
            "CapabilityDescriptor.Create(\"inventory.reserve\", \"1.2.0\", true, \"inventory-ledger\")",
            "Version, idempotency and side effects all come from the [Capability] attribute.");
    }

    [Fact]
    public void PreservesStepOrderFromTheFluentChain()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Step<CapturePayment>()
                    .Emit<OrderPlaced>(ctx => new OrderPlaced("sku"))
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        var source = run.Plan;
        var reserve = source.IndexOf("\"inventory.reserve\"", StringComparison.Ordinal);
        var capture = source.IndexOf("\"payment.capture\"", StringComparison.Ordinal);

        reserve.ShouldBeGreaterThan(0, run.Describe());
        capture.ShouldBeGreaterThan(reserve,
            "A fluent chain nests inside-out, so the outermost node is the LAST call. " +
            "Reading it without reversing produces a flow that runs backwards — a bug " +
            "that compiles and passes a one-step smoke test.");

        source.ShouldContainText("StepNode.ForCapability(0, Descriptors.Step0)", "Step 0 is the reserve.");
        source.ShouldContainText("StepNode.ForCapability(1, Descriptors.Step1)", "Step 1 is the capture.");
        source.ShouldContainText("StepNode.ForEmit(2, \"order.placed\")", "Step 2 is the emit.");
    }

    [Fact]
    public void AttachesCompensationToTheStepItFollows()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()
                    .Step<CapturePayment>()
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Plan.ShouldContainText(
            "StepNode.ForCapability(0, Descriptors.Step0, Descriptors.Step0Compensation)",
            "CompensateWith attaches to the step it follows, not to the next one. " + run.Describe());
        run.Plan.ShouldContainText(
            "StepNode.ForCapability(1, Descriptors.Step1)",
            "The second step declared no compensation.");
    }

    [Fact]
    public void EmitsLineDirectivesPointingAtTheDeclaringFile()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Plan.ShouldContainText("#line ", run.Describe());
        run.Plan.ShouldContainText(
            "/src/Flows/Sample.cs",
            "Risk R1: a breakpoint must land in the developer's own file, not in generated code.");
    }

    [Fact]
    public void EachStepGetsItsOwnLineDirective()
    {
        // A fluent chain nests its receiver inside every later call, so each invocation's
        // span starts at the head of the chain. Taking the location from the invocation
        // mapped all three steps to one line, and a breakpoint on the third landed on the
        // first — which is exactly the debugging experience R1 says must not happen.
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Step<CapturePayment>()
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        var lines = System.Text.RegularExpressions.Regex
            .Matches(run.Plan ?? string.Empty, @"#line (\d+) ")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        lines.Count.ShouldBeGreaterThan(1,
            "Every step reported the same source line. " + run.Describe());
    }

    [Fact]
    public void EmitsTheReturnClauseAsAProjection()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Return(ctx => new OrderResult(ctx.Get<Reservation>().Sku));
            }
            """));

        run.Ids.ShouldBeEmpty(run.Describe());

        // A static readonly field, so passing it to the engine allocates nothing, and the
        // author's own expression, so it means what they wrote.
        run.Plan.ShouldContainText(
            "public static readonly Func<FlowContext, Sample.OrderResult> Projection =",
            run.Describe());

        run.Plan.ShouldContainText(
            "ctx.Get<Reservation>().Sku",
            "The projection is the author's expression, copied verbatim.");
    }

    [Fact]
    public void AFlowWithoutAReturnClauseEmitsNoProjection()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>();
            }
            """));

        run.Plan!.Contains("Projection", StringComparison.Ordinal).ShouldBeFalse(
            "A flow that returns nothing must not get a projection field that returns default.");
    }

    [Fact]
    public void CopiesTheDeclaringFilesUsingsSoACopiedProjectionResolves()
    {
        // The projection is the author's text. If their file said `using System.Linq;`
        // and the generated one does not, their expression stops compiling in a file
        // they did not write.
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Plan.ShouldContainText("using FlowX;", run.Describe());
        run.Plan!.Split("using FlowX;").Length.ShouldBe(2,
            "The always-emitted usings must not be duplicated by the copied ones.");
    }

    [Fact]
    public void AnEmitStepIsReportedAsNotYetPublished()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Emit<OrderPlaced>(ctx => new OrderPlaced("sku"))
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        // A warning, not an error: the step is real and reaches the manifest, so a
        // consumer will expect the event. Silence would let that gap ship.
        run.Ids.ShouldBe(["FLOWX1024"], run.Describe());
        run.ManifestJson.ShouldNotBeNull()
            .ShouldContainText("\"event\": \"order.placed\"", run.Describe());
    }

    [Fact]
    public void RecordsSensitiveContractMembersInTheManifest()
    {
        var run = GeneratorHarness.Run("""
            using FlowX;

            namespace Sample;

            public sealed record Payment(string Amount, [property: Sensitive] string CardToken);
            public sealed record Receipt(string Id);

            [Capability("payment.take", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class TakePayment : ICapability<Payment, Receipt>
            {
                public ValueTask<Result<Receipt>> ExecuteAsync(Payment input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new Receipt("r")));
            }

            [Flow("payment.flow")]
            public sealed partial class PaymentFlow : Flow<Payment, Receipt>
            {
                protected override void Define(IFlowBuilder<Payment, Receipt> flow) => flow
                    .Step<TakePayment>()
                    .Return(ctx => new Receipt("r"));
            }
            """);

        run.Ids.ShouldBeEmpty(run.Describe());

        // `[property: Sensitive]` on a positional record parameter is the spelling users
        // actually write; Roslyn surfaces it on the generated property.
        run.ManifestJson.ShouldNotBeNull()
            .ShouldContainText("\"sensitive\"", run.Describe());

        run.ManifestJson!.ShouldContain("CardToken");
    }

    [Fact]
    public void ReadsSensitiveWrittenDirectlyOnAProperty()
    {
        var run = GeneratorHarness.Run("""
            using FlowX;

            namespace Sample;

            public sealed class Payment
            {
                public string Amount { get; init; } = "";

                [Sensitive]
                public string CardToken { get; init; } = "";
            }

            public sealed record Receipt(string Id);

            [Capability("payment.take", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class TakePayment : ICapability<Payment, Receipt>
            {
                public ValueTask<Result<Receipt>> ExecuteAsync(Payment input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new Receipt("r")));
            }

            [Flow("payment.flow")]
            public sealed partial class PaymentFlow : Flow<Payment, Receipt>
            {
                protected override void Define(IFlowBuilder<Payment, Receipt> flow) => flow
                    .Step<TakePayment>()
                    .Return(ctx => new Receipt("r"));
            }
            """);

        run.Ids.ShouldBeEmpty(run.Describe());
        run.ManifestJson!.ShouldContain("CardToken");
    }

    [Fact]
    public void AContractWithNoSecretsGetsNoSensitiveArray()
    {
        // Omitted rather than emitted empty: an empty array in every flow would be noise
        // in every `flowx diff`.
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.ManifestJson!.Contains("sensitive", StringComparison.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public void ReportsFLOWX1014WhenRetryIsAttachedToANonIdempotentCapability()
    {
        // The safety property this repository advertises most loudly, and the one it did
        // not have: .WithPolicy stored the argument's source text, so nothing ever asked
        // what was in the set. `payment.capture` declares Idempotent = false; retrying a
        // capture is a duplicate charge.
        var run = GeneratorHarness.Run(WithFlow("""
            public static class Policies
            {
                public static readonly PolicySet PaymentGateway = PolicySet
                    .Named("payment-gateway")
                    .Timeout(TimeSpan.FromSeconds(2))
                    .Retry(3);
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<CapturePayment>().WithPolicy(Policies.PaymentGateway)
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldContain("FLOWX1014", run.Describe());
    }

    [Fact]
    public void AllowsRetryOnAnIdempotentCapability()
    {
        // The other half of the rule. A gate that fires on everything is not a gate.
        var run = GeneratorHarness.Run(WithFlow("""
            public static class Policies
            {
                public static readonly PolicySet Inventory = PolicySet.Named("inventory").Retry(3);
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>().WithPolicy(Policies.Inventory)
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldNotContain("FLOWX1014", run.Describe());
    }

    [Fact]
    public void APolicySetWithoutRetryIsFineOnANonIdempotentCapability()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            public static class Policies
            {
                public static readonly PolicySet Slow = PolicySet
                    .Named("slow")
                    .Timeout(TimeSpan.FromSeconds(5));
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<CapturePayment>().WithPolicy(Policies.Slow)
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldNotContain("FLOWX1014", run.Describe());
    }

    [Fact]
    public void ReportsFLOWX1018WhenCacheIsAttachedToACapabilityWithSideEffects()
    {
        // A cache hit returns a success without performing the effect — it reports a
        // reservation that never happened.
        var run = GeneratorHarness.Run(WithFlow("""
            public static class Policies
            {
                public static readonly PolicySet Cached = PolicySet
                    .Named("cached")
                    .Cache(TimeSpan.FromMinutes(5));
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>().WithPolicy(Policies.Cached)
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldContain("FLOWX1018", run.Describe());
    }

    [Fact]
    public void APolicySetBuiltAtRunTimeIsNotGuessedAt()
    {
        // A set that is not a field or property initialiser cannot be read at compile
        // time. Reporting on a guess would produce a diagnostic nobody could act on, so
        // the reader returns nothing and the rule stays silent.
        var run = GeneratorHarness.Run(WithFlow("""
            public static class Policies
            {
                public static PolicySet Build() => PolicySet.Named("x").Retry(3);
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<CapturePayment>().WithPolicy(Policies.Build())
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldNotContain("FLOWX1014", run.Describe());
    }

    [Fact]
    public void ReadsAPolicySetDeclaredAsAnExpressionBodiedProperty()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            public static class Policies
            {
                public static PolicySet Payment => PolicySet.Named("payment").Retry(3);
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<CapturePayment>().WithPolicy(Policies.Payment)
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldContain("FLOWX1014", run.Describe());
    }

    [Fact]
    public void ReadsTheDurableProfileAndTheDeclaredDeadline()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place", Profile = ExecutionProfile.Durable, Version = "2.0.0")]
            [FlowDeadline("P30D")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Plan.ShouldContainText("ExecutionProfile.Durable", run.Describe());
        run.Plan.ShouldContainText("\"2.0.0\"", "The flow version comes from the attribute.");
        run.Plan.ShouldContainText(
            "XmlConvert.ToTimeSpan(\"P30D\")", "The deadline comes from [FlowDeadline].");
    }

    [Fact]
    public void DefaultsToTheEphemeralProfileWhenNoneIsDeclared()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Plan.ShouldContainText("ExecutionProfile.Ephemeral",
            "ADR-0003: a flow that says nothing gets the cheap profile.");
    }

    [Fact]
    public void ReportsFLOWX1001WhenTheFlowIsNotPartial()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldContain("FLOWX1001");
        run.Sources.ShouldBeEmpty(
            "Emitting a partial plan on top of a blocking error buries it under a " +
            "cascade of 'type not found'.");
    }

    [Fact]
    public void ReportsFLOWX1023WhenTheFlowDeclaresNoSteps()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.empty")]
            public sealed partial class EmptyFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow)
                {
                }
            }
            """));

        run.Ids.ShouldContain("FLOWX1023");
    }

    [Fact]
    public void ReportsFLOWX1010WhenACapabilityDeclaresNoAuthorization()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Capability("audit.write", Version = "1.0.0", Idempotent = true)]
            public sealed class WriteAudit : ICapability<PlaceOrder, OrderResult>
            {
                public ValueTask<Result<OrderResult>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new OrderResult("x")));
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<WriteAudit>()
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldContain("FLOWX1010",
            "Deny-by-default is structural. A capability without a stance must not build.");
    }

    [Fact]
    public void ReportsFLOWX1002WhenAStepIsNotACapability()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            public sealed class NotACapability { }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<NotACapability>()
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldContain("FLOWX1002");
    }

    [Fact]
    public void ReportsFLOWX1017WhenAnEphemeralFlowAwaitsASignal()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .AwaitSignal<OrderPlaced>(System.TimeSpan.FromHours(1))
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldContain("FLOWX1017",
            "An in-memory wait does not survive a deployment, a crash or a scale-in.");
    }

    [Fact]
    public void DiagnosticsPointAtTheOffendingSpanRatherThanTheFile()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        var diagnostic = run.Diagnostics.Single(d => d.Id == "FLOWX1001");

        diagnostic.Location.IsInSource.ShouldBeTrue(
            "A diagnostic without a source span cannot be clicked, and shows up at the " +
            "top of the build log instead of in the editor.");
        diagnostic.Location.GetLineSpan().Path.ShouldBe("/src/Flows/Sample.cs");
    }

    [Fact]
    public void GenerationIsDeterministicAcrossRuns()
    {
        const string Source = """
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()
                    .Step<CapturePayment>()
                    .Return(ctx => new OrderResult("id"));
            }
            """;

        var first = GeneratorHarness.Run(WithFlow(Source));
        var second = GeneratorHarness.Run(WithFlow(Source));

        second.Plan.ShouldBe(first.Plan);
        second.Manifest.ShouldBe(first.Manifest,
            "The manifest must be byte-identical too, or `flowx diff` reports changes " +
            "nobody made on every build and people stop reading it.");
    }

    [Fact]
    public void EmitsAManifestAlongsideThePlan()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()
                    .Emit<OrderPlaced>(ctx => new OrderPlaced("sku"))
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        // FLOWX1024 is expected: the flow emits, and nothing publishes yet. The manifest
        // still records the event, which is exactly why the warning exists.
        run.Ids.ShouldBe(["FLOWX1024"], run.Describe());
        run.Sources.Length.ShouldBe(2, "One plan plus one manifest.");

        var manifest = run.ManifestJson.ShouldNotBeNull();

        manifest.ShouldContainText("\"id\": \"order.place\"", "The manifest names the flow.");
        manifest.ShouldContainText("\"capability\": \"inventory.reserve@1.2.0\"", "and its steps.");
        manifest.ShouldContainText("\"mode\": \"Authenticated\"", "and each capability's authorisation stance.");
    }

    [Fact]
    public void TheEmittedManifestParsesAsJson()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        // Parsing is the assertion: a botched verbatim-string escape produces text that
        // compiles as C# but is no longer valid JSON, which nothing else would catch.
        using var document = System.Text.Json.JsonDocument.Parse(run.ManifestJson.ShouldNotBeNull());

        document.RootElement.GetProperty("flows").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public void EmitsNoManifestWhenAnalysisFailed()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldContain("FLOWX1001");
        run.Sources.ShouldBeEmpty(
            "A build with errors must not publish a manifest claiming the application " +
            "has no flows. That is a more dangerous lie than emitting nothing.");
    }

    [Fact]
    public void IgnoresTypesThatAreNotFlows()
    {
        var run = GeneratorHarness.Run(Preamble);

        run.Sources.ShouldBeEmpty();
        run.Diagnostics.ShouldBeEmpty();
    }

    /// <summary>
    /// <c>When</c> / <c>Otherwise</c> against a real compilation: the two blocks are
    /// lambda arguments, so nothing about them is reachable by unwinding the outer chain.
    /// </summary>
    /// <remarks>
    /// The pure-layer tests cover the layout arithmetic and the emitted text. This covers
    /// what they structurally cannot: that the analyzer resolves capabilities declared
    /// inside a lambda body, and that the predicate reaches the generated file as the
    /// author's own expression.
    /// </remarks>
    [Fact]
    public void CompilesAConditionalIntoBranchThenJumpOtherwise()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .When(ctx => ctx.Get<Reservation>().Sku == "rare", rare => rare
                        .Step<CapturePayment>())
                    .Otherwise(common => common
                        .Step<ReserveInventory>().CompensateWith<ReleaseInventory>())
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldBeEmpty(run.Describe());

        var source = run.Plan;

        source.ShouldContainText("StepNode.ForCapability(0, Descriptors.Step0)", run.Describe());
        source.ShouldContainText("StepNode.ForBranch(1, 4)", "The false path skips the `then` block and its jump.");
        source.ShouldContainText("StepNode.ForCapability(2, Descriptors.Step2)", "The `then` block.");
        source.ShouldContainText("StepNode.ForJump(3, 5)", "and the jump that closes it.");
        source.ShouldContainText(
            "StepNode.ForCapability(4, Descriptors.Step4, Descriptors.Step4Compensation)",
            "The alternative, with the compensation it declared inside the lambda.");

        source.ShouldContainText(
            "public static readonly Func<FlowContext, bool> Step1 = ctx => ctx.Get<Reservation>().Sku == \"rare\";",
            "The predicate is the author's expression, verbatim.");

        source.ShouldContainText("return Conditions.Step1(ctx);", "reached by step index, like everything else.");
    }

    [Fact]
    public void AConditionalWithNoOtherwiseEmitsNoJump()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .When(ctx => true, rare => rare.Step<CapturePayment>())
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldBeEmpty(run.Describe());

        // The false path targets 3, one past the last step, so the flow ends. There is
        // nothing to skip, so no jump is emitted and no index is spent on one.
        run.Plan.ShouldContainText("StepNode.ForBranch(1, 3)", run.Describe());
        run.Plan.Contains("ForJump", StringComparison.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public void TheManifestPublishesTheDeclaredNestingRatherThanTheCompiledLayout()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .When(ctx => true, rare => rare.Step<CapturePayment>())
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        using var document = System.Text.Json.JsonDocument.Parse(run.ManifestJson.ShouldNotBeNull());

        var condition = document.RootElement.GetProperty("flows")[0].GetProperty("steps")[1];

        condition.GetProperty("kind").GetString().ShouldBe("Condition");
        condition.GetProperty("branches")[0][0].GetProperty("capability").GetString()
            .ShouldBe("payment.capture@2.1.0");
    }

    /// <summary>
    /// A <c>Switch</c> end to end: the selector's value type is inferred by C# and read
    /// back off the resolved symbol, each case block is walked out of a lambda argument,
    /// and the whole thing lands in the flat step array as one node with a target per case.
    /// </summary>
    [Fact]
    public void CompilesASwitchIntoOneNodeWithATargetPerCase()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Switch(ctx => ctx.Get<Reservation>().Sku == "rare" ? Channel.Retail : Channel.Wholesale)
                    .Case(Channel.Retail, retail => retail
                        .Step<CapturePayment>())
                    .Case(Channel.Wholesale, wholesale => wholesale
                        .Step<ReserveInventory>().CompensateWith<ReleaseInventory>())
                    .Default(rest => rest
                        .Step<CapturePayment>())
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldBeEmpty(run.Describe());

        var source = run.Plan;

        source.ShouldContainText("StepNode.ForCapability(0, Descriptors.Step0)", run.Describe());
        source.ShouldContainText(
            "StepNode.ForSwitch(1, new[] { 2, 4 }, defaultTarget: 6)",
            "One node, one target per case, and the default where a miss goes.");
        source.ShouldContainText("StepNode.ForCapability(2, Descriptors.Step2)", "The first case.");
        source.ShouldContainText("StepNode.ForJump(3, 7)", "and the jump that closes it.");
        source.ShouldContainText(
            "StepNode.ForCapability(4, Descriptors.Step4, Descriptors.Step4Compensation)",
            "The second case, with the compensation it declared inside the lambda.");
        source.ShouldContainText("StepNode.ForJump(5, 7)", "and the jump that closes that one.");
        source.ShouldContainText("StepNode.ForCapability(6, Descriptors.Step6)", "The `Default` block.");

        source.ShouldContainText(
            "public static readonly Func<FlowContext, Sample.Channel> Step1 = " +
            "ctx => ctx.Get<Reservation>().Sku == \"rare\" ? Channel.Retail : Channel.Wholesale;",
            "The selector is the author's expression verbatim, typed at what C# inferred.");

        source.ShouldContainText(
            "System.Collections.Generic.EqualityComparer<Sample.Channel>.Default.Equals(value, Channel.Retail)",
            "and each case value is the author's expression too, compared without boxing.");
    }

    [Fact]
    public void ASwitchWithNoDefaultSendsAMissToTheJoin()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Switch(ctx => Channel.Retail)
                    .Case(Channel.Retail, retail => retail.Step<CapturePayment>())
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldBeEmpty(run.Describe());

        // The default target is 3 — one past the last step — so a value matching nothing
        // ends the flow rather than failing it. The last case block is also the last
        // block, so it needs no closing jump and no index is spent on one.
        run.Plan.ShouldContainText("StepNode.ForSwitch(1, new[] { 2 }, defaultTarget: 3)", run.Describe());
        run.Plan.ShouldNotContainText("ForJump", "There is nothing after the only case to skip.");
    }

    [Fact]
    public void TheManifestPublishesASwitchAsBranchesWithoutItsValues()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Switch(ctx => Channel.Retail)
                    .Case(Channel.Retail, retail => retail.Step<CapturePayment>())
                    .Default(rest => rest.Step<ReserveInventory>())
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        var json = run.ManifestJson.ShouldNotBeNull();

        using var document = System.Text.Json.JsonDocument.Parse(json);

        var node = document.RootElement.GetProperty("flows")[0].GetProperty("steps")[1];

        node.GetProperty("kind").GetString().ShouldBe("Switch");
        node.GetProperty("branches").GetArrayLength().ShouldBe(2, "One case, then the default.");
        node.GetProperty("branches")[0][0].GetProperty("capability").GetString()
            .ShouldBe("payment.capture@2.1.0");
        node.GetProperty("branches")[1][0].GetProperty("capability").GetString()
            .ShouldBe("inventory.reserve@1.2.0");

        // The rule that makes this file safe to publish is structure only, never values.
        // `Channel.Retail` is a business value; that a flow branches on *something* is
        // structure. Only the second is here.
        json.Contains("Retail", StringComparison.Ordinal).ShouldBeFalse();
        json.Contains("Channel", StringComparison.Ordinal).ShouldBeFalse();
    }

    /// <summary>
    /// The generated code for a switch must actually build, not merely parse.
    /// </summary>
    /// <remarks>
    /// The one thing every other test here structurally cannot check. A selector field
    /// typed at the wrong thing, a case value that does not resolve in the generated
    /// file's namespace, or an <c>IStepDispatcher</c> member left unimplemented all parse
    /// perfectly and all break the consumer's build.
    /// </remarks>
    [Fact]
    public void TheGeneratedCodeForASwitchCompiles()
    {
        GeneratorHarness.GeneratedCompileErrorsIn(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Switch(ctx => ctx.Get<Reservation>().Sku)
                    .Case("rare", rare => rare
                        .Step<CapturePayment>().CompensateWith<ReleaseInventory>())
                    .Case("common", common => common
                        .Step<CapturePayment>())
                    .Default(rest => rest
                        .Step<ReserveInventory>())
                    .Return(ctx => new OrderResult("id"));
            }
            """)).ShouldBeEmpty();
    }
}
