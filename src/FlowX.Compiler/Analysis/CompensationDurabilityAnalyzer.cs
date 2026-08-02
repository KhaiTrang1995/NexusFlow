using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using FlowX.Compiler.Diagnostics;
using FlowX.Compiler.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FlowX.Compiler.Analysis;

/// <summary>
/// Reports a flow that declares compensation without declaring durability: FLOWX1012.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The oldest reservation in the catalogue, and the check was never what held it
/// up.</strong> It is one predicate — <c>.CompensateWith</c> under a profile that is not
/// <c>Durable</c> — and ADR-0003's negative bullet specified it in the same sentence as
/// <see cref="FlowXDiagnostics.AwaitSignalRequiresDurable"/>, which shipped in P1. What
/// held it up was its <em>remedy</em>. <c>Profile = Durable</c> changed nothing at all
/// while <c>FlowX.Runtime</c> read no profile; then WP-52 made the runtime read it and the
/// remedy briefly changed something worse than nothing, because a durable flow with no
/// journal is refused before its first step. WP-53 and WP-55 gave a host stores to
/// register. A rule whose fix is a lie is worse than an unraised id; this one's fix stopped
/// being a lie, which is the entire reason it exists now and did not before.
/// </para>
/// <para>
/// <strong>What the remedy buys, and the engine line that makes it true.</strong> A durable
/// flow commits a row per step boundary. A recovered instance re-enters the same step loop,
/// skips each step the journal shows completed — and, as it skips one that declared a
/// compensation, <em>puts it back on the unwind stack</em>, because a resumed flow that
/// later fails must undo what the node before it did. That single behaviour is the whole of
/// what this diagnostic promises. It is deliberately not promised for two cases that are
/// still open: the unwind is not itself journaled, so a crash during compensation still
/// loses it (<c>06 §7</c> rule 4), and a resumed parent does not rebuild a skipped
/// sub-flow's compensation stack, which is WP-57. The message and the page say both, because
/// a rule that oversells its fix earns the silence the reserved id already had.
/// </para>
/// <para>
/// <strong>Severity: <c>Warning</c>, uniformly, and it is not inherited from the determinism
/// set.</strong> WP-58 shipped <c>FLOWX1007</c>–<c>FLOWX1009</c> as "Warning by default,
/// Error where the compilation can prove the code is on a durable flow's replay path", and
/// the temptation is to apply that sentence here because this rule is about a profile too.
/// It does not apply: this rule reports <em>because</em> the flow is not durable, so its
/// trigger and that escalation are mutually exclusive — there is no compilation in which
/// this rule reports and that proof exists. The one escalation a reader will propose, a
/// <c>Durable</c> parent composing this flow as a sub-flow, is the exact case the engine
/// does not honour today (see the paragraph above), so escalating on it would stop a build
/// on a guarantee the runtime does not deliver.
/// </para>
/// <para>
/// <strong>Why not an error on its own merits.</strong> The source is not wrong. A
/// compensable <c>Ephemeral</c> flow unwinds correctly on every failure that is not a
/// crash, which is the trade ADR-0003 ratified and which <c>docs/DEBT.md</c> names as its
/// example of a <em>decision</em> rather than debt — an error would make a ratified decision
/// inexpressible. And the remedy has a prerequisite outside the source file: a host with no
/// journal refuses the flow, so an error would stop the build until the author made an edit
/// whose correctness depends on a deployment fact this analyzer cannot see. Info was
/// rejected for the reason WP-58 gave and then some — it never reaches a build log, and this
/// rule fires only on flows that declined durability, which is nearly every flow.
/// <c>Warning</c> is not the lenient reading: this repository sets
/// <c>TreatWarningsAsErrors</c>, so it stops the build here, and a consumer who has decided
/// otherwise writes one <c>.editorconfig</c> line in the repository that took the decision.
/// </para>
/// <para>
/// <strong>Reported once, at the declaration</strong>, for
/// <see cref="ExecutionProfileAnalyzer"/>'s reason: a saga with nine compensable steps
/// reported nine times is how a catalogue gets suppressed wholesale, the profile is the
/// decision and the steps are its consequences, and the declaration is the single line a
/// reviewer reads. The compensation sites travel as additional locations, so the evidence is
/// one keystroke away without being nine diagnostics.
/// </para>
/// <para>
/// <strong>No code fix, and that is a decision rather than an omission.</strong>
/// <c>AwaitSignalRequiresDurableCodeFixProvider</c> writes <c>Profile = Durable</c> for
/// FLOWX1017 and the same edit would clear this rule — but there the source was internally
/// inconsistent (an in-memory wait cannot suspend at all), so the profile was the only thing
/// left to correct. Here the flow runs, and the quick action's output does not: a flow made
/// durable by one click is refused at run time with
/// <c>flow.durability_not_configured</c> until someone registers a journal and a lease
/// store. A quick action that trades a build warning for a start-up failure is the fix that
/// silences the rule rather than the fix that is correct, which is the one thing a code fix
/// may not be.
/// </para>
/// <para>
/// <strong>The condition is "does this journal?", not "is this <c>Durable</c>?", and
/// <c>Streaming</c> is therefore silent.</strong> It was written as the second and defended
/// with "<c>Streaming</c> runs on the ephemeral engine", which P7 falsified: every link in the
/// second paragraph's chain is keyed on the journal and none on the profile.
/// <c>FlowEngine.OpenJournal</c> asks <c>ExecutionProfiles.IsJournaled</c>, the emitter
/// describes the state bag — the flow's own input included — off the same question,
/// <c>PostgresRecoveryIndex</c> lists an unfinished instance on
/// <c>state IN ('Pending', 'Running', 'Compensating')</c> with no profile in the predicate,
/// <c>FlowStreamSubscriptionRegistration.Add</c> registers the plan in <c>FlowCatalog</c>
/// precisely so <c>FlowRecoveryScan</c> can resume such a row, and the skip in the step loop
/// pushes a completed compensable step back onto the unwind stack off <c>cursor.IsJournaled</c>.
/// The window a surviving node rebuilds does not race that unwind: its derived id meets
/// <c>flow_instance</c>'s primary key and <c>FlowStreamScan.DispositionFor</c> deduplicates it
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0055-a-window-names-the-instance-it-starts.md">ADR-0055</a>).
/// </para>
/// <para>
/// <strong>The remedy settles it on its own.</strong> <c>Profile = Durable</c> on a
/// stream-triggered flow is <c>FLOWX1042</c>, emits no subscription, and is refused by
/// <c>FlowStreamCatalog.Add</c> at start-up — so the rule was prescribing an edit that breaks
/// the trigger, which is the "fix that is a lie" the first paragraph says kept this id
/// reserved. A <c>Streaming</c> flow that no stream starts is still
/// <see cref="ExecutionProfileAnalyzer"/>'s, the profile buying nothing; its instances are
/// journaled per invocation, so this rule has nothing to add there either.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CompensationDurabilityAnalyzer : DiagnosticAnalyzer
{
    private const string FlowAttributeName = "FlowX.FlowAttribute";
    private const string ProfileArgument = "Profile";
    private const string CompensateWith = "CompensateWith";
    private const string FlowXNamespace = "FlowX";
    private const string BuilderInterfaceSuffix = "Builder";

    /// <summary>
    /// The profile a flow that names none is running under: ADR-0003 makes durability
    /// opt-in, so silence means <c>Ephemeral</c> and this rule reports on it.
    /// </summary>
    /// <remarks>
    /// This is the inversion that makes FLOWX1012 different from every other profile rule in
    /// the catalogue. <see cref="ExecutionProfileAnalyzer"/> and the determinism set are
    /// silent on the default and speak up about a declaration; this one is silent about a
    /// declaration and speaks up about the default, because the default is the profile that
    /// loses the work.
    /// </remarks>
    private const string DefaultProfileName = "Ephemeral";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(FlowXDiagnostics.CompensationIsNotDurable);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            return;
        }

        // The generated plan is the other part of this partial class. It carries the
        // compensation as a field of an ExecutionPlan rather than as a builder call, so it
        // would not match anyway — but a rule about a hand-written decision must never be
        // reported against a file nobody can edit or suppress in.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // On the declaration rather than on the symbol, because the question is about the
        // chain written inside Define, and a symbol action has no syntax to walk. A flow
        // split across two partial parts is answered by whichever part holds the chain,
        // which is the part whose author is making the decision.
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ClassDeclaration);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not ClassDeclarationSyntax declaration ||
            context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken)
                is not INamedTypeSymbol type)
        {
            return;
        }

        var flowAttribute = type.GetAttributes().FirstOrDefault(
            static a => a.AttributeClass?.ToDisplayString() == FlowAttributeName);

        if (flowAttribute is null)
        {
            return;
        }

        var profile = ProfileThatKeepsNoJournal(flowAttribute);

        if (profile is null)
        {
            return;
        }

        var compensations = CompensationsIn(declaration, context.SemanticModel, context.CancellationToken);

        if (compensations.Count == 0)
        {
            return;
        }

        var reported = LocationOf(declaration, flowAttribute, compensations[0], context.CancellationToken);

        context.ReportDiagnostic(Diagnostic.Create(
            FlowXDiagnostics.CompensationIsNotDurable,
            reported,

            // Every compensation site the report is not already sitting on. The primary
            // location is the decision and these are the evidence for it, so an IDE can
            // offer "go to next location" instead of the reader searching the chain — and
            // a saga with nine of them is still one entry in the build log.
            compensations
                .Select(static c => c.GetLocation())
                .Where(location => location != reported),
            type.Name,
            Cited(compensations),
            profile));
    }

    /// <summary>
    /// The flow's profile when nothing on it is journaled, or <see langword="null"/> when
    /// there is nothing to report.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A value the enum does not name — <c>(ExecutionProfile)7</c>, which C# permits — is
    /// reported rather than skipped, and this is the one place where this rule and
    /// <see cref="ExecutionProfileAnalyzer"/> deliberately disagree. That rule stays silent
    /// there because it asks "which profile is unimplemented", and an unnamed value is not
    /// an answer to that question. This one asks "does this flow journal", and an unnamed
    /// value is a perfectly clear <em>no</em>: <c>ExecutionProfiles.Journals</c> answers
    /// <c>false</c> for a name this build does not know, which is the direction that leaves
    /// the compensation stack in memory and this rule speaking about it.
    /// </para>
    /// <para>
    /// The name is read back off the enum symbol first and then put to the same predicate the
    /// generator and the emitter ask, so the three cannot drift: a profile that starts
    /// journaling is silenced here on the day it is added to that one method rather than on
    /// the day somebody remembers this file.
    /// </para>
    /// </remarks>
    private static string? ProfileThatKeepsNoJournal(AttributeData flowAttribute)
    {
        foreach (var argument in flowAttribute.NamedArguments)
        {
            if (argument.Key != ProfileArgument)
            {
                continue;
            }

            if (argument.Value.Value is not int value)
            {
                // The argument is there but the compilation cannot say what it is — a
                // broken reference, an unresolved constant. Guessing "Ephemeral" would put
                // a profile the author never wrote into the message.
                return null;
            }

            var declared = NameOf(argument.Value, value);

            return ExecutionProfiles.Journals(declared) ? null : declared;
        }

        return DefaultProfileName;
    }

    /// <summary>The enum member's name, or the cast a reader would have to have written.</summary>
    private static string NameOf(TypedConstant declared, int value) =>
        (declared.Type as INamedTypeSymbol)?.GetMembers()
            .OfType<IFieldSymbol>()
            .FirstOrDefault(field =>
                field.HasConstantValue && field.ConstantValue is int declaredValue && declaredValue == value)
            ?.Name
        ?? string.Format(CultureInfo.InvariantCulture, "(ExecutionProfile){0}", value);

    /// <summary>
    /// Every <c>.CompensateWith&lt;T&gt;</c> in the declaration, in source order — the name
    /// node rather than the invocation that contains it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A walk of the whole class rather than of the top level of the chain, because a
    /// compensation declared inside a <c>ForEach</c> body or a <c>When</c> branch is exactly
    /// as lost on a crash as one at the top level — and a rule that only looked at the top
    /// level is a rule that quietly stopped applying the day the DSL grew a nested builder.
    /// FLOWX1017 makes the same argument for the same reason.
    /// </para>
    /// <para>
    /// Resolved semantically, not matched on the name. <c>CompensateWith</c> is an unlikely
    /// collision, but <see cref="PredicatePurityAnalyzer"/> argues the general case at
    /// length — <c>Step</c>, <c>When</c> and <c>Return</c> are words other fluent libraries
    /// use — and a durability rule reasoning about somebody else's builder would be
    /// indefensible whichever word it was.
    /// </para>
    /// <para>
    /// <strong>The name node, and both halves of that are load-bearing.</strong> An
    /// <c>InvocationExpressionSyntax</c> in a fluent chain spans everything from the receiver
    /// onwards, so every call in one chain shares a start position and all of them have the
    /// same span start as <c>flow</c> — an ordering by that is a no-op, and a location built
    /// from it underlines the whole chain up to the call. The name is one token long, is
    /// unique per call, and orders correctly. This was found by an assertion on the message
    /// text, which cited the author's <em>last</em> compensation as though it were the first,
    /// because a descendant walk yields the outermost invocation first; an assertion on the
    /// diagnostic id alone would have passed.
    /// </para>
    /// </remarks>
    private static List<SimpleNameSyntax> CompensationsIn(
        ClassDeclarationSyntax declaration,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var found = new List<SimpleNameSyntax>();

        foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (invocation.Expression is MemberAccessExpressionSyntax { Name: { } name } &&
                name.Identifier.ValueText == CompensateWith &&
                IsBuilderCall(invocation, model, cancellationToken))
            {
                found.Add(name);
            }
        }

        found.Sort(static (left, right) => left.SpanStart.CompareTo(right.SpanStart));

        return found;
    }

    /// <summary>Whether the invocation resolves to a method on one of FlowX's builders.</summary>
    /// <remarks>
    /// The suffix rather than a named interface, matching <see cref="DeterminismAnalyzer"/>:
    /// a builder the DSL grows later is covered on the day it is added rather than the day
    /// someone remembers to add its name here.
    /// </remarks>
    private static bool IsBuilderCall(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        CancellationToken cancellationToken) =>
        model.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol method &&
        method.ContainingType is { } container &&
        container.TypeKind == TypeKind.Interface &&
        container.ContainingNamespace?.ToDisplayString() == FlowXNamespace &&
        container.Name.EndsWith(BuilderInterfaceSuffix, StringComparison.Ordinal);

    /// <summary>
    /// What the message quotes back as evidence: the first compensation, and how many others.
    /// </summary>
    /// <remarks>
    /// The author's own text, so the message points at something greppable in the file rather
    /// than at a type name the reader has to go and find. The count matters because it is the
    /// difference between "one step is exposed" and "this is a saga".
    /// </remarks>
    private static string Cited(List<SimpleNameSyntax> compensations) =>
        compensations.Count == 1
            ? compensations[0].ToString()
            : string.Format(
                CultureInfo.InvariantCulture,
                "{0} and {1} more",
                compensations[0].ToString(),
                compensations.Count - 1);

    /// <summary>
    /// The <c>Profile = …</c> argument, falling back to the attribute, then to the first
    /// compensation.
    /// </summary>
    /// <remarks>
    /// The profile is what the reader would edit, so it is the narrowest honest location when
    /// one was written. A flow that named no profile has no such text — the finding is about
    /// an absence — so the report lands on the <c>[Flow]</c> attribute, which is where the
    /// argument would go and where a <c>#pragma</c> covering this flow belongs. The last
    /// fallback covers an attribute that reaches the type through metadata and has no syntax
    /// at all; the compensation is then the only span in the file that is certainly real.
    /// </remarks>
    private static Location LocationOf(
        ClassDeclarationSyntax declaration,
        AttributeData flowAttribute,
        SimpleNameSyntax firstCompensation,
        CancellationToken cancellationToken)
    {
        var syntax = flowAttribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken) as AttributeSyntax;

        if (syntax is null || !declaration.Span.Contains(syntax.Span))
        {
            // The attribute is on the other part of a partial flow. Reporting into a file
            // this walk did not read would put the squiggle where the evidence is not.
            return firstCompensation.GetLocation();
        }

        var argument = syntax.ArgumentList?.Arguments
            .FirstOrDefault(static a => a.NameEquals?.Name.Identifier.ValueText == ProfileArgument);

        return argument?.GetLocation() ?? syntax.GetLocation();
    }
}
