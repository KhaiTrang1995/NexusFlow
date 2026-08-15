using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// Stage 3's <c>Validate</c>, end to end: what the compiler reads off a contract, what it emits,
/// and what a compiled flow does with a bad input.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Compiled and run, not read.</strong> Every other assertion about the emitter in this
/// project reads the generated text, which is the right shape when the emitted thing is a plan a
/// runtime interprets. A validation is not: it is a comparison, and the only way to be sure
/// <c>[Range(1, 100)]</c> means what it says is to hand a compiled flow a <c>0</c> and read the
/// refusal. <c>GeneratorHarness.GeneratedFlowFor</c> is the seam that makes that possible.
/// </para>
/// <para>
/// <strong>One test here is the reason the policy is safe to ship.</strong>
/// <see cref="ASensitiveMembersValueNeverAppearsInARefusal"/> hands a compiled flow a marked
/// member whose value is a distinctive string and reads every part of the refusal that could
/// carry it. It is a property of the emitter rather than of the message text — no generated
/// message interpolates a member — and this is what holds the emitter to it.
/// </para>
/// </remarks>
public sealed class GeneratedValidationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Contracts carrying the annotations the reader understands, and the capability behind them.
    /// </summary>
    /// <remarks>
    /// <c>System.ComponentModel.DataAnnotations</c> and no package reference: those attributes
    /// are in the shared framework, which is the whole reason FlowX does not own a second
    /// spelling of <c>[Required]</c>. The contract also marks a member <c>[Sensitive]</c>, so the
    /// flow's redaction set is non-empty and the refusal has something to leak if it can.
    /// </remarks>
    private const string Preamble =
        """
        using System.ComponentModel.DataAnnotations;
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record PlaceOrder(
            [property: Required] string Sku,
            [property: Range(1, 100)] int Quantity,
            [property: Sensitive, StringLength(4)] string Pan);

        public sealed record Reservation(string Sku);
        public sealed record OrderResult(string Id);

        [Capability("inventory.reserve", Version = "1.0.0",
            Authorization = Authorization.Internal, Idempotent = true)]
        public sealed class ReserveInventory : ICapability<PlaceOrder, Reservation>
        {
            public ValueTask<Result<Reservation>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Reservation(input.Sku)));
        }

        public static class Policies
        {
            public static readonly PolicySet Checked = PolicySet.Named("checked").Validate();
        }

        [Flow("order.place")]
        public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
        {
            protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                .Step<ReserveInventory>().WithPolicy(Policies.Checked)
                .Return(ctx => new OrderResult(ctx.Get<Reservation>().Sku));
        }
        """;

    /// <summary>The card number a refusal must never repeat back.</summary>
    /// <remarks>
    /// Long enough to break the declared <c>[StringLength(4)]</c>, so the rule that could leak
    /// it is the rule that fires — a shorter value would make the test pass by never producing
    /// the field error it is about.
    /// </remarks>
    private const string Pan = "4111111111111111";

    // ------------------------------------------------------------------------- what it emits

    /// <summary>The generated dispatcher carries a comparison per declared rule.</summary>
    /// <remarks>
    /// The text assertion, kept alongside the behavioural ones because it is the one that says
    /// <em>where</em> the enforcement lives: in the dispatcher, as source, rather than in a
    /// run-time walk of the contract. A build that started reflecting would pass every
    /// behavioural test in this file and fail this one.
    /// </remarks>
    [Fact]
    public void TheGeneratedDispatcherCarriesAComparisonPerRule()
    {
        var run = GeneratorHarness.Run(Preamble);

        var source = run.Sources
            .Single(s => s.HintName.Contains("PlaceOrderFlow", StringComparison.Ordinal))
            .Source;

        source.ShouldContain(
            "public ValidationOutcome Validate(int stepIndex, FlowContext ctx)",
            Case.Sensitive,
            "the checks are a member of the generated dispatcher, which is what makes them " +
            "reflection-free and what C2 requires.");

        source.ShouldContain("string.IsNullOrWhiteSpace(input.Sku)", Case.Sensitive);
        source.ShouldContain("input.Quantity < 1 || input.Quantity > 100", Case.Sensitive);
        source.ShouldContain("input.Pan is { Length: > 4 }", Case.Sensitive);

        source.ShouldNotContain(
            "Validator.",
            Case.Sensitive,
            "System.ComponentModel.DataAnnotations supplies the vocabulary and never the " +
            "enforcement: Validator.TryValidateObject reflects, which no trimmed or NativeAOT " +
            "build may rely on.");
    }

    // ---------------------------------------------------------------------- what it refuses

    /// <summary>A bad input is refused with one field error per broken rule.</summary>
    [Fact]
    public async Task ABadInputIsRefusedWithAFieldErrorPerBrokenRule()
    {
        var (plan, dispatcher) = GeneratorHarness.GeneratedFlowFor(Preamble, "Sample.PlaceOrderFlow");

        var result = await Run(plan, dispatcher, Order(sku: " ", quantity: 0, pan: Pan));

        result.IsSuccess.ShouldBeFalse("all three declared rules were broken.");

        result.Error!.Code.ShouldBe("policy.validation_failed");

        var failures = (IReadOnlyList<FieldError>)result.Error.Data![FlowErrors.FieldErrorsDetail]!;

        failures.Select(static f => f.Field).ShouldBe(
            ["Sku", "Quantity", "Pan"],
            "one per broken rule, in the order the contract declares them. Reporting only the " +
            "first would make a caller fix its payload one round trip at a time.");

        failures.Select(static f => f.Rule).ShouldBe(
            ["required", "range", "length"],
            "the machine-readable half, so a client can branch without parsing English.");
    }

    /// <summary>A good input is admitted and the step runs.</summary>
    /// <remarks>
    /// The positive control. Every other assertion in this file would pass against a stage 3
    /// that refused everything, which is the failure mode a file of refusal assertions hides.
    /// </remarks>
    [Fact]
    public async Task AGoodInputIsAdmittedAndTheStepRuns()
    {
        var (plan, dispatcher) = GeneratorHarness.GeneratedFlowFor(Preamble, "Sample.PlaceOrderFlow");

        var result = await Run(plan, dispatcher, Order(sku: "sku-1", quantity: 5, pan: "4111"));

        result.IsSuccess.ShouldBeTrue(
            "every rule is satisfied. " + (result.IsFailure ? result.Error!.ToString() : string.Empty));
    }

    /// <summary>A member on the boundary of its declared range is admitted.</summary>
    /// <remarks>
    /// <c>[Range(1, 100)]</c> is inclusive, and an off-by-one in the emitted comparison would
    /// refuse a value the author declared legal — which is the class of defect a generated check
    /// makes cheap to introduce and invisible to review.
    /// </remarks>
    [Fact]
    public async Task TheBoundsOfADeclaredRangeAreInside()
    {
        var (plan, dispatcher) = GeneratorHarness.GeneratedFlowFor(Preamble, "Sample.PlaceOrderFlow");

        (await Run(plan, dispatcher, Order("sku-1", 1, "4111"))).IsSuccess.ShouldBeTrue("1 is the minimum.");
        (await Run(plan, dispatcher, Order("sku-1", 100, "4111"))).IsSuccess.ShouldBeTrue("100 is the maximum.");
        (await Run(plan, dispatcher, Order("sku-1", 101, "4111"))).IsSuccess.ShouldBeFalse("101 is not.");
    }

    // ------------------------------------------------------------------- what it never says

    /// <summary>
    /// A <c>[Sensitive]</c> member's value never appears anywhere in a refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Every surface a value could travel on is read, not only the field's own
    /// message.</strong> The flow's error message, its structured detail and each field error's
    /// three members are all checked, because a leak needs only one of them and the obvious
    /// implementation — "'Pan' is 16 characters, which is more than 4" — leaks through the one a
    /// test about field names would not look at.
    /// </para>
    /// <para>
    /// It holds because of how the message is built rather than because of what it says: every
    /// message is a constant the compiler assembled from the rule's own declared bounds, so
    /// there is no expression through which the member could reach one. The generated source is
    /// checked for the value too, which is the same property asserted one level earlier.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASensitiveMembersValueNeverAppearsInARefusal()
    {
        var run = GeneratorHarness.Run(Preamble);

        foreach (var (_, text) in run.Sources)
        {
            text.ShouldNotContain(
                Pan,
                Case.Sensitive,
                "no generated message interpolates a member, so a contract's value cannot be " +
                "in the emitted source at all.");
        }

        var (plan, dispatcher) = GeneratorHarness.GeneratedFlowFor(Preamble, "Sample.PlaceOrderFlow");

        var result = await Run(plan, dispatcher, Order("sku-1", 5, Pan));

        result.IsSuccess.ShouldBeFalse("the card number is longer than the declared four.");

        result.Error!.Message.ShouldNotContain(Pan, Case.Sensitive, "the summary counts, it does not quote.");

        var failures = (IReadOnlyList<FieldError>)result.Error.Data![FlowErrors.FieldErrorsDetail]!;

        var only = failures.ShouldHaveSingleItem();

        only.Field.ShouldBe("Pan", "the caller is told which member is wrong — the name is contract, not data.");

        only.Message.ShouldBe(
            "'Pan' must be at most 4 characters.",
            "the bound the author declared, and nothing the caller sent.");

        only.Message.ShouldNotContain(Pan, Case.Sensitive);
        only.Rule.ShouldNotContain(Pan, Case.Sensitive);

        foreach (var detail in result.Error.Data!)
        {
            (detail.Value?.ToString() ?? string.Empty).ShouldNotContain(
                Pan,
                Case.Sensitive,
                $"the '{detail.Key}' detail reaches an RFC 7807 extension verbatim.");
        }
    }

    // ------------------------------------------------------------------------------ FLOWX1056

    /// <summary>A <c>Validate</c> over a contract with no rules is refused at build time.</summary>
    /// <remarks>
    /// The one misuse the compiler can see. A validation that examines every input and refuses
    /// none is a declaration that reads as satisfied and is not — the shape FLOWX1032 existed to
    /// report before every catalogued kind became declarable.
    /// </remarks>
    [Fact]
    public void ReportsFLOWX1056WhenTheContractDeclaresNoRule()
    {
        var run = GeneratorHarness.Run(FlowPlanGeneratorTests.WithFlow("""
            public static class Policies
            {
                public static readonly PolicySet Checked = PolicySet.Named("checked").Validate();
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>().WithPolicy(Policies.Checked)
                    .Return(ctx => new OrderResult("id"));
            }
            """));

        run.Ids.ShouldContain("FLOWX1056", run.Describe());

        run.Describe().ShouldContain(
            "Sample.PlaceOrder",
            Case.Sensitive,
            "and it names the contract the author has to annotate.");
    }

    /// <summary>An annotated contract does not report.</summary>
    /// <remarks>
    /// The other direction, and the one that would make the rule useless if it failed: a build
    /// error over a policy that works is worse than the silence it replaced.
    /// </remarks>
    [Fact]
    public void DoesNotReportFLOWX1056WhenTheContractIsAnnotated() =>
        GeneratorHarness.Run(Preamble).Ids.ShouldNotContain("FLOWX1056");

    /// <summary>A step with no <c>Validate</c> is not reported, however bare its contract.</summary>
    [Fact]
    public void DoesNotReportFLOWX1056WithoutAValidate() =>
        GeneratorHarness.Run(FlowPlanGeneratorTests.WithFlow("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Step<ReserveInventory>()
                    .Return(ctx => new OrderResult("id"));
            }
            """)).Ids.ShouldNotContain("FLOWX1056");

    // ---------------------------------------------------------------------------- helpers

    /// <summary>Builds the flow's input by reflection, because its type is in the loaded assembly.</summary>
    private static (string Sku, int Quantity, string Pan) Order(string sku, int quantity, string pan) =>
        (sku, quantity, pan);

    /// <summary>Runs the compiled flow with an input built for its own loaded contract type.</summary>
    /// <remarks>
    /// The input type lives in the generated assembly, so it cannot be named here. It is
    /// constructed through its primary constructor and handed to the engine's non-generic
    /// overload's typed sibling by way of <c>object</c> — which is what
    /// <c>FlowHost</c> does with a trigger's body for the same reason.
    /// </remarks>
    private static async ValueTask<FlowExecutionResult> Run(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        (string Sku, int Quantity, string Pan) order)
    {
        var contract = dispatcher.GetType().Assembly.GetType("Sample.PlaceOrder", throwOnError: true)!;

        var input = Activator.CreateInstance(contract, order.Sku, order.Quantity, order.Pan)!;

        var seed = typeof(FlowEngine)
            .GetMethods()
            .Single(m => m.Name == nameof(FlowEngine.ExecuteAsync)
                         && m.IsGenericMethod
                         && m.GetGenericArguments().Length == 1
                         && m.GetParameters().Length == 5)
            .MakeGenericMethod(contract);

        var engine = new FlowEngine(SystemClock.Instance);

        var invocation = new FlowInvocation("corr-1", "idem-1", TenantId: "acme");

        var pending = (ValueTask<FlowExecutionResult>)seed.Invoke(
            engine, [plan, dispatcher, invocation, input, Ct])!;

        return await pending;
    }
}
