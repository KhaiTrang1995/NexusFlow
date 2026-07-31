using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using FlowX.Compiler.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FlowX.Compiler.Analysis;

/// <summary>
/// Reports a flow declaring an execution profile the runtime does not implement: FLOWX1028.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The gap, and what is left of it.</strong> This rule once covered <c>Durable</c>
/// too, because <c>FlowX.Runtime</c> read no profile at all: a durable flow ran the identical
/// step loop as an ephemeral one, with no journal, no lease, no resumption and no replay, and
/// nothing anywhere said so. WP-52 closed that half — the engine journals a durable flow's
/// step boundaries and refuses to run one that has no journal to write to — so <c>Durable</c>
/// is now silent here.
/// </para>
/// <para>
/// <strong><c>Streaming</c> is not, and it is the worse hole of the two.</strong>
/// <c>06 §4</c> puts it plainly: "<c>Streaming</c> has no engine at all". Deleting this rule
/// outright when the journal landed would have handed <c>Streaming</c> exactly the silence
/// <c>Durable</c> had — and it would have to be written a second time in P7 to say so. So it
/// is narrowed rather than removed, which is what
/// <a href="../../../docs/diagnostics/FLOWX1028.md">its own deletion table</a> and ADR-0015's
/// take-down list both call for.
/// </para>
/// <para>
/// <strong>Why a diagnostic is the intervention.</strong> The stream engine is a phase of
/// work — <a href="../../../docs/20-Roadmap.md">P7</a> — and cannot be conjured by a
/// compiler rule. What a compiler rule can do
/// is remove the silence, which is the part that turns a missing feature into a
/// <em>defect</em>: an author who is told gets to decide, and an author who is not told
/// ships a payment saga believing it survives a deploy. The runtime is the other candidate
/// and is strictly worse: it cannot refuse to run a flow that is already deployed, and a
/// throw at start-up would be a build break discovered in production.
/// </para>
/// <para>
/// <strong>Reported once, at the declaration.</strong> Not on <c>AwaitSignal</c>,
/// <c>Delay</c> or <c>CompensateWith</c> under a durable flow, though each is a place the
/// gap bites. Those are consequences; the declaration is the decision, it is the single
/// line a reviewer reads, and it is the one place where a suppression is a statement about
/// this flow rather than about one of its steps. A per-step rule would also report a saga
/// with nine compensable steps nine times, which is how a catalogue gets suppressed
/// wholesale.
/// </para>
/// <para>
/// <strong>A <see cref="DiagnosticAnalyzer"/> rather than a generator diagnostic</strong>,
/// matching <see cref="TriggerDeclarationAnalyzer"/>. The question is answered entirely
/// from the <c>[Flow]</c> attribute — no graph, no chain walk, nothing the generator
/// uniquely knows — and it is worth answering on the keystroke that types
/// <c>Streaming</c>, not when the generator next runs.
/// </para>
/// <para>
/// <strong>Delete this analyzer when P7 lands the stream engine.</strong> It is scaffolding
/// for a missing phase, and a rule nobody removes when it stops being true becomes noise.
/// The reminder that watched the <c>Durable</c> half —
/// <c>RuntimeDoesNotReadTheExecutionProfile</c> in <c>FlowX.Architecture.Tests</c> — failed
/// on WP-52 as it was written to, and was deleted with the half it described rather than
/// narrowed to a subdirectory. P7 has no equivalent scaffold, because "no stream engine" is
/// not a claim any file makes that a test could catch changing; what it has is the deletion
/// table on the diagnostic's own page.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ExecutionProfileAnalyzer : DiagnosticAnalyzer
{
    private const string FlowAttributeName = "FlowX.FlowAttribute";
    private const string ProfileArgument = "Profile";

    /// <summary>
    /// <c>ExecutionProfile.Ephemeral</c>, as it appears in attribute metadata.
    /// </summary>
    /// <remarks>
    /// The attribute's default (ADR-0003: durability is opted into), so a flow that names no
    /// profile reads as zero here and is correctly silent.
    /// </remarks>
    private const int EphemeralProfile = 0;

    /// <summary>
    /// <c>ExecutionProfile.Durable</c>, as it appears in attribute metadata.
    /// </summary>
    /// <remarks>
    /// Implemented since WP-52: the engine journals a durable flow's step boundaries under a
    /// fencing token and refuses to run one that has no journal. This constant is what
    /// narrowed the rule rather than deleting it.
    /// </remarks>
    private const int DurableProfile = 1;

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(FlowXDiagnostics.ProfileIsNotHonouredByTheRuntime);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            return;
        }

        // The generated plan is a second part of the flow's class and carries no attributes
        // of its own; analysing it would re-report the hand-written declaration against a
        // file nobody can edit or suppress in.
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

        foreach (var attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != FlowAttributeName)
            {
                continue;
            }

            foreach (var argument in attribute.NamedArguments)
            {
                if (argument.Key != ProfileArgument)
                {
                    continue;
                }

                var profile = NameOfDeclaredProfile(argument.Value);

                if (profile is not null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        FlowXDiagnostics.ProfileIsNotHonouredByTheRuntime,
                        LocationOf(attribute, type, context.CancellationToken),
                        type.Name,
                        profile));
                }

                return;
            }

            return;
        }
    }

    /// <summary>
    /// The name of the declared profile, or <see langword="null"/> when there is nothing to
    /// report.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The name is read back off the enum symbol rather than from a table of constants in
    /// this file. That is deliberate: a profile added to <c>ExecutionProfile</c> later is
    /// also one the runtime does not implement on the day it is added, and a hard-coded
    /// list would let it ship silent — the exact defect this rule exists to close,
    /// reintroduced one enum member at a time. What <em>is</em> hard-coded is the far
    /// shorter list of profiles that <em>are</em> implemented, which is the safe direction
    /// for the list to be wrong in.
    /// </para>
    /// <para>
    /// A value outside the enum — <c>(ExecutionProfile)7</c>, which C# permits — is silent.
    /// It names no profile the runtime could implement or fail to implement, so a message
    /// about durability would be about the wrong problem, and this rule has no standing to
    /// invent one.
    /// </para>
    /// </remarks>
    private static string? NameOfDeclaredProfile(TypedConstant declared)
    {
        if (declared.Value is not int value || value is EphemeralProfile or DurableProfile)
        {
            return null;
        }

        return (declared.Type as INamedTypeSymbol)?.GetMembers()
            .OfType<IFieldSymbol>()
            .FirstOrDefault(field => field.HasConstantValue && field.ConstantValue is int declaredValue && declaredValue == value)
            ?.Name;
    }

    /// <summary>
    /// The <c>Profile = …</c> argument's own span, falling back to the attribute and then to
    /// the flow.
    /// </summary>
    /// <remarks>
    /// The argument is the narrowest honest location: it is the text the author would edit,
    /// and it is what a <c>#pragma</c> or an IDE squiggle should sit under. The fallbacks
    /// cover an attribute applied through metadata, which has no syntax at all;
    /// <see cref="Location.None"/> would put the message in the build log with no file,
    /// which is where diagnostics go to be ignored.
    /// </remarks>
    private static Location LocationOf(
        AttributeData attribute, INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        var syntax = attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken);

        var argument = (syntax as AttributeSyntax)?.ArgumentList?.Arguments
            .FirstOrDefault(static a => a.NameEquals?.Name.Identifier.ValueText == ProfileArgument);

        return argument?.GetLocation()
            ?? syntax?.GetLocation()
            ?? type.Locations.FirstOrDefault(static l => l.IsInSource)
            ?? Location.None;
    }
}
