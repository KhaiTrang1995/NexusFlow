using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FlowX.Functions.Tests;

/// <summary>What one generator run produced.</summary>
internal sealed record GeneratorRun(ImmutableArray<(string HintName, string Source)> Sources)
{
    /// <summary>The generated entry points, or <c>null</c> when the generator emitted nothing.</summary>
    public string? Functions => Sources
        .Where(static s => s.HintName == "FlowXFunctions.g.cs")
        .Select(static s => s.Source)
        .FirstOrDefault();
}

/// <summary>
/// Runs <see cref="FlowFunctionGenerator"/> against real source, in a real compilation.
/// </summary>
/// <remarks>
/// <para>
/// <c>FlowX.Compiler.Tests</c>'s harness and its reasons: the real FlowX assemblies are
/// referenced rather than imitated, because a suite that passes against stubs while the real
/// attributes have moved is worse than no suite.
/// </para>
/// <para>
/// <strong>The worker SDK is the one thing declared in source rather than referenced.</strong>
/// The generator's opt-in is one <c>GetTypeByMetadataName</c>, so a declaration of that type in
/// the compilation answers it exactly as the real package would — and the alternative, a
/// PackageReference to <c>Microsoft.Azure.Functions.Worker</c> in this test project, would add
/// a dependency, and its transitive graph, to the licence register for a type nobody calls.
/// The sample is where the real SDK is proved against, and it compiles the generated file.
/// </para>
/// </remarks>
internal static class GeneratorHarness
{
    /// <summary>
    /// Enough of the worker SDK for the opt-in to be true, and nothing more.
    /// </summary>
    /// <remarks>
    /// Only <c>FunctionAttribute</c> is declared, because only its presence is read. The
    /// emitted file names <c>HttpTrigger</c>, <c>ServiceBusTrigger</c> and the rest, and none
    /// of them is declared here — these tests assert on the emitted <em>text</em>, and the
    /// sample is what proves the text binds.
    /// </remarks>
    public const string WorkerSdk = """
        namespace Microsoft.Azure.Functions.Worker
        {
            [System.AttributeUsage(System.AttributeTargets.Method)]
            public sealed class FunctionAttribute : System.Attribute
            {
                public FunctionAttribute(string name) => Name = name;

                public string Name { get; }
            }
        }
        """;

    private static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    /// <summary>Runs the generator over source that has opted in.</summary>
    /// <param name="source">The flows.</param>
    public static GeneratorRun Run(string source) => Run(source, optedIn: true);

    /// <summary>Runs the generator, with the opt-in under the caller's control.</summary>
    /// <param name="source">The flows.</param>
    /// <param name="optedIn">Whether the compilation can see the worker SDK.</param>
    public static GeneratorRun Run(string source, bool optedIn)
    {
        var trees = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(source, path: "/src/Flows/Sample.cs"),
        };

        if (optedIn)
        {
            trees.Add(CSharpSyntaxTree.ParseText(WorkerSdk, path: "/src/Worker.cs"));
        }

        var compilation = CSharpCompilation.Create(
            "FlowX.FunctionTests",
            trees,
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver
            .Create(new FlowFunctionGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out _);

        var result = driver.GetRunResult().Results.Single();

        return new GeneratorRun(
            [.. result.GeneratedSources.Select(s => (s.HintName, s.SourceText.ToString()))]);
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
            typeof(FlowX.Hosting.FlowPushSeams).Assembly,
        }.Select(static a => a.Location);

        return [.. trusted.Concat(flowX)
            .Distinct(StringComparer.Ordinal)
            .Where(System.IO.File.Exists)
            .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))];
    }
}
