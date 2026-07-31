using System.Linq;
using FlowX.Compiler.Model;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// <c>.Step&lt;TCapability, TStepIn&gt;(map)</c> through the compiler: the model, the
/// emitted source, the manifest, and the rule that refuses a mapping the capability
/// cannot accept.
/// </summary>
/// <remarks>
/// <para>
/// This overload was on <c>IFlowBuilder</c> from the start, was walked by the chain
/// walker, and did nothing: neither modelled nor emitted, so the mapping delegate never
/// ran and the step bound its input from the state bag as if the mapping were not there.
/// That is worse than an unimplemented method, because FLOWX1020 recommends it as the fix
/// and <c>StepBindingAnalyzer</c> deliberately stays silent on it for exactly that reason.
/// A remedy that does nothing is a diagnostic that suppresses itself.
/// </para>
/// <para>
/// The claim these tests pin is that the mapped value lives at the <em>call site</em> and
/// nowhere else — not in the state bag. The bag is keyed on <c>typeof(T)</c>, and a
/// mapping exists precisely because no earlier step put a <c>TStepIn</c> there; writing
/// one back would invent a producer FLOWX1020 cannot see and would make two mapped steps
/// of the same type overwrite each other.
/// </para>
/// <para>
/// The generator tests use <c>GeneratedCompileErrorsIn</c> rather than only reading the
/// text. The mapping's result is passed as an argument to a typed capability, so a wrong
/// delegate type is a <c>CS1503</c> in generated source that a parse-only test cannot see
/// — the same class of defect <c>ctx.Input</c> was.
/// </para>
/// </remarks>
public sealed class StepInputMappingTests
{
    private const string Preamble = """
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record PlaceOrder(string Sku, int Quantity);
        public sealed record OrderResult(string Id);
        public sealed record ValidatedOrder(string Sku, int Quantity);
        public sealed record CaptureRequest(string Sku, int Quantity);
        public sealed record Payment(string ReceiptId);
        public sealed record Refund(string ReceiptId);

        [Capability("order.validate", Version = "1.0.0",
            Authorization = Authorization.Authenticated, Idempotent = true)]
        public sealed class ValidateOrder : ICapability<PlaceOrder, ValidatedOrder>
        {
            public ValueTask<Result<ValidatedOrder>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new ValidatedOrder(input.Sku, input.Quantity)));
        }

        [Capability("payment.capture", Version = "2.1.0",
            Authorization = Authorization.Permission, Permission = "payment.write")]
        public sealed class CapturePayment : ICapability<CaptureRequest, Payment>
        {
            public ValueTask<Result<Payment>> ExecuteAsync(CaptureRequest input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Payment(input.Sku)));
        }

        [Capability("payment.refund", Version = "1.0.0",
            Authorization = Authorization.Internal, Idempotent = true)]
        public sealed class RefundPayment : ICapability<CaptureRequest, Refund>
        {
            public ValueTask<Result<Refund>> ExecuteAsync(CaptureRequest input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Refund(input.Sku)));
        }
        """;

    private static string WithFlow(string flowDeclaration) => Preamble + "\n\n" + flowDeclaration;

    private const string Mapped = """
        [Flow("order.place")]
        public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
        {
            protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                .Step<ValidateOrder>()
                .Step<CapturePayment, CaptureRequest>(ctx => new CaptureRequest(
                    ctx.Get<ValidatedOrder>().Sku,
                    ctx.Input.Quantity))
                .Return(ctx => new OrderResult(ctx.Get<Payment>().ReceiptId));
        }
        """;

    private static string PlanFor(string source) => GeneratorHarness
        .Run(source)
        .Sources
        .Single(s => s.HintName == "Sample.PlaceOrderFlow.Flow.g.cs")
        .Source;

    [Fact]
    public void TheTestPreambleItselfCompiles()
        // Guards the harness. Without it, a typo in the shared source shows up as "the
        // generator found no flows" and the next hour goes into the wrong file.
        => GeneratorHarness.CompileErrorsIn(WithFlow(Mapped)).ShouldBeEmpty();

    // ------------------------------------------------------------------------ the model

    [Fact]
    public void AMappedStepIsAnOrdinaryCapabilityStepCarryingItsMapping()
    {
        var step = StepModel.Capability(
            1,
            "Sample.CapturePayment",
            "payment.capture",
            "2.1.0",
            isIdempotent: false,
            capabilityInput: "Sample.CaptureRequest",
            capabilityOutput: "Sample.Payment",
            stepInputMap: "ctx => new CaptureRequest(ctx.Input.Sku, 1)",
            stepInputTypeName: "Sample.CaptureRequest");

        step.Kind.ShouldBe(StepKindModel.Capability,
            "One kind for both overloads. Only where the input comes from differs — the " +
            "descriptor, the compensation and the manifest entry are blind to it.");
        step.HasInputMapping.ShouldBeTrue();
        step.NextIndex.ShouldBe(2, "A mapped step occupies one index, like any other.");
    }

    [Fact]
    public void AnUnmappedStepCarriesNoMapping()
    {
        var step = StepModel.Capability(
            0, "Sample.ValidateOrder", "order.validate", "1.0.0", isIdempotent: true);

        step.HasInputMapping.ShouldBeFalse();
        step.StepInputMap.ShouldBeNull();
        step.StepInputTypeName.ShouldBeNull();
    }

    [Fact]
    public void TheMappingSurvivesAttachingACompensation()
    {
        // `.CompensateWith<T>()` rebuilds the step with `with`, and a property it forgot
        // would drop the mapping and silently restore the old bind-from-the-bag behaviour
        // on exactly the steps that undo something.
        var step = StepModel
            .Capability(
                1, "Sample.CapturePayment", "payment.capture", "2.1.0", isIdempotent: false,
                stepInputMap: "ctx => new CaptureRequest(ctx.Input.Sku, 1)",
                stepInputTypeName: "Sample.CaptureRequest")
            .WithCompensation(StepModel.Capability(
                1, "Sample.RefundPayment", "payment.refund", "1.0.0", isIdempotent: true));

        step.HasInputMapping.ShouldBeTrue();
        step.StepInputTypeName.ShouldBe("Sample.CaptureRequest");
    }

    // -------------------------------------------------------------------- the generator

    [Fact]
    public void TheGeneratedCodeCompiles()
        // The claim the emitter cannot make for itself. The mapping's result is passed as
        // an argument to a typed capability, so a wrong delegate type is a CS1503 in
        // generated source that a parse-only test cannot see.
        => GeneratorHarness.GeneratedCompileErrorsIn(WithFlow(Mapped)).ShouldBeEmpty();

    [Fact]
    public void TheMappingIsACachedStaticTypedAtWhatTheLambdaReturns()
    {
        var plan = PlanFor(WithFlow(Mapped));

        plan.ShouldContainText("private static class StepInputs",
            "A field, not a lambda per execution: the same reason predicates, selectors " +
            "and sub-flow mappings are cached statics. Budget B2 is a hard zero.");
        plan.ShouldContainText(
            "public static readonly Func<FlowContext<Sample.PlaceOrder>, Sample.CaptureRequest> Step1",
            "Typed at the TStepIn C# inferred, and taking the typed view — which is the " +
            "only parameter type on which ctx.Input resolves.");
    }

    [Fact]
    public void TheMappingRunsAtTheStepAndFeedsTheCapability()
    {
        var plan = PlanFor(WithFlow(Mapped));

        plan.ShouldContainText(
            "await _capturePayment.ExecuteAsync(StepInputs.Step1(Typed(ctx)), ctx, ct)",
            "This is the whole of the feature: the mapping produces the step's input. " +
            "Before it, the delegate was walked and then ignored, and the step bound " +
            "ctx.Get<CaptureRequest>() from a bag nothing had written one into.");
        plan.ShouldNotContainText("ctx.Get<Sample.CaptureRequest>()",
            "A mapped step does not read the bag. That is what the mapping is for.");
    }

    [Fact]
    public void TheMappedValueIsNeverWrittenBackIntoTheStateBag()
    {
        var plan = PlanFor(WithFlow(Mapped));

        plan.ShouldNotContainText("ctx.Set(StepInputs",
            "The bag is keyed on typeof(T) and the mapping exists precisely because no " +
            "earlier step put a CaptureRequest there. Writing one back would invent a " +
            "producer FLOWX1020 cannot see, and two mapped steps of the same type would " +
            "overwrite each other's input.");

        // The step's *output* still goes in, exactly as an unmapped step's does — that is
        // what the next step and the Return projection bind to.
        plan.ShouldContainText("ctx.Set(result.Value);",
            "The step's output still goes into the bag — that is what the next step and " +
            "the Return projection bind to.");
    }

    [Fact]
    public void TwoMappedStepsOfTheSameTypeGetTheirOwnDelegateAndCannotCollide()
    {
        var plan = PlanFor(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ValidateOrder>()
                    .Step<CapturePayment, CaptureRequest>(ctx => new CaptureRequest(ctx.Input.Sku, 1))
                    .Step<RefundPayment, CaptureRequest>(ctx => new CaptureRequest(ctx.Input.Sku, 2))
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        plan.ShouldContainText("Sample.CaptureRequest> Step1",
            "One delegate for the first mapped step.");
        plan.ShouldContainText("Sample.CaptureRequest> Step2",
            "One delegate per step, keyed on the flat index rather than on the type. Two " +
            "mapped steps of the same contract are independent because neither value ever " +
            "reaches a slot the other could overwrite.");
        plan.ShouldContainText("_capturePayment.ExecuteAsync(StepInputs.Step1(Typed(ctx))",
            "Each call site names its own delegate.");
        plan.ShouldContainText("_refundPayment.ExecuteAsync(StepInputs.Step2(Typed(ctx))",
            "Each call site names its own delegate.");
    }

    [Fact]
    public void AMappedStepsCompensationIsHandedTheSameMappedInput()
    {
        var plan = PlanFor(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ValidateOrder>()
                    .Step<CapturePayment, CaptureRequest>(ctx => new CaptureRequest(ctx.Input.Sku, 1))
                        .CompensateWith<RefundPayment>()
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        plan.ShouldContainText(
            "await _refundPayment.ExecuteAsync(StepInputs.Step1(Typed(ctx)), ctx, ct)",
            "A compensation undoes the step's own input, and for a mapped step that input " +
            "was never in the bag. Reading ctx.Get<CaptureRequest>() here would throw at " +
            "the exact moment the flow is already unwinding.");
    }

    [Fact]
    public void TheMappingCarriesALineDirectiveBackToTheAuthorsLambda()
    {
        var source = WithFlow(Mapped);
        var plan = PlanFor(source);

        // Derived from the source rather than written down, so the assertion survives an
        // edit to the shared preamble instead of turning into a number nobody dares touch.
        var line = source[..source.IndexOf(
            "ctx => new CaptureRequest(", System.StringComparison.Ordinal)]
            .Count(c => c == '\n') + 1;

        plan.ShouldContainText(
            System.FormattableString.Invariant($"#line {line} \"/src/Flows/Sample.cs\""),
            "A breakpoint on the mapping must land on the mapping the author wrote — the " +
            "same promise the generated header makes for every other copied expression.");
    }

    [Fact]
    public void AFlowThatMapsNothingEmitsNoMappingClass()
    {
        var plan = PlanFor(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ValidateOrder>()
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        plan.ShouldNotContainText("StepInputs",
            "No mapping to cache, so no class for them — and no Typed helper either, " +
            "which is what keeps a linear flow's generated source the size it was.");
        plan.ShouldNotContainText("private static FlowContext<Sample.PlaceOrder> Typed(",
            "A linear flow needs no typed view, and emitting an unused helper would grow " +
            "every generated file for a shape most flows do not have.");
    }

    // -------------------------------------------------------------------- the manifest

    [Fact]
    public void TheManifestPublishesTheStepUnchanged()
    {
        var manifest = GeneratorHarness.Run(WithFlow(Mapped)).ManifestJson.ShouldNotBeNull();

        manifest.ShouldContainText("\"capability\": \"payment.capture@2.1.0\"",
            "A mapped step is a capability step. How its input is supplied is compiled " +
            "code, not published structure.");
        manifest.ShouldContainText("\"input\": \"Sample.CaptureRequest\"",
            "The mapped contract does reach the manifest — as the capability's declared " +
            "input, which is the only type involved. FLOWX1028 is what makes those two " +
            "the same type, so there is no second one to publish.");
        manifest.ShouldNotContainText("ctx =>",
            "Structure only, never values. A type is structure; a lambda's source text " +
            "names the shape of somebody's data and is not.");
        manifest.ShouldNotContainText("CaptureRequest(",
            "The mapping's body must not reach a document that is safe to publish.");
    }

    // ------------------------------------------------------------------------ FLOWX1028

    [Fact]
    public void AMappingProducingAContractTheCapabilityCannotAcceptIsRefused()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ValidateOrder>()
                    .Step<CapturePayment, ValidatedOrder>(ctx => ctx.Get<ValidatedOrder>())
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldContain("FLOWX1028", run.Describe());
        var message = run.Describe();

        message.ShouldContainText("payment.capture", "The message names the step.");
        message.ShouldContainText("Sample.ValidatedOrder", "and what the mapping produced,");
        message.ShouldContainText("Sample.CaptureRequest", "and what the capability consumes.");
    }

    [Fact]
    public void ARefusedMappingDoesNotAlsoEmitADispatcherThatCannotCompile()
    {
        // The point of reporting it at all. C# constrains TStepIn to nothing, so without
        // FLOWX1028 the mistake reaches the author as a CS1503 inside generated source —
        // which is the failure mode ctx.Input had, and the reason this harness compiles
        // what the generator wrote rather than merely parsing it.
        var source = WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ValidateOrder>()
                    .Step<CapturePayment, ValidatedOrder>(ctx => ctx.Get<ValidatedOrder>())
                    .Return(ctx => new OrderResult("id"));
            }
            """);

        GeneratorHarness.GeneratedCompileErrorsIn(source)
            .ShouldNotContain(e => e.StartsWith("CS1503", System.StringComparison.Ordinal));
    }

    [Fact]
    public void AMappingProducingASubtypeOfTheDeclaredInputIsAccepted()
    {
        // Assignability, not identity — the opposite of the rule FLOWX1020 applies, and
        // deliberately so. That rule asks what a Dictionary<Type, object> lookup finds and
        // a lookup is exact; this asks what a C# argument accepts.
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using FlowX;

            namespace Sample;

            public sealed record PlaceOrder(string Sku);
            public sealed record OrderResult(string Id);
            public record Request(string Sku);
            public sealed record PriorityRequest(string Sku) : Request(Sku);
            public sealed record Payment(string ReceiptId);

            [Capability("payment.capture", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class CapturePayment : ICapability<Request, Payment>
            {
                public ValueTask<Result<Payment>> ExecuteAsync(Request input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new Payment(input.Sku)));
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<CapturePayment, PriorityRequest>(ctx => new PriorityRequest(ctx.Input.Sku))
                    .Return(ctx => new OrderResult(ctx.Get<Payment>().ReceiptId));
            }
            """;

        GeneratorHarness.Run(source).Ids.ShouldNotContain("FLOWX1028");
        GeneratorHarness.GeneratedCompileErrorsIn(source).ShouldBeEmpty();
    }

    // ------------------------------------------------------------------------ FLOWX1011

    [Fact]
    public void AnImpureMappingIsStillReported()
    {
        // `PredicatePurityAnalyzer` listed this delegate before anything executed it, on
        // the grounds that "a rule that waits for the emitter is a rule that arrives after
        // the code it was meant to stop". This is the test that the rule survived the
        // delegate becoming real: a clock read in the mapping is now a value that reaches
        // a capability, so the replay contract in 06 §5 depends on it.
        var ids = GeneratorHarness.Analyze(
            WithFlow("""
                [Flow("order.place")]
                public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
                {
                    protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                        .Step<ValidateOrder>()
                        .Step<CapturePayment, CaptureRequest>(ctx => new CaptureRequest(
                            ctx.Input.Sku,
                            System.DateTime.UtcNow.Hour))
                        .Return(ctx => new OrderResult("id"));
                }
                """),
            new Analysis.PredicatePurityAnalyzer());

        ids.ShouldContain("FLOWX1011");
    }

    [Fact]
    public void APureMappingIsNotReported()
    {
        var ids = GeneratorHarness.Analyze(
            WithFlow(Mapped), new Analysis.PredicatePurityAnalyzer());

        ids.ShouldNotContain("FLOWX1011",
            "A mapping that reads only the context, the flow input and prior step results " +
            "is exactly what the determinism rule permits. A rule that fires on the valid " +
            "shape gets suppressed, and a suppressed rule protects nothing.");
    }
}
