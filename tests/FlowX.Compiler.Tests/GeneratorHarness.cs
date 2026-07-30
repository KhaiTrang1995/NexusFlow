using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FlowX.Compiler.Tests;

/// <summary>What one generator run produced.</summary>
internal sealed record GeneratorRun(
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableArray<(string HintName, string Source)> Sources)
{
    /// <summary>Diagnostic ids reported, in order.</summary>
    public string[] Ids => [.. Diagnostics.Select(static d => d.Id)];

    /// <summary>The single generated file, when exactly one was produced.</summary>
    public string SingleSource => Sources.Length == 1
        ? Sources[0].Source
        : throw new InvalidOperationException(
            FormattableString.Invariant($"Expected exactly one generated file, got {Sources.Length}."));

    /// <summary>Human-readable diagnostics, for assertion messages.</summary>
    public string Describe() => Diagnostics.Length == 0
        ? "(no diagnostics)"
        : string.Join("\n", Diagnostics.Select(static d =>
            $"{d.Id}: {d.GetMessage(CultureInfo.InvariantCulture)}"));
}

/// <summary>
/// Runs <see cref="FlowPlanGenerator"/> against real source, in a real compilation.
/// </summary>
/// <remarks>
/// <para>
/// The emitter and model are tested as pure functions elsewhere, which is fast and
/// covers most of the surface. This harness exists for the part that cannot be faked:
/// whether the Roslyn plumbing actually finds a flow, resolves its capabilities, and
/// attaches diagnostics to the right span.
/// </para>
/// <para>
/// It references the real FlowX assemblies rather than stub declarations. Stubs drift
/// from the contracts they imitate, and a generator test suite that passes against
/// stubs while the real attributes have moved is worse than no suite.
/// </para>
/// </remarks>
internal static class GeneratorHarness
{
    private static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    public static GeneratorRun Run(string source)
    {
        var compilation = CSharpCompilation.Create(
            "FlowX.GeneratorTests",
            [CSharpSyntaxTree.ParseText(source, path: "/src/Flows/Sample.cs")],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver
            .Create(new FlowPlanGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out _);

        var result = driver.GetRunResult().Results.Single();

        return new GeneratorRun(
            result.Diagnostics,
            [.. result.GeneratedSources.Select(s => (s.HintName, s.SourceText.ToString()))]);
    }

    /// <summary>Asserts the input compiles cleanly before the generator sees it.</summary>
    /// <remarks>
    /// Without this, a typo in a test's source string surfaces as "the generator found
    /// no flows", and the next hour is spent debugging the generator instead of the test.
    /// </remarks>
    public static string[] CompileErrorsIn(string source)
    {
        var compilation = CSharpCompilation.Create(
            "FlowX.GeneratorTests.Precheck",
            [CSharpSyntaxTree.ParseText(source)],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return [.. compilation.GetDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .Select(static d => $"{d.Id}: {d.GetMessage(CultureInfo.InvariantCulture)}")];
    }

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
