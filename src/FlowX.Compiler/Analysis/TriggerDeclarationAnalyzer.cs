using System.Collections.Immutable;
using System.Linq;
using FlowX.Compiler.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FlowX.Compiler.Analysis;

/// <summary>
/// Reports a trigger declaration the compiler cannot read: FLOWX1025.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TriggerReader"/> recognises the five trigger attributes
/// <c>FlowX.Abstractions</c> ships and skips every other <c>TriggerAttribute</c>
/// subclass, because a trigger's <c>Kind</c> is an abstract property each attribute
/// overrides — executable code, not attribute data — and there is no sound way to read
/// it from metadata. Declining to guess is the right call and
/// <a href="../../../docs/adr/ADR-0005-manifest-as-build-artifact.md">ADR-0005</a>
/// requires it: the manifest publishes what was declared, never what was plausible.
/// </para>
/// <para>
/// The defect this closes is that the skip produced <em>nothing</em>. The build
/// succeeded, the manifest carried no <c>triggers</c> entry, and <c>flowx diff</c> — which
/// classifies a removed trigger as breaking — saw an absence indistinguishable from a
/// flow that declares no trigger at all. A contract gate that silently loses its input is
/// worse than no gate, because a green result is read as a checked result.
/// </para>
/// <para>
/// A <see cref="DiagnosticAnalyzer"/> rather than a generator diagnostic, matching
/// <see cref="CapabilityAnalyzer"/> and <see cref="StepBindingAnalyzer"/>. The generator's
/// trigger pipeline drops an unrecognised attribute before it has anywhere to report
/// from, and the question is worth answering in the editor on the keystroke that applies
/// the attribute, not only when the generator next runs.
/// </para>
/// <para>
/// <strong>Scoped to <c>[Flow]</c> types.</strong> A trigger attribute applied to
/// anything else reaches no manifest either way, so reporting there would be noise about
/// a document the type was never going to appear in.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TriggerDeclarationAnalyzer : DiagnosticAnalyzer
{
    private const string FlowAttributeName = "FlowX.FlowAttribute";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(FlowXDiagnostics.TriggerCannotBeRead);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            return;
        }

        // The generated plan is a second part of the flow's class and carries no
        // attributes of its own; analysing it would only re-report the hand-written
        // declaration against a file nobody can edit.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSymbolAction(Analyze, SymbolKind.NamedType);
    }

    private static void Analyze(SymbolAnalysisContext context)
    {
        if (context.Symbol is not INamedTypeSymbol type)
        {
            return;
        }

        var attributes = type.GetAttributes();

        if (!attributes.Any(static a => a.AttributeClass?.ToDisplayString() == FlowAttributeName))
        {
            return;
        }

        foreach (var attribute in attributes)
        {
            if (!TriggerReader.IsTrigger(attribute.AttributeClass) ||
                TriggerReader.IsRecognised(attribute.AttributeClass))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                FlowXDiagnostics.TriggerCannotBeRead,
                LocationOf(attribute, type, context.CancellationToken),
                attribute.AttributeClass!.Name,
                type.Name));
        }
    }

    /// <summary>The attribute's own span, falling back to the type it is applied to.</summary>
    /// <remarks>
    /// An attribute applied through metadata — from a referenced assembly's
    /// <c>[assembly: …]</c>, or on a partial declaration in a file this compilation only
    /// has symbols for — has no syntax reference. Pointing at the type is still actionable;
    /// <see cref="Location.None"/> would put the message in the build log with no file at
    /// all, which is where diagnostics go to be ignored.
    /// </remarks>
    private static Location LocationOf(
        AttributeData attribute, INamedTypeSymbol type, System.Threading.CancellationToken cancellationToken)
    {
        var syntax = attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken);

        return syntax?.GetLocation()
            ?? type.Locations.FirstOrDefault(static l => l.IsInSource)
            ?? Location.None;
    }
}
