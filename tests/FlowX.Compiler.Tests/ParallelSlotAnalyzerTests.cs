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
/// FLOWX1013 — whether the branches of a <c>Parallel</c> write disjoint context slots.
/// </summary>
/// <remarks>
/// <para>
/// Both directions, and the silent direction carries the weight — more here than for any
/// other rule in the catalogue. A fork is written precisely when several checks have to run
/// at once, and those checks routinely share shapes; a rule that fired on
/// <c>CheckCredit</c> and <c>CheckFraud</c> because both return something plausible would
/// be suppressed on the first flow that used it.
/// </para>
/// <para>
/// The stated limits are tested too, not merely documented. A limit nobody has written a
/// test for is a limit nobody has checked is still true.
/// </para>
/// </remarks>
public sealed class ParallelSlotAnalyzerTests
{
    private const string Preamble = """
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record PlaceOrder(string Sku);
        public sealed record OrderResult(string Id);
        public sealed record CreditCheck(bool Passed);
        public sealed record FraudCheck(bool Passed);
        public sealed record SanctionsCheck(bool Passed);
        public record Quote(decimal Amount);
        public sealed record RetailQuote(decimal Amount) : Quote(Amount);

        [Capability("risk.credit", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
        public sealed class CheckCredit : ICapability<PlaceOrder, CreditCheck>
        {
            public ValueTask<Result<CreditCheck>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new CreditCheck(true)));
        }

        [Capability("risk.fraud", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
        public sealed class CheckFraud : ICapability<PlaceOrder, FraudCheck>
        {
            public ValueTask<Result<FraudCheck>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new FraudCheck(true)));
        }

        [Capability("risk.sanctions", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
        public sealed class CheckSanctions : ICapability<PlaceOrder, SanctionsCheck>
        {
            public ValueTask<Result<SanctionsCheck>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new SanctionsCheck(true)));
        }

        [Capability("quote.primary", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
        public sealed class QuotePrimary : ICapability<PlaceOrder, Quote>
        {
            public ValueTask<Result<Quote>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Quote(1m)));
        }

        [Capability("quote.secondary", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
        public sealed class QuoteSecondary : ICapability<PlaceOrder, Quote>
        {
            public ValueTask<Result<Quote>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Quote(2m)));
        }

        [Capability("quote.retail", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
        public sealed class QuoteRetail : ICapability<PlaceOrder, RetailQuote>
        {
            public ValueTask<Result<RetailQuote>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new RetailQuote(3m)));
        }
        """;

    private static string WithFlow(string body) => Preamble + """


        [Flow("order.screen")]
        public sealed partial class ScreenOrderFlow : Flow<PlaceOrder, OrderResult>
        {
            protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
        """ + "\n" + body + """

                .Return(ctx => new OrderResult("id"));
        }
        """;

    // ---------------------------------------------------------------- it fires

    [Fact]
    public void ReportsTwoBranchesProducingTheSameContract()
    {
        Analyze(WithFlow("""
                    .Parallel(p => p
                            .Branch<QuotePrimary>()
                            .Branch<QuoteSecondary>(),
                        merge: MergeStrategy.AllMustSucceed)
            """)).ShouldBe(["FLOWX1013"]);
    }

    [Fact]
    public void TheMessageNamesBothBranchesTheFlowAndTheContestedSlot()
    {
        var message = Messages(WithFlow("""
                    .Parallel(p => p
                            .Branch<CheckCredit>()
                            .Branch<QuotePrimary>()
                            .Branch<QuoteSecondary>(),
                        merge: MergeStrategy.AllMustSucceed)
            """)).Single();

        message.ShouldContain("Branches 1 and 2");
        message.ShouldContain("ScreenOrderFlow");
        // The slot is the contract type, because that is the key the state bag uses.
        message.ShouldContain("Sample.Quote");
    }

    [Fact]
    public void TheSquiggleLandsOnTheSecondBranchRatherThanTheWholeChain()
    {
        // A fluent chain nests its receiver inside every later call, so underlining the
        // invocation would highlight every earlier branch too and the reader would not know
        // which one the message meant.
        Locations(WithFlow("""
                    .Parallel(p => p
                            .Branch<QuotePrimary>()
                            .Branch<QuoteSecondary>(),
                        merge: MergeStrategy.AllMustSucceed)
            """)).ShouldBe(["Branch<QuoteSecondary>"]);
    }

    [Fact]
    public void ReportsAConflictBetweenAMultiStepBranchAndAShorthandOne()
    {
        Analyze(WithFlow("""
                    .Parallel(p => p
                            .Branch(slow => slow.Step<CheckCredit>().Step<QuotePrimary>())
                            .Branch<QuoteSecondary>(),
                        merge: MergeStrategy.AllSettled)
            """)).ShouldBe(["FLOWX1013"]);
    }

    /// <summary>
    /// A conditional inside a branch is treated as a write, even though at most one arm
    /// runs.
    /// </summary>
    /// <remarks>
    /// Deliberate. The race is real on the runs where the predicate holds, and a rule that
    /// only fired when the collision was certain would be silent on every intermittent
    /// version of this bug — which is the version that reaches production.
    /// </remarks>
    [Fact]
    public void ReportsAConflictHiddenInsideAConditionalWithinABranch()
    {
        Analyze(WithFlow("""
                    .Parallel(p => p
                            .Branch(a => a
                                .When(ctx => ctx.Input.Sku == "rare", rare => rare.Step<QuotePrimary>()))
                            .Branch<QuoteSecondary>(),
                        merge: MergeStrategy.AllMustSucceed)
            """)).ShouldBe(["FLOWX1013"]);
    }

    [Fact]
    public void ReportsEveryColliderRatherThanOnlyTheFirstPair()
    {
        // Three branches producing one contract is two conflicts, and reporting one of
        // them would leave a developer fixing the flow twice.
        Analyze(WithFlow("""
                    .Parallel(p => p
                            .Branch<QuotePrimary>()
                            .Branch<QuoteSecondary>()
                            .Branch(third => third.Step<QuoteSecondary>()),
                        merge: MergeStrategy.AllMustSucceed)
            """), distinct: false).Length.ShouldBe(2);
    }

    [Fact]
    public void ReportsAConflictInsideANestedFork()
    {
        // The inner fork is checked in its own right, and its branches are not counted as
        // branches of the outer one.
        Analyze(WithFlow("""
                    .Parallel(p => p
                            .Branch<CheckCredit>()
                            .Branch(inner => inner
                                .Parallel(q => q
                                        .Branch<QuotePrimary>()
                                        .Branch<QuoteSecondary>(),
                                    merge: MergeStrategy.AllSettled)),
                        merge: MergeStrategy.AllMustSucceed)
            """)).ShouldBe(["FLOWX1013"]);
    }

    // ---------------------------------------------------------------- it stays silent

    [Fact]
    public void SaysNothingWhenEveryBranchProducesItsOwnContract()
    {
        // The documented shape from 06 §9, which must not be reported. This is the case
        // that decides whether the rule survives contact with a real codebase.
        Analyze(WithFlow("""
                    .Parallel(p => p
                            .Branch<CheckCredit>()
                            .Branch<CheckFraud>()
                            .Branch<CheckSanctions>(),
                        merge: MergeStrategy.AllMustSucceed)
            """)).ShouldBeEmpty();
    }

    [Fact]
    public void SaysNothingAboutStepsThatMerelyRunInSequence()
    {
        Analyze(WithFlow("""
                    .Step<QuotePrimary>()
                    .Step<QuoteSecondary>()
            """)).ShouldBeEmpty("Two sequential writes are an overwrite the author can see, not a race.");
    }

    [Fact]
    public void SaysNothingAboutARepeatedSlotWithinOneBranch()
    {
        // Same reasoning: within a branch the steps are ordered, so the second write
        // winning is a decision rather than an accident.
        Analyze(WithFlow("""
                    .Parallel(p => p
                            .Branch(a => a.Step<QuotePrimary>().Step<QuoteSecondary>())
                            .Branch<CheckFraud>(),
                        merge: MergeStrategy.AllMustSucceed)
            """)).ShouldBeEmpty();
    }

    /// <summary>
    /// A derived contract is a different slot, and correctly so.
    /// </summary>
    /// <remarks>
    /// The state bag is keyed by <c>typeof(T)</c> at the static type the dispatcher writes,
    /// so <c>Quote</c> and <c>RetailQuote</c> occupy different keys and never collide. A
    /// rule that reported them would be reporting a race the runtime cannot have.
    /// </remarks>
    [Fact]
    public void SaysNothingWhenOneContractDerivesFromTheOther()
    {
        Analyze(WithFlow("""
                    .Parallel(p => p
                            .Branch<QuotePrimary>()
                            .Branch<QuoteRetail>(),
                        merge: MergeStrategy.AllMustSucceed)
            """)).ShouldBeEmpty();
    }

    [Fact]
    public void SaysNothingAboutAnotherLibrarysParallel()
    {
        // Resolved semantically rather than matched on the name. A rule about flow
        // branching that also fired inside System.Threading.Tasks.Parallel would be
        // indefensible.
        Analyze(Preamble + """


            public static class Elsewhere
            {
                public static void Run() =>
                    System.Threading.Tasks.Parallel.For(0, 2, i => { });
            }
            """).ShouldBeEmpty();
    }

    [Fact]
    public void SaysNothingAboutAForkOutsideAFlowClass()
    {
        // The message names a flow, and inventing one would make the diagnostic lie.
        Analyze(Preamble + """


            public static class Composer
            {
                public static void Compose(IFlowBuilder<PlaceOrder, OrderResult> flow) =>
                    flow.Parallel(p => p.Branch<QuotePrimary>().Branch<QuoteSecondary>(),
                        merge: MergeStrategy.AllMustSucceed);
            }
            """).ShouldBeEmpty();
    }

    /// <summary>The largest stated gap, tested so that it stays a known gap rather than a belief.</summary>
    /// <remarks>
    /// <c>FlowContext.Set</c> is public, so a capability can write any slot it likes from
    /// inside its own body and this rule will not see it. Closing that needs an effects
    /// attribute or a whole-program analysis, not a longer list — the same limit
    /// <c>PredicatePurityAnalyzer</c> has with helper methods.
    /// </remarks>
    [Fact]
    public void CannotSeeASlotACapabilityWritesFromInsideItsOwnBody()
    {
        Analyze(Preamble + """


            [Capability("risk.sneaky", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
            public sealed class SneakyCheck : ICapability<PlaceOrder, FraudCheck>
            {
                public ValueTask<Result<FraudCheck>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new FraudCheck(true)));
            }

            [Flow("order.screen")]
            public sealed partial class ScreenOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Parallel(p => p
                            .Branch<CheckCredit>()
                            .Branch<SneakyCheck>(),
                        merge: MergeStrategy.AllMustSucceed)
                    .Return(ctx => new OrderResult("id"));
            }
            """).ShouldBeEmpty(
            "Declared contracts differ, so nothing is reported. A capability writing " +
            "another slot through ctx.Set would race undetected — the documented gap.");
    }

    [Fact]
    public void SaysNothingAboutABranchWhoseBodyIsNotALambda()
    {
        Analyze(Preamble + """


            [Flow("order.screen")]
            public sealed partial class ScreenOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                private static void Body(IFlowBuilder<PlaceOrder, OrderResult> b) => b.Step<QuotePrimary>();

                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                    .Parallel(p => p.Branch(Body).Branch<QuoteSecondary>(),
                        merge: MergeStrategy.AllMustSucceed)
                    .Return(ctx => new OrderResult("id"));
            }
            """).ShouldBeEmpty("There is no body at the call site to read.");
    }

    // ---------------------------------------------------------------- harness

    private static string[] Analyze(string source, bool distinct = true)
    {
        var ids = Run(source).Select(static d => d.Id);

        return distinct
            ? [.. ids.Distinct(StringComparer.Ordinal).OrderBy(static id => id, StringComparer.Ordinal)]
            : [.. ids];
    }

    private static string[] Messages(string source) =>
        [.. Run(source).Select(static d => d.GetMessage(CultureInfo.InvariantCulture))];

    /// <summary>The source text each diagnostic underlines, which is what a squiggle lands on.</summary>
    private static string[] Locations(string source) =>
        [.. Run(source).Select(static d => d.Location.SourceTree!.GetText().ToString(d.Location.SourceSpan))];

    /// <summary>
    /// Runs the analyzer over a real compilation, refusing to proceed if the test's own
    /// source does not compile.
    /// </summary>
    /// <remarks>
    /// The pre-check is not decoration. This analyzer says nothing about a capability it
    /// cannot resolve, so a typo in a test string would make every silence assertion pass
    /// for the wrong reason — a suite that is green because the semantic model gave up.
    /// </remarks>
    private static ImmutableArray<Diagnostic> Run(string source)
    {
        var compilation = CSharpCompilation.Create(
            "FlowX.ParallelSlotTests",
            [CSharpSyntaxTree.ParseText(source, path: "/src/Flows/Sample.cs")],
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var errors = compilation.GetDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .Select(static d => $"{d.Id}: {d.GetMessage(CultureInfo.InvariantCulture)}")
            .ToArray();

        errors.ShouldBeEmpty(
            "The test's own source must compile, or the analyzer is being asked about " +
            "symbols that do not bind.");

        return compilation
            .WithAnalyzers([new ParallelSlotAnalyzer()])
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
