using System;
using FlowX;
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

        // Two reversals of the same capture, differing only in the declaration FLOWX1014
        // reads. The pair is what makes the compensation half of the rule testable: a
        // check that looked at the *step* would stay silent for both, because
        // payment.capture is not idempotent either way.
        [Capability("payment.refund", Version = "1.0.0",
            Authorization = Authorization.Permission, Permission = "payment.write",
            SideEffects = new[] { "payment-gateway" })]
        public sealed class RefundPayment : ICapability<PlaceOrder, OrderResult>
        {
            public ValueTask<Result<OrderResult>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new OrderResult(input.Sku)));
        }

        [Capability("payment.reverse", Version = "1.0.0",
            Authorization = Authorization.Permission, Permission = "payment.write",
            Idempotent = true, SideEffects = new[] { "payment-gateway" })]
        public sealed class ReverseCapture : ICapability<PlaceOrder, OrderResult>
        {
            public ValueTask<Result<OrderResult>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new OrderResult(input.Sku)));
        }
        """;

    /// <summary>The shared preamble, plus one flow declaration.</summary>
    /// <remarks>
    /// Internal rather than private because <c>EmitStagingTests</c> asks the same questions
    /// about the same sample types, and a second copy of the preamble would be a second set
    /// of contracts to keep in step.
    /// </remarks>
    internal static string WithFlow(string flowDeclaration) => Preamble + "\n\n" + flowDeclaration;

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

    // ------------------------------------------- FLOWX1014 over the compensating capability

    /// <summary>
    /// The half of FLOWX1014 that was never checked: a retry over the <em>undo</em>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The analyzer asked one question — is the <em>step</em> idempotent, and does the set
    /// contain <c>Retry</c> — and the string <c>CompensationRetry</c> appeared nowhere in it.
    /// A compensation retry over a non-idempotent reversal was therefore reported by nothing,
    /// and running a reversal twice is a second reversal.
    /// </para>
    /// <para>
    /// It was unreachable rather than harmless: until <c>.WithPolicy</c> reached the plan
    /// nothing in the DSL could declare a <c>CompensationRetry</c> at all, so the rule's gap
    /// cost nothing. The moment the declaration became reachable the gap became a live hole,
    /// which is why the rule is extended here rather than left to the runtime.
    /// </para>
    /// </remarks>
    [Fact]
    public void ReportsFLOWX1014WhenCompensationRetryIsAttachedToANonIdempotentCompensation()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            public static class Policies
            {
                public static readonly PolicySet Undo = PolicySet
                    .Named("payment-undo")
                    .CompensationRetry(attempts: 3);
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<CapturePayment>().CompensateWith<RefundPayment>()
                        .WithPolicy(Policies.Undo)
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldContain("FLOWX1014", run.Describe());

        run.Describe().ShouldContain(
            "payment.refund",
            Case.Sensitive,
            "The report must name the capability that would run twice. Naming payment.capture " +
            "would send the reader to the step, which is not the declaration that is wrong.");

        run.Describe().ShouldContain(
            "CompensationRetry",
            Case.Sensitive,
            "and it must name the policy that is at fault, because the same set may " +
            "legitimately carry a Retry for the forward step.");
    }

    /// <summary>The shape the rule has to permit — banking's, and the ordinary saga's.</summary>
    /// <remarks>
    /// <c>payment.capture</c> is not idempotent and never will be; its reversal is, because it
    /// is written against the idempotency key. It is the reversal that the compensation retry
    /// would re-dispatch, so it is the reversal's declaration that decides. A rule that read
    /// the step's declaration instead would refuse every real saga.
    /// </remarks>
    [Fact]
    public void AllowsACompensationRetryOnAnIdempotentCompensation()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            public static class Policies
            {
                public static readonly PolicySet Undo = PolicySet
                    .Named("payment-undo")
                    .CompensationRetry(attempts: 3);
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<CapturePayment>().CompensateWith<ReverseCapture>()
                        .WithPolicy(Policies.Undo)
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldNotContain("FLOWX1014", run.Describe());
    }

    /// <summary>A forward <c>Retry</c> is still judged by the step, not by its undo.</summary>
    /// <remarks>
    /// The two halves read different declarations off the same call, and widening the rule
    /// must not blur them: an idempotent reversal does not make a capture safe to retry.
    /// </remarks>
    [Fact]
    public void AnIdempotentCompensationDoesNotExcuseARetryOnTheStep()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            public static class Policies
            {
                public static readonly PolicySet Gateway = PolicySet
                    .Named("payment-gateway")
                    .Retry(3);
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<CapturePayment>().CompensateWith<ReverseCapture>()
                        .WithPolicy(Policies.Gateway)
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldContain("FLOWX1014", run.Describe());

        run.Describe().ShouldContain(
            "payment.capture",
            Case.Sensitive,
            "A Retry wraps the step, so the step's declaration is the one that decides it.");
    }

    /// <summary>
    /// The pairing is caught whichever of the two calls completes it.
    /// </summary>
    /// <remarks>
    /// <c>.CompensateWith</c> and <c>.WithPolicy</c> both return <c>IStepBuilder</c>, so both
    /// orders are legal C# and the samples use the first. A check that only ran when the
    /// policy arrived would be silent for every author who wrote the other one — a rule that
    /// depends on the order of two interchangeable calls is a rule nobody can rely on.
    /// </remarks>
    [Fact]
    public void ReportsFLOWX1014WhenTheCompensationIsDeclaredAfterThePolicy()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            public static class Policies
            {
                public static readonly PolicySet Undo = PolicySet
                    .Named("payment-undo")
                    .CompensationRetry(attempts: 3);
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<CapturePayment>().WithPolicy(Policies.Undo)
                        .CompensateWith<RefundPayment>()
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldContain("FLOWX1014", run.Describe());
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

    // ------------------------------------------------------- .WithPolicy reaches the plan

    /// <summary>
    /// The declared compensation retry arrives on the node the unwind reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the assertion that closes WP-57's named gap.</strong>
    /// <c>PolicySet.CompensationRetry</c> shipped with an executor — <c>FlowEngine.UndoAsync</c>
    /// honours attempts, backoff and retryable categories — and no way to reach it, because the
    /// emitter passed no <c>PolicyChain</c> to <c>StepNode.ForCapability</c>. Every compiled
    /// plan therefore reported <c>HasCompensationPolicies == false</c> and every failing undo
    /// was dispatched exactly once, whatever the author wrote and whatever the manifest said.
    /// </para>
    /// <para>
    /// The set is copied verbatim rather than reconstructed, for the reason a <c>merge:</c>
    /// expression is: <c>PolicySet</c>'s composition is fixed at compile time but its parameter
    /// <em>values</em> are not, so a set whose attempt count comes from configuration must
    /// still compile into the plan as written.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADeclaredCompensationRetryReachesThePlan()
    {
        var source = WithFlow("""
            public static class Policies
            {
                public static readonly PolicySet Undo = PolicySet
                    .Named("inventory-undo")
                    .CompensationRetry(attempts: 3);
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()
                        .WithPolicy(Policies.Undo)
                    .Return(ctx => new OrderResult("id"));
            }
            """);

        GeneratorHarness.GeneratedCompileErrorsIn(source).ShouldBeEmpty();

        var run = GeneratorHarness.Run(source);

        run.Ids.ShouldBeEmpty(run.Describe());

        run.Plan.ShouldContainText(
            "compensationPolicies: PolicyChain.ForCompensation(Policies.Undo, Descriptors.Step0Compensation)",
            "The chain wraps the compensating capability, so it is built against the " +
            "compensation's descriptor and not the step's.");

        run.Plan.ShouldNotContainText(
            "policies: PolicyChain.ForStep(Policies.Undo",
            "This set says nothing about the forward path, so nothing is emitted for it.");
    }

    /// <summary>
    /// <c>PolicySet.CompensationDefault</c> — the set the documents recommend — reaches the
    /// plan and retries the undo five times.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>docs/06-Execution-Engine.md</c> §7 rule 2 names this set as the documented default
    /// for compensation, and <c>docs/diagnostics/FLOWX1033.md</c> offers it as one of the two
    /// repairs for a set that promises an undo the step has not got. It lives in
    /// <c>FlowX.Abstractions</c>, so in every consuming compilation it arrives as metadata and
    /// its symbol has no <c>DeclaringSyntaxReferences</c> at all.
    /// </para>
    /// <para>
    /// The assertion is on the loaded plan rather than on the emitted text, because the text
    /// is not the claim: the claim is that the engine reads five attempts off the node while
    /// unwinding, and only <c>HasCompensationPolicies</c> and <c>CompensationRetry.Attempts</c>
    /// say so.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheDocumentedDefaultCompensationSetReachesThePlan()
    {
        var source = WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()
                        .WithPolicy(PolicySet.CompensationDefault)
                    .Return(ctx => new OrderResult("id"));
            }
            """);

        GeneratorHarness.GeneratedCompileErrorsIn(source).ShouldBeEmpty();

        var plan = GeneratorHarness.GeneratedPlanFor(source, "Sample.PlaceOrderFlow");

        plan.HasCompensationPolicies.ShouldBeTrue(
            "The recommended way to retry an undo has to be a way that works.");

        plan.Graph[0].CompensationRetry.Attempts.ShouldBe(
            5,
            "docs/06-Execution-Engine.md §7 rule 2 — five attempts, not the one a step with " +
            "no declared chain gets.");
    }

    /// <summary>
    /// Two <c>.WithPolicy(...)</c> calls on one step: the second replaces the first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>FlowAnalyzer.AttachPolicy</c> calls <c>StepModel.WithPolicy</c>, which assigns
    /// <c>PolicySetName</c> and <c>PolicyKinds</c> rather than adding to them, so a step
    /// carries one set and it is the last one written. This is what
    /// <c>docs/diagnostics/FLOWX1034.md</c> is about, and it is why FLOWX1033's repair is
    /// "split the set" and not "apply another one alongside it".
    /// </para>
    /// <para>
    /// Asserted on the manifest as well as the plan, because the two lose different things:
    /// the plan loses the <c>Timeout</c> the step declared, and the published contract loses
    /// the entry a reviewer would have read it from.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASecondPolicySetOnOneStepReplacesTheFirst()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            public static class Policies
            {
                public static readonly PolicySet Ledger = PolicySet
                    .Named("ledger")
                    .Timeout(System.TimeSpan.FromSeconds(5));

                public static readonly PolicySet Undo = PolicySet
                    .Named("inventory-undo")
                    .CompensationRetry(attempts: 5);
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()
                        .WithPolicy(Policies.Ledger)
                        .WithPolicy(Policies.Undo)
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Plan.ShouldNotContainText(
            "Policies.Ledger",
            "The first set reaches no plan node at all — the second call overwrote it.");

        run.ManifestJson.ShouldNotBeNull().ShouldNotContain(
            "\"kind\": \"Timeout\"",
            customMessage: "And it reaches no published contract either.");
    }

    /// <summary>
    /// <c>.CompensationRetry(attempts: 1)</c>: the manifest publishes a retried undo and the
    /// plan dispatches it once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The evidence behind <c>docs/diagnostics/FLOWX1035.md</c>, read off the two artifacts
    /// that disagree rather than argued from the source. <c>CompensationPolicy.IsRetrying</c>
    /// is <c>Attempts &gt; 1</c>, so a single attempt leaves
    /// <c>ExecutionPlan.HasCompensationPolicies</c> false and <c>FlowEngine.CompensateAsync</c>
    /// takes <c>CompensationPolicy.None</c> for every step in the plan — the same behaviour as
    /// a step that declared no chain at all. <c>ManifestWriter.WritePolicies</c> publishes the
    /// kind and its stage and no parameters, so nothing in the published contract distinguishes
    /// this from five attempts.
    /// </para>
    /// <para>
    /// Pinned rather than fixed. Making one attempt mean two would contradict the parameter's
    /// own documentation — "how many times the undo may be dispatched, including the first" —
    /// and would silently double a reversal for every author who wrote the honest thing.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASingleAttemptIsPublishedAsARetryAndDispatchedOnce()
    {
        var source = WithFlow("""
            public static class Policies
            {
                public static readonly PolicySet Undo = PolicySet
                    .Named("inventory-undo")
                    .CompensationRetry(attempts: 1);
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()
                        .WithPolicy(Policies.Undo)
                    .Return(ctx => new OrderResult("id"));
            }
            """);

        GeneratorHarness.Run(source).ManifestJson.ShouldNotBeNull().ShouldContain(
            "\"kind\": \"CompensationRetry\"",
            customMessage: "The published contract says this step's undo is retried.");

        var plan = GeneratorHarness.GeneratedPlanFor(source, "Sample.PlaceOrderFlow");

        plan.Graph[0].CompensationRetry.Attempts.ShouldBe(1);

        plan.HasCompensationPolicies.ShouldBeFalse(
            "And the plan the engine walks says it is dispatched once, which is what a step " +
            "with no declared chain at all already gets.");
    }

    /// <summary>
    /// The compensation's descriptor states the compensation's own idempotency.
    /// </summary>
    /// <remarks>
    /// It used to state <c>true</c>, for every compensation of every flow, written as a
    /// literal. The consequence was not a cosmetic one: <c>PolicyChain.ForCompensation</c>
    /// refuses a compensation retry over a capability that is not idempotent, and that
    /// refusal was reachable only from a hand-built plan. Against everything the compiler
    /// produced the backstop was answering a question about a value it had itself invented.
    /// </remarks>
    [Fact]
    public void TheCompensationDescriptorCarriesTheCompensationsOwnIdempotency()
    {
        var source = WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<CapturePayment>().CompensateWith<RefundPayment>()
                    .Return(ctx => new OrderResult("id"));
            }
            """);

        GeneratorHarness.GeneratedCompileErrorsIn(source).ShouldBeEmpty();

        GeneratorHarness.Run(source).Plan.ShouldContainText(
            "Step0Compensation = CapabilityDescriptor.Create(\"payment.refund\", \"1.0.0\", false, \"payment-gateway\")",
            "payment.refund declares neither idempotency nor an empty effect list, and the " +
            "descriptor the plan carries is what the runtime checks a policy against.");
    }

    /// <summary>
    /// The runtime backstop can refuse a plan the compiler produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>PolicyChainTests</c> and <c>CompensationPolicyTests</c> both prove the refusal, and
    /// both hand-build the descriptor they hand it — so both passed throughout the period in
    /// which no generated plan could ever trigger it. This one reads the descriptor out of a
    /// compiled flow's own <c>Plan</c>, which is the only way to ask whether the check applies
    /// to the artifacts the product actually ships.
    /// </para>
    /// <para>
    /// The flow declares no policy, deliberately. Now that the analyzer reports the pairing,
    /// a flow that declared one would not compile — so the only way to reach the runtime with
    /// a generated descriptor is to attach the chain here, which is also the shape of every
    /// case the analyzer cannot see: a set assembled where <c>PolicySetReader</c> cannot read it.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheRuntimeRefusesACompensationRetryOverAGeneratedNonIdempotentCompensation()
    {
        var plan = GeneratorHarness.GeneratedPlanFor(
            WithFlow("""
                [Flow("order.place")]
                public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
                {
                    protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                        .Step<CapturePayment>().CompensateWith<RefundPayment>()
                        .Return(ctx => new OrderResult("id"));
                }
                """),
            "Sample.PlaceOrderFlow");

        var compensation = plan.Graph[0].Compensation.ShouldNotBeNull();

        compensation.Id.ShouldBe("payment.refund");
        compensation.IsIdempotent.ShouldBeFalse(
            "The emitter wrote a literal true here, so every compiled compensation claimed " +
            "to be safe to run twice whatever its author declared.");

        var refusal = Should.Throw<InvalidFlowPlanException>(() => PolicyChain.ForCompensation(
            PolicySet.Named("payment-undo").CompensationRetry(attempts: 3),
            compensation));

        refusal.Message.ShouldContain("payment.refund");
    }

    /// <summary>The same backstop stays out of the way of an honest idempotent reversal.</summary>
    [Fact]
    public void TheRuntimeAcceptsACompensationRetryOverAGeneratedIdempotentCompensation()
    {
        var plan = GeneratorHarness.GeneratedPlanFor(
            WithFlow("""
                [Flow("order.place")]
                public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
                {
                    protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                        .Step<CapturePayment>().CompensateWith<ReverseCapture>()
                        .Return(ctx => new OrderResult("id"));
                }
                """),
            "Sample.PlaceOrderFlow");

        var compensation = plan.Graph[0].Compensation.ShouldNotBeNull();

        compensation.IsIdempotent.ShouldBeTrue();

        PolicyChain.ForCompensation(
                PolicySet.Named("payment-undo").CompensationRetry(attempts: 3),
                compensation)
            .Ordered.Length.ShouldBe(1, "The pairing is legal, so the chain keeps the retry.");
    }

    /// <summary>The forward half of a set reaches the plan as well — carried, still not run.</summary>
    /// <remarks>
    /// Carrying it is not executing it. Nothing in <c>FlowEngine</c> reads
    /// <c>StepNode.Policies</c>; the Policy Engine is P4, and the forward path executes zero
    /// policies before and after this change. What changes is that the plan now says what the
    /// manifest says, so the two artifacts stop disagreeing about the same source line.
    /// </remarks>
    [Fact]
    public void TheForwardHalfOfASetReachesThePlanToo()
    {
        var source = WithFlow("""
            public static class Policies
            {
                public static readonly PolicySet Ledger = PolicySet
                    .Named("ledger")
                    .Timeout(System.TimeSpan.FromSeconds(5))
                    .Audit("financial")
                    .CompensationRetry(attempts: 5);
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()
                        .WithPolicy(Policies.Ledger)
                    .Return(ctx => new OrderResult("id"));
            }
            """);

        GeneratorHarness.GeneratedCompileErrorsIn(source).ShouldBeEmpty();

        var run = GeneratorHarness.Run(source);

        run.Plan.ShouldContainText(
            "policies: PolicyChain.ForStep(Policies.Ledger, Descriptors.Step0)",
            "Timeout and Audit wrap the step, and are checked against the step's capability.");

        run.Plan.ShouldContainText(
            "compensationPolicies: PolicyChain.ForCompensation(Policies.Ledger, Descriptors.Step0Compensation)",
            "and the compensation retry wraps the undo, out of the same declared set.");
    }

    /// <summary>A step with no <c>.WithPolicy</c> emits no chain at all.</summary>
    /// <remarks>
    /// <c>ExecutionPlan.HasCompensationPolicies</c> exists so a flow that declares nothing pays
    /// nothing — budget B2's hard zero on the ephemeral path is gated on it. An emitter that
    /// passed <c>PolicyChain.Empty</c> everywhere would still be correct and would still cost a
    /// call per node at type initialisation for a feature the flow does not use.
    /// </remarks>
    [Fact]
    public void AStepWithNoDeclaredPolicyCarriesNoChain()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Plan.ShouldContainText(
            "StepNode.ForCapability(0, Descriptors.Step0, Descriptors.Step0Compensation),",
            "The node the emitter has always produced, unchanged.");

        run.Plan.ShouldNotContainText("PolicyChain", "Nothing declared, nothing carried.");
    }

    /// <summary>
    /// A set the compiler could not read is not emitted, for the reason it is not diagnosed.
    /// </summary>
    /// <remarks>
    /// <c>PolicySetReader</c> returns nothing for a set that is not a field or property
    /// initialiser in source, which is what keeps FLOWX1014 from firing on a guess. The emitter
    /// reads the same signal: an expression the compiler could not resolve to a declared set is
    /// an expression it cannot promise will bind — or even be legal — inside a static
    /// initialiser in the generated file, and emitting it would turn a working build into a
    /// compile error in a file the author did not write.
    /// </remarks>
    [Fact]
    public void APolicySetTheCompilerCouldNotReadIsNotEmitted()
    {
        var source = WithFlow("""
            public static class Policies
            {
                public static PolicySet Build() => PolicySet.Named("x").CompensationRetry(3);
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()
                        .WithPolicy(Policies.Build())
                    .Return(ctx => new OrderResult("id"));
            }
            """);

        GeneratorHarness.GeneratedCompileErrorsIn(source).ShouldBeEmpty();
        GeneratorHarness.Run(source).Plan.ShouldNotContainText(
            "PolicyChain",
            "A set built at run time cannot be inspected at compile time, and a guess in a " +
            "generated static initialiser is a build the author cannot fix.");
    }

    /// <summary>
    /// A compensation retry on a step with no compensation is dropped rather than emitted.
    /// </summary>
    /// <remarks>
    /// <c>StepNode.ForCapability</c> refuses a compensation chain with no compensation to
    /// wrap — "a policy chain that wraps nothing is a promise the unwind cannot keep" — and
    /// there is no compensating descriptor to validate the retry against in any case. So the
    /// emitter leaves it out rather than producing a plan whose type initialiser throws.
    /// <strong>This is a silent no-op, and it wants a diagnostic</strong>; raising one is
    /// <c>FlowAnalyzer</c>'s business and is proposed rather than taken here.
    /// </remarks>
    [Fact]
    public void ACompensationRetryOnAStepWithNoCompensationIsNotEmitted()
    {
        var source = WithFlow("""
            public static class Policies
            {
                public static readonly PolicySet Undo = PolicySet
                    .Named("undo")
                    .CompensationRetry(attempts: 3);
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>().WithPolicy(Policies.Undo)
                    .Return(ctx => new OrderResult("id"));
            }
            """);

        GeneratorHarness.GeneratedCompileErrorsIn(source).ShouldBeEmpty();
        GeneratorHarness.Run(source).Plan.ShouldNotContainText(
            "PolicyChain",
            "There is no compensation for the chain to wrap, and StepNode refuses one.");
    }

    /// <summary>The set is named where it was written, whatever the argument's spelling.</summary>
    /// <remarks>
    /// A named argument is legal C# — <c>.WithPolicy(policy: Policies.Undo)</c> — and the
    /// emitter copies the expression into a call of its own, where the argument's <em>name</em>
    /// is not a thing that can travel with it. Carrying the whole argument text would produce
    /// <c>ForCompensation(policy: Policies.Undo, Descriptors.Step0Compensation)</c>, which is a
    /// positional argument after a named one and does not compile.
    /// </remarks>
    [Fact]
    public void ANamedArgumentCarriesTheExpressionAndNotTheArgumentName()
    {
        var source = WithFlow("""
            public static class Policies
            {
                public static readonly PolicySet Undo = PolicySet
                    .Named("undo")
                    .CompensationRetry(attempts: 3);
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()
                        .WithPolicy(policy: Policies.Undo)
                    .Return(ctx => new OrderResult("id"));
            }
            """);

        GeneratorHarness.GeneratedCompileErrorsIn(source).ShouldBeEmpty();

        GeneratorHarness.Run(source).Plan.ShouldContainText(
            "PolicyChain.ForCompensation(Policies.Undo, Descriptors.Step0Compensation)",
            "The expression travels; the parameter name it was written against does not.");
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

        run.Ids.ShouldNotContain("FLOWX1030",
            "An undeclared stance reads as Public, which names nothing and is meant to. " +
            "Two findings for one omission would send the author to the wrong fix.");
    }

    /// <summary>
    /// FLOWX1030 — a stance that demands a named grant and names none.
    /// </summary>
    /// <remarks>
    /// FLOWX1010's rule one level down, and an error on FLOWX1010's argument. The
    /// declaration compiles, reads as enforced, and publishes
    /// <c>{"mode": "Permission"}</c> — a claim that some grant is required, naming none.
    /// It is also what left <c>FLOWX-DIFF-015</c>'s "the named permission changed" half
    /// with nothing to compare: a stance with no name has no value to move.
    /// </remarks>
    [Theory]
    [InlineData("Permission")]
    [InlineData("Policy")]
    public void ReportsFLOWX1030WhenAStanceDemandsANameAndHasNone(string mode)
    {
        var run = GeneratorHarness.Run(WithFlow($$"""
            [Capability("audit.write", Version = "1.0.0", Idempotent = true,
                Authorization = Authorization.{{mode}})]
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

        run.Ids.ShouldContain("FLOWX1030", run.Describe());

        // The message names the property to add, which differs by mode. One hard-coded
        // noun would send half the readers to the wrong one.
        run.Describe().ShouldContain(
            $"Capability 'audit.write' declares Authorization.{mode} but no {mode} name");
    }

    /// <summary>
    /// The half that matters more: FLOWX1030 stays silent on every valid stance.
    /// </summary>
    /// <remarks>
    /// Three of the five modes are complete in themselves, and a rule that reported them
    /// would make the security set the first thing a team suppressed. The named cases are
    /// included so the rule is shown to be about the missing name and not about the mode.
    /// </remarks>
    [Theory]
    [InlineData("Authorization.Public")]
    [InlineData("Authorization.Authenticated")]
    [InlineData("Authorization.Internal")]
    [InlineData("""Authorization.Permission, Permission = "audit.write" """)]
    [InlineData("""Authorization.Policy, Policy = "audit-writers" """)]
    public void DoesNotReportFLOWX1030OnAStanceThatIsCompleteInItself(string stance)
    {
        var run = GeneratorHarness.Run(WithFlow($$"""
            [Capability("audit.write", Version = "1.0.0", Idempotent = true,
                Authorization = {{stance}})]
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

        run.Ids.ShouldNotContain("FLOWX1030", run.Describe());
    }

    /// <summary>A name that is only whitespace is no name.</summary>
    /// <remarks>
    /// <c>Permission = ""</c> would otherwise satisfy the rule while publishing
    /// <c>"value": ""</c> — a permission whose name is blank, which is exactly the
    /// unenforceable stance the rule exists to refuse, wearing a value.
    /// </remarks>
    [Fact]
    public void ReportsFLOWX1030WhenTheNameIsBlank()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Capability("audit.write", Version = "1.0.0", Idempotent = true,
                Authorization = Authorization.Permission, Permission = "   ")]
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

        run.Ids.ShouldContain("FLOWX1030", run.Describe());
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
            "public static readonly Func<FlowContext<Sample.PlaceOrder>, bool> Step1 = " +
            "ctx => ctx.Get<Reservation>().Sku == \"rare\";",
            "The predicate is the author's expression, verbatim.");

        source.ShouldContainText("return Conditions.Step1(Typed(ctx));", "reached by step index, like everything else.");
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
            "public static readonly Func<FlowContext<Sample.PlaceOrder>, Sample.Channel> Step1 = " +
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
    // ------------------------------------------------------------------ parallel

    /// <summary>
    /// A <c>Parallel</c> end to end: the branches are walked out of a lambda argument on
    /// <c>IParallelBuilder</c> rather than off later chain links, and the whole thing lands
    /// in the flat step array as one node with a target per branch and a join.
    /// </summary>
    [Fact]
    public void CompilesAParallelIntoOneNodeWithATargetPerBranch()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Parallel(p => p
                            .Branch<CapturePayment>()
                            .Branch(check => check
                                .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()),
                        merge: MergeStrategy.AllMustSucceed)
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldBeEmpty(run.Describe());

        var source = run.Plan;

        source.ShouldContainText("StepNode.ForCapability(0, Descriptors.Step0)", run.Describe());
        source.ShouldContainText(
            "StepNode.ForParallel(1, new[] { 2, 3 }, joinTarget: 4, merge: MergeStrategy.AllMustSucceed)",
            "One node, one target per branch, and the join where control resumes.");
        source.ShouldContainText("StepNode.ForCapability(2, Descriptors.Step2)", "The first branch.");
        source.ShouldContainText(
            "StepNode.ForCapability(3, Descriptors.Step3, Descriptors.Step3Compensation)",
            "The second branch, with the compensation it declared inside the lambda.");

        source.ShouldNotContainText("ForJump",
            "A branch's range ends where the next branch begins, so a closing jump would " +
            "only restate the bound — and would cost an index the graph has to account for.");
    }

    [Fact]
    public void AParallelStepGetsNoDispatcherCaseOfItsOwn()
    {
        // The engine handles a fork itself: it starts the branches and applies the merge.
        // The branches' own steps need cases; the fork node does not, and one would be
        // dead code in a file whose header promises it is readable.
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Parallel(p => p.Branch<ReserveInventory>().Branch<CapturePayment>(),
                        merge: MergeStrategy.AllSettled)
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldBeEmpty(run.Describe());
        run.Plan.ShouldContainText("case 1:", "Branch 0's step.");
        run.Plan.ShouldContainText("case 2:", "Branch 1's step.");
        run.Plan.ShouldNotContainText("case 0:", "The fork itself is index 0 and needs no case.");
    }

    /// <summary>
    /// A <c>Quorum</c> is the reason <c>MergeStrategy</c> is a struct rather than an enum,
    /// and the reason the plan copies the author's expression instead of rebuilding it.
    /// </summary>
    [Fact]
    public void AQuorumsArgumentSurvivesIntoThePlanExactlyAsItWasWritten()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                private const int RequiredChecks = 2;

                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Parallel(p => p
                            .Branch<ReserveInventory>()
                            .Branch<CapturePayment>()
                            .Branch<ReserveInventory>(),
                        merge: MergeStrategy.Quorum(RequiredChecks))
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldBeEmpty(run.Describe());
        run.Plan.ShouldContainText("merge: MergeStrategy.Quorum(RequiredChecks)",
            "Rebuilding the expression from a parsed name would break the moment the " +
            "argument was anything but a literal.");
    }

    [Fact]
    public void AParallelWithOneBranchIsLaidOutInlineRatherThanForked()
    {
        // Running one thing concurrently is running it. Emitting a fork would buy a linked
        // token and a task for work that happens in exactly one order anyway, and would put
        // a decision in the manifest the flow does not make.
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Parallel(p => p.Branch<ReserveInventory>(), merge: MergeStrategy.AllMustSucceed)
                    .Step<CapturePayment>()
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldBeEmpty(run.Describe());
        run.Plan.ShouldNotContainText("ForParallel", "One branch is not a fork.");
        run.Plan.ShouldContainText("StepNode.ForCapability(0, Descriptors.Step0)",
            "The branch's step is kept — the author asked for it — and simply runs in order.");
        run.Plan.ShouldContainText("StepNode.ForCapability(1, Descriptors.Step1)",
            "and the step written after the Parallel simply follows it.");
    }

    [Fact]
    public void TheManifestPublishesAForkAsBranchesAndAMergeRule()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Parallel(p => p.Branch<CapturePayment>().Branch<ReserveInventory>(),
                        merge: MergeStrategy.Quorum(2))
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        var json = run.ManifestJson.ShouldNotBeNull();

        using var document = System.Text.Json.JsonDocument.Parse(json);

        var node = document.RootElement.GetProperty("flows")[0].GetProperty("steps")[1];

        node.GetProperty("kind").GetString().ShouldBe("Parallel");
        node.GetProperty("merge").GetString().ShouldBe("Quorum",
            "How the branches are joined is structure: it says what one branch failing " +
            "means for the flow, without saying anything about the data.");
        node.GetProperty("branches").GetArrayLength().ShouldBe(2);
        node.GetProperty("branches")[0][0].GetProperty("capability").GetString()
            .ShouldBe("payment.capture@2.1.0");
        node.GetProperty("branches")[1][0].GetProperty("capability").GetString()
            .ShouldBe("inventory.reserve@1.2.0");

        // The quorum's *size* is deliberately absent. It comes from an arbitrary
        // expression in the author's source, and publishing an evaluated constant would
        // start the manifest down the road of carrying values.
        json.Contains("\"merge\": \"Quorum(2)\"", StringComparison.Ordinal).ShouldBeFalse();
    }

    /// <summary>
    /// The generated code for a fork must actually build, not merely parse.
    /// </summary>
    /// <remarks>
    /// The merge argument in particular is the author's own expression pasted into the
    /// generated file, so it has to resolve there — which is a claim only a real
    /// compilation can settle.
    /// </remarks>
    [Fact]
    public void TheGeneratedCodeForAParallelCompiles()
    {
        GeneratorHarness.GeneratedCompileErrorsIn(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Parallel(p => p
                            .Branch<CapturePayment>()
                            .Branch(check => check
                                .Step<ReserveInventory>().CompensateWith<ReleaseInventory>())
                            .Branch(more => more.Step<CapturePayment>()),
                        merge: MergeStrategy.Quorum(2))
                    .Return(ctx => new OrderResult("id"));
            }
            """)).ShouldBeEmpty();
    }

    [Fact]
    public void TheGeneratedCodeForAParallelInsideAConditionalCompiles()
    {
        // Nesting is where a layout bug hides. The fork's indices are handed out by the
        // same shared counter the enclosing `then` block uses, so an off-by-one shows up
        // as a gap or a duplicate and StepGraph rejects it at type initialisation — which
        // is a run-time failure, not a build one, unless something compiles it first.
        GeneratorHarness.GeneratedCompileErrorsIn(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .When(ctx => ctx.Get<Reservation>().Sku == "rare", rare => rare
                        .Parallel(p => p.Branch<CapturePayment>().Branch<ReserveInventory>(),
                            merge: MergeStrategy.AllSettled))
                    .Otherwise(rest => rest.Step<CapturePayment>())
                    .Return(ctx => new OrderResult("id"));
            }
            """)).ShouldBeEmpty();
    }

    [Fact]
    public void AParallelInsideAConditionalIsNumberedInTheSameFlatSpace()
    {
        var run = GeneratorHarness.Run(WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .When(ctx => ctx.Get<Reservation>().Sku == "rare", rare => rare
                        .Parallel(p => p.Branch<CapturePayment>().Branch<ReserveInventory>(),
                            merge: MergeStrategy.AllSettled))
                    .Otherwise(rest => rest.Step<CapturePayment>())
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldBeEmpty(run.Describe());

        // 0 reserve · 1 branch(else 6) · 2 fork(3,4 join 5) · 3 capture · 4 reserve ·
        // 5 jump 7 · 6 capture.
        run.Plan.ShouldContainText("StepNode.ForBranch(1, 6)", run.Describe());
        run.Plan.ShouldContainText(
            "StepNode.ForParallel(2, new[] { 3, 4 }, joinTarget: 5, merge: MergeStrategy.AllSettled)",
            "The fork's indices come from the same shared counter the `then` block uses.");
        run.Plan.ShouldContainText("StepNode.ForJump(5, 7)",
            "The fork's join is 5, which is where the `then` block's closing jump lives.");
        run.Plan.ShouldContainText("StepNode.ForCapability(6, Descriptors.Step6)",
            "and the `Otherwise` block starts one past that jump.");
    }

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

    // -------------------------------------------------------------------------- ForEach

    /// <summary>
    /// The types the documented iteration example needs: something to split an order into
    /// lines, and a capability that takes one line.
    /// </summary>
    /// <remarks>
    /// Declared alongside the flow rather than in the shared preamble, so every other test
    /// in this file keeps compiling exactly the source it compiled before.
    /// </remarks>
    private const string Lines = """
        public sealed record OrderLine(string Sku);
        public sealed record LineBatch(System.Collections.Generic.IReadOnlyList<OrderLine> Lines);

        [Capability("order.split", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
        public sealed class SplitOrder : ICapability<PlaceOrder, LineBatch>
        {
            public ValueTask<Result<LineBatch>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new LineBatch(new[] { new OrderLine(input.Sku) })));
        }

        [Capability("inventory.reserve_line", Version = "1.0.0",
            Authorization = Authorization.Internal, Idempotent = true,
            SideEffects = new[] { "inventory-ledger" })]
        public sealed class ReserveLine : ICapability<OrderLine, Reservation>
        {
            public ValueTask<Result<Reservation>> ExecuteAsync(OrderLine input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Reservation(input.Sku)));
        }

        [Capability("inventory.release_line", Version = "1.0.0",
            Authorization = Authorization.Internal, Idempotent = true)]
        public sealed class ReleaseLine : ICapability<OrderLine, Reservation>
        {
            public ValueTask<Result<Reservation>> ExecuteAsync(OrderLine input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Reservation(input.Sku)));
        }


        """;

    /// <summary>The example from <c>08-Flow-Definition.md</c> §3.4, spelled against these types.</summary>
    private const string IteratingFlow = Lines + """
        [Flow("order.place")]
        public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
        {
            protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                .Step<SplitOrder>()
                .ForEach(ctx => ctx.Get<LineBatch>().Lines,
                        line => line.Step<ReserveLine>().CompensateWith<ReleaseLine>(),
                    options: new ForEachOptions { MaxDegreeOfParallelism = 4, ContinueOnError = false })
                .Step<CapturePayment>()
                .Return(ctx => new OrderResult("id"));
        }
        """;

    [Fact]
    public void TheGeneratedCodeForAForEachCompiles()
    {
        // The claim only a real compilation can settle. The options argument and the
        // collection selector are both the author's own expressions pasted into the
        // generated file, so they have to resolve there — and the emitted scope has to
        // type-check against an element type the engine never sees.
        GeneratorHarness.GeneratedCompileErrorsIn(WithFlow(IteratingFlow)).ShouldBeEmpty();
    }

    [Fact]
    public void AForEachIsALoopNodeFollowedByItsBodyOnce()
    {
        var run = GeneratorHarness.Run(WithFlow(IteratingFlow));

        run.Ids.ShouldBeEmpty(run.Describe());

        // 0 split · 1 foreach(body 2..3, join 3) · 2 reserve line · 3 capture.
        run.Plan.ShouldContainText(
            "StepNode.ForEach(1, joinTarget: 3, options: new ForEachOptions " +
            "{ MaxDegreeOfParallelism = 4, ContinueOnError = false })",
            "The options are copied verbatim, so a bound written as a constant on the flow " +
            "keeps working. The runtime clamps it; the generator does not evaluate it.");

        run.Plan.ShouldContainText(
            "StepNode.ForCapability(2, Descriptors.Step2, Descriptors.Step2Compensation)",
            "The body is laid out once, immediately after the loop, and its compensation " +
            "reaches the plan exactly as any other step's does.");
        run.Plan.ShouldContainText("StepNode.ForCapability(3, Descriptors.Step3)",
            "and the step after the loop is at the join.");
    }

    [Fact]
    public void TheCollectionSelectorIsEmittedAsAStaticFieldTypedAtItsElement()
    {
        var run = GeneratorHarness.Run(WithFlow(IteratingFlow));

        run.Plan.ShouldContainText(
            "public static readonly Func<FlowContext<Sample.PlaceOrder>, " +
            "System.Collections.Generic.IReadOnlyList" +
            "<Sample.OrderLine>> Step1 = ctx => ctx.Get<LineBatch>().Lines;",
            "A list rather than a sequence: the engine reads the count once, before the " +
            "first element, and that count is what bounds the loop.");

        run.Plan.ShouldContainText("return IterationScope.For(ctx, items[iteration]);",
            "The dispatcher builds the scope, because only it knows the element type.");
    }

    [Fact]
    public void TheManifestPublishesTheIterationsShapeAndNeitherItsCollectionNorItsBound()
    {
        var run = GeneratorHarness.Run(WithFlow(IteratingFlow));

        var json = run.ManifestJson.ShouldNotBeNull();

        using var document = System.Text.Json.JsonDocument.Parse(json);

        var node = document.RootElement.GetProperty("flows")[0].GetProperty("steps")[1];

        node.GetProperty("kind").GetString().ShouldBe("ForEach");
        node.GetProperty("branches").GetArrayLength().ShouldBe(1);
        node.GetProperty("branches")[0][0].GetProperty("capability").GetString()
            .ShouldBe("inventory.reserve_line@1.0.0");
        node.GetProperty("branches")[0][0].GetProperty("compensation").GetString()
            .ShouldBe("inventory.release_line@1.0.0");

        json.Contains("ctx =>", StringComparison.Ordinal).ShouldBeFalse(
            "Which collection is iterated is a statement about the author's data. The type " +
            "`LineBatch` does appear, as the contract `order.split` returns — naming a " +
            "contract is what the manifest is for; publishing the expression that reaches " +
            "into one is not.");
        json.Contains("MaxDegreeOfParallelism", StringComparison.Ordinal).ShouldBeFalse(
            "And the bound is a tuning number the committed schema has no field for.");
    }

    [Fact]
    public void AForEachInsideAConditionalIsNumberedInTheSameFlatSpace()
    {
        // Nesting is where a layout bug hides. The loop's body indices come from the same
        // shared counter the enclosing `then` block uses, so an off-by-one shows up as a
        // gap or a duplicate and StepGraph rejects it at type initialisation — which is a
        // run-time failure, not a build one, unless something compiles it first.
        var source = WithFlow(Lines + """
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<SplitOrder>()
                    .When(ctx => ctx.Get<LineBatch>().Lines.Count > 1, many => many
                        .ForEach(ctx => ctx.Get<LineBatch>().Lines,
                                line => line.Step<ReserveLine>(),
                            options: new ForEachOptions { MaxDegreeOfParallelism = 2 }))
                    .Otherwise(one => one.Step<CapturePayment>())
                    .Return(ctx => new OrderResult("id"));
            }
            """);

        GeneratorHarness.GeneratedCompileErrorsIn(source).ShouldBeEmpty();

        var run = GeneratorHarness.Run(source);

        run.Ids.ShouldBeEmpty(run.Describe());

        // 0 split · 1 branch(else 5) · 2 foreach(body 3..4, join 4) · 3 reserve line ·
        // 4 jump 6 · 5 capture.
        run.Plan.ShouldContainText("StepNode.ForBranch(1, 5)", run.Describe());
        run.Plan.ShouldContainText("StepNode.ForEach(2, joinTarget: 4, options:",
            "The loop's own index comes from the same shared counter the `then` block uses.");
        run.Plan.ShouldContainText("StepNode.ForJump(4, 6)",
            "The loop's join is 4, which is where the `then` block's closing jump lives.");
        run.Plan.ShouldContainText("StepNode.ForCapability(5, Descriptors.Step5)",
            "and the `Otherwise` block starts one past that jump.");
    }

    [Fact]
    public void AForEachWhoseBodyDeclaresNothingIsNotLaidOutAtAll()
    {
        // The same treatment a Switch with no Case and a one-branch Parallel get: a shape
        // the flow does not really have is not published as one. A loop node with an empty
        // body would also leave a gap in the index space, which StepGraph rejects at type
        // initialisation — a run-time failure for a build-time mistake.
        var source = WithFlow(Lines + """
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<SplitOrder>()
                    .ForEach(ctx => ctx.Get<LineBatch>().Lines,
                            line => { },
                        options: new ForEachOptions { MaxDegreeOfParallelism = 2 })
                    .Step<CapturePayment>()
                    .Return(ctx => new OrderResult("id"));
            }
            """);

        GeneratorHarness.GeneratedCompileErrorsIn(source).ShouldBeEmpty();

        var run = GeneratorHarness.Run(source);

        run.Plan.Contains("StepNode.ForEach", StringComparison.Ordinal).ShouldBeFalse();
        run.Plan.ShouldContainText("StepNode.ForCapability(1, Descriptors.Step1)",
            "The step after the absent loop takes the index the loop would have had.");
    }
}
