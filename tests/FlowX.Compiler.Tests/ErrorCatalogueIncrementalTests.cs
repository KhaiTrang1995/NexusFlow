using System;
using System.Linq;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// Whether editing an error factory updates the catalogue of a capability declared in a
/// different file.
/// </summary>
/// <remarks>
/// <para>
/// The catalogue rides a per-capability <c>ForAttributeWithMetadataName</c> pipeline whose
/// transform reaches out to the whole <c>Compilation</c> to follow a factory into another
/// file. A syntax provider caches its transform per syntax node, and the cache is not
/// invalidated by a change to some other tree — so the input the transform actually read
/// is wider than the input the driver believes it depends on. Everything in
/// <c>ManifestTriggerAndErrorTests</c> is a single-tree, single-run test, where that gap
/// cannot appear.
/// </para>
/// <para>
/// ADR-0014 §6 records this as an open question rather than a claimed defect, and says it
/// is "a correctness question as much as a performance one". These tests answer it. A
/// stale catalogue is not a slow build: it is a published contract that disagrees with the
/// code, which is the one failure mode the whole design of the reader exists to prevent.
/// </para>
/// </remarks>
public sealed class ErrorCatalogueIncrementalTests
{
    private const string ErrorsPath = "/src/Errors.cs";
    private const string CapabilityPath = "/src/Capabilities.cs";

    private static string ErrorsFile(string code) => $$"""
        using FlowX;

        namespace Sample;

        public static class OrderErrors
        {
            public static Error OutOfStock(string sku) =>
                new Error("{{code}}", $"'{sku}' is out of stock.", ErrorCategory.Conflict);
        }
        """;

    private const string CapabilityFile = """
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record PlaceOrder(string Sku, int Quantity);
        public sealed record OrderResult(string Id);

        [Capability("inventory.reserve", Version = "1.0.0", Authorization = Authorization.Authenticated, Idempotent = true)]
        public sealed class ReserveInventory : ICapability<PlaceOrder, OrderResult>
        {
            public ValueTask<Result<OrderResult>> ExecuteAsync(PlaceOrder input, CapabilityContext ctx, CancellationToken ct)
            {
                if (input.Quantity <= 0)
                {
                    return ValueTask.FromResult(Result.Fail<OrderResult>(OrderErrors.OutOfStock(input.Sku)));
                }

                return ValueTask.FromResult(Result.Ok(new OrderResult(input.Sku)));
            }
        }

        [Flow("order.place")]
        public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
        {
            protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) => flow
                .Step<ReserveInventory>()
                .Return(ctx => new OrderResult("id"));
        }
        """;

    /// <summary>The error codes the manifest publishes for the one capability.</summary>
    private static string[] CataloguedCodes(GeneratorRun run)
    {
        run.ManifestJson.ShouldNotBeNull(run.Describe());

        using var manifest = JsonDocument.Parse(run.ManifestJson!);

        var capability = manifest.RootElement.GetProperty("capabilities").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == "inventory.reserve");

        return capability.TryGetProperty("errors", out var errors)
            ? [.. errors.EnumerateArray().Select(e => e.GetProperty("code").GetString() ?? string.Empty)]
            : ["(withheld)"];
    }

    /// <summary>
    /// Editing the factory's code literal is reflected in the catalogue of a capability
    /// declared in another file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the test ADR-0014 §6 says does not exist. It asserts the observable fact —
    /// what the second run publishes — rather than which pipeline steps re-ran, because
    /// the manifest is what a consumer reads and a cache that re-runs more than it needs
    /// to is a performance question, not a correctness one.
    /// </para>
    /// <para>
    /// The two runs go through one driver. Calling <c>RunGenerators</c> twice on separate
    /// drivers would compare two cold runs and prove nothing about incrementality at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void EditingAnErrorFactoryInAnotherFileUpdatesTheCatalogue()
    {
        var first = GeneratorHarness.CompilationOf(
            (ErrorsPath, ErrorsFile("inventory.out_of_stock")),
            (CapabilityPath, CapabilityFile));

        var driver = GeneratorHarness.TrackingDriver().RunGenerators(first, TestContext.Current.CancellationToken);

        CataloguedCodes(GeneratorHarness.ResultOf(driver)).ShouldBe(["inventory.out_of_stock"]);

        var second = GeneratorHarness.WithFileReplaced(first, ErrorsPath, ErrorsFile("inventory.exhausted"));

        driver = driver.RunGenerators(second, TestContext.Current.CancellationToken);

        CataloguedCodes(GeneratorHarness.ResultOf(driver)).ShouldBe(
            ["inventory.exhausted"],
            "The capability's own file did not change, but what it returns did. A manifest " +
            "that still names the old code is a published contract that disagrees with the " +
            "source it claims to describe.");
    }

    /// <summary>
    /// Moving a failure path out of reach withholds the catalogue on the second run.
    /// </summary>
    /// <remarks>
    /// The complementary direction, and the more dangerous one. The first edit changes a
    /// code and a stale answer is visibly wrong; this edit changes whether the reader can
    /// answer at all, and a stale answer is a <em>complete-looking</em> catalogue for a
    /// capability the reader can no longer read. That is precisely the "short by one, and
    /// indistinguishable from right" state the reader's own remarks say it exists to avoid.
    /// </remarks>
    [Fact]
    public void MakingAnErrorFactoryUnreadableWithholdsTheCatalogueOnTheNextRun()
    {
        var first = GeneratorHarness.CompilationOf(
            (ErrorsPath, ErrorsFile("inventory.out_of_stock")),
            (CapabilityPath, CapabilityFile));

        var driver = GeneratorHarness.TrackingDriver().RunGenerators(first, TestContext.Current.CancellationToken);

        CataloguedCodes(GeneratorHarness.ResultOf(driver)).ShouldBe(["inventory.out_of_stock"]);

        const string Composed = """
            using FlowX;

            namespace Sample;

            public static class OrderErrors
            {
                public static Error OutOfStock(string sku) =>
                    new Error("inventory." + sku, $"'{sku}' is out of stock.", ErrorCategory.Conflict);
            }
            """;

        driver = driver.RunGenerators(
            GeneratorHarness.WithFileReplaced(first, ErrorsPath, Composed),
            TestContext.Current.CancellationToken);

        CataloguedCodes(GeneratorHarness.ResultOf(driver)).ShouldBe(
            ["(withheld)"],
            "The code is now composed at run time, so the catalogue is no longer knowable. " +
            "Publishing the previous run's answer would state a contract the code no longer has.");
    }

    /// <summary>
    /// Editing an unrelated file does not disturb the catalogue.
    /// </summary>
    /// <remarks>
    /// The control. Without it the two tests above would also pass on a pipeline that
    /// recomputes everything unconditionally for a reason unrelated to the edit — and
    /// would keep passing if incrementality were removed entirely.
    /// </remarks>
    [Fact]
    public void EditingAnUnrelatedFileLeavesTheCatalogueAlone()
    {
        const string UnrelatedPath = "/src/Unrelated.cs";

        var first = GeneratorHarness.CompilationOf(
            (ErrorsPath, ErrorsFile("inventory.out_of_stock")),
            (CapabilityPath, CapabilityFile),
            (UnrelatedPath, "namespace Sample; internal static class Unrelated { public const int Value = 1; }"));

        var driver = GeneratorHarness.TrackingDriver().RunGenerators(first, TestContext.Current.CancellationToken);

        CataloguedCodes(GeneratorHarness.ResultOf(driver)).ShouldBe(["inventory.out_of_stock"]);

        driver = driver.RunGenerators(
            GeneratorHarness.WithFileReplaced(
                first, UnrelatedPath, "namespace Sample; internal static class Unrelated { public const int Value = 2; }"),
            TestContext.Current.CancellationToken);

        CataloguedCodes(GeneratorHarness.ResultOf(driver)).ShouldBe(["inventory.out_of_stock"]);
    }


    /// <summary>
    /// The mechanism behind the three tests above: nothing about the catalogue is cached
    /// across an edit, anywhere in the compilation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tests above assert the outcome, which is what a consumer cares about. This one
    /// asserts why the outcome holds, which is what a reader of ADR-0014 §6 needs — because
    /// "correct" and "correct and incremental" are different answers with very different
    /// prices, and the difference is invisible in the manifest.
    /// </para>
    /// <para>
    /// <c>ForAttributeWithMetadataName</c> combines its syntactic node table with the
    /// <c>CompilationProvider</c> before invoking the user's transform. The compilation
    /// changes on every edit anywhere, so the combined input is always modified and the
    /// transform is re-invoked for <em>every</em> attributed node in the compilation — here
    /// producing <c>Unchanged</c>, which is Roslyn's word for "it ran, and the answer was
    /// the same", as distinct from <c>Cached</c>, which is "it did not run". Correctness is
    /// bought by recomputing everything, so the incremental inner loop pays the full
    /// derivation cost that ADR-0014 prices at ~3 ms per capability type on every keystroke.
    /// </para>
    /// <para>
    /// This asserts against Roslyn's own step names, which are an implementation detail it
    /// does not version. If a future Roslyn renames them, update the name; if a future
    /// Roslyn reports <c>Cached</c> here, do not update the assertion — that is the
    /// staleness bug, and <see cref="EditingAnErrorFactoryInAnotherFileUpdatesTheCatalogue"/>
    /// will have failed alongside it.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheCatalogueIsRederivedForEveryCapabilityOnEveryEditAnywhere()
    {
        const string UnrelatedPath = "/src/Unrelated.cs";
        const string TransformStep = "result_ForAttributeWithMetadataName";

        var first = GeneratorHarness.CompilationOf(
            (ErrorsPath, ErrorsFile("inventory.out_of_stock")),
            (CapabilityPath, CapabilityFile),
            (UnrelatedPath, "namespace Sample; internal static class Unrelated { public const int Value = 1; }"));

        var driver = GeneratorHarness.TrackingDriver()
            .RunGenerators(first, TestContext.Current.CancellationToken)
            .RunGenerators(
                GeneratorHarness.WithFileReplaced(
                    first, UnrelatedPath, "namespace Sample; internal static class Unrelated { public const int Value = 2; }"),
                TestContext.Current.CancellationToken);

        var steps = driver.GetRunResult().Results.Single().TrackedSteps;

        steps.Keys.ShouldContain(TransformStep, "Roslyn renamed its attribute-provider step.");

        steps[TransformStep]
            .SelectMany(static run => run.Outputs)
            .Select(static output => output.Reason)
            .ShouldNotContain(
                IncrementalStepRunReason.Cached,
                "A cached transform is a catalogue derived from a compilation that no longer " +
                "exists. Correctness here rests on the transform re-running unconditionally, " +
                "and the price of that is the whole derivation on every edit.");
    }
}
