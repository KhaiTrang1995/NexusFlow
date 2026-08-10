using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// What one keystroke costs: the work the generator redoes after an edit that changes nothing
/// it depends on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The measurement this repository did not have.</strong>
/// <c>scripts/measure-generator-cost.py</c> prices a <em>cold</em> build — every flow compiled
/// once, from nothing. That is the number the +8 % budget is written against, and it is not the
/// number a developer lives with. Editing one file in an open editor re-runs the generator, and
/// <see cref="ErrorCatalogueIncrementalTests.TheCatalogueIsRederivedForEveryCapabilityOnEveryEditAnywhere"/>
/// records why that costs nearly as much as the cold build: the per-capability transform is
/// combined with the compilation before it runs, the compilation changes on every edit
/// anywhere, so every capability's error catalogue is derived again.
/// </para>
/// <para>
/// <strong>That test asserts the reason; this one asserts the price.</strong> A reason without
/// a price cannot tell anybody whether a fix worked, and the fix under discussion — narrowing
/// the catalogue reader to the capability's own file — is exactly the kind of change whose
/// whole justification is a number. An earlier attempt at the same problem, caching one
/// semantic model per syntax tree, measured at +0.16 % against its own control and was dropped;
/// without a measurement it would have shipped as an improvement.
/// </para>
/// <para>
/// <strong>Bytes allocated, not milliseconds.</strong>
/// <c>docs/benchmarks/generator-cost-gate.md</c> settles this for the cold measurement with
/// twelve runs of one tree: wall clock moved by 139 % and allocation by 0.069 %. The same
/// instrument choice applies here for the same reason, and it is what makes an assertion
/// possible at all on a shared runner.
/// </para>
/// </remarks>
public sealed class IncrementalLoopCostTests(ITestOutputHelper output)
{
    /// <summary>Capabilities in the synthetic solution, matching the cold harness's smaller size.</summary>
    private const int Capabilities = 25;

    private const string UnrelatedPath = "/src/Unrelated.cs";

    /// <summary>
    /// An edit to a file nothing reads still costs most of a cold generation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Asserted as a ratio, because that is the property a fix moves.</strong> The
    /// absolute figures travel badly between machines; the share of the cold cost that an
    /// unrelated keystroke repeats is a property of the pipeline's shape, and stays put. Today
    /// it is close to 1 — an unrelated edit does almost all the work again — which is the
    /// finding rather than the failure.
    /// </para>
    /// <para>
    /// <strong>The ceiling is 1.05 and not 1.0.</strong> A second run can legitimately cost
    /// slightly more than the first: the driver carries its previous state tables and compares
    /// against them. Anything past that is a regression in the pipeline's shape and not noise.
    /// The floor is 0, and the point of the fix is to move the number down — when it drops,
    /// tighten this and record the new figure in the remarks so the next reader can see the
    /// direction of travel.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnUnrelatedEditRepeatsMostOfTheColdGeneration()
    {
        var first = GeneratorHarness.CompilationOf(
            [.. Files(), (UnrelatedPath, Unrelated(1))]);

        var edited = GeneratorHarness.WithFileReplaced(first, UnrelatedPath, Unrelated(2));

        // One full run outside the measurement, so what is priced below is steady-state work
        // rather than the JIT and the first-touch of every Roslyn cache in the process.
        _ = GeneratorHarness.TrackingDriver()
            .RunGenerators(first, TestContext.Current.CancellationToken)
            .RunGenerators(edited, TestContext.Current.CancellationToken);

        var driver = GeneratorHarness.TrackingDriver();

        var cold = Allocated(() => driver = driver.RunGenerators(first, TestContext.Current.CancellationToken));
        var keystroke = Allocated(() => driver = driver.RunGenerators(edited, TestContext.Current.CancellationToken));

        var repeated = (double)keystroke / cold;

        output.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{Capabilities} capabilities: cold {cold:N0} B, unrelated edit {keystroke:N0} B, "
                + $"repeated {repeated:P1}"));

        cold.ShouldBeGreaterThan(
            0,
            "The cold run allocated nothing, so this measured a generator that did not run.");

        repeated.ShouldBeLessThan(
            1.05,
            "An unrelated edit now costs more than a cold generation. Whatever the pipeline "
            + "does on a second run, it must not be more than doing it all again.");
    }

    /// <summary>Bytes this thread allocates while the action runs.</summary>
    /// <remarks>
    /// Per thread rather than per process, so a background finaliser or another test's work
    /// cannot land inside the window. Precise: the runtime counts every allocation on this
    /// thread rather than sampling.
    /// </remarks>
    private static long Allocated(Action action)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();

        action();

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static string Unrelated(int value) =>
        $"namespace Sample; internal static class Unrelated {{ public const int Value = {value}; }}";

    /// <summary>
    /// One file per capability, which is the layout the cold harness generates and the one that
    /// makes the per-file question meaningful.
    /// </summary>
    private static List<(string Path, string Source)> Files()
    {
        var files = new List<(string, string)>(Capabilities + 1)
        {
            ("/src/Errors.cs", """
                using FlowX;

                namespace Sample;

                public static class OrderErrors
                {
                    public static Error OutOfStock(string sku) =>
                        new Error("inventory.out_of_stock", $"'{sku}' is out of stock.", ErrorCategory.Conflict);
                }
                """),
        };

        for (var i = 0; i < Capabilities; i++)
        {
            files.Add(($"/src/Capability{i}.cs", Capability(i)));
        }

        return files;
    }

    /// <summary>
    /// A capability whose failure path reaches a factory in <em>another</em> file.
    /// </summary>
    /// <remarks>
    /// The shape the reader pays for. A capability whose errors are all local would be cheap
    /// however the pipeline is arranged, and would measure a case the fix under discussion does
    /// not change.
    /// </remarks>
    private static string Capability(int index)
    {
        var builder = new StringBuilder();

        builder.AppendLine("using System.Threading;");
        builder.AppendLine("using System.Threading.Tasks;");
        builder.AppendLine("using FlowX;");
        builder.AppendLine();
        builder.AppendLine("namespace Sample;");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"public sealed record Request{index}(string Sku, int Quantity);");
        builder.AppendLine(CultureInfo.InvariantCulture, $"public sealed record Reply{index}(string Id);");
        builder.AppendLine();
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"""[Capability("inventory.reserve{index}", Version = "1.0.0", Authorization = Authorization.Authenticated, Idempotent = true)]""");
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"public sealed class Reserve{index} : ICapability<Request{index}, Reply{index}>");
        builder.AppendLine("{");
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"    public ValueTask<Result<Reply{index}>> ExecuteAsync(Request{index} input, CapabilityContext ctx, CancellationToken ct)");
        builder.AppendLine("    {");
        builder.AppendLine("        if (input.Quantity <= 0)");
        builder.AppendLine("        {");
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"            return ValueTask.FromResult(Result.Fail<Reply{index}>(OrderErrors.OutOfStock(input.Sku)));");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"        return ValueTask.FromResult(Result.Ok(new Reply{index}(input.Sku)));");
        builder.AppendLine("    }");
        builder.AppendLine("}");

        return builder.ToString();
    }
}
