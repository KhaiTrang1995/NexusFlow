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
/// Checks that a <c>When</c> condition decides from the flow's own state: FLOWX1011.
/// </summary>
/// <remarks>
/// <para>
/// A branch is the only place a flow makes a decision, and the decision has to be a
/// function of what the flow knows. <c>docs/08-Flow-Definition.md</c> §3.1 has said so
/// since before anything could declare a predicate — "conditions may read only
/// <c>ctx.State</c>, <c>ctx.Input</c> and prior step results" — and
/// <c>FlowErrors.PredicateFailed</c> repeats it at run time, categorised
/// <c>Internal</c> because "a predicate is pure by construction". WP-15 shipped
/// <c>When</c>/<c>Otherwise</c>; until this analyzer existed the rule was stated in two
/// places and enforced in none.
/// </para>
/// <para>
/// <strong>What "may read the context" means here.</strong> Everything reachable from
/// the predicate's own parameter — the <c>FlowContext&lt;TIn&gt;</c> — is permitted,
/// including <c>ctx.UtcNow</c>, <c>ctx.NewId()</c> and <c>ctx.Random</c>. That is not a
/// hole in the rule, it is the point of it: <c>docs/06-Execution-Engine.md</c> §5 puts
/// the clock, identifiers and randomness in the non-deterministic zone that is
/// <em>journaled and never replayed</em>, and the context is the seam that makes them
/// reproducible. Reporting <c>ctx.UtcNow</c> would report the documented remedy, which is
/// how a build gate ends up suppressed at the top of every flow file.
/// </para>
/// <para>
/// <strong>Severity depends on the flow's profile.</strong> An error under
/// <c>Durable</c>, where a replay must take the branch it took the first time (ADR-0003,
/// and the table in <c>06</c> §5); a warning under <c>Ephemeral</c>, where there is no
/// replay and so no divergence — but where the flow is one attribute away from being
/// replayed, and where the run-time error already treats an impure predicate as a defect
/// under either profile. The documentation originally said <em>Info</em> for the
/// ephemeral case. Info is invisible in a build log, and <c>Ephemeral</c> is the only
/// profile the runtime executes today, so that setting would have shipped a rule that
/// does nothing anywhere — the exact "documented but unenforced" state this analyzer was
/// written to end.
/// </para>
/// <para>
/// <strong>How impurity is decided, and how much of it is a proof.</strong> Two
/// mechanisms, only one of which is sound:
/// </para>
/// <list type="number">
/// <item>
/// <strong>Scope — a proof.</strong> Every read is traced to the root of its access
/// chain. A root that is a local, parameter or range variable declared inside the
/// predicate is fine; one declared outside is a capture. A field, property or event of
/// the enclosing type reached through an implicit or explicit <c>this</c> is flow
/// instance state, which is how an injected service reaches a predicate. Both are
/// decided from the symbol's declaration rather than from a list, so neither can be
/// evaded by renaming anything.
/// </item>
/// <item>
/// <strong>A catalogue of known-impure statics — not a proof.</strong>
/// <c>DateTime.UtcNow</c>, <c>Guid.NewGuid()</c>, <c>Random.Shared</c>,
/// <c>Environment</c>, <c>Console</c>, <c>File</c> and a short list of neighbours. It
/// recognises what teams actually reach for and nothing else — the same stance, and the
/// same admitted limit, as <see cref="CapabilityAnalyzer"/>'s transport list. A clock
/// that is not on the list is not detected.
/// </item>
/// </list>
/// <para>
/// <strong>What it cannot catch, stated rather than implied.</strong> An unresolvable
/// call is not evidence of innocence; but for a rule that stops a build, reporting one
/// would be a guess, and a gate that fires on legitimate code is a gate people suppress:
/// </para>
/// <list type="bullet">
/// <item>
/// <strong>Nothing is interprocedural.</strong>
/// <c>ctx.Get&lt;Order&gt;().IsStillOpen()</c> is accepted, and its body may read a
/// clock. This is by far the largest gap, and closing it needs a purity attribute or a
/// whole-program analysis, not a longer list. A static or instance <em>method</em> of
/// the flow itself is accepted for the same reason.
/// </item>
/// <item>
/// A predicate that is a method group, or a variable holding a <c>Func&lt;,&gt;</c>, has
/// no lambda body at the call site and is skipped entirely.
/// </item>
/// <item>
/// A <c>static readonly</c> field is treated as constant. It is only shallowly so: a
/// <c>static readonly List&lt;T&gt;</c> is mutable state this rule permits.
/// </item>
/// <item>
/// A get-only static property is permitted. A static helper's getter is far more often a
/// constant than an ambient singleton, and reporting every one of them would fire on
/// valid code. A settable static property is reported.
/// </item>
/// <item>
/// Reflection, <c>dynamic</c>, and anything the semantic model cannot bind are skipped.
/// An unresolved symbol means the file does not compile, and the developer already has a
/// better message than this one.
/// </item>
/// </list>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PredicatePurityAnalyzer : DiagnosticAnalyzer
{
    private const string FlowAttribute = "FlowX.FlowAttribute";
    private const string FlowBuilderMetadataName = "IFlowBuilder`2";
    private const string FlowXNamespace = "FlowX";
    private const string WhenMethodName = "When";
    private const string ProfileArgument = "Profile";
    private const string NameOfOperator = "nameof";

    /// <summary><c>ExecutionProfile.Durable</c>, as it appears in attribute metadata.</summary>
    private const int DurableProfile = 1;

    /// <summary>Why a read is not permitted. The wording reaches the message verbatim.</summary>
    private enum Impurity
    {
        Clock,
        Randomness,
        Environment,
        InputOutput,
        MutableStatic,
        CapturedVariable,
        FlowInstanceState,
    }

    /// <summary>
    /// Types every member of which is impure.
    /// </summary>
    /// <remarks>
    /// A list, not a proof — see the class remarks. It only ever applies to a
    /// <em>static</em> access, one whose chain is rooted at the type name.
    /// <c>ctx.Random.Next()</c> is rooted at the context parameter and is permitted,
    /// which is the distinction the whole rule is about.
    /// </remarks>
    private static readonly Dictionary<string, Impurity> ImpureTypes = new Dictionary<string, Impurity>(StringComparer.Ordinal)
    {
        ["System.Random"] = Impurity.Randomness,
        ["System.Security.Cryptography.RandomNumberGenerator"] = Impurity.Randomness,
        ["System.Environment"] = Impurity.Environment,
        ["System.AppContext"] = Impurity.Environment,
        ["System.AppDomain"] = Impurity.Environment,
        ["System.Diagnostics.Process"] = Impurity.Environment,
        ["System.Threading.Thread"] = Impurity.Environment,
        ["System.Diagnostics.Stopwatch"] = Impurity.Clock,
        ["System.TimeProvider"] = Impurity.Clock,
        ["System.Console"] = Impurity.InputOutput,
        ["System.IO.File"] = Impurity.InputOutput,
        ["System.IO.Directory"] = Impurity.InputOutput,
        ["System.IO.FileInfo"] = Impurity.InputOutput,
        ["System.IO.DirectoryInfo"] = Impurity.InputOutput,
        ["System.Net.Dns"] = Impurity.InputOutput,
        ["System.Net.Http.HttpClient"] = Impurity.InputOutput,
    };

    /// <summary>Individual members whose containing type is otherwise perfectly usable.</summary>
    /// <remarks>
    /// <c>DateTime</c> is why this list exists separately from <see cref="ImpureTypes"/>:
    /// comparing two dates is the most ordinary thing a condition can do, and only the
    /// ambient entry points are the problem.
    /// </remarks>
    private static readonly Dictionary<string, Impurity> ImpureMembers = new Dictionary<string, Impurity>(StringComparer.Ordinal)
    {
        ["System.DateTime.Now"] = Impurity.Clock,
        ["System.DateTime.UtcNow"] = Impurity.Clock,
        ["System.DateTime.Today"] = Impurity.Clock,
        ["System.DateTimeOffset.Now"] = Impurity.Clock,
        ["System.DateTimeOffset.UtcNow"] = Impurity.Clock,
        ["System.Guid.NewGuid"] = Impurity.Randomness,
    };

    /// <summary>Namespace-qualified, without <c>global::</c> and without type arguments.</summary>
    private static readonly SymbolDisplayFormat CatalogueFormat = new SymbolDisplayFormat(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(FlowXDiagnostics.PredicateMustBePure);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            return;
        }

        // The generated partial declares no predicate, so it has nothing to say here;
        // excluding it explicitly keeps the analyzer off code it did not write, matching
        // StepBindingAnalyzer and CapabilityAnalyzer.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // Per invocation rather than per flow class. A When nests inside another branch's
        // block to any depth, and registering on the call itself reaches all of them
        // without re-implementing the chain walk for blocks.
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not InvocationExpressionSyntax invocation ||
            !IsBuilderWhen(invocation, context.SemanticModel, context.CancellationToken))
        {
            return;
        }

        // Argument 0 is the predicate. Argument 1 is the branch body, which is a builder
        // chain and none of this rule's business.
        if (invocation.ArgumentList.Arguments.Count == 0 ||
            invocation.ArgumentList.Arguments[0].Expression is not LambdaExpressionSyntax predicate)
        {
            return;
        }

        var flow = EnclosingFlow(invocation, context.SemanticModel, context.CancellationToken);

        if (flow is null)
        {
            // A When outside a [Flow] class — a helper that composes a builder, say. The
            // message names a flow, and inventing one would make the diagnostic lie.
            return;
        }

        var severity = IsDurable(flow) ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;

        foreach (var read in ImpureReads(predicate, context.SemanticModel, context.CancellationToken))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                FlowXDiagnostics.PredicateMustBePure,
                read.Node.GetLocation(),
                severity,
                additionalLocations: null,
                properties: null,
                flow.Name,
                read.Node.ToString(),
                Phrase(read.Impurity)));
        }
    }

    /// <summary>Every read inside the predicate the rule can show is not the flow's own state.</summary>
    private static IEnumerable<(SyntaxNode Node, Impurity Impurity)> ImpureReads(
        LambdaExpressionSyntax predicate,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var body = (SyntaxNode?)predicate.Block ?? predicate.ExpressionBody;

        if (body is null)
        {
            yield break;
        }

        foreach (var node in body.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // nameof(x) reads the name and never the value — the one construct that
            // provably touches nothing.
            if (IsInsideNameOf(node, body))
            {
                continue;
            }

            if (node is ObjectCreationExpressionSyntax creation)
            {
                var constructed = model.GetSymbolInfo(creation, cancellationToken).Symbol is IMethodSymbol constructor
                    ? Lookup(ImpureTypes, Name(constructor.ContainingType))
                    : null;

                if (constructed is not null)
                {
                    yield return (creation, constructed.Value);
                }

                continue;
            }

            if (!IsAccessRoot(node))
            {
                continue;
            }

            var found = Classify(node, predicate, model, cancellationToken);

            if (found is not null)
            {
                yield return found.Value;
            }
        }
    }

    /// <summary>
    /// Whether this node begins an access chain rather than continuing one.
    /// </summary>
    /// <remarks>
    /// The distinction the rule rests on. In <c>ctx.Get&lt;Order&gt;().Total</c> only
    /// <c>ctx</c> is a root; <c>Get</c> and <c>Total</c> are members of a value that came
    /// from it, and classifying them independently would report every ordinary property
    /// read on a step result.
    /// </remarks>
    private static bool IsAccessRoot(SyntaxNode node)
    {
        if (node is ThisExpressionSyntax || node is BaseExpressionSyntax)
        {
            return true;
        }

        if (node is not SimpleNameSyntax)
        {
            return false;
        }

        switch (node.Parent)
        {
            case MemberAccessExpressionSyntax member:
                return member.Expression == node;
            case QualifiedNameSyntax qualified:
                return qualified.Left == node;
            case MemberBindingExpressionSyntax:
            case AliasQualifiedNameSyntax:
            case NameColonSyntax:
            case NameEqualsSyntax:
                return false;
            default:
                return true;
        }
    }

    /// <summary>Decides one access root, returning <c>null</c> when it is permitted or unknowable.</summary>
    private static (SyntaxNode Node, Impurity Impurity)? Classify(
        SyntaxNode root,
        LambdaExpressionSyntax predicate,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        // `this.X` / `base.X`. The flow instance is not the flow's state, and an injected
        // service reaches a predicate exactly this way.
        if (root is ThisExpressionSyntax || root is BaseExpressionSyntax)
        {
            return root.Parent is MemberAccessExpressionSyntax access && access.Expression == root
                ? Instance(access, model.GetSymbolInfo(access.Name, cancellationToken).Symbol)
                : Read(root, Impurity.FlowInstanceState);
        }

        var symbol = model.GetSymbolInfo(root, cancellationToken).Symbol;

        if (symbol is null)
        {
            // Unresolved: the file does not compile and this rule has nothing to add.
            return null;
        }

        switch (symbol)
        {
            case INamespaceOrTypeSymbol:
                // A type name is not a read. What matters is the static member, if any,
                // reached through it.
                return StaticMemberOn(root, model, cancellationToken);

            case ILocalSymbol local:
                return local.IsConst || IsDeclaredInside(local, predicate)
                    ? null
                    : Read(root, Impurity.CapturedVariable);

            case IParameterSymbol:
            case IRangeVariableSymbol:
                // Includes the predicate's own context parameter, whose declaration is
                // part of the lambda node.
                return IsDeclaredInside(symbol, predicate) ? null : Read(root, Impurity.CapturedVariable);

            default:
                // A member named with no receiver: either an implicit `this.` or a static
                // of the enclosing type.
                return symbol.IsStatic ? StaticMember(root, symbol) : Instance(root, symbol);
        }
    }

    /// <summary>
    /// An instance member reached through an implicit or explicit <c>this</c>.
    /// </summary>
    /// <remarks>
    /// Fields, properties and events are reported: reading one is provably a read of
    /// state that lives outside the context. A <em>method</em> is not, for the same
    /// reason nothing here is interprocedural — its body might touch nothing at all, and
    /// a static helper on the same class is already accepted. Reporting only the instance
    /// ones would be an inconsistency the developer would have to guess at.
    /// </remarks>
    private static (SyntaxNode Node, Impurity Impurity)? Instance(SyntaxNode node, ISymbol? symbol) =>
        symbol is null || symbol is IMethodSymbol ? null : Read(node, Impurity.FlowInstanceState);

    /// <summary>
    /// Walks up a chain of namespace and type names to the first real member.
    /// </summary>
    /// <remarks>
    /// <c>System.DateTime.UtcNow</c> parses as two nested member accesses whose root
    /// identifier is a namespace, so the classification has to climb rather than look at
    /// one node. A chain that never reaches a member — a <c>typeof</c>, a type argument,
    /// a pattern's type — is permitted.
    /// </remarks>
    private static (SyntaxNode Node, Impurity Impurity)? StaticMemberOn(
        SyntaxNode root,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var current = root;

        while (current.Parent is MemberAccessExpressionSyntax member && member.Expression == current)
        {
            var symbol = model.GetSymbolInfo(member.Name, cancellationToken).Symbol;

            if (symbol is null)
            {
                return null;
            }

            if (symbol is INamespaceOrTypeSymbol)
            {
                current = member;
                continue;
            }

            return StaticMember(member, symbol);
        }

        return null;
    }

    /// <summary>Decides a static member: catalogue first, then mutability.</summary>
    private static (SyntaxNode Node, Impurity Impurity)? StaticMember(SyntaxNode node, ISymbol symbol)
    {
        var container = symbol.ContainingType;

        if (container is null)
        {
            return null;
        }

        // A constant or an enum member is a literal wearing a name.
        if (symbol is IFieldSymbol { IsConst: true } || container.TypeKind == TypeKind.Enum)
        {
            return null;
        }

        var containerName = Name(container);
        var member = Lookup(ImpureMembers, containerName + "." + symbol.Name)
            ?? Lookup(ImpureTypes, containerName);

        if (member is not null)
        {
            return Read(node, member.Value);
        }

        // Not on the list, so the remaining question is whether the symbol is state that
        // something else can change between two runs of the same flow.
        switch (symbol)
        {
            case IFieldSymbol { IsReadOnly: false }:
            case IPropertySymbol { SetMethod: not null }:
                return Read(node, Impurity.MutableStatic);
            default:
                return null;
        }
    }

    /// <summary>Whether the symbol is declared within the predicate lambda.</summary>
    /// <remarks>
    /// Nested lambdas count: the item parameter of an <c>.Any(item =&gt; …)</c> inside the
    /// predicate is declared inside the predicate, and treating it as a capture would
    /// report the most natural way to ask a question about a collection.
    /// </remarks>
    private static bool IsDeclaredInside(ISymbol symbol, LambdaExpressionSyntax predicate)
    {
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            if (reference.SyntaxTree == predicate.SyntaxTree && predicate.Span.Contains(reference.Span))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the invocation is <c>IFlowBuilder&lt;,&gt;.When</c>.</summary>
    /// <remarks>
    /// Resolved semantically rather than matched on the name. <c>When</c> is a word other
    /// fluent libraries use, and a rule about flow branching that also fired inside a
    /// mocking framework's <c>When</c> would be indefensible.
    /// </remarks>
    private static bool IsBuilderWhen(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax member ||
            member.Name.Identifier.ValueText != WhenMethodName)
        {
            return false;
        }

        return model.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol method &&
               method.ContainingType?.MetadataName == FlowBuilderMetadataName &&
               method.ContainingType.ContainingNamespace?.ToDisplayString() == FlowXNamespace;
    }

    /// <summary>The <c>[Flow]</c>-attributed type this node is written in, or <c>null</c>.</summary>
    private static INamedTypeSymbol? EnclosingFlow(
        SyntaxNode node,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        for (var declaration = node.FirstAncestorOrSelf<ClassDeclarationSyntax>();
             declaration is not null;
             declaration = declaration.Parent?.FirstAncestorOrSelf<ClassDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(declaration, cancellationToken) is INamedTypeSymbol type &&
                type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == FlowAttribute))
            {
                return type;
            }
        }

        return null;
    }

    /// <summary>Whether the flow declares <c>Profile = ExecutionProfile.Durable</c>.</summary>
    /// <remarks>
    /// The attribute's default is <c>Ephemeral</c> — ADR-0003, durability is opted into —
    /// so an omitted argument correctly reads as the lower severity.
    /// </remarks>
    private static bool IsDurable(INamedTypeSymbol flow)
    {
        foreach (var attribute in flow.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != FlowAttribute)
            {
                continue;
            }

            foreach (var argument in attribute.NamedArguments)
            {
                if (argument.Key == ProfileArgument && argument.Value.Value is int profile)
                {
                    return profile == DurableProfile;
                }
            }
        }

        return false;
    }

    /// <summary>Whether the node sits inside a <c>nameof</c> within the predicate.</summary>
    private static bool IsInsideNameOf(SyntaxNode node, SyntaxNode body)
    {
        for (var current = node; current is not null && current != body.Parent; current = current.Parent)
        {
            if (current is InvocationExpressionSyntax invocation &&
                invocation.Expression is IdentifierNameSyntax name &&
                name.Identifier.ValueText == NameOfOperator)
            {
                return true;
            }
        }

        return false;
    }

    private static (SyntaxNode Node, Impurity Impurity)? Read(SyntaxNode node, Impurity impurity) => (node, impurity);

    private static Impurity? Lookup(Dictionary<string, Impurity> catalogue, string key) =>
        catalogue.TryGetValue(key, out var impurity) ? impurity : (Impurity?)null;

    private static string Name(ITypeSymbol type) =>
        type.OriginalDefinition.ToDisplayString(CatalogueFormat);

    /// <summary>How the reason reads. Written to complete "…, which is {phrase}".</summary>
    private static string Phrase(Impurity impurity)
    {
        switch (impurity)
        {
            case Impurity.Clock:
                return "the system clock";
            case Impurity.Randomness:
                return "a source of randomness or identity";
            case Impurity.Environment:
                return "ambient process or environment state";
            case Impurity.InputOutput:
                return "an external service or I/O";
            case Impurity.MutableStatic:
                return "mutable static state";
            case Impurity.CapturedVariable:
                return "a variable captured from outside the condition";
            default:
                return "state held on the flow instance rather than in the context";
        }
    }
}
