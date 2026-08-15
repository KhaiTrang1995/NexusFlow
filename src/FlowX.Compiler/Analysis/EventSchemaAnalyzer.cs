using System.Collections.Immutable;
using FlowX.Compiler.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FlowX.Compiler.Analysis;

/// <summary>
/// Reports an <c>[EventSchema("…")]</c> whose value is not a semantic version: FLOWX1055.
/// </summary>
/// <remarks>
/// <para>
/// <strong>On the type, not on the <c>.Emit</c> call.</strong> The attribute declares a
/// property of the contract, and a contract nothing emits today is one somebody emits
/// tomorrow — reporting only at a call site would let the mistake sit in the source until
/// then, which is exactly how long it takes for the value to be believed.
/// </para>
/// <para>
/// <strong>Judged by <see cref="EventSchemaReader"/> and never by a second parser.</strong>
/// The reader keeps only what this rule would not report, so what is reported is precisely
/// what does not reach the manifest. A rule with its own copy of the grammar is how a
/// diagnostic comes to fire on a version the document carried anyway —
/// <c>FLOWX1054</c>'s arrangement, for its reason.
/// </para>
/// <para>
/// A <see cref="DiagnosticAnalyzer"/> rather than a generator diagnostic, matching
/// <see cref="TriggerDeclarationAnalyzer"/>: the question is answerable from the type alone
/// and is worth answering on the keystroke that writes the attribute.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EventSchemaAnalyzer : DiagnosticAnalyzer
{
    private const string AttributeName = "FlowX.EventSchemaAttribute";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(FlowXDiagnostics.EventSchemaVersionCannotBeRead);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            return;
        }

        // Generated code carries no hand-written attribute; analysing it would only
        // re-report a declaration against a file nobody can edit.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSymbolAction(Analyze, SymbolKind.NamedType);
    }

    private static void Analyze(SymbolAnalysisContext context)
    {
        if (context.Symbol is not INamedTypeSymbol type ||
            EventSchemaReader.Malformed(type) is not { } declared)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            FlowXDiagnostics.EventSchemaVersionCannotBeRead,
            LocationOf(type, context.CancellationToken),
            type.Name,
            declared));
    }

    /// <summary>
    /// The attribute's own syntax where it can be found, and the type's name otherwise.
    /// </summary>
    /// <remarks>
    /// The value is what the author has to fix, so the squiggle belongs on the value rather
    /// than on the record's name three lines below it. An attribute from metadata — a
    /// contract declared in a referenced assembly — has no syntax here, and the type's own
    /// location is the nearest honest place to point.
    /// </remarks>
    private static Location? LocationOf(ISymbol type, System.Threading.CancellationToken ct)
    {
        foreach (var attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == AttributeName &&
                attribute.ApplicationSyntaxReference?.GetSyntax(ct) is { } syntax)
            {
                return syntax.GetLocation();
            }
        }

        return type.Locations.Length > 0 ? type.Locations[0] : Location.None;
    }
}
