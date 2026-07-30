using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace FlowX.Compiler.CodeFixes;

/// <summary>
/// FLOWX1017 — sets <c>Profile = ExecutionProfile.Durable</c> on a flow that suspends.
/// </summary>
/// <remarks>
/// <para>
/// The third mechanical repair, and the one that needed the most argument. There are two
/// ways to satisfy FLOWX1017 — make the flow durable, or stop suspending — and a fix that
/// picks between two designs is a fix that guesses. This one does not guess: the author
/// wrote <c>AwaitSignal</c>, so the flow suspends, and the profile is then the only thing
/// that is inconsistent with what the source already says. <c>docs/diagnostics/FLOWX1017.md</c>
/// documents exactly this edit as the repair and "None" as the grounds for suppression.
/// </para>
/// <para>
/// The direction matters too. Durable is the safe end of the axis: applying this fix
/// buys a journal row per suspension, while the fix that removed the suspension would
/// silently delete behaviour the author asked for. A quick action may cost you money;
/// it may not lose your work.
/// </para>
/// <para>
/// What it does <em>not</em> do is widen the flow's deadline. The documentation page
/// shows <c>[FlowDeadline("P30D")]</c> alongside the profile because a flow that waits
/// for a human needs longer than thirty seconds — but the right value is a business
/// decision, and inventing one would be the guess this fix otherwise avoids.
/// </para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AwaitSignalRequiresDurableCodeFixProvider))]
[Shared]
public sealed class AwaitSignalRequiresDurableCodeFixProvider : CodeFixProvider
{
    private const string DiagnosticId = "FLOWX1017";
    private const string FlowAttribute = "FlowX.FlowAttribute";
    private const string ProfileArgument = "Profile";
    private const string Title = "Set Profile = ExecutionProfile.Durable";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(DiagnosticId);

    /// <inheritdoc />
    public override FixAllProvider? GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);

        if (root is null || semanticModel is null)
        {
            return;
        }

        foreach (var diagnostic in context.Diagnostics)
        {
            var declaration = root.FindToken(diagnostic.Location.SourceSpan.Start)
                .Parent?.FirstAncestorOrSelf<ClassDeclarationSyntax>();

            if (declaration is null)
            {
                continue;
            }

            var attribute = FindFlowAttribute(declaration, semanticModel, context.CancellationToken);

            if (attribute is null)
            {
                continue;
            }

            var span = attribute.Span;

            context.RegisterCodeFix(
                CodeAction.Create(
                    Title,
                    cancellationToken => MakeDurableAsync(context.Document, span, cancellationToken),
                    equivalenceKey: DiagnosticId),
                diagnostic);
        }
    }

    /// <summary>
    /// The <c>[Flow]</c> attribute, resolved semantically rather than by name.
    /// </summary>
    /// <remarks>
    /// Matching on the text <c>Flow</c> would also match a user's own <c>[Flow]</c> in
    /// another namespace and, worse, would miss <c>[FlowX.Flow(...)]</c> written out in a
    /// file with no <c>using</c>.
    /// </remarks>
    private static AttributeSyntax? FindFlowAttribute(
        ClassDeclarationSyntax declaration,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var attribute in declaration.AttributeLists.SelectMany(static list => list.Attributes))
        {
            var attributeType = semanticModel.GetSymbolInfo(attribute, cancellationToken).Symbol?.ContainingType;

            if (attributeType?.ToDisplayString() == FlowAttribute)
            {
                return attribute;
            }
        }

        return null;
    }

    private static async Task<Document> MakeDurableAsync(
        Document document,
        TextSpan span,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

        var attribute = root?.FindNode(span, getInnermostNodeForTie: true)
            ?.FirstAncestorOrSelf<AttributeSyntax>();

        if (root is null || attribute is null)
        {
            return document;
        }

        var updated = AttributeEditor.WithNamedArgument(
            attribute,
            ProfileArgument,
            AttributeEditor.QualifiedMemberAccess("FlowX.ExecutionProfile.Durable"));

        return document.WithSyntaxRoot(root.ReplaceNode(attribute, updated));
    }
}
