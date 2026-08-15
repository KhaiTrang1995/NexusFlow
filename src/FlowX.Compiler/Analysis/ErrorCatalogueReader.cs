using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using FlowX.Compiler.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowX.Compiler.Analysis;

/// <summary>
/// Enumerates the failures a capability can return, by reading its own source.
/// </summary>
/// <remarks>
/// <para>
/// <a href="../../../docs/adr/ADR-0007-result-over-exceptions.md">ADR-0007</a> chose
/// <c>Result&lt;T&gt;</c> over exceptions partly so that "failure paths … are enumerable
/// in the manifest, so error catalogues, OpenAPI responses and client SDKs are
/// generated". Nothing enumerated them. This does.
/// </para>
/// <para>
/// <strong>There is no <c>[Error]</c> attribute, and adding one would have been the
/// wrong answer.</strong> The declaration mechanism already exists and is documented:
/// <c>docs/07-Capability-Model.md §7</c> requires errors to be declared in a static
/// factory class per domain, "so error codes are enumerable — they appear in the manifest
/// and in generated OpenAPI". A second, parallel declaration on the capability would be a
/// list that has to be kept in step with the code by hand, and the first time it drifted
/// the manifest would be confidently wrong. Reading the code that already exists cannot
/// drift.
/// </para>
/// <para>
/// <strong>What is read, and from where.</strong> The catalogue is a statement about what
/// leaves <c>ExecuteAsync</c>, so the scan starts at <c>ExecuteAsync</c> and at nothing
/// else. From there it follows the value: through the <c>Result&lt;T&gt;</c> the method
/// returns, through the <c>ValueTask</c> that carries it, through the arms of a
/// conditional, into any method whose source this compilation has, and — once a failure
/// takes the shape of an <c>Error</c> — through a factory invocation, a field, a property,
/// or the <c>.With(...)</c> chain that decorates it, until it reaches the
/// <c>new Error(code, message, category)</c> or the <c>Result.Fail&lt;T&gt;(code, message,
/// category)</c> that produced it. The code and the category are taken from there; the
/// message never is.
/// </para>
/// <para>
/// <strong>Why the walk starts at the entry point rather than at the class.</strong> An
/// earlier version asked every node in the capability's whole class declaration for its
/// type and kept the ones that were <c>Error</c>. That reported errors the capability
/// cannot return — an <c>Error</c> built in an overridden hook nothing calls, or in a
/// helper left behind by a refactor, was published as one it returns — because a lexical
/// walk never asks what is reachable. Starting from the one member the contract says
/// produces the output, and following values from there, asks it by construction.
/// </para>
/// <para>
/// <strong>What it refuses to do.</strong> When a trail cannot be followed — a factory in
/// a referenced assembly, whose source this compilation does not have; a code composed at
/// run time; an <c>Error</c> arriving as a parameter; a <c>Result&lt;T&gt;</c> handed back
/// by an injected collaborator — the catalogue is marked incomplete and the manifest omits
/// it entirely. A catalogue that is short by one is indistinguishable from one that is
/// right, and a consumer cannot tell it is being lied to. Absent is a state a consumer can
/// see.
/// </para>
/// <para>
/// <strong>The empty catalogue is a conclusion, not a default.</strong> <c>errors: []</c>
/// is published only when every value that can reach the method's output was traced to a
/// success — <c>Result.Ok</c>, or a value converted into <c>Result&lt;T&gt;</c>. Finding no
/// <c>Error</c> is not the same as establishing there is none: a failure that stays inside
/// a <c>Result&lt;T&gt;</c> for its whole journey never takes the shape of an <c>Error</c>
/// in the capability's source, and reporting "no failures" for it was a positive claim that
/// happened to be false. Every shape that is not understood now reaches
/// <see cref="Scan.Complete"/> instead.
/// </para>
/// </remarks>
public static class ErrorCatalogueReader
{
    private const string ErrorTypeName = "Error";
    private const string ResultTypeName = "Result";
    private const string FlowXNamespace = "FlowX";
    private const string CapabilityInterface = "ICapability`2";
    private const string EntryPointName = "ExecuteAsync";

    /// <summary>
    /// The last answer given for a capability, and the trees that answer was read from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why a memo and not incrementality.</strong>
    /// <c>ForAttributeWithMetadataName</c> combines its node table with the compilation before
    /// invoking a transform, and the compilation changes on every edit anywhere — so Roslyn
    /// re-invokes this reader for every capability on every keystroke and no arrangement of the
    /// pipeline changes that. What <em>can</em> change is the price of a re-invocation:
    /// <c>IncrementalLoopCostTests</c> measured an unrelated edit repeating 98.7 % of a cold
    /// generation, and nearly all of it is binding bodies whose answers cannot have moved.
    /// </para>
    /// <para>
    /// <strong>What makes reuse safe.</strong> A syntax tree is immutable, so an unchanged file
    /// keeps the same instance across compilations and an edited one does not. A remembered
    /// answer is reusable when every tree it was read from is still <em>in</em> the compilation
    /// being asked about — an edited file's old tree is not, so its capabilities recompute — and
    /// when no tree has been added or removed, which is the one way a new declaration could
    /// change an answer without touching a tree that was read. That second condition is what
    /// stops this from being a staleness bug of the kind
    /// <c>ErrorCatalogueIncrementalTests</c> exists to catch.
    /// </para>
    /// <para>
    /// <strong>Keyed on the capability's own tree, so it is bounded by construction.</strong>
    /// Entries die with the tree they belong to; nothing has to decide a capacity, and a
    /// generator that ran over a solution yesterday holds nothing today.
    /// </para>
    /// </remarks>
    private static readonly ConditionalWeakTable<SyntaxTree, ConcurrentDictionary<string, Memo>> Remembered =
        new ConditionalWeakTable<SyntaxTree, ConcurrentDictionary<string, Memo>>();

    /// <summary>How many trees a compilation had, counted once per compilation.</summary>
    /// <remarks>
    /// <c>Compilation.SyntaxTrees</c> is an enumerable, and walking it once per capability would
    /// make this check quadratic in a solution's file count — which is the shape of cost this
    /// whole memo exists to remove.
    /// </remarks>
    private static readonly ConditionalWeakTable<Compilation, object> TreeCounts =
        new ConditionalWeakTable<Compilation, object>();

    /// <summary>The semantic models every scan of one compilation shares.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Per compilation, not per scan.</strong> <see cref="Scan"/>'s own dictionary
    /// already stops one capability from binding a file twice, and that is as far as it goes:
    /// the next capability starts a new scan and builds its own model of the same tree. A
    /// capability's error factories are shared by construction — <c>docs/07-Capability-Model.md
    /// §7</c> requires one static factory class per domain — so every capability in a domain
    /// followed its trail into the same file and bound those factory bodies again. On the
    /// 50-flow synthetic subject that was 524 models over 51 distinct trees.
    /// </para>
    /// <para>
    /// A model's answers are a function of the compilation and the tree, so sharing one changes
    /// no answer; what it changes is how many times the bodies behind those answers are bound.
    /// </para>
    /// <para>
    /// Keyed weakly on the compilation, so the models die with it and an edit — which produces
    /// a new compilation — carries nothing across. What this holds is one model per tree the
    /// pass asked about, which is a subset of the trees it was going to bind anyway.
    /// </para>
    /// </remarks>
    private static readonly ConditionalWeakTable<Compilation, ConcurrentDictionary<SyntaxTree, SemanticModel>> Models =
        new ConditionalWeakTable<Compilation, ConcurrentDictionary<SyntaxTree, SemanticModel>>();

    private sealed class Memo
    {
        public Memo(CapabilityErrorCatalogue catalogue, SyntaxTree[] readFrom, int treeCount)
        {
            Catalogue = catalogue;
            ReadFrom = readFrom;
            TreeCount = treeCount;
        }

        public CapabilityErrorCatalogue Catalogue { get; }

        public SyntaxTree[] ReadFrom { get; }

        public int TreeCount { get; }

        /// <summary>Whether this answer can be handed back for the compilation given.</summary>
        public bool ReusableIn(Compilation compilation, int treeCount) =>
            TreeCount == treeCount && ReadFrom.All(compilation.ContainsSyntaxTree);
    }

    private static int TreeCountOf(Compilation compilation)
    {
        if (TreeCounts.TryGetValue(compilation, out var counted))
        {
            return (int)counted;
        }

        var count = compilation.SyntaxTrees.Count();

        TreeCounts.Add(compilation, count);

        return count;
    }

    /// <summary>Reads the capability's error catalogue, or <c>null</c> if the type is not one.</summary>
    /// <param name="capability">The capability's class symbol.</param>
    /// <param name="compilation">
    /// The compilation, so factories declared in other files can be followed. A factory in
    /// another <em>assembly</em> has no syntax here and makes the catalogue incomplete.
    /// </param>
    /// <param name="cancellationToken">Cancellation from the generator pipeline.</param>
    public static CapabilityErrorCatalogue? Read(
        INamedTypeSymbol? capability,
        Compilation compilation,
        CancellationToken cancellationToken = default)
    {
        var info = CapabilityReader.Read(capability);

        if (info is null || capability is null || compilation is null)
        {
            return null;
        }

        var entryPoint = EntryPoint(capability);
        var declaredIn = entryPoint?.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree;
        var key = capability.ToDisplayString();
        var treeCount = TreeCountOf(compilation);

        if (declaredIn is not null
            && Remembered.TryGetValue(declaredIn, out var memos)
            && memos.TryGetValue(key, out var memo)
            && memo.ReusableIn(compilation, treeCount))
        {
            return memo.Catalogue;
        }

        var scan = new Scan(capability);

        // A capability whose entry point has no syntax here — one from a referenced
        // assembly, or one this reader could not identify — says nothing about what it
        // returns, which is different from saying it returns nothing.
        if (entryPoint is null || entryPoint.DeclaringSyntaxReferences.Length == 0)
        {
            scan.Complete = false;
        }
        else
        {
            foreach (var reference in entryPoint.DeclaringSyntaxReferences)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var node = reference.GetSyntax(cancellationToken);
                var model = scan.Model(compilation, node.SyntaxTree);

                foreach (var root in Roots(node, model))
                {
                    Resolve(root.Expression, root.IsError, model, compilation, scan, cancellationToken);
                }
            }
        }

        var catalogue = new CapabilityErrorCatalogue(info.Id, info.Version, scan.Found, scan.Complete);

        if (declaredIn is not null)
        {
            Remembered.GetOrCreateValue(declaredIn)[key] =
                new Memo(catalogue, scan.Dependencies(), treeCount);
        }

        return catalogue;
    }

    /// <summary>The capability's implementation of <c>ICapability&lt;,&gt;.ExecuteAsync</c>.</summary>
    /// <remarks>
    /// Through the interface rather than by name, so an explicit implementation and an
    /// implementation inherited from a base class both resolve to the member that actually
    /// runs — which is the one whose failures the manifest is describing.
    /// </remarks>
    private static IMethodSymbol? EntryPoint(INamedTypeSymbol capability)
    {
        foreach (var contract in capability.AllInterfaces)
        {
            if (contract.MetadataName != CapabilityInterface
                || !IsFlowXNamespace(contract.ContainingNamespace))
            {
                continue;
            }

            foreach (var member in contract.GetMembers(EntryPointName))
            {
                if (capability.FindImplementationForInterfaceMember(member) is IMethodSymbol implementation)
                {
                    return implementation;
                }
            }
        }

        return null;
    }

    /// <summary>What one scan has found so far, and whether it still believes itself.</summary>
    private sealed class Scan
    {
        public Scan(INamedTypeSymbol capability) => Capability = capability;

        /// <summary>The concrete type whose catalogue this is. Fixes virtual dispatch.</summary>
        public INamedTypeSymbol Capability { get; }

        public HashSet<ISymbol> Visited { get; } = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        public List<CapabilityErrorModel> Found { get; } = new List<CapabilityErrorModel>();

        public bool Complete { get; set; } = true;

        /// <summary>
        /// One semantic model per tree, and the record of which trees this answer depends on.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Reused, because a fresh model caches nothing.</strong>
        /// <c>Compilation.GetSemanticModel</c> hands back a new instance every call and a
        /// model's bound nodes live on the instance, so a scan that followed five helpers in one
        /// file used to bind that file six times.
        /// </para>
        /// <para>
        /// <strong>And it doubles as the dependency list.</strong> A tree reaches this
        /// dictionary exactly when something asked a semantic question of it, which is exactly
        /// when the answer could have come from it — so the keys are what
        /// the memo above has to re-check. Recording the dependency separately would be
        /// a second list to keep in step with this one.
        /// </para>
        /// </remarks>
        private readonly Dictionary<SyntaxTree, SemanticModel> _models =
            new Dictionary<SyntaxTree, SemanticModel>();

        /// <summary>The one semantic model this compilation uses for a tree.</summary>
        private static SemanticModel SharedModel(Compilation compilation, SyntaxTree tree)
        {
            var models = Models.GetOrCreateValue(compilation);

            // TryGetValue first so the common case does not build a model to throw away:
            // GetOrAdd takes the value, not a factory, because the factory overload that
            // avoids the closure allocation is not in netstandard2.0.
            return models.TryGetValue(tree, out var model)
                ? model
                : models.GetOrAdd(tree, compilation.GetSemanticModel(tree));
        }

        /// <summary>The semantic model for a tree, reused for the length of this scan.</summary>
        /// <param name="compilation">The compilation the model comes from.</param>
        /// <param name="tree">The tree to bind.</param>
        public SemanticModel Model(Compilation compilation, SyntaxTree tree)
        {
            if (!_models.TryGetValue(tree, out var model))
            {
                model = SharedModel(compilation, tree);
                _models.Add(tree, model);
            }

            return model;
        }

        /// <summary>Every tree this scan read.</summary>
        public SyntaxTree[] Dependencies() => _models.Keys.ToArray();
    }

    /// <summary>An expression the scan must account for, and which of the two kinds it is.</summary>
    private readonly struct Root
    {
        public Root(ExpressionSyntax expression, bool isError)
        {
            Expression = expression;
            IsError = isError;
        }

        public ExpressionSyntax Expression { get; }

        /// <summary>True for an <c>Error</c>; false for something carrying a <c>Result&lt;T&gt;</c>.</summary>
        public bool IsError { get; }
    }

    /// <summary>
    /// The outermost expressions inside a node that can carry a failure out of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two kinds qualify: an expression of type <c>Error</c>, and an expression carrying a
    /// <c>Result&lt;T&gt;</c> — the <c>Result&lt;T&gt;</c> itself, or the <c>Task</c>,
    /// <c>ValueTask</c> or configured awaitable wrapped round it. The second kind is what
    /// makes an empty catalogue mean something: a failure travelling inside a
    /// <c>Result&lt;T&gt;</c> is invisible to the first, and was previously reported as no
    /// failure at all.
    /// </para>
    /// <para>
    /// Outermost, not every one: in <c>new Error(...).With("sku", sku)</c> both the
    /// creation and the invocation have type <c>Error</c>, and they are one failure, not
    /// two. Stopping the descent at the first hit and unwrapping from there is what keeps
    /// the count right — and for the <c>Result</c> kind it is what keeps the reading
    /// structural: everything below an outermost <c>Result</c>-carrying expression is
    /// reached by <see cref="ResolveResult"/>, which knows which positions are failures and
    /// which are the value.
    /// </para>
    /// <para>
    /// <strong>One semantic query per node, which is what the walk is written out for.</strong>
    /// The obvious spelling — <c>DescendantNodes(n =&gt; !IsFailurePath(n))</c> followed by
    /// <c>Where(IsFailurePath)</c> — asks the same question about the same node twice:
    /// once to decide whether to descend into it, once to decide whether to keep it. Both
    /// asks bind, and B12-scale §5.2 measured 20 762 of the 39 964 binds this reader
    /// performed on a 50-flow project as that duplicate. The walk below visits the same
    /// nodes in the same document order and yields the same list; it just asks once, and
    /// hands the answer on so the first dispatch does not ask again.
    /// </para>
    /// </remarks>
    private static List<Root> Roots(SyntaxNode scope, SemanticModel model)
    {
        var roots = new List<Root>();

        // DescendantNodes consults the predicate on the scope itself before descending, and
        // never yields the scope. Both are reproduced here: a failure-carrying scope has no
        // roots inside it, because it is one.
        if (Classify(scope, model) != Carrier.None)
        {
            return roots;
        }

        // Explicit stack rather than recursion: this walks whatever depth of nested
        // expression the source happens to contain, and a generator must not be the thing
        // that overflows on it.
        var pending = new Stack<SyntaxNode>();
        PushChildren(scope, pending);

        while (pending.Count > 0)
        {
            var node = pending.Pop();
            var carrier = Classify(node, model);

            if (carrier != Carrier.None)
            {
                roots.Add(new Root((ExpressionSyntax)node, carrier == Carrier.Error));
                continue;
            }

            PushChildren(node, pending);
        }

        return roots;
    }

    /// <summary>Pushes a node's children so the stack pops them in document order.</summary>
    private static void PushChildren(SyntaxNode parent, Stack<SyntaxNode> pending)
    {
        var children = parent.ChildNodesAndTokens();

        for (var index = children.Count - 1; index >= 0; index--)
        {
            if (children[index].AsNode() is { } child)
            {
                pending.Push(child);
            }
        }
    }

    /// <summary>What a node can carry out of the expression it sits in.</summary>
    private enum Carrier
    {
        /// <summary>Nothing this reader has to account for.</summary>
        None,

        /// <summary>An expression whose value is an <c>Error</c>.</summary>
        Error,

        /// <summary>An expression whose value is, or wraps, a <c>Result&lt;T&gt;</c>.</summary>
        Result,
    }

    /// <summary>Whether a node is an expression that can carry a failure, and which kind.</summary>
    /// <remarks>
    /// The symbol check is what separates a value from a mention: the return type on
    /// <c>public static Error Declined(…)</c> and the type name in <c>new Error(…)</c> are
    /// both nodes whose type is <c>Error</c>, and neither is a failure path. Excluding
    /// every <c>TypeSyntax</c> instead would have been simpler and wrong — an error held
    /// in a field and returned by its bare name is an <c>IdentifierNameSyntax</c>, which is
    /// a <c>TypeSyntax</c> too, and it would have been dropped silently. The same check
    /// keeps <c>ValueTask&lt;Result&lt;T&gt;&gt;</c> written as a return type from being
    /// read as a value.
    /// </remarks>
    private static Carrier Classify(SyntaxNode node, SemanticModel model)
    {
        if (node is not ExpressionSyntax expression)
        {
            return Carrier.None;
        }

        var type = model.GetTypeInfo(expression).Type;
        var carrier = CarrierOf(type);

        return carrier != Carrier.None && model.GetSymbolInfo(expression).Symbol is not ITypeSymbol
            ? carrier
            : Carrier.None;
    }

    private static Carrier CarrierOf(ITypeSymbol? type) =>
        IsErrorType(type) ? Carrier.Error
        : CarriesResult(type) ? Carrier.Result
        : Carrier.None;

    private static bool IsErrorType(ITypeSymbol? type) =>
        type is not null
        && type.Name == ErrorTypeName
        && IsFlowXNamespace(type.ContainingNamespace);

    /// <summary>Whether this is the top-level <c>FlowX</c> namespace.</summary>
    /// <remarks>
    /// Exactly what <c>ContainingNamespace?.ToDisplayString() == "FlowX"</c> asked, without the
    /// string — <c>StepBindingAnalyzer</c> makes the same trade for the same reason: the display
    /// is the dotted path from the global namespace, so equality with a one-segment name says
    /// the segment is <c>FlowX</c> and its parent is global.
    /// </remarks>
    private static bool IsFlowXNamespace(INamespaceSymbol? candidate) =>
        candidate is { Name: FlowXNamespace }
        && candidate.ContainingNamespace is { IsGlobalNamespace: true };

    /// <summary>Whether a namespace is exactly the three-segment path given.</summary>
    /// <remarks>
    /// Read from the inside out, because that is the direction the symbol links. Same trade as
    /// <see cref="IsFlowXNamespace"/>, and it matters more here: <see cref="CarriesResult"/> is
    /// asked about every arity-1 generic the walk meets, and building
    /// <c>"System.Threading.Tasks"</c> to throw it away was the most repeated allocation in this
    /// reader.
    /// </remarks>
    private static bool IsNamespace(INamespaceSymbol? candidate, string outer, string middle, string inner) =>
        candidate is { } innermost
        && innermost.Name == inner
        && innermost.ContainingNamespace is { } parent
        && parent.Name == middle
        && parent.ContainingNamespace is { } grandparent
        && grandparent.Name == outer
        && grandparent.ContainingNamespace is { IsGlobalNamespace: true };

    /// <summary>Whether a type is <c>Result&lt;T&gt;</c>, or an awaitable wrapped round one.</summary>
    /// <remarks>
    /// Every capability returns <c>ValueTask&lt;Result&lt;T&gt;&gt;</c>, and an
    /// <c>await … .ConfigureAwait(false)</c> puts a third type in the middle. Treating the
    /// wrappers as the thing they carry is what lets the trail through a one-line
    /// delegating capability be followed at all — and, where it cannot be followed, be
    /// refused rather than silently reported as no failure.
    /// </remarks>
    private static bool CarriesResult(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named || named.Arity != 1)
        {
            return false;
        }

        // The name first and the namespace only for a name that could match, because this is
        // asked of every arity-1 generic in every body the walk enters and almost none of them
        // are one of these five.
        if (named.Name == ResultTypeName)
        {
            return IsFlowXNamespace(named.ContainingNamespace);
        }

        var isAwaitable = named.Name switch
        {
            "Task" or "ValueTask" =>
                IsNamespace(named.ContainingNamespace, "System", "Threading", "Tasks"),
            "ConfiguredValueTaskAwaitable" or "ConfiguredTaskAwaitable" =>
                IsNamespace(named.ContainingNamespace, "System", "Runtime", "CompilerServices"),
            _ => false,
        };

        return isAwaitable && CarriesResult(named.TypeArguments[0]);
    }

    private static bool IsResultType(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && named.Name == ResultTypeName
        && named.Arity == 1
        && IsFlowXNamespace(named.ContainingNamespace);

    /// <summary>Either <c>Result</c> or <c>Result&lt;T&gt;</c> — where the factories live.</summary>
    private static bool IsResultContainer(ITypeSymbol? type) =>
        type is not null
        && type.Name == ResultTypeName
        && IsFlowXNamespace(type.ContainingNamespace);

    /// <summary>Resolves an expression whose carrier kind is already known.</summary>
    private static void Resolve(
        ExpressionSyntax expression,
        bool isError,
        SemanticModel model,
        Compilation compilation,
        Scan scan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var target = Unwrap(expression);

        if (isError)
        {
            ResolveError(target, model, compilation, scan, cancellationToken);
        }
        else
        {
            ResolveResult(target, model, compilation, scan, cancellationToken);
        }
    }

    /// <summary>Resolves an expression sitting in a position that can hold a failure.</summary>
    /// <remarks>
    /// <para>
    /// The three answers are: it is an <c>Error</c>, it carries a <c>Result&lt;T&gt;</c>, or
    /// it is the success value on its way into one. The third needs no reading —
    /// <c>Result&lt;T&gt;</c> is only ever entered from a <c>T</c> or from an <c>Error</c>,
    /// so an expression that is neither cannot be carrying a failure.
    /// </para>
    /// <para>
    /// An expression with no type of its own is either a target-typed conditional or switch,
    /// whose arms are read instead, or something this reader does not understand — a
    /// <c>throw</c> arm, most often — and the catalogue is refused.
    /// </para>
    /// </remarks>
    private static void Resolve(
        ExpressionSyntax expression,
        SemanticModel model,
        Compilation compilation,
        Scan scan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var target = Unwrap(expression);
        var type = model.GetTypeInfo(target, cancellationToken).Type;

        switch (CarrierOf(type))
        {
            case Carrier.Error:
                ResolveError(target, model, compilation, scan, cancellationToken);
                return;

            case Carrier.Result:
                ResolveResult(target, model, compilation, scan, cancellationToken);
                return;
        }

        if (type is not null)
        {
            // The success value. Nothing to read, and nothing lost by not reading it.
            return;
        }

        switch (target)
        {
            case ConditionalExpressionSyntax conditional:
                Resolve(conditional.WhenTrue, model, compilation, scan, cancellationToken);
                Resolve(conditional.WhenFalse, model, compilation, scan, cancellationToken);
                return;

            case SwitchExpressionSyntax branch:
                foreach (var arm in branch.Arms)
                {
                    Resolve(arm.Expression, model, compilation, scan, cancellationToken);
                }

                return;

            default:
                scan.Complete = false;
                return;
        }
    }

    /// <summary>Reads an expression whose value is an <c>Error</c>.</summary>
    private static void ResolveError(
        ExpressionSyntax target,
        SemanticModel model,
        Compilation compilation,
        Scan scan,
        CancellationToken cancellationToken)
    {
        switch (target)
        {
            case BaseObjectCreationExpressionSyntax creation:
                ReadConstruction(creation, model, scan);
                return;

            case InvocationExpressionSyntax invocation:
                ResolveErrorInvocation(invocation, model, compilation, scan, cancellationToken);
                return;

            // Both arms are failure paths, and both belong in the catalogue.
            case ConditionalExpressionSyntax conditional:
                Resolve(conditional.WhenTrue, model, compilation, scan, cancellationToken);
                Resolve(conditional.WhenFalse, model, compilation, scan, cancellationToken);
                return;

            case SwitchExpressionSyntax branch:
                foreach (var arm in branch.Arms)
                {
                    Resolve(arm.Expression, model, compilation, scan, cancellationToken);
                }

                return;

            // `error with { Data = … }` decorates an error; the code comes from the operand.
            case WithExpressionSyntax with:
                Resolve(with.Expression, model, compilation, scan, cancellationToken);
                return;

            case SimpleNameSyntax or MemberAccessExpressionSyntax:
                Follow(model.GetSymbolInfo(target, cancellationToken).Symbol, compilation, scan, cancellationToken);
                return;

            default:
                scan.Complete = false;
                return;
        }
    }

    private static void ResolveErrorInvocation(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        Compilation compilation,
        Scan scan,
        CancellationToken cancellationToken)
    {
        if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
        {
            scan.Complete = false;
            return;
        }

        // An instance method on Error itself — `.With(key, value)` — returns a copy
        // carrying extra structured detail. The code and category are the receiver's.
        if (!method.IsStatic && IsErrorType(method.ContainingType))
        {
            if (invocation.Expression is MemberAccessExpressionSyntax access)
            {
                Resolve(access.Expression, model, compilation, scan, cancellationToken);
            }
            else
            {
                scan.Complete = false;
            }

            return;
        }

        Follow(Dispatch(method, invocation.Expression, scan), compilation, scan, cancellationToken);
    }

    /// <summary>Reads an expression that carries a <c>Result&lt;T&gt;</c>.</summary>
    /// <remarks>
    /// This is the half that makes <c>errors: []</c> a claim the reader has earned. Every
    /// shape below either accounts for the failure the value can hold or admits it cannot,
    /// and the default admits it: a <c>Result&lt;T&gt;</c> whose provenance this reader
    /// cannot name is exactly the case that used to be published as no failure at all.
    /// </remarks>
    private static void ResolveResult(
        ExpressionSyntax target,
        SemanticModel model,
        Compilation compilation,
        Scan scan,
        CancellationToken cancellationToken)
    {
        switch (target)
        {
            case ConditionalExpressionSyntax conditional:
                Resolve(conditional.WhenTrue, model, compilation, scan, cancellationToken);
                Resolve(conditional.WhenFalse, model, compilation, scan, cancellationToken);
                return;

            case SwitchExpressionSyntax branch:
                foreach (var arm in branch.Arms)
                {
                    Resolve(arm.Expression, model, compilation, scan, cancellationToken);
                }

                return;

            // `await x` is the value x will carry, and a cast changes nothing about it.
            case AwaitExpressionSyntax awaited:
                Resolve(awaited.Expression, model, compilation, scan, cancellationToken);
                return;

            case CastExpressionSyntax cast:
                Resolve(cast.Expression, model, compilation, scan, cancellationToken);
                return;

            case InvocationExpressionSyntax invocation:
                ResolveResultInvocation(invocation, model, compilation, scan, cancellationToken);
                return;

            // `new ValueTask<Result<T>>(inner)` wraps a result that is read on its own terms.
            // A parameterless one is `default`, which is a success.
            case BaseObjectCreationExpressionSyntax creation when !IsResultType(model.GetTypeInfo(creation, cancellationToken).Type):
                foreach (var argument in creation.ArgumentList?.Arguments ?? default)
                {
                    Resolve(argument.Expression, model, compilation, scan, cancellationToken);
                }

                return;

            case SimpleNameSyntax or MemberAccessExpressionSyntax:
                Follow(model.GetSymbolInfo(target, cancellationToken).Symbol, compilation, scan, cancellationToken);
                return;

            default:
                scan.Complete = false;
                return;
        }
    }

    private static void ResolveResultInvocation(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        Compilation compilation,
        Scan scan,
        CancellationToken cancellationToken)
    {
        if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
        {
            scan.Complete = false;
            return;
        }

        if (IsResultContainer(method.ContainingType))
        {
            switch (method.Name)
            {
                // A success, stated. This is the only expression that lets a capability
                // publish an empty catalogue.
                case "Ok":
                    return;

                case "Fail":
                    ReadFailure(invocation, method, model, compilation, scan, cancellationToken);
                    return;

                // Map projects the value and propagates the error unchanged, so the failure
                // is the receiver's.
                case "Map" when !method.IsStatic:
                    ResolveReceiver(invocation, model, compilation, scan, cancellationToken);
                    return;
            }
        }

        // Task plumbing: `ValueTask.FromResult(r)` and `r.ConfigureAwait(false)` are the
        // same value on the other side, and every capability's signature has one of them.
        if (method.IsStatic
            && method.Name == "FromResult"
            && IsNamespace(method.ContainingType?.ContainingNamespace, "System", "Threading", "Tasks"))
        {
            var arguments = invocation.ArgumentList.Arguments;

            if (arguments.Count == 1)
            {
                Resolve(arguments[0].Expression, model, compilation, scan, cancellationToken);
            }
            else
            {
                scan.Complete = false;
            }

            return;
        }

        if (!method.IsStatic && method.Name == "ConfigureAwait")
        {
            ResolveReceiver(invocation, model, compilation, scan, cancellationToken);
            return;
        }

        Follow(Dispatch(method, invocation.Expression, scan), compilation, scan, cancellationToken);
    }

    /// <summary>Resolves the receiver of a member invocation that only forwards its value.</summary>
    private static void ResolveReceiver(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        Compilation compilation,
        Scan scan,
        CancellationToken cancellationToken)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax access)
        {
            Resolve(access.Expression, model, compilation, scan, cancellationToken);
        }
        else
        {
            scan.Complete = false;
        }
    }

    /// <summary>Reads a <c>Result.Fail&lt;T&gt;(…)</c>, whichever overload was called.</summary>
    /// <remarks>
    /// <para>
    /// Two overloads, and until this reader learned the second one they behaved as
    /// opposites. <c>Fail&lt;T&gt;(Error)</c> mentions an <c>Error</c>, so the failure was
    /// visible and was followed. <c>Fail&lt;T&gt;(code, message, category)</c> — first-party,
    /// documented as being "for call sites that do not have a shared error factory" —
    /// mentions two strings and an enum, so nothing was visible, nothing was refused, and
    /// the capability was published as returning no error at all.
    /// </para>
    /// <para>
    /// The parts overload is the more readable of the two: the code and the category are
    /// arguments at the call site, so it resolves to a correct catalogue rather than to a
    /// withheld one.
    /// </para>
    /// </remarks>
    private static void ReadFailure(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        SemanticModel model,
        Compilation compilation,
        Scan scan,
        CancellationToken cancellationToken)
    {
        var arguments = invocation.ArgumentList.Arguments;

        if (method.Parameters.Length == 1 && IsErrorType(method.Parameters[0].Type))
        {
            if (arguments.Count == 1)
            {
                Resolve(arguments[0].Expression, model, compilation, scan, cancellationToken);
            }
            else
            {
                scan.Complete = false;
            }

            return;
        }

        ReadCodeAndCategory(arguments, method.Parameters, model, scan);
    }

    /// <summary>Reads the code and category off a <c>new Error(...)</c>.</summary>
    private static void ReadConstruction(BaseObjectCreationExpressionSyntax creation, SemanticModel model, Scan scan)
    {
        if (model.GetSymbolInfo(creation).Symbol is not IMethodSymbol constructor
            || !IsErrorType(constructor.ContainingType))
        {
            scan.Complete = false;
            return;
        }

        ReadCodeAndCategory(
            creation.ArgumentList?.Arguments ?? default,
            constructor.Parameters,
            model,
            scan);
    }

    /// <summary>Takes the two structural facts out of an argument list.</summary>
    /// <remarks>
    /// <para>
    /// Arguments are matched to parameter names rather than to positions, so a named
    /// argument or a reordered call reads the same. Both must be compile-time constants:
    /// a code assembled at run time is not an identifier anyone can branch on, and
    /// publishing a guess at it would be worse than admitting the catalogue is incomplete.
    /// </para>
    /// <para>
    /// The message parameter is never read. It is the one field of an <c>Error</c> that
    /// routinely interpolates business values — <c>$"'{sku}' has {available} in stock."</c>
    /// — and the manifest publishes structure, never values.
    /// </para>
    /// </remarks>
    private static void ReadCodeAndCategory(
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        System.Collections.Immutable.ImmutableArray<IParameterSymbol> parameters,
        SemanticModel model,
        Scan scan)
    {
        string? code = null;
        string? category = null;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            var name = argument.NameColon?.Name.Identifier.ValueText
                ?? (index < parameters.Length ? parameters[index].Name : null);

            if (string.Equals(name, "Code", System.StringComparison.OrdinalIgnoreCase))
            {
                code = model.GetConstantValue(argument.Expression).Value as string;
            }
            else if (string.Equals(name, "Category", System.StringComparison.OrdinalIgnoreCase))
            {
                category = CategoryName(model.GetConstantValue(argument.Expression).Value);
            }
        }

        if (string.IsNullOrEmpty(code) || category is null)
        {
            scan.Complete = false;
            return;
        }

        scan.Found.Add(new CapabilityErrorModel(code!, category));
    }

    /// <summary>
    /// Maps <c>ErrorCategory</c>'s underlying value back to its name.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than derived, for the reason <c>CapabilityReader</c> gives about
    /// <c>Authorization</c>: reordering the enum is a breaking change nothing here would
    /// catch, and it should surface as a failing test rather than as a manifest that
    /// silently recategorises every error. An unrecognised value returns <c>null</c>, which
    /// makes the catalogue incomplete rather than inventing a category — the category is
    /// what a transport maps to a status code, so a wrong one is a wrong wire contract.
    /// </remarks>
    private static string? CategoryName(object? value) => value switch
    {
        0 => "Validation",
        1 => "NotFound",
        2 => "Conflict",
        3 => "Forbidden",
        4 => "Unavailable",
        5 => "Internal",
        _ => null,
    };

    /// <summary>
    /// Resolves a virtual call made on the capability itself to the member that will run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A base class's template method calling an abstract hook — <c>Reject&lt;T&gt;</c>
    /// calling <c>Invalid</c> — binds to the declaration on the base, which has no body to
    /// read. The catalogue being read is one concrete capability's, and the receiver is that
    /// capability, so the override that will actually run is a compile-time fact.
    /// </para>
    /// <para>
    /// Only for a call on <c>this</c>, implicit or written, and only when the member is
    /// declared somewhere in the capability's own hierarchy. <c>base.Invalid(…)</c> is a
    /// non-virtual call and keeps its symbol; <c>other.Invalid(…)</c> is some other object,
    /// whose runtime type is not knowable here.
    /// </para>
    /// </remarks>
    private static IMethodSymbol Dispatch(IMethodSymbol method, ExpressionSyntax callee, Scan scan)
    {
        if (method.IsStatic || !(method.IsAbstract || method.IsVirtual || method.IsOverride))
        {
            return method;
        }

        var onThis = callee switch
        {
            MemberAccessExpressionSyntax access => access.Expression is ThisExpressionSyntax,
            SimpleNameSyntax => true,
            _ => false,
        };

        if (!onThis || !DeclaredInHierarchyOf(scan.Capability, method.ContainingType))
        {
            return method;
        }

        for (var current = scan.Capability; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers(method.Name))
            {
                if (member is not IMethodSymbol candidate)
                {
                    continue;
                }

                for (var overridden = candidate.OverriddenMethod;
                    overridden is not null;
                    overridden = overridden.OverriddenMethod)
                {
                    if (SymbolEqualityComparer.Default.Equals(
                        overridden.OriginalDefinition,
                        method.OriginalDefinition))
                    {
                        return candidate;
                    }
                }
            }
        }

        return method;
    }

    private static bool DeclaredInHierarchyOf(INamedTypeSymbol capability, INamedTypeSymbol? declaring)
    {
        if (declaring is null)
        {
            return false;
        }

        for (var current = capability; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, declaring.OriginalDefinition))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Follows a symbol to its declaration and resolves the failures it produces.</summary>
    /// <remarks>
    /// A symbol with no declaring syntax lives in another assembly. Its body is not in this
    /// compilation, so the trail ends and the catalogue is incomplete — which is a fact
    /// about the build, and is reported as one rather than rounded down to an empty list.
    /// </remarks>
    private static void Follow(
        ISymbol? symbol,
        Compilation compilation,
        Scan scan,
        CancellationToken cancellationToken)
    {
        if (symbol is null)
        {
            scan.Complete = false;
            return;
        }

        // Recursion guard, and a cache: a factory invoked from three capabilities is read
        // once per scan, and a mutually recursive pair terminates.
        if (!scan.Visited.Add(symbol))
        {
            return;
        }

        // A constructed generic — `InvalidQuantity<Receipt>()` — carries the declaration of
        // its definition, and that is the source to read.
        var references = symbol.DeclaringSyntaxReferences.Length > 0
            ? symbol.DeclaringSyntaxReferences
            : symbol.OriginalDefinition.DeclaringSyntaxReferences;

        if (references.Length == 0)
        {
            scan.Complete = false;
            return;
        }

        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var node = reference.GetSyntax(cancellationToken);
            var model = scan.Model(compilation, node.SyntaxTree);
            var roots = Roots(node, model);

            if (roots.Count == 0)
            {
                // Something that yields a failure, whose declaration contains no expression
                // that could carry one. Whatever it does, this reader does not understand it.
                scan.Complete = false;
                continue;
            }

            foreach (var root in roots)
            {
                Resolve(root.Expression, root.IsError, model, compilation, scan, cancellationToken);
            }
        }
    }

    /// <summary>Strips parentheses and null-forgiving operators, which change nothing here.</summary>
    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        if (expression is ParenthesizedExpressionSyntax parenthesised)
        {
            return Unwrap(parenthesised.Expression);
        }

        return expression is PostfixUnaryExpressionSyntax suppression
            && suppression.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SuppressNullableWarningExpression)
            ? Unwrap(suppression.Operand)
            : expression;
    }
}
