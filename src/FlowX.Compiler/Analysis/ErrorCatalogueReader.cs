using System.Collections.Generic;
using System.Linq;
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
/// <strong>How it reads.</strong> Every expression of type <c>Error</c> inside the
/// capability is a failure path. Each one is followed — through a factory invocation,
/// through a field or property, through the arms of a conditional, through the
/// <c>.With(...)</c> chain that decorates an error with structured detail — until it
/// reaches the <c>new Error(code, message, category)</c> that produced it. The code and
/// the category are taken from there; the message never is.
/// </para>
/// <para>
/// <strong>What it refuses to do.</strong> When a trail cannot be followed — a factory in
/// a referenced assembly, whose source this compilation does not have; a code composed at
/// run time; an <c>Error</c> arriving as a parameter — the catalogue is marked incomplete
/// and the manifest omits it entirely. A catalogue that is short by one is indistinguishable
/// from one that is right, and a consumer cannot tell it is being lied to. Absent is a
/// state a consumer can see.
/// </para>
/// </remarks>
public static class ErrorCatalogueReader
{
    private const string ErrorTypeName = "Error";
    private const string FlowXNamespace = "FlowX";

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

        var scan = new Scan();

        if (capability.DeclaringSyntaxReferences.Length == 0)
        {
            // A capability from a referenced assembly. Its attribute is readable and its
            // body is not, so nothing can be said about what it returns.
            scan.Complete = false;
        }

        foreach (var reference in capability.DeclaringSyntaxReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var node = reference.GetSyntax(cancellationToken);
            var model = compilation.GetSemanticModel(node.SyntaxTree);

            foreach (var root in Roots(node, model))
            {
                Resolve(root, model, compilation, scan, cancellationToken);
            }
        }

        return new CapabilityErrorCatalogue(info.Id, info.Version, scan.Found, scan.Complete);
    }

    /// <summary>What one scan has found so far, and whether it still believes itself.</summary>
    private sealed class Scan
    {
        public HashSet<ISymbol> Visited { get; } = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        public List<CapabilityErrorModel> Found { get; } = new List<CapabilityErrorModel>();

        public bool Complete { get; set; } = true;
    }

    /// <summary>
    /// The outermost <c>Error</c>-typed expressions inside a node.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Outermost, not every one: in <c>new Error(...).With("sku", sku)</c> both the
    /// creation and the invocation have type <c>Error</c>, and they are one failure, not
    /// two. Stopping the descent at the first hit and unwrapping from there is what keeps
    /// the count right.
    /// </para>
    /// <para>
    /// <strong>One semantic query per node, which is what the walk is written out for.</strong>
    /// The obvious spelling — <c>DescendantNodes(n =&gt; !IsErrorExpression(n))</c> followed by
    /// <c>Where(IsErrorExpression)</c> — asks the same question about the same node twice:
    /// once to decide whether to descend into it, once to decide whether to keep it. Both
    /// asks bind, and B12-scale §5.2 measured 20 762 of the 39 964 binds this reader
    /// performed on a 50-flow project as that duplicate. The walk below visits the same
    /// nodes in the same document order and yields the same list; it just asks once.
    /// </para>
    /// </remarks>
    private static List<ExpressionSyntax> Roots(SyntaxNode scope, SemanticModel model)
    {
        var roots = new List<ExpressionSyntax>();

        // DescendantNodes consults the predicate on the scope itself before descending, and
        // never yields the scope. Both are reproduced here: an Error-typed scope has no
        // roots inside it, because it is one.
        if (IsErrorExpression(scope, model))
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

            if (IsErrorExpression(node, model))
            {
                roots.Add((ExpressionSyntax)node);
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

    /// <summary>Whether a node is an expression whose <em>value</em> is an <c>Error</c>.</summary>
    /// <remarks>
    /// The symbol check is what separates a value from a mention: the return type on
    /// <c>public static Error Declined(…)</c> and the type name in <c>new Error(…)</c> are
    /// both nodes whose type is <c>Error</c>, and neither is a failure path. Excluding
    /// every <c>TypeSyntax</c> instead would have been simpler and wrong — an error held
    /// in a field and returned by its bare name is an <c>IdentifierNameSyntax</c>, which is
    /// a <c>TypeSyntax</c> too, and it would have been dropped silently.
    /// </remarks>
    private static bool IsErrorExpression(SyntaxNode node, SemanticModel model) =>
        node is ExpressionSyntax expression
        && IsErrorType(model.GetTypeInfo(expression).Type)
        && model.GetSymbolInfo(expression).Symbol is not ITypeSymbol;

    private static bool IsErrorType(ITypeSymbol? type) =>
        type is not null
        && type.Name == ErrorTypeName
        && type.ContainingNamespace?.ToDisplayString() == FlowXNamespace;

    private static void Resolve(
        ExpressionSyntax expression,
        SemanticModel model,
        Compilation compilation,
        Scan scan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var target = Unwrap(expression);

        switch (target)
        {
            case BaseObjectCreationExpressionSyntax creation:
                ReadConstruction(creation, model, scan);
                return;

            case InvocationExpressionSyntax invocation:
                ResolveInvocation(invocation, model, compilation, scan, cancellationToken);
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

    private static void ResolveInvocation(
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

        Follow(method, compilation, scan, cancellationToken);
    }

    /// <summary>Reads the code and category off a <c>new Error(...)</c>.</summary>
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
    private static void ReadConstruction(BaseObjectCreationExpressionSyntax creation, SemanticModel model, Scan scan)
    {
        if (model.GetSymbolInfo(creation).Symbol is not IMethodSymbol constructor
            || !IsErrorType(constructor.ContainingType))
        {
            scan.Complete = false;
            return;
        }

        string? code = null;
        string? category = null;
        var arguments = creation.ArgumentList?.Arguments ?? default;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            var name = argument.NameColon?.Name.Identifier.ValueText
                ?? (index < constructor.Parameters.Length ? constructor.Parameters[index].Name : null);

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

    /// <summary>Follows a symbol to its declaration and resolves the errors it produces.</summary>
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

        var references = symbol.DeclaringSyntaxReferences;

        if (references.Length == 0)
        {
            scan.Complete = false;
            return;
        }

        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var node = reference.GetSyntax(cancellationToken);
            var model = compilation.GetSemanticModel(node.SyntaxTree);
            var roots = Roots(node, model);

            if (roots.Count == 0)
            {
                // Something that yields an Error, whose declaration contains no expression
                // of that type. Whatever it does, this reader does not understand it.
                scan.Complete = false;
                continue;
            }

            foreach (var root in roots)
            {
                Resolve(root, model, compilation, scan, cancellationToken);
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
