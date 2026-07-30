using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowX.Compiler.CodeFixes;

/// <summary>
/// FLOWX1001 — adds the <c>partial</c> modifier to a flow declaration.
/// </summary>
/// <remarks>
/// <para>
/// The one diagnostic in the set whose repair is complete, single-valued and carries no
/// design decision: the generator emits a second part of the class, so the class must be
/// partial, and there is no other way to satisfy it. <c>docs/diagnostics/FLOWX1001.md</c>
/// says suppression is never legitimate, which is the same statement from the other side.
/// </para>
/// <para>
/// Batch fixing is offered because the edit is local to one declaration and cannot
/// interact with the next one — the case that makes Fix All dangerous elsewhere.
/// </para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(FlowMustBePartialCodeFixProvider))]
[Shared]
public sealed class FlowMustBePartialCodeFixProvider : CodeFixProvider
{
    /// <summary>
    /// The id is a literal rather than a reference to the descriptor.
    /// </summary>
    /// <remarks>
    /// This assembly deliberately does not link against FlowX.Compiler — see the csproj
    /// for why, and <c>CodeFixFitnessTests</c> for what stops the two copies drifting.
    /// </remarks>
    private const string DiagnosticId = "FLOWX1001";

    private const string Title = "Add the 'partial' modifier";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(DiagnosticId);

    /// <inheritdoc />
    public override FixAllProvider? GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

        if (root is null)
        {
            return;
        }

        foreach (var diagnostic in context.Diagnostics)
        {
            // The diagnostic points at the class identifier, so the declaration is an
            // ancestor of the token there rather than the node the span covers.
            var declaration = root.FindToken(diagnostic.Location.SourceSpan.Start)
                .Parent?.FirstAncestorOrSelf<ClassDeclarationSyntax>();

            if (declaration is null || declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    Title,
                    cancellationToken => AddPartialAsync(context.Document, declaration, cancellationToken),
                    equivalenceKey: DiagnosticId),
                diagnostic);
        }
    }

    private static async Task<Document> AddPartialAsync(
        Document document,
        ClassDeclarationSyntax declaration,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

        return root is null
            ? document
            : document.WithSyntaxRoot(root.ReplaceNode(declaration, WithPartial(declaration)));
    }

    /// <summary>Inserts <c>partial</c> as the last modifier, which is where C# requires it.</summary>
    /// <remarks>
    /// The no-modifier case is not hypothetical — an internal flow is written
    /// <c>class OrderFlow</c> — and it is the case that goes wrong: the indentation in
    /// front of <c>class</c> is that token's leading trivia, so inserting a modifier
    /// without moving it produces <c>partialclass</c> against the margin.
    /// </remarks>
    private static ClassDeclarationSyntax WithPartial(ClassDeclarationSyntax declaration)
    {
        var partial = SyntaxFactory.Token(SyntaxKind.PartialKeyword);

        if (declaration.Modifiers.Count > 0)
        {
            return declaration.WithModifiers(
                declaration.Modifiers.Add(partial.WithTrailingTrivia(SyntaxFactory.Space)));
        }

        var keyword = declaration.Keyword;

        return declaration
            .WithKeyword(keyword.WithLeadingTrivia(SyntaxFactory.TriviaList()))
            .WithModifiers(SyntaxFactory.TokenList(
                partial.WithLeadingTrivia(keyword.LeadingTrivia).WithTrailingTrivia(SyntaxFactory.Space)));
    }
}
