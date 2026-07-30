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

        run.Ids.ShouldBeEmpty(run.Describe());
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
}
