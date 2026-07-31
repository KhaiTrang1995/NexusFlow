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
/// FLOWX1007, FLOWX1008 and FLOWX1009 — the determinism rules for the code a flow runs.
/// </summary>
/// <remarks>
/// <para>
/// Both directions for every rule, and the silent direction carries the weight. These
/// three read arbitrary member bodies rather than a chain of known builder calls, so a
/// false positive lands on ordinary C# — a date comparison, an injected dependency, a
/// capability doing the I/O it exists to do — and a build gate that fires on those is a
/// gate suppressed at the top of the file, after which it protects nothing.
/// </para>
/// <para>
/// The severity cases are here in as much detail as the detection cases. "Warning by
/// default, error where the compilation proves a durable flow reaches this code" is the
/// decision this package exists to make, and a later change that flattened it to one
/// severity everywhere would otherwise pass every test in this file.
/// </para>
/// <para>
/// The last section is about the seam with FLOWX1011. The two rules partition the flow
/// class between them — the builder lambdas belong to FLOWX1011, everything else to these
/// — and the failure mode is silent in both directions: an overlap reports one mistake
/// twice under two ids, and a gap lets a clock through a hole neither rule admits to.
/// </para>
/// </remarks>
public sealed class DeterminismAnalyzerTests
{
    private const string Preamble = """
        using System;
        using System.Collections.Generic;
        using System.Diagnostics;
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record PlaceOrder(string Sku, int Quantity);
        public sealed record ValidatedOrder(string Sku, int Quantity, DateTimeOffset Placed, DateTimeOffset Due);
        public sealed record OrderPlacedResult(string ReservationId);

        public interface IInventoryStore { ValueTask<int> AvailableAsync(string sku, CancellationToken ct); }
        """;

    private static string With(string body) => Preamble + "\n\n" + body;

    /// <summary>A capability whose only variable is its body.</summary>
    private static string Capability(string body) => With(
        $$"""
        [Capability("order.validate", Version = "1.0.0", Authorization = Authorization.Internal)]
        public sealed class ValidateOrder : ICapability<PlaceOrder, ValidatedOrder>
        {
            public ValueTask<Result<ValidatedOrder>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
            {
                {{body}}
            }
        }
        """);

    /// <summary>A capability whose only variable is what it declares alongside its body.</summary>
    private static string CapabilityDeclaring(string members) => With(
        $$"""
        [Capability("order.validate", Version = "1.0.0", Authorization = Authorization.Internal)]
        public sealed class ValidateOrder : ICapability<PlaceOrder, ValidatedOrder>
        {
            {{members}}

            public ValueTask<Result<ValidatedOrder>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new ValidatedOrder(input.Sku, input.Quantity, ctx.UtcNow, ctx.UtcNow)));
        }
        """);

    /// <summary>A clean capability, plus a flow at the profile under test that steps through it.</summary>
    private static string CapabilityUnder(string profile, string members) => With(
        $$"""
        [Capability("order.validate", Version = "1.0.0", Authorization = Authorization.Internal)]
        public sealed class ValidateOrder : ICapability<PlaceOrder, ValidatedOrder>
        {
            {{members}}

            public ValueTask<Result<ValidatedOrder>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new ValidatedOrder(input.Sku, input.Quantity, ctx.UtcNow, ctx.UtcNow)));
        }

        [Flow("order.place", Profile = ExecutionProfile.{{profile}})]
        public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
        {
            protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                flow
                    .Step<ValidateOrder>()
                    .Return(ctx => new OrderPlacedResult("r"));
        }
        """);

    // ------------------------------------------------------------------ silent

    [Fact]
    public void TheReferenceSampleShapeIsClean()
    {
        // The four capabilities samples/ecommerce actually ships, reduced to the shapes
        // these rules look at: a const, a readonly injected port, and every ambient value
        // taken from the context. Pinned here because the sample is the only application
        // in the repository, and a rule that fires on it is a rule nobody could adopt.
        Analyze(With("""
            [Capability("order.validate", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class ValidateOrder : ICapability<PlaceOrder, ValidatedOrder>
            {
                private const decimal UnitPrice = 19.99m;

                public ValueTask<Result<ValidatedOrder>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new ValidatedOrder(input.Sku, input.Quantity, ctx.UtcNow, ctx.UtcNow)));
            }

            [Capability("inventory.reserve", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class ReserveInventory : ICapability<ValidatedOrder, OrderPlacedResult>
            {
                private readonly IInventoryStore _store;

                public ReserveInventory(IInventoryStore store) => _store = store;

                public async ValueTask<Result<OrderPlacedResult>> ExecuteAsync(ValidatedOrder input, CapabilityContext ctx, CancellationToken ct)
                {
                    var available = await _store.AvailableAsync(input.Sku, ct).ConfigureAwait(false);

                    return available < input.Quantity
                        ? Result.Fail<OrderPlacedResult>(new Error("inventory.out_of_stock", "no", ErrorCategory.Conflict))
                        : Result.Ok(new OrderPlacedResult(ctx.IdempotencyKey));
                }
            }

            [Flow("order.place", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral, Owner = "orders")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow)
                {
                    ArgumentNullException.ThrowIfNull(flow);

                    flow
                        .Step<ValidateOrder>()
                        .Step<ReserveInventory>()
                        .Return(ctx => new OrderPlacedResult(ctx.Get<OrderPlacedResult>().ReservationId));
                }
            }
            """)).ShouldBeEmpty();
    }

    [Fact]
    public void TheContextClockIsReadable() =>
        // The documented remedy for FLOWX1007. A rule that fires on its own fix is a rule
        // people turn off — ctx.UtcNow is journaled on first read, which is the whole
        // point of the seam (06 §5).
        Analyze(Capability("""
            var due = ctx.UtcNow.AddDays(1);
            return ValueTask.FromResult(Result.Ok(new ValidatedOrder(input.Sku, input.Quantity, ctx.UtcNow, due)));
            """)).ShouldBeEmpty();

    [Fact]
    public void ContextIdentityAndRandomnessAreReadable() =>
        // The remedy for FLOWX1008: ctx.NewId() and ctx.Random's seed are what the journal
        // captures, and ctx.IdempotencyKey is stable across a retry as well as a replay.
        Analyze(Capability("""
            var id = ctx.NewId();
            var jitter = ctx.Random.Next(10);
            var key = ctx.IdempotencyKey;
            return ValueTask.FromResult(Result.Ok(new ValidatedOrder(key + id + jitter, input.Quantity, ctx.UtcNow, ctx.UtcNow)));
            """)).ShouldBeEmpty();

    [Fact]
    public void ADateComparisonIsReadable() =>
        // DateTime is why the catalogue lists members rather than the whole type: comparing
        // two dates that arrived in the input is the most ordinary thing a capability does.
        Analyze(Capability("""
            var late = input.Quantity > 0 && DateTimeOffset.MinValue < DateTimeOffset.MaxValue;
            return ValueTask.FromResult(Result.Ok(new ValidatedOrder(input.Sku, late ? 1 : 0, ctx.UtcNow, ctx.UtcNow)));
            """)).ShouldBeEmpty();

    [Fact]
    public void AnInjectedDependencyIsNotMutableState() =>
        // A capability that could not hold a port would be a capability that cannot do
        // anything. Readonly is the whole distinction FLOWX1009 draws.
        Analyze(CapabilityDeclaring("private readonly IInventoryStore _store = null!;")).ShouldBeEmpty();

    [Fact]
    public void AConstantIsNotMutableState() =>
        Analyze(CapabilityDeclaring("private const decimal UnitPrice = 19.99m;")).ShouldBeEmpty();

    [Fact]
    public void AStaticReadOnlyFieldIsNotMutableState() =>
        // Permitted, and only shallowly sound — a static readonly List<T> would slip
        // through. Documented as a limit rather than presented as a check.
        Analyze(CapabilityDeclaring("private static readonly string[] Skus = [];")).ShouldBeEmpty();

    [Fact]
    public void AnInitOnlyPropertyIsNotMutableState() =>
        // An init accessor can only run while the object is being constructed, which is
        // exactly the property this rule asks about.
        Analyze(CapabilityDeclaring("public string Region { get; init; } = \"eu\";")).ShouldBeEmpty();

    [Fact]
    public void AGetOnlyPropertyIsNotMutableState() =>
        Analyze(CapabilityDeclaring("public string Region => \"eu\";")).ShouldBeEmpty();

    [Fact]
    public void AnOrdinaryTypeIsNotChecked() =>
        // The rules are about capabilities and flows. An adapter behind a port may hold a
        // cache and read a clock; that is what makes it the place those things belong.
        Analyze(With("""
            public sealed class InMemoryInventoryStore : IInventoryStore
            {
                private int _reserved;
                private static DateTime _lastSweep = DateTime.UtcNow;
                public string Region { get; set; } = "eu";

                public ValueTask<int> AvailableAsync(string sku, CancellationToken ct)
                {
                    _reserved++;
                    _lastSweep = DateTime.UtcNow;
                    return ValueTask.FromResult(Guid.NewGuid().GetHashCode());
                }
            }
            """)).ShouldBeEmpty();

    [Fact]
    public void InputOutputInACapabilityIsNotReported() =>
        // 06 §5 puts capability bodies in the non-deterministic zone on purpose: their
        // results are journaled. These rules are about the three values the journal
        // reproduces, not about a capability doing the work it exists to do.
        Analyze(Capability("""
            Console.WriteLine(input.Sku);
            var host = Environment.MachineName;
            return ValueTask.FromResult(Result.Ok(new ValidatedOrder(host, input.Quantity, ctx.UtcNow, ctx.UtcNow)));
            """)).ShouldBeEmpty();

    [Fact]
    public void NameOfReadsTheNameAndNotTheValue() =>
        Analyze(Capability("""
            var name = nameof(DateTime.UtcNow) + nameof(Guid.NewGuid);
            return ValueTask.FromResult(Result.Ok(new ValidatedOrder(name, input.Quantity, ctx.UtcNow, ctx.UtcNow)));
            """)).ShouldBeEmpty();

    // ------------------------------------------------------------------ FLOWX1007

    [Fact]
    public void ReportsFLOWX1007ForDateTimeUtcNow() =>
        // The exact violation: a capability stamping its own output from the machine clock
        // instead of the one the journal captured.
        Analyze(Capability("""
            return ValueTask.FromResult(Result.Ok(new ValidatedOrder(input.Sku, input.Quantity, DateTime.UtcNow, ctx.UtcNow)));
            """)).ShouldBe(["FLOWX1007"]);

    [Fact]
    public void ReportsFLOWX1007ForDateTimeOffsetNow() =>
        Analyze(Capability("""
            return ValueTask.FromResult(Result.Ok(new ValidatedOrder(input.Sku, input.Quantity, DateTimeOffset.Now, ctx.UtcNow)));
            """)).ShouldBe(["FLOWX1007"]);

    [Fact]
    public void ReportsFLOWX1007ForDateTimeToday() =>
        Analyze(Capability("""
            var today = DateTime.Today;
            return ValueTask.FromResult(Result.Ok(new ValidatedOrder(input.Sku, today.Day, ctx.UtcNow, ctx.UtcNow)));
            """)).ShouldBe(["FLOWX1007"]);

    [Fact]
    public void ReportsFLOWX1007ForAStopwatch() =>
        // A clock reached through a type rather than a member, and through a constructor
        // rather than a static: both spellings, one rule.
        Analyze(Capability("""
            var elapsed = Stopwatch.GetTimestamp() + new Stopwatch().ElapsedTicks;
            return ValueTask.FromResult(Result.Ok(new ValidatedOrder(input.Sku, (int)elapsed, ctx.UtcNow, ctx.UtcNow)));
            """)).ShouldBe(["FLOWX1007"]);

    [Fact]
    public void ReportsFLOWX1007ForEnvironmentTickCount() =>
        // Environment as a whole is ambient process state and is FLOWX1011's business
        // inside a flow delegate; TickCount specifically is a clock, and the member entry
        // is what lets a rule about clocks tell the two apart.
        Analyze(Capability("""
            var ticks = Environment.TickCount64;
            return ValueTask.FromResult(Result.Ok(new ValidatedOrder(input.Sku, (int)ticks, ctx.UtcNow, ctx.UtcNow)));
            """)).ShouldBe(["FLOWX1007"]);

    // ------------------------------------------------------------------ FLOWX1008

    [Fact]
    public void ReportsFLOWX1008ForGuidNewGuid() =>
        // The exact violation, and the one with teeth: a capability minting its own
        // idempotency key means a retried attempt sends a different one, and the remote
        // system that deduplicates on it performs the effect twice.
        Analyze(Capability("""
            var key = Guid.NewGuid().ToString();
            return ValueTask.FromResult(Result.Ok(new ValidatedOrder(key, input.Quantity, ctx.UtcNow, ctx.UtcNow)));
            """)).ShouldBe(["FLOWX1008"]);

    [Fact]
    public void ReportsFLOWX1008ForRandomShared() =>
        Analyze(Capability("""
            var jitter = Random.Shared.Next(10);
            return ValueTask.FromResult(Result.Ok(new ValidatedOrder(input.Sku, jitter, ctx.UtcNow, ctx.UtcNow)));
            """)).ShouldBe(["FLOWX1008"]);

    [Fact]
    public void ReportsFLOWX1008ForANewRandom() =>
        // Constructed rather than reached statically. A rule that only looked at static
        // access would miss the shorter spelling of the same mistake.
        Analyze(Capability("""
            var jitter = new Random().Next(10);
            return ValueTask.FromResult(Result.Ok(new ValidatedOrder(input.Sku, jitter, ctx.UtcNow, ctx.UtcNow)));
            """)).ShouldBe(["FLOWX1008"]);

    // ------------------------------------------------------------------ FLOWX1009

    [Fact]
    public void ReportsFLOWX1009ForAMutableInstanceField() =>
        // The exact violation. A capability is resolved once and invoked concurrently, so
        // this counter is shared by every in-flight order.
        Analyze(CapabilityDeclaring("private int _reserved;")).ShouldBe(["FLOWX1009"]);

    [Fact]
    public void ReportsFLOWX1009ForAMutableStaticField() =>
        Analyze(CapabilityDeclaring("private static int _total;")).ShouldBe(["FLOWX1009"]);

    [Fact]
    public void ReportsFLOWX1009ForASettableProperty() =>
        Analyze(CapabilityDeclaring("public string Region { get; set; } = \"eu\";")).ShouldBe(["FLOWX1009"]);

    [Fact]
    public void ReportsFLOWX1009OncePerDeclaredVariable() =>
        // Two names on one field declaration are two pieces of state, and a reader fixing
        // one should still see the other.
        Report(CapabilityDeclaring("private int _reserved, _released;"))
            .Count(d => d.Id == "FLOWX1009")
            .ShouldBe(2);

    [Fact]
    public void ReportsFLOWX1009OnAFlowThatHoldsState() =>
        // 06 §5 words this row as mutable state reachable from a flow. The flow's own
        // declaration is the part of that claim this rule can prove.
        Analyze(With("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                private int _invocations;

                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow.Return(ctx => new OrderPlacedResult("r"));
            }
            """)).ShouldBe(["FLOWX1009"]);

    // ------------------------------------------------------- the seam with FLOWX1011

    [Fact]
    public void AClockInABuilderDelegateBelongsToFLOWX1011() =>
        // The overlap this analyzer is written to avoid. FLOWX1011 reports this line, with
        // a stronger rule and a message about conditions; a second id on the same span
        // would make a reader choose which diagnostic to believe.
        Analyze(With("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow.Return(ctx => new OrderPlacedResult(DateTime.UtcNow.ToString()));
            }
            """)).ShouldBeEmpty();

    [Fact]
    public void AClockInTheDefineBodyOutsideADelegateIsReported() =>
        // The gap on the other side of the same seam. FLOWX1011 reads the lambdas; nothing
        // read the statements around them, and a value computed here is captured into
        // whatever delegate follows.
        Analyze(With("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow)
                {
                    var stamp = DateTime.UtcNow;
                    flow.Return(ctx => new OrderPlacedResult("r"));
                }
            }
            """)).ShouldBe(["FLOWX1007"]);

    [Fact]
    public void AClockInAFlowHelperMethodIsReported() =>
        // FLOWX1011 is not interprocedural and says so. A helper on the flow class is
        // where that gap is widest, and it is inside this rule's subject.
        Analyze(With("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                private static bool IsOpen() => DateTime.UtcNow.Hour < 17;

                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow.Return(ctx => new OrderPlacedResult("r"));
            }
            """)).ShouldBe(["FLOWX1007"]);

    // ------------------------------------------------------------------ severity

    [Fact]
    public void IsAWarningWhenNoDurableFlowReachesTheCapability() =>
        // ADR-0003 says informational here. Info never appears in a build log, so it would
        // ship a rule that does nothing anywhere — the state these three were already in.
        // Under TreatWarningsAsErrors this still stops the build; a consumer who has
        // decided otherwise lowers it once, in .editorconfig.
        Severities(CapabilityUnder("Ephemeral", "private int _reserved;"))
            .ShouldBe([DiagnosticSeverity.Warning]);

    [Fact]
    public void IsAnErrorWhenADurableFlowStepsThroughTheCapability() =>
        // The escalation, and the whole reason severity is decided per report rather than
        // on the descriptor: this capability is on a replay path the compilation can see.
        Severities(CapabilityUnder("Durable", "private int _reserved;"))
            .ShouldBe([DiagnosticSeverity.Error]);

    [Fact]
    public void IsAWarningWhenNothingInTheCompilationNamesTheCapability() =>
        // A capability with no caller in this compilation could be stepped through by a
        // durable flow in another assembly. The rule reports it, and does not claim to
        // know: escalating on a guess would break a build for something unprovable.
        Severities(CapabilityDeclaring("private int _reserved;"))
            .ShouldBe([DiagnosticSeverity.Warning]);

    [Fact]
    public void IsAnErrorThroughASubFlowOfADurableParent() =>
        // A sub-flow runs inside its parent's instance, so a capability reached only
        // through an Ephemeral sub-flow of a Durable parent is on a replay path just the
        // same. Without the transitive step the escalation would be one composition away
        // from being avoidable.
        Severities(With("""
            [Capability("order.validate", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class ValidateOrder : ICapability<PlaceOrder, ValidatedOrder>
            {
                private int _reserved;

                public ValueTask<Result<ValidatedOrder>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(new ValidatedOrder(input.Sku, input.Quantity, ctx.UtcNow, ctx.UtcNow)));
            }

            [Flow("order.validate-flow", Profile = ExecutionProfile.Ephemeral)]
            public sealed partial class ValidateOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow
                        .Step<ValidateOrder>()
                        .Return(ctx => new OrderPlacedResult("r"));
            }

            [Flow("order.place", Profile = ExecutionProfile.Durable)]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow
                        .SubFlow<ValidateOrderFlow, PlaceOrder>(ctx => ctx.Input)
                        .Return(ctx => new OrderPlacedResult("r"));
            }
            """))
            .ShouldBe([DiagnosticSeverity.Error]);

    [Fact]
    public void ADurableFlowsOwnMembersAreAnError() =>
        Severities(With("""
            [Flow("order.place", Profile = ExecutionProfile.Durable)]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                private int _invocations;

                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow.Return(ctx => new OrderPlacedResult("r"));
            }
            """))
            .ShouldBe([DiagnosticSeverity.Error]);

    [Fact]
    public void AnOmittedProfileIsTreatedAsEphemeral() =>
        // FlowAttribute defaults to Ephemeral: durability is opted into, never out of.
        Severities(With("""
            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
            {
                private int _invocations;

                protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
                    flow.Return(ctx => new OrderPlacedResult("r"));
            }
            """))
            .ShouldBe([DiagnosticSeverity.Warning]);

    // ------------------------------------------------------- location and message

    [Fact]
    public void TheSquiggleLandsOnTheRead() =>
        // Not on the capability and not on the whole statement: a body with one bad term
        // among ten should squiggle the one.
        Locations(Capability("""
            return ValueTask.FromResult(Result.Ok(new ValidatedOrder(input.Sku, input.Quantity, DateTime.UtcNow, ctx.UtcNow)));
            """))
            .Single()
            .ShouldBe("DateTime.UtcNow");

    [Fact]
    public void TheSquiggleLandsOnTheFieldName() =>
        Locations(CapabilityDeclaring("private int _reserved;")).Single().ShouldBe("_reserved");

    [Fact]
    public void TheMessageNamesTheTypeAndTheRead() =>
        Messages(Capability("""
            return ValueTask.FromResult(Result.Ok(new ValidatedOrder(input.Sku, input.Quantity, DateTime.UtcNow, ctx.UtcNow)));
            """))
            .Single()
            .ShouldBe(
                "'ValidateOrder' reads 'DateTime.UtcNow', which is the ambient clock; a replay " +
                "reproduces only what the journal captured, and what it captures is ctx.UtcNow");

    [Fact]
    public void TheMessageNamesWhichKindOfStateItFound() =>
        Messages(CapabilityDeclaring("private static int _total;"))
            .Single()
            .ShouldBe("'ValidateOrder' holds mutable state: the static field '_total' can be assigned after construction");

    // ------------------------------------------------------------------ harness

    private static string[] Analyze(string source) =>
        [.. Report(source).Select(static d => d.Id).Distinct(StringComparer.Ordinal).OrderBy(static id => id, StringComparer.Ordinal)];

    private static string[] Messages(string source) =>
        [.. Report(source).Select(static d => d.GetMessage(CultureInfo.InvariantCulture))];

    /// <summary>The source text each diagnostic underlines, which is what a squiggle lands on.</summary>
    private static string[] Locations(string source) =>
        [.. Report(source).Select(static d => d.Location.SourceTree!.GetText().ToString(d.Location.SourceSpan))];

    private static DiagnosticSeverity[] Severities(string source) =>
        [.. Report(source).Select(static d => d.Severity)];

    /// <summary>
    /// Runs the analyzer over a real compilation, refusing to proceed if the test's own
    /// source does not compile.
    /// </summary>
    /// <remarks>
    /// The pre-check is not decoration. This analyzer says nothing about a symbol it
    /// cannot resolve, so a typo in a test string would make every silence assertion pass
    /// for the wrong reason — a suite that is green because the semantic model gave up.
    /// </remarks>
    private static ImmutableArray<Diagnostic> Report(string source)
    {
        var compilation = CSharpCompilation.Create(
            "FlowX.DeterminismTests",
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
            .WithAnalyzers([new DeterminismAnalyzer()])
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
