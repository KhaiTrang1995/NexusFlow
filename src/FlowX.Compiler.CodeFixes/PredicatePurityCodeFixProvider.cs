using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace FlowX.Compiler.CodeFixes;

/// <summary>
/// FLOWX1011 — moves an ambient clock, identifier or random source onto the flow context.
/// </summary>
/// <remarks>
/// <para>
/// The fourth mechanical repair, and the narrowest. FLOWX1011 reports six different kinds
/// of impurity and only one of them has a repair a compiler can write: a read of the
/// ambient clock, identity or randomness has an exact counterpart on
/// <c>FlowContext</c>, which the journal reproduces on replay
/// (<c>docs/06-Execution-Engine.md</c> §5). The other kinds — a captured variable, an
/// injected service, mutable static state — need a decision about where the value should
/// come from, usually a new step, and a quick action that guessed at that would be
/// rewriting the flow's graph on the developer's behalf.
/// </para>
/// <para>
/// <strong>It follows the diagnostic wherever the diagnostic goes.</strong> FLOWX1011
/// covers every <c>IFlowBuilder</c> delegate that takes the flow context — the
/// <c>Switch</c> selector, the <c>Return</c> projection, the <c>Emit</c> maps, the step
/// input mappings — and the rewrite is identical at all of them, because the parameter
/// being rewritten onto is the same <c>FlowContext&lt;TIn&gt;</c> in each. Nothing here
/// enumerates those methods; it asks only whether the enclosing call is on the builder,
/// so a construct the analyzer learns next is fixable on the day it is reported.
/// </para>
/// <para>
/// <strong>Every rewrite is type-exact, which is the bar for offering it at all.</strong>
/// <c>DateTimeOffset.UtcNow</c> and <c>ctx.UtcNow</c> are the same type;
/// <c>Guid.NewGuid()</c> and <c>ctx.NewId()</c> are the same type; <c>Random.Shared</c>
/// and <c>ctx.Random</c> are the same type. <c>DateTime.UtcNow</c> is rewritten to
/// <c>ctx.UtcNow.UtcDateTime</c> rather than to <c>ctx.UtcNow</c>, because the shorter
/// form would not compile against a <c>DateTime</c> comparison and a fix that does not
/// compile is worse than no fix.
/// </para>
/// <para>
/// <strong>What is deliberately not offered.</strong> <c>DateTime.Now</c>,
/// <c>DateTime.Today</c> and <c>DateTimeOffset.Now</c> are local time. Every context
/// counterpart is UTC, so the rewrite would change which branch is taken either side of
/// midnight — silently, and only in some time zones. That is a business decision, so the
/// diagnostic stands and the developer makes it.
/// </para>
/// <para>
/// Under the <c>Ephemeral</c> profile the rewrite changes nothing observable today: there
/// is no journal in this release, so <c>ctx.UtcNow</c> reads the same clock by another
/// name. What it changes is which seam the read goes through, and that seam is the one
/// P2's journal will record. Being honest about this is why the fix is worth having and
/// also why it is not described as making the flow deterministic.
/// </para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(PredicatePurityCodeFixProvider))]
[Shared]
public sealed class PredicatePurityCodeFixProvider : CodeFixProvider
{
    private const string DiagnosticId = "FLOWX1011";
    private const string FlowBuilderMetadataName = "IFlowBuilder`2";
    private const string FlowXNamespace = "FlowX";
    private const string Discard = "_";

    /// <summary>
    /// The ambient reads with an exact counterpart on the context, keyed by
    /// <c>&lt;containing type&gt;.&lt;member&gt;</c>.
    /// </summary>
    private static readonly Dictionary<string, string> ContextCounterparts = new Dictionary<string, string>(System.StringComparer.Ordinal)
    {
        ["System.DateTimeOffset.UtcNow"] = "UtcNow",
        ["System.DateTime.UtcNow"] = "UtcNow.UtcDateTime",
        ["System.Guid.NewGuid"] = "NewId",
        ["System.Random.Shared"] = "Random",
    };

    private static readonly SymbolDisplayFormat TypeNameFormat = new SymbolDisplayFormat(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(DiagnosticId);

    /// <inheritdoc />
    public override FixAllProvider? GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);

        if (root is null || model is null)
        {
            return;
        }

        foreach (var diagnostic in context.Diagnostics)
        {
            // Re-read the tree rather than trusting the span. A diagnostic outlives the
            // edit that resolved it, and the IDE keeps offering yesterday's squiggle
            // until analysis catches up.
            var offending = root.FindNode(diagnostic.Location.SourceSpan) as MemberAccessExpressionSyntax;

            if (offending is null)
            {
                continue;
            }

            var counterpart = Counterpart(offending, model, context.CancellationToken);
            var parameter = PredicateParameterOf(offending, model, context.CancellationToken);

            if (counterpart is null || parameter is null)
            {
                continue;
            }

            var replacement = parameter + "." + counterpart;

            context.RegisterCodeFix(
                CodeAction.Create(
                    "Read it from the flow context: " + replacement,
                    _ => Task.FromResult(Rewrite(context.Document, root, offending, replacement)),
                    equivalenceKey: nameof(PredicatePurityCodeFixProvider) + ":" + counterpart),
                diagnostic);
        }
    }

    private static Document Rewrite(
        Document document,
        SyntaxNode root,
        MemberAccessExpressionSyntax offending,
        string replacement) =>
        document.WithSyntaxRoot(root.ReplaceNode(
            offending,
            SyntaxFactory.ParseExpression(replacement)
                .WithTriviaFrom(offending)
                .WithAdditionalAnnotations(Formatter.Annotation)));

    /// <summary>The context member this ambient read maps to, or <c>null</c> when there is none.</summary>
    private static string? Counterpart(
        MemberAccessExpressionSyntax offending,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var symbol = model.GetSymbolInfo(offending, cancellationToken).Symbol;

        if (symbol?.ContainingType is null)
        {
            return null;
        }

        var key = symbol.ContainingType.OriginalDefinition.ToDisplayString(TypeNameFormat) + "." + symbol.Name;

        return ContextCounterparts.TryGetValue(key, out var counterpart) ? counterpart : null;
    }

    /// <summary>
    /// The name of the context parameter of the builder delegate this node sits in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <em>outermost</em> lambda inside the builder call, not the nearest one: a read
    /// inside <c>.Any(line =&gt; …)</c> must still be rewritten onto the context, and
    /// naming <c>line</c> would produce something that does not compile. The walk stops at
    /// the first enclosing <c>IFlowBuilder&lt;,&gt;</c> call, so the outermost lambda
    /// <em>of that call</em> is what is named — which is what makes a <c>.Return(…)</c>
    /// nested inside a <c>.When(…)</c> branch name its own parameter rather than the
    /// branch's.
    /// </para>
    /// <para>
    /// It holds for a projection exactly as it did for a predicate, and for the same
    /// reason: what matters is that the lambda's parameter <em>is</em> the
    /// <c>FlowContext&lt;TIn&gt;</c>, which is true of every delegate FLOWX1011 covers —
    /// they differ only in what they return. So this deliberately does not repeat the
    /// analyzer's table of covered methods. It cannot: the two live in separate assemblies
    /// with no reference between them, and a copied table is a table that drifts. Asking
    /// only "is the enclosing call on the flow builder" is both sufficient and immune to
    /// the analyzer growing a row.
    /// </para>
    /// <para>
    /// Returns <c>null</c> for a discarded parameter, which cannot be referenced at all.
    /// </para>
    /// </remarks>
    private static string? PredicateParameterOf(
        SyntaxNode node,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is not InvocationExpressionSyntax invocation || !IsBuilderCall(invocation, model, cancellationToken))
            {
                continue;
            }

            var lambda = ContextLambdaAround(invocation, node);

            if (lambda is null)
            {
                return null;
            }

            var name = ParameterName(lambda);

            return name == Discard ? null : name;
        }

        return null;
    }

    /// <summary>The builder call's own argument lambda containing <paramref name="node"/>.</summary>
    /// <remarks>
    /// By argument rather than by index, so a named argument and a delegate that is not
    /// the first parameter both resolve — the analyzer reports against a parameter
    /// position it looks up on the method symbol, and hard-coding <c>0</c> here would make
    /// the fix disappear exactly where the diagnostic still appears.
    /// </remarks>
    private static LambdaExpressionSyntax? ContextLambdaAround(InvocationExpressionSyntax invocation, SyntaxNode node)
    {
        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (argument.Expression is LambdaExpressionSyntax lambda && lambda.Span.Contains(node.Span))
            {
                return lambda;
            }
        }

        return null;
    }

    private static string? ParameterName(LambdaExpressionSyntax lambda)
    {
        switch (lambda)
        {
            case SimpleLambdaExpressionSyntax simple:
                return simple.Parameter.Identifier.ValueText;
            case ParenthesizedLambdaExpressionSyntax parenthesized when parenthesized.ParameterList.Parameters.Count == 1:
                return parenthesized.ParameterList.Parameters[0].Identifier.ValueText;
            default:
                return null;
        }
    }

    /// <summary>Whether the invocation is any method declared on <c>IFlowBuilder&lt;,&gt;</c>.</summary>
    /// <remarks>
    /// Resolved semantically, never by identifier: another library's <c>When</c> or
    /// <c>Switch</c> must not attract a quick action that rewrites its lambda onto a
    /// context it does not have.
    /// </remarks>
    private static bool IsBuilderCall(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        CancellationToken cancellationToken) =>
        invocation.Expression is MemberAccessExpressionSyntax &&
        model.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol method &&
        method.ContainingType?.MetadataName == FlowBuilderMetadataName &&
        method.ContainingType.ContainingNamespace?.ToDisplayString() == FlowXNamespace;
}
