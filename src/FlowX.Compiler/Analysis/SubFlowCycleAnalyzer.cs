using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using FlowX.Compiler.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FlowX.Compiler.Analysis;

/// <summary>
/// Checks that sub-flow composition forms a DAG: FLOWX1021.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/08-Flow-Definition.md</c> §3.7 has said since before anything could declare a
/// sub-flow that "cycles are a <strong>compile error</strong> (<c>FLOWX1021</c>). The flow
/// graph is a DAG, always." Until this analyzer existed the id was reserved and the sentence
/// was a promise the compiler was not keeping.
/// </para>
/// <para>
/// <strong>Why a cycle is worse than an infinite loop.</strong> Every other repetition the
/// DSL can express is bounded by construction: a target must point forward, a
/// <c>ForEach</c> reads its element count once before it starts. A composition cycle is
/// bounded by nothing. Each level rents a pooled context, opens a compensation scope and
/// shortens the deadline, so the recursion ends either in a stack overflow — which takes
/// the process down rather than failing one flow — or in a deadline breach several levels
/// from the flow that caused it. Neither failure names the mistake.
/// </para>
/// <para>
/// <strong>How it works.</strong> One pass over the compilation builds a directed graph:
/// a node per type carrying <c>[Flow]</c>, and an edge per <c>.SubFlow&lt;TFlow, …&gt;()</c>
/// written in that type's <c>Define</c> chain, resolved through the semantic model. A
/// breadth-first walk over that graph then reports every flow that reaches itself, naming the
/// path — because the useful fact about a cycle is never that there is one, it is which
/// edge to cut.
/// </para>
/// <para>
/// <strong>Reported once per flow on the cycle, at that flow's declaration.</strong> Not
/// once per edge: a three-flow cycle has three edges and reporting all of them would print
/// the same cycle three times with three different arrows, none of which is more wrong than
/// the others. Reporting at the declaration rather than at the <c>SubFlow</c> call is the
/// same choice FLOWX1005 and FLOWX1017 make — the defect is a property of the flow, not of
/// one line in it.
/// </para>
/// <para>
/// <strong>What it proves, and what it cannot.</strong> Within one compilation, for edges
/// written as a direct <c>.SubFlow&lt;T, …&gt;()</c> in a <c>Define</c> chain, the answer is
/// exact: the graph is built from resolved symbols, nothing is matched on a name and nothing
/// is guessed. Outside that, it is silent, and the silences are worth stating rather than
/// implying:
/// </para>
/// <list type="bullet">
/// <item>
/// <strong>An edge into a referenced assembly is not followed.</strong> The child's
/// <c>Define</c> body is not in this compilation — only its compiled plan is, and a plan
/// carries the child's <em>own</em> sub-flow ids but not the symbols to resolve them. So
/// <c>A → B</c> is seen when <c>B</c> is a reference, and <c>B → A</c> is not; the cycle
/// closes and no build reports it. That is the largest gap, and it is why
/// <c>FlowEngine.MaxSubFlowDepth</c> exists: a cycle this analyzer cannot see fails one
/// flow at run time instead of overflowing the stack.
/// </item>
/// <item>
/// <strong>A flow reached through anything but a type argument is invisible.</strong>
/// <c>TFlow</c> is a type parameter, so there is no way to name a flow through a variable
/// today — but a future overload that took one, or a helper method that composed on the
/// caller's behalf, would not be followed.
/// </item>
/// <item>
/// <strong>A <c>Define</c> body that is not a chain is skipped</strong>, which is the same
/// limit <c>FlowChainWalker</c> already documents and for the same reason: a method group or
/// an <c>Action</c> in a variable has no body at the call site.
/// </item>
/// <item>
/// <strong>Anything that does not bind is skipped.</strong> An unresolved flow type means
/// the file does not compile, and that message is better than this one.
/// </item>
/// </list>
/// <para>
/// The gaps all fall the same way — <em>silent where it cannot prove a cycle</em>, never
/// reporting on a guess. A rule that failed a build over a composition that is in fact
/// acyclic would be suppressed at the top of the file and would then protect nothing.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SubFlowCycleAnalyzer : DiagnosticAnalyzer
{
    private const string FlowAttribute = "FlowX.FlowAttribute";
    private const string SubFlowMethodName = "SubFlow";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(FlowXDiagnostics.SubFlowCycle);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            return;
        }

        // The generated partial composes nothing of its own — it holds the plan and the
        // dispatcher — so it has nothing to say here.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // A compilation action, and it has to be. Reachability is not a property of one
        // syntax node or one symbol: `A → B → C → A` is invisible from any of the three
        // declarations on its own, and an analyzer registered per flow would either miss it
        // or rebuild the whole graph once per flow.
        context.RegisterCompilationStartAction(static start =>
        {
            var builder = new GraphBuilder();

            start.RegisterSyntaxNodeAction(
                context => builder.Add(context), SyntaxKind.ClassDeclaration);

            start.RegisterCompilationEndAction(builder.Report);
        });
    }

    /// <summary>Collects the composition edges, then walks them once at the end.</summary>
    /// <remarks>
    /// Two phases rather than one, because a cycle can only be seen once every edge is
    /// known. The syntax action runs concurrently — <c>EnableConcurrentExecution</c>
    /// is on for the same reason every other analyzer here has it — so the collection is
    /// guarded; the walk happens once, on one thread, at compilation end.
    /// </remarks>
    private sealed class GraphBuilder
    {
        private readonly object _sync = new object();

        /// <summary>Flow type → the flow types it composes, in declaration order.</summary>
        private readonly Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> _edges =
            new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(SymbolEqualityComparer.Default);

        /// <summary>Flow type → where to report against it.</summary>
        private readonly Dictionary<INamedTypeSymbol, Location> _declarations =
            new Dictionary<INamedTypeSymbol, Location>(SymbolEqualityComparer.Default);

        /// <summary>Flow type → its business identity, which is what a reader recognises.</summary>
        private readonly Dictionary<INamedTypeSymbol, string> _ids =
            new Dictionary<INamedTypeSymbol, string>(SymbolEqualityComparer.Default);

        internal void Add(SyntaxNodeAnalysisContext context)
        {
            if (context.Node is not ClassDeclarationSyntax declaration ||
                context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken)
                    is not INamedTypeSymbol flowType)
            {
                return;
            }

            var attribute = flowType.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == FlowAttribute);

            if (attribute is null)
            {
                return;
            }

            var id = attribute.ConstructorArguments.Length > 0
                ? attribute.ConstructorArguments[0].Value as string ?? flowType.Name
                : flowType.Name;

            var composed = Composed(declaration, context.SemanticModel, context.CancellationToken);

            lock (_sync)
            {
                _ids[flowType] = id;

                // The first declaration wins. A partial class has several, and the
                // generated one is excluded above, so in practice there is one — but
                // reporting against whichever part the scheduler visited last would make
                // the diagnostic's location non-deterministic.
                if (!_declarations.ContainsKey(flowType))
                {
                    _declarations[flowType] = declaration.Identifier.GetLocation();
                }

                if (!_edges.TryGetValue(flowType, out var existing))
                {
                    _edges[flowType] = composed;
                    return;
                }

                existing.AddRange(composed);
            }
        }

        /// <summary>The flow types one declaration's <c>Define</c> chain composes.</summary>
        /// <remarks>
        /// Every <c>.SubFlow&lt;TFlow, …&gt;()</c> anywhere in the chain, nested blocks
        /// included: a composition inside a <c>When</c> or a <c>ForEach</c> body is exactly
        /// as much of a cycle as one at the top level, and a rule that only looked at the
        /// top level would quietly stop applying the day somebody wrapped the call in a
        /// condition. Read straight off the syntax rather than through
        /// <c>FlowChainWalker</c>, because this needs no ordering and no layout — only the
        /// set of edges.
        /// </remarks>
        private static List<INamedTypeSymbol> Composed(
            ClassDeclarationSyntax declaration,
            SemanticModel semanticModel,
            System.Threading.CancellationToken cancellationToken)
        {
            var composed = new List<INamedTypeSymbol>();

            foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (invocation.Expression is not MemberAccessExpressionSyntax member ||
                    member.Name is not GenericNameSyntax generic ||
                    generic.Identifier.ValueText != SubFlowMethodName ||
                    generic.TypeArgumentList.Arguments.Count == 0)
                {
                    continue;
                }

                var target = semanticModel
                    .GetSymbolInfo(generic.TypeArgumentList.Arguments[0], cancellationToken)
                    .Symbol as INamedTypeSymbol;

                if (target is not null && target.TypeKind != TypeKind.Error && !composed.Contains(target, SymbolEqualityComparer.Default))
                {
                    composed.Add(target);
                }
            }

            return composed;
        }

        internal void Report(CompilationAnalysisContext context)
        {
            foreach (var flow in _edges.Keys.OrderBy(f => f.ToDisplayString(), System.StringComparer.Ordinal))
            {
                var path = FindCycleFrom(flow, context.CancellationToken);

                if (path is null)
                {
                    continue;
                }

                context.ReportDiagnostic(Diagnostic.Create(
                    FlowXDiagnostics.SubFlowCycle,
                    _declarations.TryGetValue(flow, out var location) ? location : Location.None,
                    Id(flow),
                    string.Join(" → ", path.Select(Id))));
            }
        }

        /// <summary>
        /// The shortest composition path from <paramref name="start"/> back to itself, or
        /// <c>null</c> when there is none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A breadth-first walk rather than a depth-first one, so the reported path is the
        /// <em>shortest</em> cycle through this flow. That matters for a message a person
        /// has to act on: a depth-first walk through a densely composed application can
        /// return a fifteen-flow path when a two-flow one exists, and the reader then has
        /// fifteen edges to consider instead of two.
        /// </para>
        /// <para>
        /// Only cycles that pass through <paramref name="start"/> are reported for
        /// <paramref name="start"/>. A flow that merely <em>reaches</em> a cycle is not
        /// itself a cycle, and blaming it would send the reader to a file with nothing wrong
        /// in it.
        /// </para>
        /// </remarks>
        private List<INamedTypeSymbol>? FindCycleFrom(
            INamedTypeSymbol start,
            System.Threading.CancellationToken cancellationToken)
        {
            var previous = new Dictionary<INamedTypeSymbol, INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var queue = new Queue<INamedTypeSymbol>();

            queue.Enqueue(start);
            seen.Add(start);

            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var current = queue.Dequeue();

                if (!_edges.TryGetValue(current, out var next))
                {
                    continue;
                }

                foreach (var child in next)
                {
                    if (SymbolEqualityComparer.Default.Equals(child, start))
                    {
                        return Rebuild(previous, start, current);
                    }

                    if (seen.Add(child))
                    {
                        previous[child] = current;
                        queue.Enqueue(child);
                    }
                }
            }

            return null;
        }

        /// <summary>Walks the parent links back to the start and returns the closed cycle.</summary>
        private static List<INamedTypeSymbol> Rebuild(
            Dictionary<INamedTypeSymbol, INamedTypeSymbol> previous,
            INamedTypeSymbol start,
            INamedTypeSymbol last)
        {
            var path = new List<INamedTypeSymbol> { last };

            for (var node = last;
                 previous.TryGetValue(node, out var parent);
                 node = parent)
            {
                path.Add(parent);
            }

            path.Reverse();

            // Closed, so the message reads `order.place → order.fulfil → order.place` and a
            // reader can see it is a loop without having to infer it from the first name.
            path.Add(start);
            return path;
        }

        private string Id(INamedTypeSymbol flow) =>
            _ids.TryGetValue(flow, out var id) ? id : flow.Name;
    }
}
