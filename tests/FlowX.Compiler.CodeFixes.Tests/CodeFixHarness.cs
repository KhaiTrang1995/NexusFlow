using System.Collections.Immutable;
using System.Globalization;
using FlowX.Compiler.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace FlowX.Compiler.CodeFixes.Tests;

/// <summary>A source file as it goes into a test project.</summary>
internal sealed record TestDocument(string Name, string Source);

/// <summary>
/// Runs a code fix the way an IDE does: real workspace, real diagnostics from the real
/// generator, real <see cref="CodeAction"/> application including Roslyn's simplification
/// and formatting pass.
/// </summary>
/// <remarks>
/// <para>
/// The shortcut this deliberately avoids is asserting that a fix was <em>offered</em>.
/// A provider that registers an action for the right diagnostic and then produces
/// <c>partialclass</c> passes that assertion. Everything here goes through
/// <see cref="CodeAction.GetOperationsAsync"/> so the text under assertion is the text a
/// developer would end up with — including the post-processing that turns
/// <c>global::FlowX.Authorization.Authenticated</c> back into the short form.
/// </para>
/// <para>
/// Diagnostics come from running <see cref="FlowPlanGenerator"/> and
/// <see cref="CapabilityAnalyzer"/> over the workspace's own compilation rather than from
/// hand-built <see cref="Diagnostic"/> objects. A hand-built diagnostic can point wherever
/// the test author found convenient, which is how a fix that works only in the test suite
/// gets written. The staleness tests are the deliberate exception: a diagnostic that has
/// outlived its cause cannot, by definition, come from a run over the current tree.
/// </para>
/// </remarks>
internal static class CodeFixHarness
{
    private static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    /// <summary>Builds a single-project solution containing the given documents.</summary>
    public static Project CreateProject(params TestDocument[] documents)
    {
        var workspace = new AdhocWorkspace();

        var project = workspace.AddProject(
            ProjectInfo.Create(
                ProjectId.CreateNewId("FlowX.CodeFixTests"),
                VersionStamp.Default,
                "FlowX.CodeFixTests",
                "FlowX.CodeFixTests",
                LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable),
                metadataReferences: References));

        foreach (var document in documents)
        {
            project = workspace
                .AddDocument(project.Id, document.Name, SourceText.From(document.Source))
                .Project;
        }

        return project;
    }

    /// <summary>Every FlowX diagnostic the compiler raises for this project.</summary>
    public static ImmutableArray<Diagnostic> Diagnose(Project project)
    {
        var compilation = Compile(project);

        var generated = CSharpGeneratorDriver
            .Create(new FlowPlanGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out _)
            .GetRunResult()
            .Diagnostics;

        var analyzed = compilation
            .WithAnalyzers([new CapabilityAnalyzer()])
            .GetAnalyzerDiagnosticsAsync()
            .GetAwaiter()
            .GetResult();

        return [.. generated, .. analyzed];
    }

    /// <summary>The ids raised, sorted, for readable assertions.</summary>
    public static string[] DiagnosticIds(Project project) =>
        [.. Diagnose(project).Select(static d => d.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>The single diagnostic with this id, failing loudly when there is not exactly one.</summary>
    public static Diagnostic Single(Project project, string id)
    {
        var diagnostics = Diagnose(project);
        var matches = diagnostics.Where(d => d.Id == id).ToArray();

        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException(
                $"Expected exactly one {id}, got {matches.Length}. All diagnostics: {Describe(diagnostics)}");
    }

    /// <summary>The actions a provider offers for a diagnostic, in the order it offers them.</summary>
    public static ImmutableArray<CodeAction> OfferedFixes(
        Project project,
        CodeFixProvider provider,
        Diagnostic diagnostic) =>
        OfferedFixes(
            project.Solution.GetDocument(diagnostic.Location.SourceTree)
                ?? throw new InvalidOperationException(
                    $"{diagnostic.Id} was reported in a tree that is not a document of the project."),
            provider,
            diagnostic);

    /// <summary>
    /// The same, for a diagnostic that did not come from this document's own compilation.
    /// </summary>
    /// <remarks>
    /// Needed to test staleness. A diagnostic outlives the edit that resolved it — the
    /// IDE keeps offering yesterday's squiggle until analysis catches up — so a provider
    /// has to re-read the tree instead of trusting what it was handed.
    /// </remarks>
    public static ImmutableArray<CodeAction> OfferedFixes(
        Document document,
        CodeFixProvider provider,
        Diagnostic diagnostic)
    {
        var actions = ImmutableArray.CreateBuilder<CodeAction>();

        var context = new CodeFixContext(
            document,
            diagnostic,
            (action, _) => actions.Add(action),
            CancellationToken.None);

        provider.RegisterCodeFixesAsync(context).GetAwaiter().GetResult();

        return actions.ToImmutable();
    }

    /// <summary>Applies an action and returns the solution it produced.</summary>
    /// <remarks>
    /// Through <see cref="CodeAction.GetOperationsAsync"/>, which is what runs the
    /// simplifier and formatter over annotated nodes. Calling
    /// <c>GetChangedSolutionAsync</c> instead would skip that pass and quietly assert
    /// against text no user ever sees.
    /// </remarks>
    public static Solution Apply(CodeAction action)
    {
        var operations = action.GetOperationsAsync(CancellationToken.None).GetAwaiter().GetResult();

        var change = operations.OfType<ApplyChangesOperation>().SingleOrDefault()
            ?? throw new InvalidOperationException($"'{action.Title}' produced no solution change.");

        return change.ChangedSolution;
    }

    /// <summary>Applies the one action a provider offers for the one diagnostic with this id.</summary>
    public static Project ApplyOnlyFix(Project project, CodeFixProvider provider, string diagnosticId)
    {
        var diagnostic = Single(project, diagnosticId);
        var action = OfferedFixes(project, provider, diagnostic).Single();

        return Apply(action).GetProject(project.Id)!;
    }

    /// <summary>The current text of a document, by name.</summary>
    public static string TextOf(Project project, string documentName) =>
        project.Documents.Single(d => d.Name == documentName)
            .GetTextAsync(CancellationToken.None).GetAwaiter().GetResult().ToString();

    /// <summary>
    /// C# compile errors for the project <em>including</em> the generator's output.
    /// </summary>
    /// <remarks>
    /// Both halves matter. The user's source has to bind, and the plan the generator
    /// emits into it has to bind as well — a fix that satisfied the analyzer while
    /// leaving the class unable to accept its generated part would pass every assertion
    /// about diagnostics and fail on the developer's machine.
    /// </remarks>
    public static string[] CompileErrors(Project project)
    {
        CSharpGeneratorDriver
            .Create(new FlowPlanGenerator())
            .RunGeneratorsAndUpdateCompilation(Compile(project), out var updated, out _);

        return [.. updated.GetDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .Select(static d => $"{d.Id}: {d.GetMessage(CultureInfo.InvariantCulture)}")];
    }

    private static Compilation Compile(Project project) =>
        project.GetCompilationAsync(CancellationToken.None).GetAwaiter().GetResult()
        ?? throw new InvalidOperationException("The project produced no compilation.");

    private static string Describe(ImmutableArray<Diagnostic> diagnostics) =>
        diagnostics.Length == 0
            ? "(none)"
            : string.Join("; ", diagnostics.Select(static d => $"{d.Id}@{d.Location.GetLineSpan().StartLinePosition}"));

    /// <summary>
    /// The platform assemblies plus the real FlowX ones.
    /// </summary>
    /// <remarks>
    /// Copied in spirit from <c>GeneratorHarness</c> in FlowX.Compiler.Tests, and for the
    /// reason stated there: stub declarations of <c>[Capability]</c> drift from the real
    /// attribute, and a suite that passes against stubs is worse than no suite. Here it
    /// matters twice over, because the FLOWX1010 fix writes an enum member name that only
    /// binds if the real enum is present.
    /// </remarks>
    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var trusted = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(static path => path.Length > 0);

        var flowX = new[]
        {
            typeof(FlowX.ICapability<,>).Assembly,
            typeof(FlowX.ExecutionPlan).Assembly,
            typeof(FlowX.Runtime.FlowEngine).Assembly,
        }.Select(static a => a.Location);

        return [.. trusted.Concat(flowX)
            .Distinct(StringComparer.Ordinal)
            .Where(File.Exists)
            .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))];
    }
}
