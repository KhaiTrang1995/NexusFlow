using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace FlowX.Compiler.CodeFixes;

/// <summary>
/// FLOWX1033 — removes a <c>.WithPolicy(...)</c> that reaches no plan node.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Offered for one case, and the narrowness is the whole design.</strong> FLOWX1033
/// has two documented repairs — declare the compensation the step turns out to have, or stop
/// promising one — and choosing between them is choosing between two designs, which is the
/// guess <c>AwaitSignalRequiresDurableCodeFixProvider</c>'s remarks refuse to make. Neither is
/// mechanical in general: the first needs a capability only the author knows, and the second
/// means editing a <c>PolicySet</c> that other steps may legitimately share.
/// </para>
/// <para>
/// <strong>There is exactly one case where no design decision is involved.</strong> When the
/// named set declares <c>CompensationRetry</c> and nothing else, and the step has no
/// compensation, <c>FlowEmitter</c> emits <em>neither</em> half of the policy argument — the
/// forward chain needs a kind that is not the compensation retry, and the compensation chain
/// needs a compensation. The call already compiles to nothing, so deleting it cannot change
/// what the engine executes. That is the fix, and the analyzer says when it applies by
/// setting a property on the report rather than leaving this assembly to work it out again.
/// </para>
/// <para>
/// <strong>What it does change is the manifest, deliberately.</strong>
/// <c>ManifestWriter</c> publishes <c>CompensationRetry</c> for this step today, so applying
/// the fix removes an entry from <c>flowx.manifest.json</c> and <c>flowx diff</c> will report
/// it. The entry being removed is the promise of a retried undo for a step that has no undo —
/// removing a false claim from a published contract is the point of the rule, not a side
/// effect to be apologised for. An author who wanted the entry to be true wants the other
/// repair, which is why this one is offered rather than applied.
/// </para>
/// <para>
/// The direction is safe in the sense that page asks for: applying it deletes a declaration
/// that has no effect, never behaviour. A quick action may cost you a manifest row; it may
/// not lose your work.
/// </para>
/// <para>
/// <strong>Which is why a comment anywhere on the call withdraws the offer.</strong> A
/// comment written between the step and its policy is prose about the step, and it lives in
/// the leading trivia of the <c>.</c> token — inside the node being deleted, not beside it.
/// Carrying it across means rebuilding a trivia list, and every rule for doing that is right
/// for one layout and wrong for the next: a comment re-indented to column zero, or a blank
/// line where the call used to be, is a fix a reviewer has to tidy up after. Declining costs
/// an author two keystrokes; guessing costs them a sentence they wrote.
/// </para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(CompensationRetryHasNoCompensationCodeFixProvider))]
[Shared]
public sealed class CompensationRetryHasNoCompensationCodeFixProvider : CodeFixProvider
{
    private const string DiagnosticId = "FLOWX1033";
    private const string Title = "Remove the .WithPolicy(...) call, which reaches no plan node";

    /// <summary>
    /// The property the analyzer sets when deleting the call is provably behaviour-preserving.
    /// </summary>
    /// <remarks>
    /// A string literal rather than a reference to
    /// <c>DeclaredPolicyAnalyzer.CallReachesNoPlanNodeProperty</c>, for the reason the
    /// diagnostic id beside it is one: this assembly must not link against
    /// <c>FlowX.Compiler</c>, and <c>CodeFixFitnessTests</c> is where the two copies are held
    /// to each other from a project that can see both.
    /// </remarks>
    public const string CallReachesNoPlanNodeProperty = "FlowX.CallReachesNoPlanNode";

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
            if (!ReachesNoPlanNode(diagnostic) ||
                CallAt(root, diagnostic.Location.SourceSpan) is not { } call ||
                CarriesAComment(call))
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    Title,
                    cancellationToken => RemoveAsync(context.Document, call.Span, cancellationToken),
                    equivalenceKey: DiagnosticId),
                diagnostic);
        }
    }

    /// <summary>
    /// Whether the analyzer proved that removing the call changes nothing the engine runs.
    /// </summary>
    /// <remarks>
    /// Absent or anything but <c>True</c> means no fix, rather than a fix applied on a
    /// default. A property bag arrives from whatever version of the analyzer the host loaded,
    /// and offering a destructive edit because a key was missing is how a quick action ends up
    /// deleting a declaration the plan does carry.
    /// </remarks>
    private static bool ReachesNoPlanNode(Diagnostic diagnostic) =>
        diagnostic.Properties.TryGetValue(CallReachesNoPlanNodeProperty, out var reaches) &&
        string.Equals(reaches, bool.TrueString, System.StringComparison.Ordinal);

    /// <summary>
    /// The <c>.WithPolicy(...)</c> invocation the report points into.
    /// </summary>
    /// <remarks>
    /// The diagnostic's span is the <c>WithPolicy</c> identifier, not the invocation — a
    /// fluent chain nests its receiver inside every later call, so the invocation's own span
    /// starts at the head of the chain and an edit keyed on it would be an edit to the whole
    /// of <c>Define</c>. Walking out from the identifier is what makes the location a squiggle
    /// on one call and still an anchor for the right node.
    /// </remarks>
    private static InvocationExpressionSyntax? CallAt(SyntaxNode root, TextSpan span) =>
        root.FindToken(span.Start).Parent
            ?.AncestorsAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(static call => call.Expression is MemberAccessExpressionSyntax);

    private static async Task<Document> RemoveAsync(
        Document document,
        TextSpan span,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

        if (root?.FindNode(span, getInnermostNodeForTie: true) is not InvocationExpressionSyntax call ||
            call.Expression is not MemberAccessExpressionSyntax member)
        {
            return document;
        }

        // The receiver's own trailing trivia is the line break that separated it from the
        // call now being deleted, so keeping it as well would leave trailing whitespace
        // where the call used to be. The call's is what joins the receiver to whatever
        // followed it.
        return document.WithSyntaxRoot(root.ReplaceNode(
            call,
            member.Expression.WithTrailingTrivia(call.GetTrailingTrivia())));
    }

    /// <summary>
    /// Whether any comment would be deleted along with the call.
    /// </summary>
    /// <remarks>
    /// The span checked is wider than the call's own tokens on purpose. A comment written
    /// above a <c>.WithPolicy</c> attaches as leading trivia of the <c>.</c> token, which is
    /// <em>inside</em> the node being removed and would vanish with it; a comment inside the
    /// argument list is the author explaining the set. Either way there is prose here that
    /// this fix cannot re-place, so it withdraws rather than choosing where to put it.
    /// </remarks>
    private static bool CarriesAComment(InvocationExpressionSyntax call) =>
        call.DescendantTrivia(descendIntoTrivia: false).Any(static trivia =>
            trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
            trivia.IsKind(SyntaxKind.MultiLineCommentTrivia));
}
