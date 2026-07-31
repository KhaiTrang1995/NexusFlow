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
/// Checks that the branches of a <c>Parallel</c> write disjoint context slots: FLOWX1013.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/06-Execution-Engine.md</c> §9 has said since before anything could declare a
/// fork that parallel branches "write to <strong>disjoint</strong> context slots — enforced
/// at compile time (<c>FLOWX1013</c>) so parallel steps cannot race on shared state".
/// Until this analyzer existed the id was reserved and the sentence was a promise the
/// compiler was not keeping.
/// </para>
/// <para>
/// <strong>What a "slot" is.</strong> The flow's context is a bag keyed by CLR type: the
/// generated dispatcher ends every capability step with <c>ctx.Set(result.Value)</c>, typed
/// at that capability's declared output contract. So the slot a step writes <em>is</em> its
/// capability's <c>TOut</c>, and two branches writing the same slot means two capabilities
/// in different branches declaring the same output type. Whichever finishes last wins, and
/// the step after the join reads a value chosen by the thread pool.
/// </para>
/// <para>
/// <strong>Why the runtime cannot do this instead.</strong> Both writes are legal, both
/// succeed, and the dictionary is guarded — there is no corruption to detect and no
/// exception to raise. A runtime check would have to compare the two values and decide
/// whether they disagree, which is a business question. The only place the conflict is
/// visible as a conflict is where the branches are declared.
/// </para>
/// <para>
/// <strong>What it proves, and what it does not.</strong> The mechanism is sound as far as
/// it goes: every <c>.Step&lt;T&gt;()</c> and <c>.Branch&lt;T&gt;()</c> inside a branch is
/// resolved to a symbol, its <c>ICapability&lt;,&gt;</c> implementation is read, and the
/// output type is compared by fully-qualified name. Nothing is guessed and nothing is
/// matched on a name. But the model of "what a branch writes" is a model, and it is
/// incomplete in ways worth stating rather than implying:
/// </para>
/// <list type="bullet">
/// <item>
/// <strong>A capability that calls <c>ctx.Set&lt;T&gt;()</c> in its own body is invisible.</strong>
/// <c>FlowContext.Set</c> is public, so a capability can write any slot it likes and this
/// rule will not see it. This is the largest gap and it is the same one
/// <see cref="PredicatePurityAnalyzer"/> has: closing it needs an effects attribute or a
/// whole-program analysis, not a longer list.
/// </item>
/// <item>
/// <strong>Only exact type identity is a conflict.</strong> The state bag is keyed by
/// <c>typeof(T)</c> at the static type, so two branches producing <c>RetailQuote</c> and
/// <c>Quote</c> do not collide even if one derives from the other — and correctly so, since
/// they occupy different keys.
/// </item>
/// <item>
/// <strong>Duplicate writes <em>within</em> one branch are not reported.</strong> Two steps
/// in sequence writing the same slot is an overwrite the author can see in the order they
/// wrote; there is no race and no ambiguity about which one wins.
/// </item>
/// <item>
/// <strong>A branch whose body is not a lambda is skipped.</strong> A method group, or a
/// variable holding an <c>Action&lt;IFlowBuilder&lt;,&gt;&gt;</c>, has no body at the call
/// site — the same limit <c>FlowChainWalker</c> already documents, and for the same reason.
/// </item>
/// <item>
/// <strong>Anything that does not bind is skipped.</strong> An unresolved capability means
/// the file does not compile, and that message is better than this one.
/// </item>
/// <item>
/// <strong>Sub-flows are not followed, and no longer need to be.</strong> A branch may
/// contain a <c>.SubFlow&lt;T, TIn&gt;(...)</c>, and the child writes nothing into
/// <em>this</em> flow's context — it runs on its own context, from its own input, and its
/// result does not come back. So the silence here is sound rather than convenient; it does
/// rest on that property, so propagating a child's output into its parent's bag would mean
/// following the edge.
/// </item>
/// </list>
/// <para>
/// The gaps all fall the same way — <em>silent where it cannot prove a conflict</em>, never
/// reporting on a guess. A rule about concurrency that fired on a correct flow would be
/// suppressed at the top of the file, and would then protect nothing.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ParallelSlotAnalyzer : DiagnosticAnalyzer
{
    private const string FlowAttribute = "FlowX.FlowAttribute";
    private const string FlowBuilderMetadataName = "IFlowBuilder`2";
    private const string ParallelBuilderMetadataName = "IParallelBuilder`2";
    private const string FlowXNamespace = "FlowX";
    private const string ParallelMethodName = "Parallel";
    private const string BranchMethodName = "Branch";
    private const string StepMethodName = "Step";
    private const string CapabilityInterface = "ICapability`2";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(FlowXDiagnostics.ParallelBranchesMustWriteDisjointSlots);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            return;
        }

        // The generated partial declares no fork of its own, so it has nothing to say
        // here; excluding it explicitly keeps the analyzer off code it did not write,
        // matching PredicatePurityAnalyzer and CapabilityAnalyzer.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // Per invocation rather than per flow class. A Parallel nests inside a branch of
        // another one, or inside a `When` block, to any depth; registering on the call
        // itself reaches all of them without re-implementing the chain walk.
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not InvocationExpressionSyntax invocation ||
            !IsBuilderParallel(invocation, context.SemanticModel, context.CancellationToken))
        {
            return;
        }

        var flow = EnclosingFlow(invocation, context.SemanticModel, context.CancellationToken);

        if (flow is null)
        {
            // A Parallel outside a [Flow] class — a helper that composes a builder, say.
            // The message names a flow, and inventing one would make the diagnostic lie.
            return;
        }

        var branches = Branches(invocation);

        if (branches.Count < 2)
        {
            // One branch cannot race with anything, and the generator lays it out inline.
            return;
        }

        // Declaration order, because that is what the message names and what the manifest's
        // `branches` array is indexed by. A dictionary keyed by slot would report the pair
        // in whatever order the hash fell out, which changes the message between builds.
        var writers = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var index = 0; index < branches.Count; index++)
        {
            foreach (var slot in SlotsWrittenBy(branches[index], context.SemanticModel, context.CancellationToken))
            {
                if (writers.TryGetValue(slot, out var earlier))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        FlowXDiagnostics.ParallelBranchesMustWriteDisjointSlots,
                        BranchLocation(branches[index]),
                        earlier,
                        index,
                        flow.Name,
                        slot));

                    continue;
                }

                writers.Add(slot, index);
            }
        }
    }

    /// <summary>The <c>.Branch(...)</c> calls inside a <c>Parallel</c>'s first argument, in declaration order.</summary>
    /// <remarks>
    /// Read off the syntax of the branches lambda rather than through
    /// <c>FlowChainWalker</c>, because an analyzer must not depend on the generator's
    /// walker being loaded — and because a descendant search reaches a branch written
    /// across several statements, which the fluent walker deliberately does not.
    /// </remarks>
    private static List<InvocationExpressionSyntax> Branches(InvocationExpressionSyntax parallel)
    {
        var branches = new List<InvocationExpressionSyntax>();
        var arguments = parallel.ArgumentList.Arguments;

        if (arguments.Count == 0 ||
            arguments[0].Expression is not LambdaExpressionSyntax lambda)
        {
            return branches;
        }

        foreach (var node in lambda.DescendantNodes())
        {
            if (node is InvocationExpressionSyntax candidate &&
                candidate.Expression is MemberAccessExpressionSyntax member &&
                member.Name.Identifier.ValueText == BranchMethodName &&
                // A nested Parallel's own branches belong to that fork, not to this one.
                NearestEnclosingBranchesLambda(candidate) == lambda)
            {
                branches.Add(candidate);
            }
        }

        // A fluent chain nests inside-out, so a descendant walk finds the last call first.
        branches.Reverse();
        return branches;
    }

    /// <summary>
    /// The branches lambda a <c>.Branch(...)</c> call belongs to — its own fork's, not an
    /// enclosing one's.
    /// </summary>
    /// <remarks>
    /// Without this, a <c>Parallel</c> nested inside a branch of another would have its
    /// inner branches counted as branches of the outer fork as well, and the outer fork
    /// would report a conflict between two blocks that never run as siblings.
    /// </remarks>
    private static LambdaExpressionSyntax? NearestEnclosingBranchesLambda(SyntaxNode branch)
    {
        for (var node = branch.Parent; node is not null; node = node.Parent)
        {
            if (node is LambdaExpressionSyntax lambda)
            {
                return lambda;
            }
        }

        return null;
    }

    /// <summary>Where a branch's squiggle lands: the <c>Branch</c> identifier itself.</summary>
    /// <remarks>
    /// The member name, not the invocation. A fluent chain nests its receiver inside every
    /// later call, so an invocation's span begins at the head of the chain — underlining it
    /// would highlight every earlier branch too, and the reader would not know which one
    /// the message meant.
    /// </remarks>
    private static Location BranchLocation(InvocationExpressionSyntax branch) =>
        branch.Expression is MemberAccessExpressionSyntax member
            ? member.Name.GetLocation()
            : branch.GetLocation();

    /// <summary>
    /// Every context slot a branch writes: the output contract of each capability it
    /// invokes, at any nesting depth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole subtree, deliberately. A capability invoked inside a <c>When</c> block
    /// inside a branch still writes its result into the same shared bag, and a rule that
    /// only looked at the branch's top-level chain would miss exactly the flows complex
    /// enough to get this wrong.
    /// </para>
    /// <para>
    /// <strong>A conditional's arms are not treated as exclusive</strong>, even though at
    /// most one of them runs. A branch whose <c>When</c> writes <c>Quote</c> in the
    /// <c>then</c> arm races a sibling that writes <c>Quote</c> unconditionally, and the
    /// race is real on the runs where the predicate holds. Reporting the possibility is
    /// right; a rule that only fired when the collision was certain would be silent on
    /// every intermittent version of this bug, which is the version that reaches
    /// production.
    /// </para>
    /// </remarks>
    private static List<string> SlotsWrittenBy(
        InvocationExpressionSyntax branch,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var slots = new List<string>();

        // `.Branch<TCapability>()` — the shorthand for a one-step branch. Its type argument
        // is on the Branch call itself rather than on a Step inside it.
        AddSlot(slots, CapabilityOn(branch, model, cancellationToken));

        var body = BranchBody(branch);

        if (body is null)
        {
            return slots;
        }

        foreach (var node in body.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (node is InvocationExpressionSyntax invocation &&
                invocation.Expression is MemberAccessExpressionSyntax member &&
                member.Name.Identifier.ValueText is StepMethodName or BranchMethodName)
            {
                AddSlot(slots, CapabilityOn(invocation, model, cancellationToken));
            }
        }

        return slots;
    }

    private static void AddSlot(List<string> slots, string? slot)
    {
        // Within one branch a repeated slot is an overwrite the author can see in the order
        // they wrote, not a race — so it is recorded once and never reported here.
        if (slot != null && !slots.Contains(slot, StringComparer.Ordinal))
        {
            slots.Add(slot);
        }
    }

    /// <summary>The lambda body of a <c>.Branch(b =&gt; …)</c>, or <c>null</c> for the generic form.</summary>
    private static SyntaxNode? BranchBody(InvocationExpressionSyntax branch)
    {
        var arguments = branch.ArgumentList.Arguments;

        if (arguments.Count == 0 || arguments[0].Expression is not LambdaExpressionSyntax lambda)
        {
            return null;
        }

        return (SyntaxNode?)lambda.Block ?? lambda.ExpressionBody;
    }

    /// <summary>
    /// The fully-qualified output contract of the capability named by a builder call's
    /// first type argument, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Read from the <c>ICapability&lt;TIn, TOut&gt;</c> the type implements, because that
    /// is what the generated dispatcher writes into the context — not from the method's own
    /// signature, which knows nothing about contracts. A type implementing the interface
    /// more than once is skipped: FLOWX1015 already reports it, and picking one of two
    /// contracts to guess with would be worse than saying nothing.
    /// </remarks>
    private static string? CapabilityOn(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax member ||
            member.Name is not GenericNameSyntax generic ||
            generic.TypeArgumentList.Arguments.Count == 0)
        {
            return null;
        }

        var symbol = model.GetSymbolInfo(generic.TypeArgumentList.Arguments[0], cancellationToken).Symbol
            as ITypeSymbol;

        if (symbol is null)
        {
            return null;
        }

        var contracts = symbol.AllInterfaces
            .Where(i => i.MetadataName == CapabilityInterface &&
                        i.ContainingNamespace?.ToDisplayString() == FlowXNamespace &&
                        i.TypeArguments.Length == 2)
            .ToList();

        return contracts.Count == 1
            ? contracts[0].TypeArguments[1]
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .Replace("global::", string.Empty)
            : null;
    }

    /// <summary>Whether the invocation is <c>IFlowBuilder&lt;,&gt;.Parallel</c>.</summary>
    /// <remarks>
    /// Resolved semantically rather than matched on the name, for the reason
    /// <see cref="PredicatePurityAnalyzer"/> gives about <c>When</c>: <c>Parallel</c> is a
    /// word other libraries use, and a rule about flow branching that also fired inside
    /// <c>System.Threading.Tasks.Parallel</c> would be indefensible.
    /// </remarks>
    private static bool IsBuilderParallel(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax member ||
            member.Name.Identifier.ValueText != ParallelMethodName)
        {
            return false;
        }

        return model.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol method &&
               method.ContainingType?.MetadataName is FlowBuilderMetadataName or ParallelBuilderMetadataName &&
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
}
