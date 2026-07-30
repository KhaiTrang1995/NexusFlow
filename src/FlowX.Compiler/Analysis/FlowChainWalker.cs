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

    /// <summary>Where this call sits in source, for the emitted <c>#line</c> directive.</summary>
    /// <remarks>
    /// The member name, deliberately, not <see cref="Invocation"/>. A fluent chain nests
    /// its receiver inside every later call, so each invocation's span <em>begins</em> at
    /// the head of the chain — using it maps every step in a flow to the same line, and
    /// a breakpoint on the third step lands on the first. The name is the only node whose
    /// span belongs to this link alone.
    /// </remarks>
    public Location CallLocation => Invocation.Expression is MemberAccessExpressionSyntax member
        ? member.Name.GetLocation()
        : Invocation.GetLocation();
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
    /// <param name="defineBody">The <c>Define</c> method's body or arrow clause.</param>
    /// <param name="builderParameterName">
    /// The name of <c>Define</c>'s builder parameter. Supplied, the walker accepts only a
    /// chain rooted at that identifier; omitted, it accepts any invocation chain.
    /// </param>
    /// <remarks>
    /// The parameter name is what tells a chain from an ordinary call. A statement-bodied
    /// <c>Define</c> that opens with <c>ArgumentNullException.ThrowIfNull(flow)</c> — which
    /// CA1062 requires — contains two invocation statements, and both are structurally
    /// identical: a member access on an identifier. Without knowing which identifier is the
    /// builder, a guard clause reads as a one-link chain named <c>ThrowIfNull</c>, and a
    /// log line written <em>after</em> the chain silently replaces it.
    /// </remarks>
    public static IReadOnlyList<ChainLink> Walk(SyntaxNode? defineBody, string? builderParameterName = null)
    {
        if (defineBody is null)
        {
            return new List<ChainLink>();
        }

        return Unwind(FindOutermostInvocation(defineBody, builderParameterName));
    }

    /// <summary>
    /// Returns the builder chain declared inside one of a link's block arguments — the
    /// <c>then</c> of a <c>When</c>, the body of an <c>Otherwise</c> — in source order.
    /// </summary>
    /// <param name="link">The chain link to descend into.</param>
    /// <param name="argumentIndex">
    /// Position of the <c>Action&lt;IFlowBuilder&lt;,&gt;&gt;</c> argument: 1 for
    /// <c>When(predicate, then)</c>, 0 for <c>Otherwise(alternative)</c>.
    /// </param>
    /// <remarks>
    /// <para>
    /// A block is written as a lambda taking a builder of its own, so its chain is rooted
    /// at the lambda's parameter and not at <c>Define</c>'s. Passing that parameter to
    /// <see cref="Walk"/> is what keeps this from reading an ordinary projection lambda —
    /// <c>ctx =&gt; ctx.Get&lt;Order&gt;()</c> — as a one-link chain named <c>Get</c>.
    /// </para>
    /// <para>
    /// Returns an empty list for anything that is not a lambda. A method group or a
    /// variable holding an <c>Action</c> is legal C# and its body is not visible here;
    /// the flow would compile to a conditional with an empty branch rather than to a
    /// generator crash, and FLOWX1023 already reports a flow that declared nothing.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ChainLink> WalkBlock(ChainLink link, int argumentIndex)
    {
        if (link is null)
        {
            return new List<ChainLink>();
        }

        var arguments = link.Invocation.ArgumentList.Arguments;

        if (argumentIndex < 0 || argumentIndex >= arguments.Count ||
            arguments[argumentIndex].Expression is not LambdaExpressionSyntax lambda)
        {
            return new List<ChainLink>();
        }

        var body = (SyntaxNode?)lambda.Block ?? lambda.ExpressionBody;

        return Walk(body, BlockParameterName(lambda));
    }

    /// <summary>The name of a block lambda's single parameter, or <c>null</c> if it has none.</summary>
    private static string? BlockParameterName(LambdaExpressionSyntax lambda) => lambda switch
    {
        SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.ValueText,
        ParenthesizedLambdaExpressionSyntax parenthesized when parenthesized.ParameterList.Parameters.Count == 1 =>
            parenthesized.ParameterList.Parameters[0].Identifier.ValueText,
        _ => null,
    };

    /// <summary>Unwinds one nested invocation into its links, in source order.</summary>
    private static List<ChainLink> Unwind(InvocationExpressionSyntax? outermost)
    {
        var links = new List<ChainLink>();
        var current = outermost;

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

    /// <summary>True when unwinding this invocation bottoms out at the named identifier.</summary>
    private static bool IsRootedAt(InvocationExpressionSyntax invocation, string builderParameterName)
    {
        ExpressionSyntax current = invocation;

        while (current is InvocationExpressionSyntax call)
        {
            if (call.Expression is not MemberAccessExpressionSyntax member)
            {
                return false;
            }

            current = member.Expression;
        }

        return current is IdentifierNameSyntax identifier &&
               identifier.Identifier.ValueText == builderParameterName;
    }

    private static InvocationExpressionSyntax? FindOutermostInvocation(
        SyntaxNode body,
        string? builderParameterName)
    {
        switch (body)
        {
            case ArrowExpressionClauseSyntax arrow:
                return Accept(arrow.Expression as InvocationExpressionSyntax, builderParameterName);

            case InvocationExpressionSyntax invocation:
                return Accept(invocation, builderParameterName);

            case BlockSyntax block:
                // Last first: a later chain supersedes an earlier one, the same way the
                // builder itself would have.
                for (var i = block.Statements.Count - 1; i >= 0; i--)
                {
                    if (block.Statements[i] is not ExpressionStatementSyntax statement ||
                        statement.Expression is not InvocationExpressionSyntax candidate)
                    {
                        continue;
                    }

                    if (builderParameterName is null || IsRootedAt(candidate, builderParameterName))
                    {
                        return candidate;
                    }
                }

                return null;

            default:
                return null;
        }
    }

    /// <summary>Keeps an expression-bodied chain only when it is rooted at the builder.</summary>
    /// <remarks>
    /// The block-bodied case has always applied this test; the expression-bodied one did
    /// not, which did not matter while the only caller passed a <c>Define</c> body. It
    /// matters now that block lambdas are walked too: <c>ctx =&gt; ctx.Get&lt;Order&gt;()</c>
    /// is an invocation rooted at an identifier, and without the test it reads as a chain
    /// of one step named <c>Get</c>.
    /// </remarks>
    private static InvocationExpressionSyntax? Accept(
        InvocationExpressionSyntax? invocation,
        string? builderParameterName) =>
        invocation is null || builderParameterName is null || IsRootedAt(invocation, builderParameterName)
            ? invocation
            : null;
}
