using System;
using System.Collections.Immutable;
using System.Linq;
using FlowX.Compiler.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// FLOWX1020 — whether a flow's steps can hand values to each other.
/// </summary>
/// <remarks>
/// Both directions for every case, and the negative direction carries the weight: this
/// rule reads step order, which is the thing developers change most often, so a false
/// positive here would be suppressed at the top of every flow file within a week. The
/// reference sample's real shape is pinned as a case that must stay silent.
/// </remarks>
public sealed class StepBindingAnalyzerTests
{
    private const string Preamble = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record PlaceOrder(string Sku, int Quantity);
        public sealed record ValidatedOrder(string Sku, int Quantity);
        public sealed record Reservation(string ReservationId);
        public sealed record Payment(string ReceiptId);
        public sealed record OrderPlacedResult(string ReservationId);
        public sealed record OrderPlaced(string ReservationId);

        [Capability("order.validate", Version = "1.0.0", Authorization = Authorization.Internal)]
        public sealed class ValidateOrder : ICapability<PlaceOrder, ValidatedOrder>
        {
            public ValueTask<Result<ValidatedOrder>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new ValidatedOrder(input.Sku, input.Quantity)));
        }

        [Capability("inventory.reserve", Version = "1.0.0", Authorization = Authorization.Internal)]
        public sealed class ReserveInventory : ICapability<ValidatedOrder, Reservation>
        {
            public ValueTask<Result<Reservation>> ExecuteAsync(ValidatedOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Reservation("r")));
        }

        [Capability("inventory.release", Version = "1.0.0", Authorization = Authorization.Internal)]
        public sealed class ReleaseInventory : ICapability<ValidatedOrder, Reservation>
        {
            public ValueTask<Result<Reservation>> ExecuteAsync(ValidatedOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Reservation("r")));
        }

        [Capability("payment.capture", Version = "1.0.0", Authorization = Authorization.Internal)]
        public sealed class CapturePayment : ICapability<Reservation, Payment>
        {
            public ValueTask<Result<Payment>> ExecuteAsync(Reservation input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Payment("p")));
        }
        """;

    private static string With(string body) => Preamble + "\n\n" + body;

    /// <summary>The preamble with an extra file-level using directive in front of it.</summary>
    private static string WithAlias(string directive, string body) =>
        directive + "\n" + Preamble + "\n\n" + body;

    [Fact]
    public void TheReferenceSampleShapeIsClean()
    {
        // ValidateOrder → ReserveInventory → CapturePayment, with a compensation, an
        // Emit and a Return, exactly as samples/ecommerce declares it. If this ever
        // reports, the rule has broken the only flow the repository actually ships.
        Analyze(With("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow)
                {
                    ArgumentNullException.ThrowIfNull(flow);

                    flow
                        .Step<ValidateOrder>()
                        .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()
                        .Step<CapturePayment>()
                        .Emit<OrderPlaced>(ctx => new OrderPlaced(ctx.Get<Reservation>().ReservationId))
                        .Return(ctx => new OrderPlacedResult(ctx.Get<Reservation>().ReservationId));
                }
            }
            """)).ShouldBeEmpty();
    }

    [Fact]
    public void ReportsFLOWX1020WhenAStepRunsBeforeItsProducer()
    {
        // The failure this rule exists for: the flow compiles, deploys, and throws
        // InvalidOperationException on the first request, for every request.
        Analyze(With("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow
                        .Step<ReserveInventory>()
                        .Step<ValidateOrder>();
            }
            """)).ShouldContain("FLOWX1020");
    }

    [Fact]
    public void TheMessageNamesTheStepTheMissingTypeAndWhatIsAvailable()
    {
        // A diagnostic that says only "incompatible" makes the developer reconstruct
        // the state bag by hand. All three facts are load-bearing.
        var message = Messages(With("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow.Step<CapturePayment>();
            }
            """)).Single();

        message.ShouldContain("CapturePayment");
        message.ShouldContain("Reservation");
        message.ShouldContain("PlaceOrder");
    }

    [Fact]
    public void ReportsAtTheStepThatCannotBeFed()
    {
        // Pointing at the flow, or at the first line of the chain, would make a
        // four-step flow report four identical squiggles in the same place.
        var location = Locations(With("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow
                        .Step<ValidateOrder>()
                        .Step<CapturePayment>()
                        .Step<ReserveInventory>();
            }
            """)).Single();

        location.ShouldBe("CapturePayment");
    }

    [Fact]
    public void TheFlowsOwnInputIsAvailableToEveryStep()
    {
        // The engine seeds it before step 1, and the bag is never cleared — so a step
        // late in the chain may still bind to it. A rule that only looked at the
        // immediately preceding step would reject this.
        Analyze(With("""
            [Capability("order.audit", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class AuditOrder : ICapability<PlaceOrder, OrderPlacedResult>
            {
                public ValueTask<Result<OrderPlacedResult>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new OrderPlacedResult("r")));
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow
                        .Step<ValidateOrder>()
                        .Step<ReserveInventory>()
                        .Step<AuditOrder>();
            }
            """)).ShouldBeEmpty();
    }

    [Fact]
    public void AnOutputStaysAvailableToStepsAfterTheNextOne()
    {
        // Step 3 binds to what step 1 produced, with step 2 in between.
        Analyze(With("""
            [Capability("order.reprice", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class RepriceOrder : ICapability<ValidatedOrder, Payment>
            {
                public ValueTask<Result<Payment>> ExecuteAsync(ValidatedOrder input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new Payment("p")));
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow
                        .Step<ValidateOrder>()
                        .Step<ReserveInventory>()
                        .Step<RepriceOrder>();
            }
            """)).ShouldBeEmpty();
    }

    [Fact]
    public void ABaseTypeIsNotSatisfiedByADerivedProducer()
    {
        // The state bag is keyed on typeof(T) and both sides use the declared contract,
        // so ctx.Get<OrderBase>() misses a slot written as DerivedOrder and throws. The
        // rule agrees with the runtime rather than with the assignability intuition —
        // being polite here would mean staying silent on a flow that genuinely breaks.
        Analyze(With("""
            public abstract record OrderBase(string Sku);
            public sealed record DerivedOrder(string Sku) : OrderBase(Sku);

            [Capability("order.derive", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class DeriveOrder : ICapability<PlaceOrder, DerivedOrder>
            {
                public ValueTask<Result<DerivedOrder>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new DerivedOrder(input.Sku)));
            }

            [Capability("order.consume", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class ConsumeBase : ICapability<OrderBase, Payment>
            {
                public ValueTask<Result<Payment>> ExecuteAsync(OrderBase input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new Payment("p")));
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow
                        .Step<DeriveOrder>()
                        .Step<ConsumeBase>();
            }
            """)).ShouldContain("FLOWX1020");
    }

    [Fact]
    public void AnInterfaceIsNotSatisfiedByAnImplementingProducer()
    {
        // Same reasoning as the base class, and worth its own case because an interface
        // is the shape a team reaches for when they want a step to be substitutable.
        Analyze(With("""
            public interface IPriced { }
            public sealed record PricedOrder(string Sku) : IPriced;

            [Capability("order.price", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class PriceOrder : ICapability<PlaceOrder, PricedOrder>
            {
                public ValueTask<Result<PricedOrder>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new PricedOrder(input.Sku)));
            }

            [Capability("order.ship", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class ShipOrder : ICapability<IPriced, Payment>
            {
                public ValueTask<Result<Payment>> ExecuteAsync(IPriced input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new Payment("p")));
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow
                        .Step<PriceOrder>()
                        .Step<ShipOrder>();
            }
            """)).ShouldContain("FLOWX1020");
    }

    [Fact]
    public void ACompensationIsNotCheckedAgainstTheBag()
    {
        // The dispatcher hands a compensation the input of the step it undoes, not
        // something fetched under the compensation's own contract. Reporting here would
        // point at a type nothing looks up. ReleaseFromReservation declares an input the
        // flow can supply at that point anyway; what matters is that it is not the
        // *compensation's* contract driving the decision — see the next test.
        Analyze(With("""
            [Capability("inventory.release2", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class ReleaseFromReservation : ICapability<Payment, Reservation>
            {
                public ValueTask<Result<Reservation>> ExecuteAsync(Payment input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new Reservation("r")));
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow
                        .Step<ValidateOrder>()
                        .Step<ReserveInventory>().CompensateWith<ReleaseFromReservation>();
            }
            """)).ShouldBeEmpty();
    }

    [Fact]
    public void ACompensationDoesNotMakeItsOutputAvailable()
    {
        // A compensation runs only on the failure path, after which no later step runs.
        // Counting its output would suppress a genuine ordering error.
        Analyze(With("""
            [Capability("order.undo", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class UndoValidation : ICapability<PlaceOrder, Reservation>
            {
                public ValueTask<Result<Reservation>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new Reservation("r")));
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow
                        .Step<ValidateOrder>().CompensateWith<UndoValidation>()
                        .Step<CapturePayment>();
            }
            """)).ShouldContain("FLOWX1020");
    }

    [Fact]
    public void AMappedStepSuppliesItsOwnInput()
    {
        // .Step<TCapability, TStepIn>(map) is the documented fix for this diagnostic.
        // Reporting it would fire on the remedy, which is how a rule gets disabled.
        Analyze(With("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow
                        .Step<CapturePayment, Reservation>(ctx => new Reservation(ctx.Input.Sku))
                        .Step<ValidateOrder>();
            }
            """)).ShouldBeEmpty();
    }

    [Fact]
    public void AMappedStepStillCountsAsAProducer()
    {
        // It runs and it returns, so what it returns reaches the bag.
        Analyze(With("""
            [Capability("payment.settle", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class SettlePayment : ICapability<Payment, OrderPlacedResult>
            {
                public ValueTask<Result<OrderPlacedResult>> ExecuteAsync(Payment input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new OrderPlacedResult("r")));
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow
                        .Step<CapturePayment, Reservation>(ctx => new Reservation(ctx.Input.Sku))
                        .Step<SettlePayment>();
            }
            """)).ShouldBeEmpty();
    }

    [Fact]
    public void StaysSilentAfterABranchItCannotLinearise()
    {
        // A When branch can put anything into the bag out of sight of this walk. The
        // honest answer past that point is nothing at all — see the analyzer's remarks.
        Analyze(With("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow
                        .When(ctx => ctx.Input.Quantity > 0, then => then.Step<ValidateOrder>())
                        .Step<ReserveInventory>();
            }
            """)).ShouldBeEmpty();
    }

    [Fact]
    public void StepsBeforeABranchAreStillChecked()
    {
        // Nothing hidden can have run before the branch, so the prefix is still
        // decidable — abandoning the whole flow would throw away the part that is known.
        Analyze(With("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow
                        .Step<CapturePayment>()
                        .When(ctx => ctx.Input.Quantity > 0, then => then.Step<ValidateOrder>())
                        .Step<ReserveInventory>();
            }
            """)).ShouldContain("FLOWX1020");
    }

    [Fact]
    public void AnEmitDoesNotProduceAValueLaterStepsCanBind()
    {
        // Emit builds its event from a lambda and publishes it. The event never enters
        // the bag, so a step consuming one is still unfed.
        Analyze(With("""
            [Capability("order.announce", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class AnnounceOrder : ICapability<OrderPlaced, Payment>
            {
                public ValueTask<Result<Payment>> ExecuteAsync(OrderPlaced input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new Payment("p")));
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow
                        .Step<ValidateOrder>()
                        .Emit<OrderPlaced>(ctx => new OrderPlaced("r"))
                        .Step<AnnounceOrder>();
            }
            """)).ShouldContain("FLOWX1020");
    }

    [Fact]
    public void ANonCapabilityStepIsLeftToFLOWX1002()
    {
        // The type has no contract to read, so this rule has no defensible answer and
        // gives none — a second error about the same line would be noise.
        Analyze(With("""
            public sealed class NotACapability { }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow
                        .Step<NotACapability>()
                        .Step<CapturePayment>();
            }
            """)).ShouldBeEmpty();
    }

    // ---------------------------------------------------------------------------------
    // How a step's type argument is spelled.
    //
    // The rule resolves it against the binder just inside the flow class's opening brace
    // rather than at the call site, which is what keeps it from binding the whole Define
    // body — see the analyzer's remarks. These four pin the spellings where the two scopes
    // could conceivably disagree. Every one is a flow that MUST report: a type argument
    // that fails to resolve makes this rule abandon the flow and say nothing, so a case
    // written the silent way would pass whether or not the name was ever found.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void ANamespaceQualifiedCapabilityNameResolves()
    {
        Analyze(With("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow
                        .Step<Sample.ReserveInventory>()
                        .Step<Sample.ValidateOrder>();
            }
            """)).ShouldContain("FLOWX1020");
    }

    [Fact]
    public void AnAliasedCapabilityNameResolves()
    {
        // An alias belongs to the file rather than to a position in it, so both scopes
        // see it — but only through a binder that carries the file's usings at all.
        Analyze(WithAlias("using Reserve = Sample.ReserveInventory;", """
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow.Step<Reserve>();
            }
            """)).ShouldContain("FLOWX1020");
    }

    [Fact]
    public void ACapabilityNestedInTheFlowResolves()
    {
        // The flow's own members are in scope at its opening brace, which is the one
        // thing a namespace-level binder would not have given.
        Analyze(With("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                [Capability("order.finish", Version = "1.0.0", Authorization = Authorization.Internal)]
                public sealed class Finish : ICapability<Reservation, Payment>
                {
                    public ValueTask<Result<Payment>> ExecuteAsync(Reservation input, CapabilityContext ctx, CancellationToken ct)
                        => ValueTask.FromResult(Result.Ok(new Payment("p")));
                }

                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow.Step<Finish>();
            }
            """)).ShouldContain("FLOWX1020");
    }

    [Fact]
    public void AConstructedGenericCapabilityNameResolves()
    {
        Analyze(With("""
            [Capability("order.settle", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class Settle<T> : ICapability<T, OrderPlacedResult>
                where T : class
            {
                public ValueTask<Result<OrderPlacedResult>> ExecuteAsync(T input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new OrderPlacedResult("r")));
            }

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow.Step<Settle<Payment>>();
            }
            """)).ShouldContain("FLOWX1020");
    }

    [Fact]
    public void AClassThatIsNotAFlowIsNotChecked()
    {
        // The rule is about flows. An ordinary class that happens to have a Define
        // method is none of its business.
        Analyze(With("""
            public sealed class NotAFlow
            {
                public void Define(object flow) { }
            }
            """)).ShouldBeEmpty();
    }

    /// <summary>Runs the analyzer and returns the ids it reported.</summary>
    /// <remarks>
    /// A local harness rather than a new <c>GeneratorHarness</c> entry point: that type
    /// is shared with every other suite in this project, and widening it for one rule
    /// would make every other suite recompile against a signature it does not use. The
    /// references are the same real FlowX assemblies, for the reason the harness states
    /// — stubs drift from the contracts they imitate.
    /// </remarks>
    private static string[] Analyze(string source) =>
        [.. Run(source).Select(static d => d.Id).Distinct(StringComparer.Ordinal).OrderBy(static id => id, StringComparer.Ordinal)];

    private static string[] Messages(string source) =>
        [.. Run(source).Select(static d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))];

    /// <summary>The source text each diagnostic underlines, which is what a squiggle lands on.</summary>
    private static string[] Locations(string source) =>
        [.. Run(source).Select(static d => d.Location.SourceTree!.GetText().ToString(d.Location.SourceSpan))];

    private static ImmutableArray<Diagnostic> Run(string source)
    {
        var compilation = CSharpCompilation.Create(
            "FlowX.StepBindingTests",
            [CSharpSyntaxTree.ParseText(source, path: "/src/Flows/Sample.cs")],
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        return compilation
            .WithAnalyzers([new StepBindingAnalyzer()])
            .GetAnalyzerDiagnosticsAsync()
            .GetAwaiter()
            .GetResult();
    }

    private static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var trusted = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(System.IO.Path.PathSeparator)
            .Where(static path => path.Length > 0);

        var flowX = new[]
        {
            typeof(FlowX.ICapability<,>).Assembly,
            typeof(FlowX.ExecutionPlan).Assembly,
            typeof(FlowX.Runtime.FlowEngine).Assembly,
        }.Select(static a => a.Location);

        return [.. trusted.Concat(flowX)
            .Distinct(StringComparer.Ordinal)
            .Where(System.IO.File.Exists)
            .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))];
    }
}
