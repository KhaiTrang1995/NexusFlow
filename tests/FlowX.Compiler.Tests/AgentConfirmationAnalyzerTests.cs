using FlowX.Compiler.Analysis;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// FLOWX1046 — an agent tool that asks nobody about a consequence it declares.
/// </summary>
/// <remarks>
/// <para>
/// The negative cases outnumber the positive one, which is the balance the diagnostics index asks
/// for: a rule with false positives is suppressed everywhere and then protects nothing. The three
/// that must stay silent are the ones an author will actually write — no <c>Confirmation</c>
/// argument at all, <c>Never</c> over capabilities with no side effect, and a compensation whose
/// capability has one.
/// </para>
/// <para>
/// Every source here is compiled first (<see cref="Analyze"/> asserts no C# errors), so a test
/// that stops reporting because the sample stopped compiling fails as a compile error rather than
/// as a silent pass.
/// </para>
/// </remarks>
public sealed class AgentConfirmationAnalyzerTests
{
    private const string Preamble = """
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record PlaceOrder(string Sku, int Quantity);
        public sealed record OrderResult(string Id);

        [Capability("order.validate", Version = "1.0.0", Authorization = Authorization.Authenticated, Idempotent = true)]
        public sealed class ValidateOrder : ICapability<PlaceOrder, OrderResult>
        {
            public ValueTask<Result<OrderResult>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new OrderResult(input.Sku)));
        }

        [Capability("payment.capture", Version = "1.0.0", Authorization = Authorization.Permission,
                    Permission = "payment.write", Idempotent = false, SideEffects = ["payment-gateway"])]
        public sealed class CapturePayment : ICapability<PlaceOrder, OrderResult>
        {
            public ValueTask<Result<OrderResult>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new OrderResult(input.Sku)));
        }

        [Capability("payment.refund", Version = "1.0.0", Authorization = Authorization.Internal,
                    Idempotent = true, SideEffects = ["payment-gateway"])]
        public sealed class RefundPayment : ICapability<PlaceOrder, OrderResult>
        {
            public ValueTask<Result<OrderResult>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new OrderResult(input.Sku)));
        }
        """;

    /// <summary>The declaration the rule exists for.</summary>
    [Fact]
    public void NeverOverADeclaredSideEffectIsReported()
    {
        var reported = Messages(FlowWith(
            "[AgentTrigger(Description = \"Place an order.\", Confirmation = ConfirmationMode.Never)]",
            ".Step<ValidateOrder>()\n        .Step<CapturePayment>()"));

        reported.ShouldHaveSingleItem();

        // The message names the flow, the capability and the effect — the three things an author
        // needs to decide whether the suppression is honest.
        reported[0].ShouldContain("FLOWX1046");
        reported[0].ShouldContain("order.place");
        reported[0].ShouldContain("payment.capture");
        reported[0].ShouldContain("payment-gateway");
    }

    /// <summary>
    /// A flow that declares no <c>Confirmation</c> is silent, because the default is the safe
    /// answer.
    /// </summary>
    /// <remarks>
    /// The most important negative case. <c>RequiredForSideEffects</c> is what
    /// <c>AgentTriggerAttribute.Confirmation</c> initialises to, so reporting here would fire on
    /// every agent tool with a consequence and mean nothing.
    /// </remarks>
    [Fact]
    public void ADefaultedConfirmationIsNotReported() =>
        Analyze(FlowWith(
            "[AgentTrigger(Description = \"Place an order.\")]",
            ".Step<ValidateOrder>()\n        .Step<CapturePayment>()"))
            .ShouldBeEmpty();

    /// <summary>The mode written out explicitly is the same as the default.</summary>
    [Fact]
    public void RequiredForSideEffectsIsNotReported() =>
        Analyze(FlowWith(
            "[AgentTrigger(Description = \"Place an order.\", Confirmation = ConfirmationMode.RequiredForSideEffects)]",
            ".Step<ValidateOrder>()\n        .Step<CapturePayment>()"))
            .ShouldBeEmpty();

    /// <summary>
    /// <c>Never</c> over a flow with no declared side effect is the accurate declaration.
    /// </summary>
    /// <remarks>
    /// <c>ticket.search</c> in <c>samples/ai-agent</c> is this flow. If the rule reported here it
    /// would be reporting on the one case where <c>Never</c> is exactly right, and its readers
    /// would learn to suppress it.
    /// </remarks>
    [Fact]
    public void NeverOverAReadOnlyFlowIsNotReported() =>
        Analyze(FlowWith(
            "[AgentTrigger(Description = \"Place an order.\", Confirmation = ConfirmationMode.Never)]",
            ".Step<ValidateOrder>()"))
            .ShouldBeEmpty();

    /// <summary>A flow with no agent trigger at all is not this rule's business.</summary>
    [Fact]
    public void AFlowWithNoAgentTriggerIsNotReported() =>
        Analyze(FlowWith(
            "[HttpTrigger(\"POST\", \"/api/v1/orders\")]",
            ".Step<ValidateOrder>()\n        .Step<CapturePayment>()"))
            .ShouldBeEmpty();

    /// <summary>
    /// A compensation's side effects are not counted, because the descriptor does not publish them.
    /// </summary>
    /// <remarks>
    /// The set counted is the set <c>McpToolCatalog</c> projects, which reads the manifest's
    /// <c>step.capability</c> and not its <c>step.compensation</c>. Reporting here would name an
    /// effect that never reaches <c>confirmationRequired</c>, so no edit to the trigger could
    /// change the thing the message complains about.
    /// </remarks>
    [Fact]
    public void ACompensationsSideEffectsAreNotCounted() =>
        Analyze(FlowWith(
            "[AgentTrigger(Description = \"Place an order.\", Confirmation = ConfirmationMode.Never)]",
            ".Step<ValidateOrder>().CompensateWith<RefundPayment>()"))
            .ShouldBeEmpty();

    /// <summary>A step inside a branch is a step this tool can run.</summary>
    /// <remarks>
    /// The walk descends into nested builders for the reason the descriptor's own does: a refund
    /// behind an <c>Otherwise</c> is a consequence the tool has, and a rule that only read the top
    /// level would have quietly stopped applying the day the DSL grew a nested builder.
    /// </remarks>
    [Fact]
    public void AnEffectfulStepInsideABranchIsReported()
    {
        var reported = Messages(FlowWith(
            "[AgentTrigger(Description = \"Place an order.\", Confirmation = ConfirmationMode.Never)]",
            ".When(ctx => ctx.Get<PlaceOrder>().Quantity > 0, branch => branch.Step<CapturePayment>())"));

        reported.ShouldHaveSingleItem();
        reported[0].ShouldContain("FLOWX1046");
    }

    private static string FlowWith(string trigger, string steps) =>
        Preamble + "\n\n" + $$"""
        [Flow("order.place", Version = "1.0.0")]
        {{trigger}}
        public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
        {
            protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                {{steps}}
                .Return(ctx => new OrderResult("id"));
        }
        """;

    private static string[] Analyze(string source)
    {
        GeneratorHarness.CompileErrorsIn(source).ShouldBeEmpty();

        return GeneratorHarness.Analyze(source, new AgentConfirmationAnalyzer());
    }

    /// <summary>
    /// The same run, with the messages, for the assertions that are about what the report says.
    /// </summary>
    /// <remarks>
    /// The id alone proves the rule fired. What an author needs from it is the flow, the capability
    /// and the effect, and a message that named none of them would satisfy every assertion on the id.
    /// </remarks>
    private static string[] Messages(string source)
    {
        GeneratorHarness.CompileErrorsIn(source).ShouldBeEmpty();

        return GeneratorHarness.AnalyzeWithMessages(source, new AgentConfirmationAnalyzer());
    }
}
