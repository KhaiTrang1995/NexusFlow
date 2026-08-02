using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using FlowX.Compiler.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FlowX.Compiler.Analysis;

/// <summary>
/// Reports an agent tool that asks nobody about a consequence it declares: FLOWX1046.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The rule joins two declarations that cannot see each other.</strong>
/// <c>[AgentTrigger(Confirmation = ConfirmationMode.Never)]</c> is on the flow;
/// <c>[Capability(SideEffects = [...])]</c> is on each capability the flow's chain reaches,
/// usually in another file and often owned by another team. Neither author can read the other's
/// decision, and the consequence of the pair — a tool descriptor publishing
/// <c>confirmationRequired: false</c> for a call that moves money — appears in neither file.
/// </para>
/// <para>
/// <strong>The side effects are read from the chain, not from the flow's attributes</strong>, for
/// <see cref="CompensationDurabilityAnalyzer"/>'s reason: the question is about what was written
/// inside <c>Define</c>, and a symbol action has no syntax to walk. A flow split across two
/// partial parts is answered by whichever part holds the chain, which is the part whose author is
/// making the decision.
/// </para>
/// <para>
/// <strong>Only <c>.Step&lt;T&gt;</c>, deliberately — not <c>.CompensateWith&lt;T&gt;</c>.</strong>
/// The set counted here is the set <c>McpToolCatalog</c> projects into the descriptor, which reads
/// the manifest's <c>step.capability</c> and not its <c>step.compensation</c>. A rule that counted
/// more than the descriptor publishes would report on a tool whose <c>confirmationRequired</c> the
/// author cannot change by any edit to the trigger, because the effect it named never reaches the
/// annotation. If that projection widens, this widens with it and the two stay one answer.
/// </para>
/// <para>
/// <strong>The chain is walked whole, including nested builders.</strong> A step inside a
/// <c>When</c> branch or a <c>ForEach</c> body is a step this tool can run, and the descriptor's
/// own walk descends into branches for exactly that reason — a confirmation requirement computed
/// from the top level alone would be wrong for the flow that has a refund behind an
/// <c>Otherwise</c>.
/// </para>
/// <para>
/// <strong>Silent on a flow whose capabilities the compilation cannot resolve.</strong> A capability
/// in a referenced assembly whose attribute this build cannot read contributes nothing, and the
/// rule then reports nothing rather than reporting "no side effects" as though that were a fact.
/// The rule fires on evidence of a consequence, never on the absence of evidence.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AgentConfirmationAnalyzer : DiagnosticAnalyzer
{
    private const string FlowAttributeName = "FlowX.FlowAttribute";
    private const string AgentTriggerAttributeName = "FlowX.AgentTriggerAttribute";
    private const string CapabilityAttributeName = "FlowX.CapabilityAttribute";
    private const string ConfirmationArgument = "Confirmation";
    private const string SideEffectsArgument = "SideEffects";
    private const string StepMethod = "Step";
    private const string FlowXNamespace = "FlowX";
    private const string BuilderInterfaceSuffix = "Builder";

    /// <summary>
    /// <c>ConfirmationMode.Never</c>, as it appears in attribute metadata.
    /// </summary>
    /// <remarks>
    /// The only value that trips this rule, so it is the only one worth naming. Compared as the
    /// underlying value because that is all attribute data carries, and spelled out here rather
    /// than derived for <c>TriggerReader.ManifestKindName</c>'s reason: reordering the enum is a
    /// breaking change the compiler cannot see, and it should surface as a failing test rather
    /// than as a rule that quietly stops firing.
    /// </remarks>
    private const int NeverConfirmation = 0;

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(FlowXDiagnostics.AgentToolDeclaresNoConfirmation);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            return;
        }

        // The generated plan is the other part of this partial class and carries the steps as
        // ExecutionPlan fields rather than as builder calls. A rule about a hand-written decision
        // must never be reported against a file nobody can edit or suppress in.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

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

        var attributes = type.GetAttributes();

        if (!attributes.Any(static a => a.AttributeClass?.ToDisplayString() == FlowAttributeName))
        {
            return;
        }

        var trigger = attributes.FirstOrDefault(
            static a => a.AttributeClass?.ToDisplayString() == AgentTriggerAttributeName);

        if (trigger is null || !DeclaresNever(trigger))
        {
            return;
        }

        var effectful = EffectfulSteps(declaration, context.SemanticModel, context.CancellationToken);

        if (effectful.Count == 0)
        {
            return;
        }

        var first = effectful[0];

        context.ReportDiagnostic(Diagnostic.Create(
            FlowXDiagnostics.AgentToolDeclaresNoConfirmation,
            LocationOf(declaration, trigger, first.Site, context.CancellationToken),

            // Every other effectful step, as evidence. The primary location is the decision and
            // these are what it costs, so an IDE offers "go to next location" and a flow with six
            // of them is still one entry in the build log.
            effectful.Skip(1).Select(static step => step.Site.GetLocation()),
            FlowIdOf(type, attributes),
            first.Capability,
            string.Join(", ", first.SideEffects)));
    }

    /// <summary>Whether the trigger declares <c>Confirmation = ConfirmationMode.Never</c>.</summary>
    /// <remarks>
    /// An absent argument is <em>not</em> this value, and the difference is the whole rule.
    /// <c>AgentTriggerAttribute.Confirmation</c> initialises to <c>RequiredForSideEffects</c>, so
    /// a flow that says nothing is asking for the safe behaviour; only a flow that wrote the word
    /// <c>Never</c> has switched it off. An argument the compilation cannot evaluate is treated as
    /// absent, because guessing would put a declaration the author never wrote into the message.
    /// </remarks>
    private static bool DeclaresNever(AttributeData trigger) => trigger.NamedArguments.Any(
        static pair => pair.Key == ConfirmationArgument &&
            pair.Value.Value is int mode &&
            mode == NeverConfirmation);

    /// <summary>
    /// Every <c>.Step&lt;T&gt;</c> in the declaration whose capability declares a side effect, in
    /// source order.
    /// </summary>
    private static List<EffectfulStep> EffectfulSteps(
        ClassDeclarationSyntax declaration, SemanticModel model, CancellationToken cancellationToken)
    {
        var found = new List<EffectfulStep>();

        foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (invocation.Expression is not MemberAccessExpressionSyntax { Name: GenericNameSyntax name } ||
                name.Identifier.ValueText != StepMethod ||
                name.TypeArgumentList.Arguments.Count == 0 ||
                !IsBuilderCall(invocation, model, cancellationToken))
            {
                continue;
            }

            if (model.GetSymbolInfo(name.TypeArgumentList.Arguments[0], cancellationToken).Symbol
                is not INamedTypeSymbol capability)
            {
                continue;
            }

            var effects = SideEffectsOf(capability);

            if (effects.Count > 0)
            {
                found.Add(new EffectfulStep(name, CapabilityIdOf(capability), effects));
            }
        }

        found.Sort(static (left, right) => left.Site.SpanStart.CompareTo(right.Site.SpanStart));

        return found;
    }

    /// <summary>The capability's declared side effects, or an empty list.</summary>
    /// <remarks>
    /// Read from the attribute rather than from <c>CapabilityReader</c>, because that reader wants
    /// a whole model and this wants one array — and because a capability whose attribute this
    /// compilation cannot see yields nothing here, which is the silence the rule's own remarks
    /// promise.
    /// </remarks>
    private static List<string> SideEffectsOf(INamedTypeSymbol capability)
    {
        var effects = new List<string>();

        foreach (var attribute in capability.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != CapabilityAttributeName)
            {
                continue;
            }

            foreach (var argument in attribute.NamedArguments)
            {
                if (argument.Key != SideEffectsArgument || argument.Value.Values.IsDefault)
                {
                    continue;
                }

                effects.AddRange(argument.Value.Values
                    .Select(static value => value.Value as string)
                    .Where(static effect => !string.IsNullOrEmpty(effect))
                    .Select(static effect => effect!));
            }
        }

        return effects;
    }

    /// <summary>The capability's declared id, or its type name when the attribute carries none.</summary>
    private static string CapabilityIdOf(INamedTypeSymbol capability) =>
        capability.GetAttributes()
            .Where(static a => a.AttributeClass?.ToDisplayString() == CapabilityAttributeName)
            .Where(static a => a.ConstructorArguments.Length > 0)
            .Select(static a => a.ConstructorArguments[0].Value as string)
            .FirstOrDefault(static id => !string.IsNullOrEmpty(id)) ?? capability.Name;

    /// <summary>Whether the invocation resolves to a method on one of FlowX's builders.</summary>
    /// <remarks>
    /// <see cref="CompensationDurabilityAnalyzer.IsBuilderCall"/>'s shape and its reason:
    /// <c>Step</c> is a word other fluent libraries use, and a rule about agent safety reasoning
    /// over somebody else's builder would be indefensible.
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

    /// <summary>The flow's declared id, or its type name when the attribute carries none.</summary>
    private static string FlowIdOf(INamedTypeSymbol type, ImmutableArray<AttributeData> attributes) =>
        attributes
            .Where(static a => a.AttributeClass?.ToDisplayString() == FlowAttributeName)
            .Where(static a => a.ConstructorArguments.Length > 0)
            .Select(static a => a.ConstructorArguments[0].Value as string)
            .FirstOrDefault(static id => !string.IsNullOrEmpty(id)) ?? type.Name;

    /// <summary>
    /// The <c>Confirmation = …</c> argument, falling back to the attribute, then to the step.
    /// </summary>
    /// <remarks>
    /// The argument is the text the reader would delete, so it is the narrowest honest location.
    /// The last fallback covers a trigger that reaches the type through metadata or through the
    /// other part of a partial flow, where the step is the only span in this file that is
    /// certainly real.
    /// </remarks>
    private static Location LocationOf(
        ClassDeclarationSyntax declaration,
        AttributeData trigger,
        SimpleNameSyntax firstStep,
        CancellationToken cancellationToken)
    {
        if (trigger.ApplicationSyntaxReference?.GetSyntax(cancellationToken) is not AttributeSyntax syntax ||
            !declaration.Span.Contains(syntax.Span))
        {
            return firstStep.GetLocation();
        }

        var argument = syntax.ArgumentList?.Arguments
            .FirstOrDefault(static a => a.NameEquals?.Name.Identifier.ValueText == ConfirmationArgument);

        return argument?.GetLocation() ?? syntax.GetLocation();
    }

    /// <summary>One step whose capability declares a consequence.</summary>
    private sealed class EffectfulStep
    {
        public EffectfulStep(SimpleNameSyntax site, string capability, List<string> sideEffects)
        {
            Site = site;
            Capability = capability;
            SideEffects = sideEffects;
        }

        /// <summary>The <c>Step</c> name node — one token, unique per call, ordering correctly.</summary>
        public SimpleNameSyntax Site { get; }

        /// <summary>The capability's declared id, for the message.</summary>
        public string Capability { get; }

        /// <summary>What it declared.</summary>
        public List<string> SideEffects { get; }
    }
}
