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

    /// <summary>The attribute a schedule is declared with, and the input a schedule can give.</summary>
    private const string CronTriggerAttributeName = "FlowX.CronTriggerAttribute";

    private const string ScheduledFireName = "FlowX.ScheduledFire";

    private const string FlowBaseName = "FlowX.Flow";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(
            FlowXDiagnostics.TriggerDeclaresNoKind,
            FlowXDiagnostics.ScheduledFlowCannotBeFired);

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

        ReportUnfireableSchedules(context, type, attributes);
    }

    /// <summary>
    /// Reports FLOWX1038 on each <c>[CronTrigger]</c> the host would have nothing to do with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Here rather than in the generator, for the reason FLOWX1025 is here.</strong>
    /// The generator's schedule pipeline works from collected models with no syntax attached, so
    /// it has nowhere to point; the analyzer has the attribute's own span. It is also the
    /// question worth answering on the keystroke that applies the attribute rather than when the
    /// generator next runs — the whole failure mode is a declaration that looks right.
    /// </para>
    /// <para>
    /// <strong>One report per attribute, not one per flow.</strong> A flow may declare several
    /// schedules and every one of them is unfireable for the same reason, so pointing at each is
    /// what makes "remove this or fix the flow" a decision the author can take per line.
    /// </para>
    /// </remarks>
    private static void ReportUnfireableSchedules(
        SymbolAnalysisContext context, INamedTypeSymbol type, ImmutableArray<AttributeData> attributes)
    {
        var reason = UnfireableReason(type, attributes);

        if (reason is null)
        {
            return;
        }

        foreach (var attribute in attributes)
        {
            if (attribute.AttributeClass?.ToDisplayString() != CronTriggerAttributeName)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                FlowXDiagnostics.ScheduledFlowCannotBeFired,
                LocationOf(attribute, type, context.CancellationToken),
                FlowIdOf(type, attributes),
                reason));
        }
    }

    /// <summary>
    /// Why nothing could fire this flow's schedules, or <c>null</c> when something can.
    /// </summary>
    /// <remarks>
    /// The two reasons are checked in the order an author would repair them: a flow whose input
    /// is wrong cannot be started at all, and a flow whose profile is wrong would be started too
    /// often. Reporting both at once would give one line two fixes, and the first is the one
    /// that changes the flow's signature.
    /// </remarks>
    private static string? UnfireableReason(
        INamedTypeSymbol type, ImmutableArray<AttributeData> attributes)
    {
        if (!attributes.Any(static a => a.AttributeClass?.ToDisplayString() == CronTriggerAttributeName))
        {
            return null;
        }

        if (InputOf(type) is not { } input)
        {
            // The base type did not resolve, so C# is already reporting something more useful
            // about the same span and this rule would be piling on.
            return null;
        }

        if (input.ToDisplayString() != ScheduledFireName)
        {
            return
                $"its input contract is '{input.ToDisplayString()}' and a schedule has only an " +
                "occurrence to give it — declare it as Flow<ScheduledFire, TOut>";
        }

        return IsDurable(attributes)
            ? null
            : "it does not declare ExecutionProfile.Durable, so nothing journals its instances " +
              "and every node in the fleet would run every occurrence — declare " +
              "Profile = ExecutionProfile.Durable";
    }

    /// <summary>The <c>TIn</c> of the <c>Flow&lt;TIn, TOut&gt;</c> this type derives from.</summary>
    private static ITypeSymbol? InputOf(INamedTypeSymbol type)
    {
        for (var candidate = type.BaseType; candidate is not null; candidate = candidate.BaseType)
        {
            if (candidate.ConstructedFrom?.ToDisplayString() is { } name &&
                name.StartsWith(FlowBaseName + "<", System.StringComparison.Ordinal) &&
                candidate.TypeArguments.Length == 2)
            {
                return candidate.TypeArguments[0] is IErrorTypeSymbol ? null : candidate.TypeArguments[0];
            }
        }

        return null;
    }

    /// <summary>Whether <c>[Flow]</c> names <c>Durable</c>.</summary>
    /// <remarks>
    /// <c>ExecutionProfile.Durable</c> is <c>1</c>. Compared as the underlying value rather than
    /// by name because that is all attribute data carries, and spelled out here rather than
    /// derived for the reason <c>TriggerReader.ManifestKindName</c> gives about its own map:
    /// reordering the enum is a breaking change the compiler cannot see, and it should surface
    /// as a failing test rather than as a rule that quietly stops firing.
    /// </remarks>
    private static bool IsDurable(ImmutableArray<AttributeData> attributes) => attributes
        .Where(static a => a.AttributeClass?.ToDisplayString() == FlowAttributeName)
        .SelectMany(static a => a.NamedArguments)
        .Any(static pair => pair.Key == "Profile" && pair.Value.Value is int profile && profile == 1);

    /// <summary>The flow's declared id, or its type name when the attribute carries none.</summary>
    private static string FlowIdOf(INamedTypeSymbol type, ImmutableArray<AttributeData> attributes) =>
        attributes
            .Where(static a => a.AttributeClass?.ToDisplayString() == FlowAttributeName)
            .Where(static a => a.ConstructorArguments.Length > 0)
            .Select(static a => a.ConstructorArguments[0].Value as string)
            .FirstOrDefault(static id => !string.IsNullOrEmpty(id)) ?? type.Name;

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
