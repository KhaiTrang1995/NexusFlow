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
/// FLOWX1010 — declares an authorisation stance on a capability.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no default here, so this fix does not supply one.</strong> The
/// obvious implementation writes <c>Authorization.Public</c> and clears the error in one
/// keystroke, which would make the capability world-readable — precisely the outcome
/// FLOWX1010 exists to prevent. A diagnostic that says "there is no permissive default"
/// and a quick action that quietly picks the most permissive value is worse than no
/// quick action: the developer sees an error, presses the lightbulb, and ships an
/// unauthenticated endpoint believing the compiler approved it.
/// </para>
/// <para>
/// So two stances are offered and the developer picks one. Both are restrictive and both
/// are complete in themselves.
/// </para>
/// <para>
/// <c>Permission</c> and <c>Policy</c> are deliberately absent even though they are the
/// commonest real answers. Each needs a name — <c>Permission = "payment.write"</c> — that
/// nothing in the source implies, and writing the stance without the name produces a
/// declaration that reads as enforced and names nothing to enforce. Nothing rejects that
/// combination today, so the wrong code would compile, pass, and reach the manifest as a
/// claim about access control. A developer who wants a permission types one word more
/// than the fix would have saved.
/// </para>
/// <para>
/// <c>Public</c> is absent for the same reason plus one: it requires an
/// <c>[ApprovedBy]</c> naming a reviewer and a date, and a tool cannot author a review.
/// </para>
/// <para>
/// <strong>The fix edits a different file from the one the lightbulb appeared in.</strong>
/// FLOWX1010 is reported at the <c>.Step&lt;T&gt;()</c> that pulls the capability into a
/// flow, because that is where the generator resolves it, but the stance belongs on the
/// capability's own declaration. Each action names the capability type so the developer
/// can see which file is about to change.
/// </para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(CapabilityAuthorizationCodeFixProvider))]
[Shared]
public sealed class CapabilityAuthorizationCodeFixProvider : CodeFixProvider
{
    private const string DiagnosticId = "FLOWX1010";
    private const string CapabilityAttribute = "FlowX.CapabilityAttribute";
    private const string AuthorizationArgument = "Authorization";

    /// <summary>
    /// The stances a tool may choose on the developer's behalf, in the order they are
    /// offered. Restrictive first; nothing here is permissive and nothing here needs a
    /// value the source does not contain.
    /// </summary>
    private static readonly string[] OfferedStances = ["Authenticated", "Internal"];

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(DiagnosticId);

    /// <summary>No Fix All. See the remarks.</summary>
    /// <remarks>
    /// Batch fixing would answer a security question once and apply the answer to every
    /// capability in the solution, which is the same mistake as a permissive default
    /// wearing a different hat. It is also unsound mechanically: one capability used by
    /// three flows produces three diagnostics whose fixes all rewrite the same
    /// declaration, and the batch fixer resolves that by discarding all but one.
    /// </remarks>
    public override FixAllProvider? GetFixAllProvider() => null;

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
            var type = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
                ?.FirstAncestorOrSelf<TypeSyntax>();

            if (type is null)
            {
                continue;
            }

            if (semanticModel.GetSymbolInfo(type, context.CancellationToken).Symbol is not INamedTypeSymbol capability)
            {
                continue;
            }

            var target = await FindDeclarationAsync(
                context.Document.Project.Solution,
                capability,
                context.CancellationToken).ConfigureAwait(false);

            // A capability from a referenced assembly has no syntax to edit. Offering
            // nothing is right: the fix is a change to someone else's package.
            if (target is null)
            {
                continue;
            }

            foreach (var stance in OfferedStances)
            {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        $"Declare Authorization.{stance} on '{capability.Name}'",
                        cancellationToken => DeclareStanceAsync(target, stance, cancellationToken),

                        // The key names the stance and not the capability: two actions are
                        // equivalent when they make the same decision, wherever they apply.
                        equivalenceKey: DiagnosticId + ":" + stance),
                    diagnostic);
            }
        }
    }

    /// <summary>
    /// The <c>[Capability]</c> attribute on the capability's own declaration, wherever in
    /// the solution that is.
    /// </summary>
    /// <remarks>
    /// Every declaring reference is examined rather than the first, because a capability
    /// may be partial and the attribute sits on exactly one of its parts.
    /// </remarks>
    private static async Task<AttributeTarget?> FindDeclarationAsync(
        Solution solution,
        INamedTypeSymbol capability,
        CancellationToken cancellationToken)
    {
        foreach (var reference in capability.DeclaringSyntaxReferences)
        {
            var document = solution.GetDocument(reference.SyntaxTree);

            if (document is null)
            {
                continue;
            }

            var node = await reference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);

            if (node is not ClassDeclarationSyntax declaration)
            {
                continue;
            }

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);

            if (semanticModel is null)
            {
                continue;
            }

            foreach (var attribute in declaration.AttributeLists.SelectMany(static list => list.Attributes))
            {
                var attributeType = semanticModel.GetSymbolInfo(attribute, cancellationToken).Symbol?.ContainingType;

                if (attributeType?.ToDisplayString() != CapabilityAttribute)
                {
                    continue;
                }

                // Already declared: the diagnostic is stale, or something else raised it.
                // Offering a fix that overwrites a deliberate stance is not acceptable.
                var declared = attribute.ArgumentList?.Arguments
                    .Any(a => a.NameEquals?.Name.Identifier.ValueText == AuthorizationArgument) == true;

                if (!declared)
                {
                    return new AttributeTarget(document, attribute.Span);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Writes the stance onto the capability's attribute and returns the whole solution,
    /// because the document changed is usually not the one the fix was invoked from.
    /// </summary>
    private static async Task<Solution> DeclareStanceAsync(
        AttributeTarget target,
        string stance,
        CancellationToken cancellationToken)
    {
        var solution = target.Document.Project.Solution;
        var root = await target.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

        // Located by span rather than by node identity: the action runs later than the
        // registration, against whatever root the document has by then.
        var attribute = root?.FindNode(target.Span, getInnermostNodeForTie: true)
            ?.FirstAncestorOrSelf<AttributeSyntax>();

        if (root is null || attribute is null)
        {
            return solution;
        }

        var updated = AttributeEditor.WithNamedArgument(
            attribute,
            AuthorizationArgument,
            AttributeEditor.QualifiedMemberAccess("FlowX.Authorization." + stance));

        return solution.WithDocumentSyntaxRoot(target.Document.Id, root.ReplaceNode(attribute, updated));
    }

    /// <summary>Which document holds the <c>[Capability]</c> attribute, and where in it.</summary>
    private sealed class AttributeTarget
    {
        internal AttributeTarget(Document document, TextSpan span)
        {
            Document = document;
            Span = span;
        }

        internal Document Document { get; }

        internal TextSpan Span { get; }
    }
}
