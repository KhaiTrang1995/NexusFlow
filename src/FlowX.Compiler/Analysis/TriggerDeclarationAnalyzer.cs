using System.Collections.Immutable;
using System.Linq;
using FlowX.Compiler.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FlowX.Compiler.Analysis;

/// <summary>
/// Reports a trigger attribute that declares no <c>[TriggerKind]</c>: FLOWX1025.
/// </summary>
/// <remarks>
/// <para>
/// A trigger's <c>Kind</c> property is abstract and each attribute overrides it —
/// executable code, not attribute data — so it cannot be read from metadata. The kind is
/// therefore declared a second time <em>as</em> data, with <c>[TriggerKind(...)]</c> on the
/// attribute class, and <see cref="TriggerReader.KindOf"/> reads that, across an assembly
/// boundary, without running anything. An attribute carrying no marker declares no family
/// the compiler can read and is skipped rather than guessed at, which
/// <a href="../../../docs/adr/ADR-0005-manifest-as-build-artifact.md">ADR-0005</a>
/// requires: the manifest publishes what was declared, never what was plausible.
/// </para>
/// <para>
/// <strong>What the rule says now.</strong> Not "this trigger cannot be read" — since the
/// marker exists, it can be — but "this trigger attribute declares no kind", which is a
/// defect with an owner and a one-line fix. The consequence is unchanged, and is why it is
/// worth reporting: the build succeeds, the manifest carries no <c>triggers</c> entry, and
/// <c>flowx diff</c> — which classifies a removed trigger as breaking — sees an absence
/// indistinguishable from a flow that declares no trigger at all. A contract gate that
/// silently loses its input is worse than no gate, because a green result is read as a
/// checked result.
/// </para>
/// <para>
/// A <see cref="DiagnosticAnalyzer"/> rather than a generator diagnostic, matching
/// <see cref="CapabilityAnalyzer"/> and <see cref="StepBindingAnalyzer"/>. The generator's
/// trigger pipeline drops an undeclared attribute before it has anywhere to report from,
/// and the question is worth answering in the editor on the keystroke that applies the
/// attribute, not only when the generator next runs.
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
        ImmutableArray.Create(FlowXDiagnostics.TriggerDeclaresNoKind);

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
            // Exactly what TriggerReader skips: a trigger whose family it cannot read.
            if (!TriggerReader.IsTrigger(attribute.AttributeClass) ||
                TriggerReader.KindOf(attribute.AttributeClass) is not null)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                FlowXDiagnostics.TriggerDeclaresNoKind,
                LocationOf(attribute, type, context.CancellationToken),
                SeverityFor(attribute.AttributeClass!),
                additionalLocations: null,
                properties: null,
                attribute.AttributeClass!.Name,
                type.Name));
        }
    }

    /// <summary>How loud to be, given who can fix it.</summary>
    /// <remarks>
    /// <para>
    /// An <strong>error</strong> when the trigger attribute is declared in this
    /// compilation: the fix is one line in a file the person reading the diagnostic owns,
    /// and a rule nobody has to obey is not a rule. The same escalation FLOWX1011 makes for
    /// a <c>Durable</c> flow, and for the same reason — the severity follows what the
    /// author can actually do about it.
    /// </para>
    /// <para>
    /// A <strong>warning</strong> when it arrives from a referenced assembly. The marker
    /// belongs on the attribute class, so a consumer of a plugin that has not added one
    /// cannot fix this in their own repository at all; erroring would make using a
    /// third-party transport fail their build over someone else's omission, which
    /// <c>17-Plugin-System.md §1</c> rules out. It stays a warning they can see, report
    /// upstream, and downgrade with a recorded reason.
    /// </para>
    /// </remarks>
    /// <param name="attributeClass">The trigger attribute that carries no marker.</param>
    private static DiagnosticSeverity SeverityFor(INamedTypeSymbol attributeClass) =>
        attributeClass.Locations.Any(static location => location.IsInSource)
            ? DiagnosticSeverity.Error
            : DiagnosticSeverity.Warning;

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
