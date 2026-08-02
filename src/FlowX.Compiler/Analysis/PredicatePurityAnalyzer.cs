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
/// Checks that a flow's conditions, selectors and projections decide from the flow's own
/// state: FLOWX1011.
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
/// <strong>The rule was never only about <c>When</c>.</strong> The first version of this
/// analyzer hard-coded one method name, and <c>Switch</c> — which
/// <c>08</c> §3.2 says "obeys the same determinism rule as a <c>When</c> predicate" and
/// which <c>FlowErrors.SelectorFailed</c> restates at run time — arrived immediately
/// afterwards with nothing checking it, putting selectors exactly where predicates had
/// been. <c>06</c> §5 puts routing decisions <em>and</em> the <c>Return</c> projection in
/// the deterministic zone together. So the subject of this rule is
/// <see cref="CoveredDelegates"/>: every <c>IFlowBuilder</c> method that takes a
/// <c>Func&lt;FlowContext&lt;TIn&gt;, …&gt;</c>. A shape the DSL grows later is one row in
/// that table, not another branch in this file — which is the whole reason the table
/// exists rather than a second method name beside the first.
/// </para>
/// <para>
/// <strong>A delegate is listed the day the DSL accepts it, not the day something runs
/// it.</strong> A rule that waits for the emitter arrives after the code it was meant to
/// stop, and checking a lambda that nothing yet executes costs nothing and is already
/// correct on the day something does. <c>ForEach</c>, <c>SubFlow</c> and the mapping of
/// <c>Step&lt;TCapability, TStepIn&gt;(map)</c> were each listed here before
/// <see cref="FlowAnalyzer"/> modelled them; all three are now modelled and compiled, and
/// all three are enforced by the same rows, unchanged.
/// </para>
/// <para>
/// <strong>What "may read the context" means here.</strong> Everything reachable from
/// the delegate's own parameter — the <c>FlowContext&lt;TIn&gt;</c> — is permitted,
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
/// delegate is fine; one declared outside is a capture. A field, property or event of
/// the enclosing type reached through an implicit or explicit <c>this</c> is flow
/// instance state, which is how an injected service reaches a flow lambda. Both are
/// decided from the symbol's declaration rather than from a list, so neither can be
/// evaded by renaming anything.
/// </item>
/// <item>
/// <strong>A catalogue of known-impure statics — not a proof.</strong>
/// <c>DateTime.UtcNow</c>, <c>Guid.NewGuid()</c>, <c>Random.Shared</c>,
/// <c>Environment</c>, <c>Console</c>, <c>File</c> and a short list of neighbours. It
/// recognises what teams actually reach for and nothing else — the same stance, and the
/// same admitted limit, as <see cref="CapabilityAnalyzer"/>'s transport list. A clock
/// that is not on the list is not detected. The catalogue lives in
/// <see cref="AmbientReads"/> rather than in this file, because
/// <see cref="DeterminismAnalyzer"/> asks the same question of a capability body: a clock
/// added there is recognised by both rules on the same day, which is the only way two
/// rules about one subject stay in agreement.
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
/// A delegate that is a method group, or a variable holding a <c>Func&lt;,&gt;</c>, has
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
    private const string ProfileArgument = "Profile";

    /// <summary><c>ExecutionProfile.Durable</c>, as it appears in attribute metadata.</summary>
    private const int DurableProfile = 1;

    /// <summary>
    /// Every <c>IFlowBuilder</c> delegate the determinism rule covers, one row each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole dispatch. Adding a shape the DSL grows later is a row here and
    /// nothing else, because the alternative is what this rule already lived through once:
    /// one hard-coded method name, and a second construct under the identical rule
    /// shipping unchecked beside it.
    /// </para>
    /// <para>
    /// The row is (method name, index of the delegate parameter, what the delegate is
    /// called in the message). The index is resolved against the method's
    /// <em>parameters</em> rather than by counting arguments, so a named argument and an
    /// omitted optional one both land on the right lambda; a method name appearing twice
    /// is a method with two delegates, and both get checked.
    /// </para>
    /// <para>
    /// What is deliberately absent: <c>Otherwise</c>, <c>Case</c>, <c>Default</c>,
    /// <c>Branch</c> and <c>Parallel</c> take an <c>Action&lt;IFlowBuilder&gt;</c>, which
    /// is a block of the flow rather than an expression evaluated during it. Nothing is
    /// lost by skipping them — every builder call written inside such a block is its own
    /// invocation and is analysed on its own.
    /// </para>
    /// </remarks>
    private static readonly CoveredDelegate[] CoveredDelegates =
    {
        // The branch decision. 08 §3.1, and the rule this analyzer was written for.
        new CoveredDelegate("When", 0, "condition"),

        // "The selector obeys the same determinism rule as a When predicate" — 08 §3.2,
        // written before anything checked it.
        new CoveredDelegate("Switch", 0, "Switch selector"),

        // Routing again: which collection is iterated decides how many times the body runs.
        new CoveredDelegate("ForEach", 0, "ForEach selector"),

        // And a third time, with the worst consequence of the three. A poll's `until` decides
        // whether the loop is over, and it is asked again on every resume — so one that reads
        // the ambient clock answers differently on the node that picks the instance up than it
        // did on the one that parked it, and the flow either walks past a poll that never
        // succeeded or polls for ever. Named by parameter, so `until:` written in any position
        // is the argument this reads.
        new CoveredDelegate("PollUntil", 0, "PollUntil condition"),

        // 06 §5 puts "Projection / Return expression" inside the deterministic zone by name.
        new CoveredDelegate("Return", 0, "Return projection"),

        // An event whose payload is a clock read is a payload that differs on replay.
        new CoveredDelegate("Emit", 0, "Emit projection"),
        new CoveredDelegate("EmitOnFailure", 0, "EmitOnFailure projection"),

        // Step inputs are the thing the replay contract in 06 §5 requires to be
        // byte-identical, which is exactly what an ambient read in the mapping breaks.
        new CoveredDelegate("Step", 0, "Step input mapping"),
        new CoveredDelegate("SubFlow", 0, "SubFlow input mapping"),
    };

    /// <summary>
    /// <see cref="CoveredDelegates"/> indexed by method name, so an invocation of
    /// something else is rejected on a hash lookup before the semantic model is asked
    /// anything. This action runs on every invocation in the compilation.
    /// </summary>
    private static readonly ILookup<string, CoveredDelegate> CoveredByName =
        CoveredDelegates.ToLookup(static site => site.MethodName, StringComparer.Ordinal);

    /// <summary>One builder delegate this rule covers.</summary>
    private readonly struct CoveredDelegate
    {
        public CoveredDelegate(string methodName, int parameterIndex, string noun)
        {
            MethodName = methodName;
            ParameterIndex = parameterIndex;
            Noun = noun;
        }

        /// <summary>Matched against <c>IFlowBuilder&lt;,&gt;</c>, never against the identifier alone.</summary>
        public string MethodName { get; }

        /// <summary>Which parameter of the method holds the delegate.</summary>
        public int ParameterIndex { get; }

        /// <summary>
        /// What a reader should see this construct called. Reaches the message verbatim,
        /// and is pluralised there with a trailing <c>s</c> — "the condition in flow 'X'
        /// reads …; conditions may read only …". A reader who sees the wrong noun stops
        /// believing the diagnostic, so "condition" is not reused for a projection.
        /// </summary>
        public string Noun { get; }
    }

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

        // The generated partial declares no flow lambda, so it has nothing to say here;
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
            invocation.Expression is not MemberAccessExpressionSyntax member)
        {
            return;
        }

        var name = member.Name.Identifier.ValueText;

        if (!CoveredByName.Contains(name))
        {
            return;
        }

        var method = BuilderMethod(invocation, context.SemanticModel, context.CancellationToken);

        if (method is null)
        {
            return;
        }

        var flow = EnclosingFlow(invocation, context.SemanticModel, context.CancellationToken);

        if (flow is null)
        {
            // A builder call outside a [Flow] class — a helper that composes a chain, say.
            // The message names a flow, and inventing one would make the diagnostic lie.
            return;
        }

        var severity = IsDurable(flow) ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;

        foreach (var site in CoveredByName[name])
        {
            // The other arguments are branch bodies, options and case values: a builder
            // chain or a constant, and none of this rule's business.
            if (ArgumentFor(invocation, method, site.ParameterIndex) is not LambdaExpressionSyntax lambda)
            {
                continue;
            }

            foreach (var read in ImpureReads(lambda, context.SemanticModel, context.CancellationToken))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    FlowXDiagnostics.PredicateMustBePure,
                    read.Node.GetLocation(),
                    severity,
                    additionalLocations: null,
                    properties: null,
                    site.Noun,
                    flow.Name,
                    read.Node.ToString(),
                    AmbientReads.Phrase(read.Impurity, site.Noun)));
            }
        }
    }

    /// <summary>
    /// The argument bound to the method's parameter at <paramref name="parameterIndex"/>.
    /// </summary>
    /// <remarks>
    /// Not simply the argument at that position. <c>.Return(projection: ctx =&gt; …)</c> is
    /// legal C#, and a rule that missed it would be a rule with a spelling that turns it
    /// off. Positional arguments must precede named ones, so an argument with no
    /// <c>NameColon</c> is at its own index by definition. Returns <c>null</c> when the
    /// parameter is not supplied — the no-argument <c>Step&lt;TCapability&gt;()</c>
    /// overload reaches here and simply has no mapping to check.
    /// </remarks>
    private static ExpressionSyntax? ArgumentFor(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        int parameterIndex)
    {
        if (parameterIndex >= method.Parameters.Length)
        {
            return null;
        }

        var parameter = method.Parameters[parameterIndex].Name;
        var arguments = invocation.ArgumentList.Arguments;

        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];

            if (argument.NameColon is null)
            {
                if (i == parameterIndex)
                {
                    return argument.Expression;
                }
            }
            else if (argument.NameColon.Name.Identifier.ValueText == parameter)
            {
                return argument.Expression;
            }
        }

        return null;
    }

    /// <summary>Every read inside the delegate the rule can show is not the flow's own state.</summary>
    private static IEnumerable<(SyntaxNode Node, Impurity Impurity)> ImpureReads(
        LambdaExpressionSyntax lambda,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var body = (SyntaxNode?)lambda.Block ?? lambda.ExpressionBody;

        if (body is null)
        {
            yield break;
        }

        foreach (var node in body.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // nameof(x) reads the name and never the value — the one construct that
            // provably touches nothing.
            if (AmbientReads.IsInsideNameOf(node, body))
            {
                continue;
            }

            if (node is ObjectCreationExpressionSyntax creation)
            {
                if (AmbientReads.Constructed(creation, model, cancellationToken) is { } constructed)
                {
                    yield return constructed;
                }

                continue;
            }

            if (!AmbientReads.IsAccessRoot(node))
            {
                continue;
            }

            var found = Classify(node, lambda, model, cancellationToken);

            if (found is not null)
            {
                yield return found.Value;
            }
        }
    }

    /// <summary>Decides one access root, returning <c>null</c> when it is permitted or unknowable.</summary>
    private static (SyntaxNode Node, Impurity Impurity)? Classify(
        SyntaxNode root,
        LambdaExpressionSyntax lambda,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        // `this.X` / `base.X`. The flow instance is not the flow's state, and an injected
        // service reaches a flow lambda exactly this way.
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
                return AmbientReads.StaticMemberOn(root, model, cancellationToken);

            case ILocalSymbol local:
                return local.IsConst || IsDeclaredInside(local, lambda)
                    ? null
                    : Read(root, Impurity.CapturedVariable);

            case IParameterSymbol:
            case IRangeVariableSymbol:
                // Includes the delegate's own context parameter, whose declaration is
                // part of the lambda node.
                return IsDeclaredInside(symbol, lambda) ? null : Read(root, Impurity.CapturedVariable);

            default:
                // A member named with no receiver: either an implicit `this.` or a static
                // of the enclosing type.
                return symbol.IsStatic ? AmbientReads.StaticMember(root, symbol) : Instance(root, symbol);
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

    /// <summary>Whether the symbol is declared within the builder delegate's lambda.</summary>
    /// <remarks>
    /// Nested lambdas count: the item parameter of an <c>.Any(item =&gt; …)</c> inside the
    /// delegate is declared inside the delegate, and treating it as a capture would
    /// report the most natural way to ask a question about a collection.
    /// </remarks>
    private static bool IsDeclaredInside(ISymbol symbol, LambdaExpressionSyntax lambda)
    {
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            if (reference.SyntaxTree == lambda.SyntaxTree && lambda.Span.Contains(reference.Span))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The invocation as a method on <c>IFlowBuilder&lt;,&gt;</c>, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Resolved semantically rather than matched on the name, and the table has made that
    /// matter more rather than less: <c>When</c>, <c>Switch</c>, <c>Step</c> and
    /// <c>Return</c> are all words other fluent libraries use, and a determinism rule
    /// firing inside a mocking framework's <c>When</c> or a query builder's <c>Switch</c>
    /// would be indefensible. <c>IStepBuilder</c>, <c>IConditionalBuilder</c> and
    /// <c>ISwitchBuilder</c> all extend the interface, so a call on any of them resolves
    /// to the same declaring type and needs no row of its own.
    /// </remarks>
    private static IMethodSymbol? BuilderMethod(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        CancellationToken cancellationToken) =>
        model.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol method &&
        method.ContainingType?.MetadataName == FlowBuilderMetadataName &&
        method.ContainingType.ContainingNamespace?.ToDisplayString() == FlowXNamespace
            ? method
            : null;

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

    private static (SyntaxNode Node, Impurity Impurity)? Read(SyntaxNode node, Impurity impurity) => (node, impurity);
}
