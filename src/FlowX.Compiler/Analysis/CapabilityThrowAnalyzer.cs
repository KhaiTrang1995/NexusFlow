using System.Collections.Immutable;
using System.Linq;
using FlowX.Compiler.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FlowX.Compiler.Analysis;

/// <summary>
/// Checks that a capability expresses its failures as values: FLOWX1016.
/// </summary>
/// <remarks>
/// <para>
/// <c>07-Capability-Model.md §3</c> rule 2 is two claims in one sentence — "returns
/// <c>Result&lt;TOut&gt;</c>" and "expected failures are values". The first enforces
/// itself: <c>ICapability&lt;TIn, TOut&gt;.ExecuteAsync</c> returns
/// <c>ValueTask&lt;Result&lt;TOut&gt;&gt;</c>, so a capability that does not return a
/// <c>Result</c> does not compile. The second was checked by nothing, and a
/// <c>throw</c> in a capability body is the exact shape ADR-0007 was written against:
/// the outcome disappears from the signature, from the manifest's error catalogue, and
/// from the category-driven retry decision, and the engine reports it as a defect
/// because from the outside it is indistinguishable from one.
/// </para>
/// <para>
/// <strong>What this proves and what it merely guesses.</strong> Containment is a
/// proof: a <c>throw</c> in the syntax of <c>ExecuteAsync</c> — including in a lambda
/// or local function written inside it — is a <c>throw</c> the engine will catch, and
/// no analysis is needed to establish that. <em>Whether the thrown thing is an expected
/// business outcome</em> is not a proof and cannot be one, because "expected" is a
/// statement about a domain. This analyzer decides it from the exception's type against
/// <see cref="DefectSignals"/> — a list, in the same sense as FLOWX1003's transport
/// namespaces — and the list is drawn so the rule errs towards silence.
/// </para>
/// <para>
/// <strong>What it does not see.</strong> Nothing here is interprocedural: a private
/// helper on the same class, an extension method, or an adapter the capability calls may
/// throw anything at all and this says nothing about it. It does not follow a thrown
/// variable — only <c>throw new …</c> is recognised, so <c>throw _cached;</c> and
/// <c>throw Errors.Declined();</c> pass. A bare <c>throw;</c> is a rethrow of something
/// the capability did not create, which ADR-0007 permits as an infrastructure-fault
/// signal, and is not reported.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CapabilityThrowAnalyzer : DiagnosticAnalyzer
{
    private const string CapabilityMetadataName = "ICapability`2";
    private const string FlowXNamespace = "FlowX";
    private const string ExecuteMethodName = "ExecuteAsync";

    /// <summary>
    /// Exception types that signal a defect rather than a business outcome.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A list, not a proof</strong>, and deliberately generous: every entry is a
    /// false negative the rule accepts in order not to fire on code that is right. The
    /// <c>Argument*</c> family is the largest concession — <c>ArgumentException</c> is
    /// both the CA1062 guard clause the engine's own contract requires and the idiomatic
    /// .NET spelling of a validation failure, and there is no way to tell the two apart
    /// from the type. Guards win, because a rule that fires on
    /// <c>ArgumentNullException.ThrowIfNull(input)</c> written out longhand would be
    /// firing on the pattern the analyzers already demand.
    /// </para>
    /// <para>
    /// Matched by exact name, not by base type. A domain exception deriving from
    /// <c>ArgumentException</c> is a domain exception, and inheriting from a guard type
    /// should not buy silence.
    /// </para>
    /// </remarks>
    private static readonly string[] DefectSignals =
    [
        "System.ArgumentException",
        "System.ArgumentNullException",
        "System.ArgumentOutOfRangeException",
        "System.NotImplementedException",
        "System.NotSupportedException",
        "System.ObjectDisposedException",
        "System.OperationCanceledException",
        "System.Threading.Tasks.TaskCanceledException",
        "System.InvalidCastException",
        "System.NullReferenceException",
        "System.IndexOutOfRangeException",
        "System.OutOfMemoryException",
        "System.StackOverflowException",
        "System.PlatformNotSupportedException",
        "System.Diagnostics.UnreachableException",
    ];

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(FlowXDiagnostics.ExpectedFailureIsThrown);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            return;
        }

        // Generated code is ours. The emitted dispatcher throws ArgumentOutOfRangeException
        // on an unreachable step index, which is a defect signal and would be excluded
        // anyway — but a rule about hand-written capabilities has no business reading
        // machine-written source, and saying so is cheaper than relying on the list.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // A syntax action on the method rather than a symbol action on the type: the
        // subject is a statement inside one body, and this registration arrives with the
        // semantic model for that tree already built.
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.MethodDeclaration);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not MethodDeclarationSyntax declaration ||
            declaration.Identifier.ValueText != ExecuteMethodName ||
            context.SemanticModel.GetDeclaredSymbol(declaration) is not IMethodSymbol method ||
            !IsCapabilityExecute(method))
        {
            return;
        }

        var body = (SyntaxNode?)declaration.Body ?? declaration.ExpressionBody;

        if (body is null)
        {
            return;
        }

        foreach (var creation in ThrownConstructions(body))
        {
            if (IsHandledLocally(creation, body))
            {
                continue;
            }

            var thrown = context.SemanticModel.GetTypeInfo(creation).Type;

            if (thrown is null || thrown.TypeKind == TypeKind.Error || IsDefectSignal(thrown))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                FlowXDiagnostics.ExpectedFailureIsThrown,
                creation.GetLocation(),
                method.ContainingType.Name,
                thrown.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }
    }

    /// <summary>
    /// Every <c>new …</c> that is thrown somewhere inside this body.
    /// </summary>
    /// <remarks>
    /// Statements and expressions both, so <c>x ?? throw new …</c> and a switch arm's
    /// <c>_ =&gt; throw new …</c> are seen. Descendants rather than direct children,
    /// because a lambda or a local function declared inside <c>ExecuteAsync</c> still runs
    /// inside the call the engine made — a <c>throw</c> in one leaks exactly as far.
    /// </remarks>
    private static System.Collections.Generic.IEnumerable<ObjectCreationExpressionSyntax> ThrownConstructions(
        SyntaxNode body) =>
        body.DescendantNodes()
            .Select(static node => node switch
            {
                ThrowStatementSyntax statement => statement.Expression,
                ThrowExpressionSyntax expression => expression.Expression,
                _ => null,
            })
            .OfType<ObjectCreationExpressionSyntax>();

    /// <summary>
    /// Whether the throw sits in a <c>try</c> block the capability itself guards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>throw</c> inside a <c>try</c> that has a <c>catch</c> may never leave the
    /// method — the catch may translate it into a <c>Result.Fail</c>, which is the very
    /// thing this rule asks for. The analyzer cannot tell whether it does, so it says
    /// nothing, and the silence is the deliberate direction to be wrong in.
    /// </para>
    /// <para>
    /// The <c>catch</c> and <c>finally</c> clauses are not covered by this: a
    /// <c>throw new …</c> there is a translation of one exception into another, which is
    /// the shape ADR-0007 pushes towards a value, and it is not caught by the <c>try</c>
    /// it belongs to.
    /// </para>
    /// </remarks>
    private static bool IsHandledLocally(SyntaxNode creation, SyntaxNode body)
    {
        for (var node = creation.Parent; node is not null && node != body.Parent; node = node.Parent)
        {
            if (node.Parent is TryStatementSyntax guarded &&
                guarded.Block == node &&
                guarded.Catches.Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDefectSignal(ITypeSymbol thrown) =>
        System.Array.IndexOf(DefectSignals, thrown.ToDisplayString()) >= 0;

    /// <summary>Whether this method is the capability's <c>ExecuteAsync</c> implementation.</summary>
    /// <remarks>
    /// Resolved through the interface rather than matched on the name, so an unrelated
    /// <c>ExecuteAsync</c> overload on the same class is not analysed and an explicit
    /// interface implementation still is.
    /// </remarks>
    private static bool IsCapabilityExecute(IMethodSymbol method)
    {
        foreach (var candidate in method.ContainingType.AllInterfaces)
        {
            if (candidate.MetadataName != CapabilityMetadataName ||
                !IsFlowXNamespace(candidate.ContainingNamespace))
            {
                continue;
            }

            foreach (var member in candidate.GetMembers(ExecuteMethodName))
            {
                if (SymbolEqualityComparer.Default.Equals(
                        method.ContainingType.FindImplementationForInterfaceMember(member),
                        method))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether this is the top-level <c>FlowX</c> namespace.</summary>
    private static bool IsFlowXNamespace(INamespaceSymbol? candidate) =>
        candidate is { Name: FlowXNamespace } && candidate.ContainingNamespace is { IsGlobalNamespace: true };
}
