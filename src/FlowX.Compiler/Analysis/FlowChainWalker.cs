using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowX.Compiler.Analysis;

/// <summary>One link in a <c>Define</c> chain: the method called and its type arguments.</summary>
public sealed class ChainLink
{
    internal ChainLink(string methodName, IReadOnlyList<TypeSyntax> typeArguments, InvocationExpressionSyntax invocation)
    {
        MethodName = methodName;
        TypeArguments = typeArguments;
        Invocation = invocation;
    }

    /// <summary>The builder method: <c>Step</c>, <c>CompensateWith</c>, <c>Emit</c>, and so on.</summary>
    public string MethodName { get; }

    /// <summary>Generic arguments, e.g. the <c>T</c> of <c>.Step&lt;T&gt;()</c>.</summary>
    public IReadOnlyList<TypeSyntax> TypeArguments { get; }

    /// <summary>The syntax node, for resolving symbols and reporting diagnostics at the right spot.</summary>
    public InvocationExpressionSyntax Invocation { get; }
}

/// <summary>
/// Unwinds a fluent <c>Define</c> chain into declaration order.
/// </summary>
/// <remarks>
/// <para>
/// A fluent chain nests inside-out: <c>a.Step&lt;A&gt;().Step&lt;B&gt;()</c> parses as
/// an invocation of <c>Step&lt;B&gt;</c> whose receiver is the invocation of
/// <c>Step&lt;A&gt;</c>. So the outermost node is the <em>last</em> call, and reading
/// the chain in source order means walking down the receivers and reversing.
/// </para>
/// <para>
/// Getting this backwards produces a flow whose steps run in reverse — a bug that
/// compiles, passes a smoke test on a one-step flow, and corrupts every real one.
/// <c>FlowChainWalkerTests</c> pins the order down explicitly for that reason.
/// </para>
/// </remarks>
public static class FlowChainWalker
{
    /// <summary>Returns the chain's links in source order, or an empty list if there is no chain.</summary>
    public static IReadOnlyList<ChainLink> Walk(SyntaxNode? defineBody)
    {
        var links = new List<ChainLink>();

        if (defineBody is null)
        {
            return links;
        }

        var current = FindOutermostInvocation(defineBody);

        while (current is not null)
        {
            if (current.Expression is not MemberAccessExpressionSyntax member)
            {
                break;
            }

            var typeArguments = member.Name is GenericNameSyntax generic
                ? (IReadOnlyList<TypeSyntax>)generic.TypeArgumentList.Arguments
                : new List<TypeSyntax>();

            links.Add(new ChainLink(member.Name.Identifier.ValueText, typeArguments, current));

            // Walk down the receiver. When it is no longer an invocation we have reached
            // the builder parameter itself, which ends the chain.
            current = member.Expression as InvocationExpressionSyntax;
        }

        links.Reverse();
        return links;
    }

    private static InvocationExpressionSyntax? FindOutermostInvocation(SyntaxNode body)
    {
        switch (body)
        {
            case ArrowExpressionClauseSyntax arrow:
                return arrow.Expression as InvocationExpressionSyntax;

            case InvocationExpressionSyntax invocation:
                return invocation;

            case BlockSyntax block:
                // A statement-bodied Define. Only the last expression statement can be
                // the chain; anything earlier is setup the DSL does not model.
                for (var i = block.Statements.Count - 1; i >= 0; i--)
                {
                    if (block.Statements[i] is ExpressionStatementSyntax statement &&
                        statement.Expression is InvocationExpressionSyntax candidate)
                    {
                        return candidate;
                    }
                }

                return null;

            default:
                return null;
        }
    }
}
