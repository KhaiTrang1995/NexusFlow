using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using FlowX.Compiler.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// FLOWX1011 — whether a <c>When</c> condition decides from the flow's own state.
/// </summary>
/// <remarks>
/// <para>
/// Both directions for every rule, and the silent direction carries the weight. This one
/// reads arbitrary expressions rather than a chain of known builder calls, so its false
/// positives would land on ordinary C# — a comparison against a constant, a LINQ
/// predicate over a step result — and a build gate that fires on those gets suppressed
/// at the top of the file, after which it protects nothing.
/// </para>
/// <para>
/// The severity cases are here as well as the detection cases, because "error under
/// Durable, warning under Ephemeral" is a deliberate decision and not an implementation
/// detail; a later change that flattened it would otherwise pass every test.
/// </para>
/// </remarks>
public sealed class PredicatePurityAnalyzerTests
{
    private const string Preamble = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public enum Channel { Retail, Wholesale }

        public sealed record OrderLine(string Sku, int Quantity);
        public sealed record PlaceOrder(string Sku, int Quantity);
        public sealed record ValidatedOrder(
            string Sku, int Quantity, decimal Total, Channel Channel, IReadOnlyList<OrderLine> Lines);
        public sealed record OrderPlacedResult(string ReservationId);

        public interface IPricingService { bool IsPromotional(string sku); }

        /// <summary>A look-alike fluent API, to prove the rule is not matching on a method name.</summary>
        public interface IMatcher { IMatcher When(Func<PlaceOrder, bool> predicate, string name); }

        public static class Rules
        {
            public const decimal Cutoff = 5_000m;
            public static readonly decimal Ceiling = 10_000m;
            public static decimal Floor = 1m;
            public static decimal Configured { get; set; } = 2m;
            public static decimal Fixed { get; } = 3m;
            public static decimal Twice(decimal value) => value * 2m;
        }

        [Capability("order.validate", Version = "1.0.0", Authorization = Authorization.Internal)]
        public sealed class ValidateOrder : ICapability<PlaceOrder, ValidatedOrder>
        {
            public ValueTask<Result<ValidatedOrder>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new ValidatedOrder(
                    input.Sku, input.Quantity, 1m, Channel.Retail, Array.Empty<OrderLine>())));
        }

        [Capability("order.approve", Version = "1.0.0", Authorization = Authorization.Internal)]
        public sealed class RequireApproval : ICapability<ValidatedOrder, OrderPlacedResult>
        {
            public ValueTask<Result<OrderPlacedResult>> ExecuteAsync(ValidatedOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new OrderPlacedResult("r")));
        }
        """;

    private static string With(string body) => Preamble + "\n\n" + body;

    /// <summary>A one-branch flow whose only variable is the condition and the profile.</summary>
    private static string Condition(string expression, string profile = "Ephemeral") => With(
        $$"""
        [Flow("order.place", Profile = ExecutionProfile.{{profile}})]
        public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
        {
            protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                flow
                    .Step<ValidateOrder>()
                    .When({{expression}}, then => then.Step<RequireApproval>())
                    .Return(ctx => new OrderPlacedResult("r"));
        }
        """);

    // ---------------------------------------------------------------- silent

    [Fact]
    public void TheReferenceSampleShapeIsClean()
    {
        // samples/ecommerce declares no branch at all, so the rule must have nothing to
        // say about the only flow the repository actually ships. Pinned here because a
        // rule registered on every invocation in the compilation is exactly the kind that
        // starts reporting on something it was never about.
        Analyze(With("""
            [Flow("order.place", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "orders")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow)
                {
                    ArgumentNullException.ThrowIfNull(flow);

                    flow
                        .Step<ValidateOrder>()
                        .Step<RequireApproval>()
                        .Return(ctx => new OrderPlacedResult("r"));
                }
            }
            """)).ShouldBeEmpty();
    }

    [Fact]
    public void TheFlowInputIsReadable() =>
        Analyze(Condition("ctx => ctx.Input.Quantity > 10")).ShouldBeEmpty();

    [Fact]
    public void APriorStepResultIsReadable() =>
        // The rule's entire purpose is to leave this alone: a chain of member accesses
        // rooted at the context, however long.
        Analyze(Condition("ctx => ctx.Get<ValidatedOrder>().Total > 100m")).ShouldBeEmpty();

    [Fact]
    public void TryGetIsReadable() =>
        Analyze(Condition("ctx => ctx.TryGet<ValidatedOrder>(out var order) && order.Quantity > 0"))
            .ShouldBeEmpty();

    [Fact]
    public void TheContextClockIsReadable() =>
        // ctx.UtcNow is journaled on first read, which is what makes it replayable — see
        // 06 §5. Reporting it would report the documented fix for this very diagnostic,
        // and a rule that fires on its own remedy is a rule people turn off.
        Analyze(Condition("ctx => ctx.UtcNow.Hour < 17")).ShouldBeEmpty();

    [Fact]
    public void ContextIdentityAndRandomnessAreReadable() =>
        Analyze(Condition("ctx => ctx.Random.Next(10) > 5 || ctx.NewId() != Guid.Empty")).ShouldBeEmpty();

    [Fact]
    public void AConstantIsReadable() =>
        Analyze(Condition("ctx => ctx.Get<ValidatedOrder>().Total > Rules.Cutoff")).ShouldBeEmpty();

    [Fact]
    public void AStaticReadOnlyFieldIsReadable() =>
        // Permitted, and only shallowly sound — a static readonly List<T> would slip
        // through. Documented as a limit rather than presented as a check.
        Analyze(Condition("ctx => ctx.Get<ValidatedOrder>().Total < Rules.Ceiling")).ShouldBeEmpty();

    [Fact]
    public void AGetOnlyStaticPropertyIsReadable() =>
        // A static helper's getter is far more often a constant than an ambient
        // singleton. Reporting every one of them would fire on valid code.
        Analyze(Condition("ctx => ctx.Get<ValidatedOrder>().Total > Rules.Fixed")).ShouldBeEmpty();

    [Fact]
    public void AnEnumMemberIsReadable() =>
        Analyze(Condition("ctx => ctx.Get<ValidatedOrder>().Channel == Channel.Wholesale")).ShouldBeEmpty();

    [Fact]
    public void ALambdaParameterInsideTheConditionIsReadable() =>
        // The natural way to ask a question about a collection. Treating the item as a
        // capture would report it, which is the false positive most likely to be hit.
        Analyze(Condition("ctx => ctx.Get<ValidatedOrder>().Lines.Any(line => line.Quantity > 0)"))
            .ShouldBeEmpty();

    [Fact]
    public void AStaticMethodCallIsNotReported() =>
        // Not because it is known to be pure — nothing here is interprocedural — but
        // because reporting every unresolvable call would fire on Math.Max.
        Analyze(Condition("ctx => Math.Max(ctx.Input.Quantity, 1) > 2 && Rules.Twice(1m) > 0m"))
            .ShouldBeEmpty();

    [Fact]
    public void PatternsAndFrameworkConstantsAreReadable() =>
        // The sweep that matters most: everything an ordinary condition is made of.
        // StringComparison.Ordinal is an enum member, decimal.MaxValue is a static
        // readonly field, and a property pattern reads a type name in an expression
        // position — each of which the classification has to let past.
        Analyze(Condition(
            """
            ctx => ctx.Get<ValidatedOrder>() is { Quantity: > 0 }
                && ctx.Input.Sku.StartsWith("X", StringComparison.Ordinal)
                && ctx.Get<ValidatedOrder>().Total < decimal.MaxValue
            """)).ShouldBeEmpty();

    [Fact]
    public void TheDeadlineAndRemainingBudgetAreReadable() =>
        // Both come off the context, and TimeSpan.Zero is a static readonly field.
        Analyze(Condition("ctx => ctx.TimeRemaining > TimeSpan.Zero && ctx.Deadline > ctx.UtcNow"))
            .ShouldBeEmpty();

    [Fact]
    public void ASwitchExpressionOverAStepResultIsReadable() =>
        Analyze(Condition(
            """
            ctx => ctx.Get<ValidatedOrder>().Channel switch
            {
                Channel.Retail => ctx.Input.Quantity > 1,
                _ => false,
            }
            """)).ShouldBeEmpty();

    [Fact]
    public void ALocalDeclaredInsideABlockBodiedConditionIsReadable() =>
        Analyze(Condition(
            """
            ctx =>
            {
                var total = ctx.Get<ValidatedOrder>().Total;
                return total > 0m;
            }
            """)).ShouldBeEmpty();

    [Fact]
    public void AConstLocalIsReadable()
    {
        // A const local is inlined by the compiler; there is no capture and no state.
        Analyze(With("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow)
                {
                    const decimal cutoff = 5m;

                    flow
                        .Step<ValidateOrder>()
                        .When(ctx => ctx.Get<ValidatedOrder>().Total > cutoff, then => then.Step<RequireApproval>())
                        .Return(ctx => new OrderPlacedResult("r"));
                }
            }
            """)).ShouldBeEmpty();
    }

    [Fact]
    public void NameOfReadsTheNameAndNotTheValue()
    {
        // The one construct that provably touches nothing. Without the guard, the symbol
        // inside nameof resolves to a captured local and reports.
        Analyze(With("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow)
                {
                    var cutoff = 5m;

                    flow
                        .Step<ValidateOrder>()
                        .When(ctx => ctx.Input.Sku == nameof(cutoff), then => then.Step<RequireApproval>())
                        .Return(ctx => new OrderPlacedResult("r"));
                }
            }
            """)).ShouldBeEmpty();
    }

    [Fact]
    public void AnotherLibrarysWhenIsNotTouched()
    {
        // "When" is a word fluent libraries use. The rule resolves the method to
        // IFlowBuilder<,> rather than matching the identifier, because a determinism
        // diagnostic firing inside a mocking framework would be indefensible.
        Analyze(With("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                private static readonly IMatcher Matcher = null!;

                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow)
                {
                    Matcher.When(order => DateTime.UtcNow.Hour > 1, "after one");

                    flow.Step<ValidateOrder>().Return(ctx => new OrderPlacedResult("r"));
                }
            }
            """)).ShouldBeEmpty();
    }

    [Fact]
    public void AConditionOutsideAFlowIsNotReported()
    {
        // A helper composing a builder. The message names a flow, and there is no flow
        // here to name — inventing one would make the diagnostic lie about where the
        // branch lives. A stated limit, not an oversight.
        Analyze(With("""
            public static class Branching
            {
                public static void Add(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow.When(ctx => DateTime.UtcNow.Hour > 1, then => then.Step<RequireApproval>());
            }
            """)).ShouldBeEmpty();
    }

    [Fact]
    public void APredicateThatIsNotALambdaIsNotReported()
    {
        // A method group has no body at the call site. Silence here is the honest answer
        // and is documented on the page; it is also the one hole a determined author can
        // walk through.
        Analyze(With("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                private static bool AfterOne(FlowContext<PlaceOrder> ctx) => DateTime.UtcNow.Hour > 1;

                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow
                        .Step<ValidateOrder>()
                        .When(AfterOne, then => then.Step<RequireApproval>())
                        .Return(ctx => new OrderPlacedResult("r"));
            }
            """)).ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- reported

    [Fact]
    public void ReportsAClock() =>
        Analyze(Condition("ctx => DateTime.UtcNow.Hour < 17")).ShouldContain("FLOWX1011");

    [Fact]
    public void ReportsAFullyQualifiedClock() =>
        // The chain is rooted at a namespace, so the classification has to climb through
        // System and System.DateTime before it reaches anything that is a read.
        Analyze(Condition("ctx => System.DateTimeOffset.Now.Hour < 17")).ShouldContain("FLOWX1011");

    [Fact]
    public void ReportsLocalTime() =>
        Analyze(Condition("ctx => DateTime.Today.DayOfWeek == DayOfWeek.Monday")).ShouldContain("FLOWX1011");

    [Fact]
    public void ReportsIdentityGeneration() =>
        Analyze(Condition("ctx => Guid.NewGuid() != Guid.Empty")).ShouldContain("FLOWX1011");

    [Fact]
    public void ReportsAmbientRandomness() =>
        Analyze(Condition("ctx => Random.Shared.Next(10) > 5")).ShouldContain("FLOWX1011");

    [Fact]
    public void ReportsAConstructedRandom() =>
        Analyze(Condition("ctx => new Random().Next(10) > 5")).ShouldContain("FLOWX1011");

    [Fact]
    public void ReportsTheEnvironment() =>
        Analyze(Condition("ctx => Environment.MachineName.Length > 3")).ShouldContain("FLOWX1011");

    [Fact]
    public void ReportsMutableStaticState() =>
        Analyze(Condition("ctx => ctx.Get<ValidatedOrder>().Total > Rules.Floor")).ShouldContain("FLOWX1011");

    [Fact]
    public void ReportsASettableStaticProperty() =>
        // A setter is the difference between a constant and ambient state something else
        // can change between two runs of the same flow.
        Analyze(Condition("ctx => ctx.Get<ValidatedOrder>().Total > Rules.Configured")).ShouldContain("FLOWX1011");

    [Fact]
    public void ReportsACapturedVariable()
    {
        // Decided from the symbol's declaration rather than from a list, so it cannot be
        // evaded: the value is computed once, outside the flow's state, and the branch
        // then depends on something the manifest cannot see.
        Analyze(With("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow)
                {
                    var cutoff = Rules.Ceiling;

                    flow
                        .Step<ValidateOrder>()
                        .When(ctx => ctx.Get<ValidatedOrder>().Total > cutoff, then => then.Step<RequireApproval>())
                        .Return(ctx => new OrderPlacedResult("r"));
                }
            }
            """)).ShouldContain("FLOWX1011");
    }

    [Fact]
    public void ReportsAnInjectedService()
    {
        // The case the DSL comment names: a condition that "reads an external service".
        // It reaches a predicate as a field on the flow, and no list of impure types
        // would ever contain the application's own interface.
        Analyze(With("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                private readonly IPricingService _pricing = null!;

                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow
                        .Step<ValidateOrder>()
                        .When(ctx => _pricing.IsPromotional(ctx.Input.Sku), then => then.Step<RequireApproval>())
                        .Return(ctx => new OrderPlacedResult("r"));
            }
            """)).ShouldContain("FLOWX1011");
    }

    [Fact]
    public void ReportsAnExplicitThisAccess()
    {
        Analyze(With("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                private bool Enabled => true;

                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow
                        .Step<ValidateOrder>()
                        .When(ctx => this.Enabled, then => then.Step<RequireApproval>())
                        .Return(ctx => new OrderPlacedResult("r"));
            }
            """)).ShouldContain("FLOWX1011");
    }

    [Fact]
    public void ReportsAConditionNestedInsideABranch()
    {
        // Registered per invocation rather than per flow, so nesting depth costs nothing.
        Analyze(With("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow
                        .Step<ValidateOrder>()
                        .When(ctx => ctx.Input.Quantity > 0, then => then
                            .When(inner => DateTime.UtcNow.Hour > 1, deep => deep.Step<RequireApproval>()))
                        .Return(ctx => new OrderPlacedResult("r"));
            }
            """)).ShouldContain("FLOWX1011");
    }

    // ---------------------------------------------------------------- message and severity

    [Fact]
    public void TheMessageNamesTheFlowTheExpressionAndTheReason()
    {
        // Three facts, all load-bearing: a message that says only "impure" leaves the
        // developer scanning an expression for which sub-expression is the problem.
        var message = Messages(Condition("ctx => DateTime.UtcNow.Hour < 17")).Single();

        message.ShouldContain("PlaceOrderFlow");
        message.ShouldContain("DateTime.UtcNow");
        message.ShouldContain("the system clock");
    }

    [Fact]
    public void ReportsAtTheOffendingExpression() =>
        // Not at the When, and not at the whole predicate: a condition with one bad term
        // among five should squiggle the one.
        Locations(Condition("ctx => ctx.Input.Quantity > 0 && DateTime.UtcNow.Hour < 17"))
            .Single()
            .ShouldBe("DateTime.UtcNow");

    [Fact]
    public void IsAnErrorInADurableFlow() =>
        // A durable flow is replayed, and a branch that is not a function of flow state
        // takes a different path on replay. ADR-0003 and 06 §5.
        Severities(Condition("ctx => DateTime.UtcNow.Hour < 17", profile: "Durable"))
            .Single()
            .ShouldBe(DiagnosticSeverity.Error);

    [Fact]
    public void IsAWarningInAnEphemeralFlow() =>
        // Nothing is replayed, so nothing diverges — but the flow is one attribute away
        // from being replayed, and Ephemeral is the only profile the runtime executes
        // today. Info, which is what ADR-0003 originally said, would be invisible in a
        // build log and would ship a rule that does nothing anywhere.
        Severities(Condition("ctx => DateTime.UtcNow.Hour < 17"))
            .Single()
            .ShouldBe(DiagnosticSeverity.Warning);

    [Fact]
    public void AnOmittedProfileIsTreatedAsEphemeral() =>
        // FlowAttribute defaults to Ephemeral: durability is opted into, never out of.
        Severities(With("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow
                        .Step<ValidateOrder>()
                        .When(ctx => DateTime.UtcNow.Hour < 17, then => then.Step<RequireApproval>())
                        .Return(ctx => new OrderPlacedResult("r"));
            }
            """))
            .Single()
            .ShouldBe(DiagnosticSeverity.Warning);

    // ---------------------------------------------------------------- harness

    private static string[] Analyze(string source) =>
        [.. Run(source).Select(static d => d.Id).Distinct(StringComparer.Ordinal).OrderBy(static id => id, StringComparer.Ordinal)];

    private static string[] Messages(string source) =>
        [.. Run(source).Select(static d => d.GetMessage(CultureInfo.InvariantCulture))];

    /// <summary>The source text each diagnostic underlines, which is what a squiggle lands on.</summary>
    private static string[] Locations(string source) =>
        [.. Run(source).Select(static d => d.Location.SourceTree!.GetText().ToString(d.Location.SourceSpan))];

    private static DiagnosticSeverity[] Severities(string source) =>
        [.. Run(source).Select(static d => d.Severity)];

    /// <summary>
    /// Runs the analyzer over a real compilation, refusing to proceed if the test's own
    /// source does not compile.
    /// </summary>
    /// <remarks>
    /// The pre-check is not decoration. This analyzer says nothing about a symbol it
    /// cannot resolve, so a typo in a test string would make every silence assertion pass
    /// for the wrong reason — a suite that is green because the semantic model gave up.
    /// </remarks>
    private static ImmutableArray<Diagnostic> Run(string source)
    {
        var compilation = CSharpCompilation.Create(
            "FlowX.PredicatePurityTests",
            [CSharpSyntaxTree.ParseText(source, path: "/src/Flows/Sample.cs")],
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var errors = compilation.GetDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .Select(static d => $"{d.Id}: {d.GetMessage(CultureInfo.InvariantCulture)}")
            .ToArray();

        errors.ShouldBeEmpty("The test's own source must compile, or the analyzer is being asked about symbols that do not bind.");

        return compilation
            .WithAnalyzers([new PredicatePurityAnalyzer()])
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
