using System.Linq;
using System.Text;
using System.Threading;
using FlowX.Compiler.Analysis;
using FlowX.Compiler.Emit;
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
