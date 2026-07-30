using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using FlowX.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FlowX.Benchmarks;

/// <summary>
/// Budget B12: what the compile-time machinery costs a build.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0002 bought quality goals Q1, Q3, Q7 and constraint C2 by moving orchestration to
/// compile time, and accepted generator complexity as permanent legacy risk (R1). Its
/// <em>revisit-when</em> clause names a number: <strong>build overhead above 8 %
/// sustained</strong>. Until this existed the number was never measured, so the clause
/// could never have fired.
/// </para>
/// <para>
/// Measured against Roslyn directly rather than by timing <c>dotnet build</c>. An
/// MSBuild wall-clock comparison is dominated by restore, dependency resolution and file
/// IO — none of which the generator touches — and the alternative of maintaining a
/// parallel non-FlowX copy of the sample would be a second thing to keep in step. Here
/// the control is the same compilation without the generator driver, which is exactly
/// the "identical non-FlowX code" the budget names.
/// </para>
/// <para>
/// The compilation is representative rather than minimal: three capabilities, a flow
/// with a compensation, an emit and a return. A one-step flow would measure the driver's
/// fixed cost and report an overhead nobody will experience.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[HideColumns("Job", "Error", "StdDev", "Median", "RatioSD")]
public class CompilerBenchmarks
{
    private ImmutableArray<MetadataReference> _references;
    private SyntaxTree _tree = null!;

    [GlobalSetup]
    public void Setup()
    {
        _references = ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(static path => path.Length > 0)
            .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path)))
            .Concat(
            [
                MetadataReference.CreateFromFile(typeof(Flow<,>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(FlowX.Runtime.FlowEngine).Assembly.Location),
            ])
            .ToImmutableArray();

        _tree = CSharpSyntaxTree.ParseText(Source, path: "/src/Flows/Sample.cs");
    }

    /// <summary>
    /// A fresh compilation for every iteration.
    /// </summary>
    /// <remarks>
    /// Load-bearing, and the first version of this benchmark got it wrong. Roslyn caches
    /// diagnostics on a compilation instance, so reusing one made the control measure a
    /// cache hit while the generator path — which produces a <em>new</em> compilation —
    /// measured a full bind. The reported overhead was 175×, and it was an artefact of
    /// the harness rather than a fact about the generator. Both arms now pay the same
    /// binding cost.
    /// </remarks>
    private CSharpCompilation NewCompilation() => CSharpCompilation.Create(
        "FlowX.BuildOverhead",
        [_tree],
        _references,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    /// <summary>The control: the same source compiled with nothing of ours attached.</summary>
    [Benchmark(Baseline = true, Description = "Compile without the generator")]
    public int WithoutGenerator() => NewCompilation().GetDiagnostics().Length;

    /// <summary>
    /// Running the generator, and nothing else. <strong>This is the B12 number.</strong>
    /// </summary>
    /// <remarks>
    /// The cost the generator adds that a hand-written equivalent would not pay. It does
    /// not bind the generated output, deliberately — see <see cref="WithGenerator"/>.
    /// </remarks>
    [Benchmark(Description = "Run the generator only")]
    public int GeneratorOnly()
    {
        CSharpGeneratorDriver
            .Create(new FlowPlanGenerator())
            .RunGeneratorsAndUpdateCompilation(NewCompilation(), out var updated, out _);

        return updated.SyntaxTrees.Count();
    }

    /// <summary>
    /// The whole compile-time story: run the generator, then bind what it produced.
    /// </summary>
    /// <remarks>
    /// <strong>Not the budget, and reporting it as one would overstate the cost.</strong>
    /// Most of the difference from the control is binding the emitted plan and dispatcher
    /// — code the control does not contain at all, and which an application written
    /// without FlowX would have hand-written and paid to bind anyway. Measured because
    /// the total is what a developer waits for; attributed carefully because only part
    /// of it is ours.
    /// </remarks>
    [Benchmark(Description = "Compile with the generator, output bound")]
    public int WithGenerator()
    {
        CSharpGeneratorDriver
            .Create(new FlowPlanGenerator())
            .RunGeneratorsAndUpdateCompilation(NewCompilation(), out var updated, out _);

        return updated.GetDiagnostics().Length;
    }

    private const string Source = """
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record PlaceOrder(string Sku, int Quantity);
        public sealed record ValidatedOrder(string Sku, int Quantity);
        public sealed record Reservation(string Sku, string Id);
        public sealed record Payment(string ReceiptId);
        public sealed record OrderPlaced(string Id);
        public sealed record OrderResult(string Id);

        [Capability("order.validate", Version = "1.0.0", Authorization = Authorization.Authenticated, Idempotent = true)]
        public sealed class ValidateOrder : ICapability<PlaceOrder, ValidatedOrder>
        {
            public ValueTask<Result<ValidatedOrder>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new ValidatedOrder(input.Sku, input.Quantity)));
        }

        [Capability("inventory.reserve", Version = "1.0.0", Authorization = Authorization.Authenticated,
            Idempotent = true, SideEffects = new[] { "inventory-ledger" })]
        public sealed class ReserveInventory : ICapability<ValidatedOrder, Reservation>
        {
            public ValueTask<Result<Reservation>> ExecuteAsync(ValidatedOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Reservation(input.Sku, ctx.IdempotencyKey)));
        }

        [Capability("inventory.release", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
        public sealed class ReleaseInventory : ICapability<ValidatedOrder, Reservation>
        {
            public ValueTask<Result<Reservation>> ExecuteAsync(ValidatedOrder input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Reservation(input.Sku, ctx.IdempotencyKey)));
        }

        [Capability("payment.capture", Version = "2.1.0", Authorization = Authorization.Permission,
            Permission = "payment.write", SideEffects = new[] { "payment-gateway" })]
        public sealed class CapturePayment : ICapability<Reservation, Payment>
        {
            public ValueTask<Result<Payment>> ExecuteAsync(Reservation input, CapabilityContext ctx, CancellationToken ct)
                => ValueTask.FromResult(Result.Ok(new Payment(input.Id)));
        }

        [Flow("order.place", Version = "1.0.0", Profile = ExecutionProfile.Ephemeral)]
        [FlowDeadline("PT30S")]
        public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
        {
            protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                .Step<ValidateOrder>()
                .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()
                .Step<CapturePayment>()
                .Emit<OrderPlaced>(ctx => new OrderPlaced(ctx.Get<Reservation>().Id))
                .Return(ctx => new OrderResult(ctx.Get<Payment>().ReceiptId));
        }
        """;
}
