using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using FlowX.Compiler.Analysis;
using FlowX.Compiler.Emit;
using FlowX.Compiler.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace FlowX.Compiler;

/// <summary>
/// Emits a compiled execution plan and step dispatcher for every <c>[Flow]</c> in the
/// compilation.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately thin. Everything this class does is: find flow declarations, hand each
/// to <see cref="FlowAnalyzer"/>, hand the resulting model to <see cref="FlowEmitter"/>,
/// and report whatever diagnostics came back. All of the judgement lives in the two
/// layers it calls, and both are testable without a compilation.
/// </para>
/// <para>
/// <strong>Incremental, and it matters.</strong> A generator that re-runs on every
/// keystroke makes the IDE unusable on a large solution, which is how a team ends up
/// disabling the generator and hand-writing what it produced. The pipeline is keyed on
/// syntax that names <c>[Flow]</c> so typing inside an unrelated method costs nothing,
/// and budget B12 caps total build overhead at 8 %.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class FlowPlanGenerator : IIncrementalGenerator
{
    private const string FlowAttributeName = "FlowX.FlowAttribute";
    private const string CapabilityAttributeName = "FlowX.CapabilityAttribute";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var flows = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                FlowAttributeName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, cancellationToken) => Analyze(ctx, cancellationToken))
            .Where(static result => result is not null);

        context.RegisterSourceOutput(flows, static (production, result) => Produce(production, result!));

        // The manifest describes the whole application, so it needs every flow at once.
        // Collect() introduces a barrier — any flow changing regenerates the manifest —
        // which is correct: a manifest built from a stale subset would be worse than no
        // manifest, because `flowx diff` would trust it.
        var application = context.CompilationProvider.Select(
            static (compilation, _) => compilation.AssemblyName ?? "Application");

        // Source pointers in the manifest are written relative to this, so the document
        // does not carry the build agent's directory layout. Supplied by the props file
        // shipped in the analyzer package; null when a host does not provide it, which
        // Relativise handles by leaving the path alone.
        var projectDirectory = context.AnalyzerConfigOptionsProvider.Select(
            static (options, _) => options.GlobalOptions.TryGetValue("build_property.projectdir", out var dir)
                ? dir
                : null);

        // Triggers are read from attributes on the flow's class rather than from its
        // Define chain, so they come down their own pipeline and never enter FlowModel.
        // The plan emitter has no use for them — the flow body cannot observe a trigger
        // (ADR-0004) — and keeping them out of the model is what makes that true in the
        // code and not only in the documentation.
        var triggers = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                FlowAttributeName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, cancellationToken) => ReadTriggers(ctx, cancellationToken))
            .Where(static result => result is not null);

        // Error catalogues are keyed on [Capability], not on [Flow], so every capability
        // in the compilation is read — including one no flow has a step for yet. The
        // manifest joins on id@version and uses what it needs.
        var errorCatalogues = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                CapabilityAttributeName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, cancellationToken) => ErrorCatalogueReader.Read(
                    ctx.TargetSymbol as INamedTypeSymbol, ctx.SemanticModel.Compilation, cancellationToken))
            .Where(static result => result is not null);

        context.RegisterSourceOutput(
            flows.Collect()
                .Combine(triggers.Collect())
                .Combine(errorCatalogues.Collect())
                .Combine(application)
                .Combine(projectDirectory),
            static (production, data) =>
            {
                var ((((analysed, declared), catalogues), applicationName), directory) = data;
                ProduceManifest(production, analysed, declared, catalogues, applicationName, directory);
            });
    }

    /// <summary>Reads the trigger attributes off one flow declaration.</summary>
    /// <remarks>
    /// The flow id is read from <c>[Flow]</c> here rather than taken from the analysed
    /// model, because this pipeline runs independently of that one: a trigger set that
    /// waited for a flow to analyse cleanly would vanish from the manifest whenever the
    /// flow body had an unrelated error, which is precisely when a reviewer is looking.
    /// </remarks>
    private static FlowTriggersModel? ReadTriggers(
        GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.TargetSymbol is not INamedTypeSymbol symbol)
        {
            return null;
        }

        var declared = TriggerReader.Read(symbol);

        if (declared.Count == 0)
        {
            return null;
        }

        var attribute = context.Attributes.FirstOrDefault(a => a.ConstructorArguments.Length > 0);
        var flowId = attribute?.ConstructorArguments[0].Value as string ?? symbol.Name;

        return new FlowTriggersModel(flowId, declared);
    }

    private static void ProduceManifest(
        SourceProductionContext production,
        ImmutableArray<AnalysisResult?> results,
        ImmutableArray<FlowTriggersModel?> triggers,
        ImmutableArray<CapabilityErrorCatalogue?> errorCatalogues,
        string applicationName,
        string? projectDirectory)
    {
        var models = results
            .Where(static r => r is { IsSuccess: true, Model: not null })
            .Select(static r => r!.Model!)
            .ToList();

        if (models.Count == 0)
        {
            // No flows, or none that analysed cleanly. Emitting an empty manifest here
            // would let a build with errors publish a document claiming the application
            // has no flows, which is a more dangerous lie than emitting nothing.
            return;
        }

        var manifest = ManifestWriter.Write(
            applicationName,
            "1.0.0",
            models,
            projectDirectory,
            [.. triggers.Where(static t => t is not null).Select(static t => t!)],
            [.. errorCatalogues.Where(static c => c is not null).Select(static c => c!)]);

        production.AddSource("FlowXManifest.g.cs", SourceText.From(EmitManifestHolder(manifest), Encoding.UTF8));
    }

    /// <summary>
    /// Wraps the manifest JSON in a C# constant.
    /// </summary>
    /// <remarks>
    /// A source generator must not write files. It runs inside the IDE on every
    /// keystroke, its output is cached by the compiler, and file IO from that position
    /// breaks incrementality and races with the build. So the manifest travels as a
    /// compiled-in constant, and <c>flowx manifest</c> (WP-9) writes it to disk from
    /// there. The build artifact ADR-0005 asks for is produced by the CLI; the content
    /// is produced here, deterministically.
    /// </remarks>
    private static string EmitManifestHolder(string manifest)
    {
        var writer = new SourceWriter();

        writer.Line(FlowEmitter.Header.TrimEnd('\n'));
        writer.Line("#nullable enable");
        writer.Line();
        writer.Line("namespace FlowX.Generated;");
        writer.Line();
        writer.Line("/// <summary>The application's compiled manifest. See ADR-0005.</summary>");
        writer.Line("public static class FlowXManifest");
        writer.OpenBrace();
        writer.Line("/// <summary>The manifest document, byte-identical across builds of identical source.</summary>");
        writer.Line("public const string Json = @\"" + manifest.Replace("\"", "\"\"") + "\";");
        writer.CloseBrace();

        return writer.ToString();
    }

    private static AnalysisResult? Analyze(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.TargetNode is not ClassDeclarationSyntax declaration ||
            context.TargetSymbol is not INamedTypeSymbol symbol)
        {
            return null;
        }

        return FlowAnalyzer.Analyze(symbol, declaration, context.SemanticModel);
    }

    private static void Produce(SourceProductionContext production, AnalysisResult result)
    {
        foreach (var diagnostic in result.Diagnostics)
        {
            production.ReportDiagnostic(diagnostic);
        }

        if (!result.IsSuccess || result.Model is null)
        {
            // Diagnostics have been reported; emitting a partial plan on top of them
            // would bury the real error under a cascade of "type not found".
            return;
        }

        production.AddSource(
            FlowEmitter.FileNameFor(result.Model),
            SourceText.From(FlowEmitter.Emit(result.Model), Encoding.UTF8));
    }
}
