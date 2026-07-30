using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using FlowX.Compiler.Analysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FlowX.Compiler.Tests;

/// <summary>What one generator run produced.</summary>
internal sealed record GeneratorRun(
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableArray<(string HintName, string Source)> Sources)
{
    /// <summary>Diagnostic ids reported, in order.</summary>
    public string[] Ids => [.. Diagnostics.Select(static d => d.Id)];

    /// <summary>The generated plan for the single flow in the run.</summary>
    /// <remarks>
    /// Named for what it is rather than for how many files there happen to be. An
    /// earlier version returned "the one source", which quietly broke every test the
    /// day the manifest became a second output — the tests were coupled to a file
    /// count instead of to the thing they were asserting about.
    /// </remarks>
    public string Plan
    {
        get
        {
            var plans = Sources.Where(static s => s.HintName.EndsWith(".Flow.g.cs", StringComparison.Ordinal)).ToArray();

            return plans.Length == 1
                ? plans[0].Source
                : throw new InvalidOperationException(
                    FormattableString.Invariant($"Expected exactly one generated plan, got {plans.Length}."));
        }
    }

    /// <summary>The generated manifest holder source, or <c>null</c> when none was produced.</summary>
    public string? Manifest => Sources
        .Where(static s => s.HintName == "FlowXManifest.g.cs")
        .Select(static s => s.Source)
        .FirstOrDefault();

    /// <summary>The manifest JSON itself, unwrapped from the C# verbatim string.</summary>
    /// <remarks>
    /// Asserting on the holder source directly does not work and fails confusingly: the
    /// JSON lives inside a verbatim string literal, so every <c>"</c> in it appears as
    /// <c>""</c>. Unwrapping here means tests assert on JSON, which is what they are
    /// actually about.
    /// </remarks>
    public string? ManifestJson
    {
        get
        {
            var holder = Manifest;

            if (holder is null)
            {
                return null;
            }

            var start = holder.IndexOf("@\"", StringComparison.Ordinal) + 2;
            var end = holder.LastIndexOf("\";", StringComparison.Ordinal);

            return start < 2 || end <= start
                ? null
                : holder[start..end].Replace("\"\"", "\"", StringComparison.Ordinal);
        }
    }

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

    /// <summary>Runs <see cref="CapabilityAnalyzer"/> and returns the ids it reported.</summary>
    /// <remarks>
    /// A separate entry point because the analyzer is not a generator: it runs over the
    /// compilation's symbols rather than producing source, so the generator driver never
    /// invokes it. Testing it through <see cref="Run"/> would have reported nothing and
    /// looked like a passing test.
    /// </remarks>
    public static string[] Analyze(string source)
    {
        var compilation = CSharpCompilation.Create(
            "FlowX.AnalyzerTests",
            [CSharpSyntaxTree.ParseText(source, path: "/src/Flows/Sample.cs")],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var diagnostics = compilation
            .WithAnalyzers([new CapabilityAnalyzer()])
            .GetAnalyzerDiagnosticsAsync()
            .GetAwaiter()
            .GetResult();

        return [.. diagnostics.Select(static d => d.Id).Distinct().OrderBy(static id => id, StringComparer.Ordinal)];
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

    /// <summary>
    /// Runs the generator and compiles what it produced, returning the errors.
    /// </summary>
    /// <remarks>
    /// <see cref="Run"/> discards the updated compilation, so nothing it asserts proves
    /// the emitted C# builds — and "the text looks right" and "the text compiles" are
    /// different claims. <c>FlowEmitterTests</c> parses the output, which catches a
    /// syntax error but not a name that fails to resolve, a delegate whose type argument
    /// is wrong, or an interface member left unimplemented. This binds it.
    /// </remarks>
    public static string[] GeneratedCompileErrorsIn(string source)
    {
        var compilation = CSharpCompilation.Create(
            "FlowX.GeneratorTests.Generated",
            [CSharpSyntaxTree.ParseText(source, path: "/src/Flows/Sample.cs")],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        CSharpGeneratorDriver
            .Create(new FlowPlanGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);

        return [.. updated.GetDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .Select(static d => $"{d.Id} at {d.Location.GetLineSpan().StartLinePosition}: " +
                                d.GetMessage(CultureInfo.InvariantCulture))];
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
