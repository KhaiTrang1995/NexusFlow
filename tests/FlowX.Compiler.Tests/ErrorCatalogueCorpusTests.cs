using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using FlowX.Compiler.Analysis;
using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// How often <c>ErrorCatalogueReader</c> can actually read a capability, measured against
/// a corpus written on purpose to be argued with.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0014 §6 calls the real withheld rate "the single measurement most likely to change
/// the decision", and records that the only non-synthetic data point is
/// <c>samples/ecommerce</c>: four capabilities, four complete catalogues, written by the
/// reader's own authors against the exact convention it reads. That is a tautology rather
/// than evidence.
/// </para>
/// <para>
/// <strong>This is not evidence about real code either, and it must not be read as
/// though it were.</strong> There is no FlowX code in the world to sample; the corpus was
/// written by the same hand that reports the number, so the number is a property of the
/// file list. What is transferable is the per-pattern table — whether a given C# shape
/// resolves is a fact about the reader, not about the sample — and that table is what
/// <c>docs/benchmarks/B13-error-catalogue-resolution.md</c> argues from. The aggregate is
/// reported because refusing to report it would be worse, and it is labelled everywhere it
/// appears.
/// </para>
/// <para>
/// Every specimen carries its own ground truth in a <c>[Specimen]</c> attribute, so the
/// classification below is computed rather than asserted case by case, and a specimen
/// whose behaviour changes fails here rather than quietly moving a number in a document.
/// </para>
/// </remarks>
public sealed class ErrorCatalogueCorpusTests
{
    /// <summary>Where a specimen ended up.</summary>
    internal enum Outcome
    {
        /// <summary>Published, and it matches what the capability can return.</summary>
        Resolved,

        /// <summary>Published, empty, and the capability really cannot fail.</summary>
        ResolvedEmpty,

        /// <summary>Not published, and the capability can fail.</summary>
        Withheld,

        /// <summary>Published, and wrong.</summary>
        FalseComplete,
    }

    /// <summary>One specimen's declared intent and measured result.</summary>
    internal sealed record Row(
        string Specimen,
        string Group,
        string Pattern,
        Outcome Expected,
        Outcome Actual,
        string[] Truth,
        string[] Published,
        string Why);

    private static readonly Lazy<ImmutableArray<Row>> Measured = new(Measure);

    // The corpus lives beside the test on disk and is copied next to the assembly by the
    // project file. Reading it as text rather than compiling it in is the whole point: a
    // third of it does not resolve, and one file is meant to be a different assembly.
    private static DirectoryInfo CorpusDirectory
    {
        get
        {
            var directory = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "Corpus"));

            directory.Exists.ShouldBeTrue($"No corpus at {directory.FullName}.");

            return directory;
        }
    }

    /// <summary>Every specimen behaves the way the corpus says it does.</summary>
    /// <remarks>
    /// One assertion over the whole corpus rather than one test per specimen: the useful
    /// failure message is the list of everything that moved, and thirty near-identical
    /// facts read better as a table than as thirty test names.
    /// </remarks>
    [Fact]
    public void EverySpecimenLandsWhereTheCorpusSaysItDoes()
    {
        var mismatched = Measured.Value
            .Where(static row => row.Expected != row.Actual)
            .Select(static row =>
                $"{row.Specimen}: expected {row.Expected}, got {row.Actual} " +
                $"(truth [{string.Join(", ", row.Truth)}], published [{string.Join(", ", row.Published)}])")
            .ToArray();

        mismatched.ShouldBeEmpty(
            "A specimen changed behaviour. Either the reader moved, or the corpus's claim " +
            "about that C# shape was wrong — both are worth stopping for, and both invalidate " +
            "the table in docs/benchmarks/B13-error-catalogue-resolution.md.");
    }

    /// <summary>The headline counts the report is built on.</summary>
    /// <remarks>
    /// <para>
    /// Written out so the document and the measurement cannot drift apart. Adding a
    /// specimen is meant to fail this test: the numbers in
    /// <c>docs/benchmarks/B13-error-catalogue-resolution.md</c> then have to be restated in
    /// the same commit, which is the reviewable-diff contract
    /// <c>docs/benchmarks/generator-cost-baseline.json</c> already uses.
    /// </para>
    /// <para>
    /// These are counts over a hand-picked file list. They are not a rate observed on real
    /// code, and B13 §2 says at length why no honest one can be taken today.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheCorpusCountsAreTheOnesTheReportQuotes()
    {
        var counts = Measured.Value
            .GroupBy(static row => row.Actual)
            .ToDictionary(static group => group.Key, static group => group.Count());

        int Count(Outcome outcome) => counts.TryGetValue(outcome, out var value) ? value : 0;

        Measured.Value.Length.ShouldBe(38);
        Count(Outcome.Resolved).ShouldBe(18);
        Count(Outcome.ResolvedEmpty).ShouldBe(1);
        Count(Outcome.Withheld).ShouldBe(15);
        Count(Outcome.FalseComplete).ShouldBe(4);
    }

    /// <summary>
    /// The reader can publish a catalogue that is complete and wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted on its own because it contradicts a premise the rest of the design rests
    /// on. <c>CapabilityErrorCatalogue</c>'s remarks say a partial catalogue "is not
    /// published at all, and the manifest's <c>errors</c> array is absent rather than
    /// short"; ADR-0014 §8 lists "a field that cannot be wrong: correct, or explicitly
    /// absent" as the decision's first positive consequence, and §3 C rejects a declared
    /// list precisely because "a declared list can be wrong, and a derived one cannot".
    /// </para>
    /// <para>
    /// A derived one can. The reader identifies failure paths by finding expressions of
    /// type <c>Error</c>, so a failure that never takes that shape in the capability's own
    /// source is not an unreadable path — it is no path at all. The scan finds nothing,
    /// has nothing to refuse, and publishes <c>errors: []</c>, which the schema defines as
    /// the positive statement "analysed, returns no declared error".
    /// </para>
    /// <para>
    /// This is reported, not fixed: it is a defect in <c>src/</c>, and which way to fix it
    /// is a design decision for the owner. See B13 §5.
    /// </para>
    /// </remarks>
    [Fact]
    public void AFailureThatNeverTakesTheShapeOfAnErrorIsPublishedAsNoFailureAtAll()
    {
        var wrong = Measured.Value
            .Where(static row => row.Actual == Outcome.FalseComplete)
            .Where(static row => row.Published.Length == 0)
            .Select(static row => row.Specimen)
            .ToArray();

        wrong.ShouldBe(
            ["DelegatingCapability", "ResultFailFromParts", "ResultReturningHelper"],
            ignoreOrder: true);
    }

    /// <summary>
    /// The reader also over-reports: it publishes errors a capability cannot return.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mirror of the case above, and it arrives from the opposite direction.
    /// <c>Roots</c> walks the capability's whole class declaration and asks each node its
    /// type; it never asks whether anything reaches that node. So an <c>Error</c>
    /// constructed in a member nothing calls — a hook the base declares and this capability
    /// overrides but never invokes, a helper left behind by a refactor, a branch guarded by
    /// a feature flag that is off — is published as a failure the capability can return.
    /// </para>
    /// <para>
    /// It is the less alarming of the two directions: a consumer that handles an error
    /// which never arrives has wasted effort, where one that fails to handle an error which
    /// does arrive has a bug. But it is the same premise breaking. Both are reported to the
    /// owner rather than fixed; see B13 §5.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnErrorConstructedInUnreachableCodeIsPublishedAsOneTheCapabilityCanReturn()
    {
        var over = Measured.Value.Single(static row => row.Specimen == "InheritedHelper");

        over.Truth.ShouldBe(["order.forbidden/Forbidden"]);
        over.Published.ShouldBe(["order.forbidden/Forbidden", "order.invalid/Validation"]);
    }

    /// <summary>The corpus compiles, or none of the rest means anything.</summary>
    /// <remarks>
    /// A specimen with a binding error would make its Error-typed expressions unresolvable
    /// for a reason that has nothing to do with the reader, and would be counted as a
    /// withheld case. That is the one way this measurement could flatter its own thesis by
    /// accident, so it is checked first.
    /// </remarks>
    [Fact]
    public void TheCorpusCompilesCleanly()
    {
        var compilation = Corpus();

        compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .Select(static d => $"{d.Location.GetLineSpan()}: {d.Id} {d.GetMessage(CultureInfo.InvariantCulture)}")
            .ShouldBeEmpty();
    }

    /// <summary>Every corpus row, for the report generator and for other tests.</summary>
    internal static ImmutableArray<Row> Rows => Measured.Value;

    /// <summary>Builds the corpus compilation, with <c>Shared.cs</c> as a real reference.</summary>
    private static Microsoft.CodeAnalysis.CSharp.CSharpCompilation Corpus()
    {
        var files = CorpusDirectory
            .GetFiles("*.cs")
            .OrderBy(static file => file.Name, StringComparer.Ordinal)
            .ToArray();

        var shared = files.Single(static file => file.Name == "Shared.cs");

        var sharedCompilation = GeneratorHarness.CompilationOf(
            "Corpus.Shared",
            [],
            (shared.Name, File.ReadAllText(shared.FullName)));

        using var image = new MemoryStream();
        var emitted = sharedCompilation.Emit(image);

        emitted.Success.ShouldBeTrue(
            string.Join("\n", emitted.Diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error)));

        return GeneratorHarness.CompilationOf(
            "Corpus",
            [MetadataReference.CreateFromImage(image.ToArray())],
            [.. files
                .Where(static file => file.Name != "Shared.cs")
                .Select(static file => (file.Name, File.ReadAllText(file.FullName)))]);
    }

    private static ImmutableArray<Row> Measure()
    {
        var compilation = Corpus();
        var specimenAttribute = compilation.GetTypeByMetadataName("Corpus.SpecimenAttribute");

        specimenAttribute.ShouldNotBeNull("The corpus lost its ground-truth marker.");

        var rows = new List<Row>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);

            var declarations = tree.GetRoot()
                .DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>();

            foreach (var declaration in declarations)
            {
                if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol type)
                {
                    continue;
                }

                var marker = type.GetAttributes().FirstOrDefault(a =>
                    SymbolEqualityComparer.Default.Equals(a.AttributeClass, specimenAttribute));

                if (marker is null)
                {
                    continue;
                }

                rows.Add(RowFor(type, marker, compilation, Path.GetFileNameWithoutExtension(tree.FilePath)));
            }
        }

        return [.. rows.OrderBy(static row => row.Group, StringComparer.Ordinal)
            .ThenBy(static row => row.Specimen, StringComparer.Ordinal)];
    }

    private static Row RowFor(INamedTypeSymbol type, AttributeData marker, Compilation compilation, string group)
    {
        var catalogue = ErrorCatalogueReader.Read(type, compilation);

        catalogue.ShouldNotBeNull($"{type.Name} is marked a specimen but is not a capability.");

        var published = catalogue!.Errors
            .Select(static error => $"{error.Code}/{error.Category}")
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        var truth = Strings(marker, "Truth")
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        var actual = !catalogue.IsComplete
            ? Outcome.Withheld
            : published.SequenceEqual(truth, StringComparer.Ordinal)
                ? published.Length == 0 ? Outcome.ResolvedEmpty : Outcome.Resolved
                : Outcome.FalseComplete;

        return new Row(
            type.Name,
            group,
            marker.ConstructorArguments[0].Value as string ?? string.Empty,
            ExpectedOf(marker),
            actual,
            truth,
            published,
            Named(marker, "Why") as string ?? string.Empty);
    }

    /// <summary>Reads the declared expectation by enum member <em>name</em>.</summary>
    /// <remarks>
    /// By name rather than by numeric value, because <c>Corpus.Expect</c> and
    /// <see cref="Outcome"/> are two independent enums that happen to be declared in the
    /// same order. Casting one to the other would work today and would silently mislabel
    /// every specimen the day either list gains a member.
    /// </remarks>
    private static Outcome ExpectedOf(AttributeData marker)
    {
        var declared = marker.NamedArguments
            .Where(static pair => string.Equals(pair.Key, "Expect", StringComparison.Ordinal))
            .Select(static pair => pair.Value)
            .FirstOrDefault();

        var name = (declared.Type as INamedTypeSymbol)?
            .GetMembers()
            .OfType<IFieldSymbol>()
            .FirstOrDefault(field => field.HasConstantValue && Equals(field.ConstantValue, declared.Value))?
            .Name;

        name.ShouldNotBeNull("A specimen declares no Expect, or one this test cannot name.");

        return Enum.Parse<Outcome>(name!);
    }

    private static object? Named(AttributeData attribute, string name) => attribute.NamedArguments
        .Where(pair => string.Equals(pair.Key, name, StringComparison.Ordinal))
        .Select(static pair => pair.Value.Value)
        .FirstOrDefault();

    private static IEnumerable<string> Strings(AttributeData attribute, string name) => attribute.NamedArguments
        .Where(pair => string.Equals(pair.Key, name, StringComparison.Ordinal))
        .SelectMany(static pair => pair.Value.Values)
        .Select(static value => value.Value as string)
        .Where(static value => !string.IsNullOrEmpty(value))
        .Select(static value => value!);
}
