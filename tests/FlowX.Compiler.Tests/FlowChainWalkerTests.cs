using FlowX.Compiler.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// Unwinding a fluent <c>Define</c> chain into declaration order.
/// </summary>
/// <remarks>
/// The order is the whole point. A chain nests inside-out, so the outermost node is
/// the <em>last</em> call; getting it backwards produces a flow whose steps run in
/// reverse — a bug that compiles, passes a smoke test on a one-step flow, and corrupts
/// every real one. These tests exist so that cannot happen quietly.
/// </remarks>
public sealed class FlowChainWalkerTests
{
    [Fact]
    public void ReadsAnExpressionBodiedChainInSourceOrder()
    {
        var links = Walk("""
            class C
            {
                void Define(B flow) => flow
                    .Step<A>()
                    .Step<B>()
                    .Return(ctx => 1);
            }
            """);

        links.Select(l => l.MethodName).ShouldBe(["Step", "Step", "Return"]);
        links[0].TypeArguments[0].ToString().ShouldBe("A");
        links[1].TypeArguments[0].ToString().ShouldBe("B");
    }

    [Fact]
    public void ReadsAStatementBodiedChain()
    {
        // The shape the ecommerce sample uses, because CA1062 requires a null check
        // before the chain. It was uncovered until the sample forced it.
        var links = Walk("""
            class C
            {
                void Define(B flow)
                {
                    ArgumentNullException.ThrowIfNull(flow);

                    flow.Step<A>().Step<B>();
                }
            }
            """);

        links.Select(l => l.MethodName).ShouldBe(["Step", "Step"]);
    }

    [Fact]
    public void IgnoresStatementsBeforeTheChain()
    {
        var links = Walk("""
            class C
            {
                void Define(B flow)
                {
                    var unused = 1;
                    flow.Step<A>();
                }
            }
            """);

        links.Count.ShouldBe(1);
    }

    [Fact]
    public void AGuardClauseIsNotMistakenForAChain()
    {
        // `ArgumentNullException.ThrowIfNull(flow)` is an invocation statement whose
        // expression is a member access on an identifier — structurally identical to
        // `flow.Step<A>()`. Only the receiver's name tells them apart, which is why the
        // walker is given the builder parameter's name.
        Walk("""
            class C
            {
                void Define(B flow)
                {
                    ArgumentNullException.ThrowIfNull(flow);
                }
            }
            """).ShouldBeEmpty();
    }

    [Fact]
    public void AStatementAfterTheChainDoesNotReplaceIt()
    {
        // The walker reads the last chain, so an unrelated trailing call would otherwise
        // win — and the flow would silently compile to nothing.
        var links = Walk("""
            class C
            {
                void Define(B flow)
                {
                    flow.Step<A>().Step<B>();
                    Console.WriteLine("built");
                }
            }
            """);

        links.Select(l => l.MethodName).ShouldBe(["Step", "Step"]);
    }

    [Fact]
    public void WithoutABuilderNameAnyInvocationChainIsAccepted()
    {
        // The name is optional so the walker stays usable on a body whose parameter list
        // could not be read. It is less precise, and the analyzer always supplies it.
        FlowChainWalker.Walk(Parse("""
            class C
            {
                void Define(B flow)
                {
                    ArgumentNullException.ThrowIfNull(flow);
                }
            }
            """)).Count.ShouldBe(1);
    }

    [Fact]
    public void ReturnsNothingForANullBody()
        => FlowChainWalker.Walk(null).ShouldBeEmpty();

    [Fact]
    public void ReturnsNothingForABodyThatIsNotAChain()
    {
        // An expression-bodied Define that is not an invocation at all. The walker must
        // return nothing rather than throw: a generator that crashes on unfamiliar
        // syntax takes the whole build down with it.
        var body = Parse("""
            class C
            {
                int Define(B flow) => 1;
            }
            """);

        FlowChainWalker.Walk(body).ShouldBeEmpty();
    }

    [Fact]
    public void EachLinkReportsItsOwnSourceLine()
    {
        // The defect this pins: a chain nests its receiver inside every later call, so
        // every invocation's span *starts* at the head of the chain. Taking the location
        // from the invocation mapped all three steps to one line, and a breakpoint on
        // the third landed on the first.
        var links = Walk("""
            class C
            {
                void Define(B flow) => flow
                    .Step<A>()
                    .Step<B>()
                    .Step<C>();
            }
            """);

        var reported = links
            .Select(l => l.CallLocation.GetLineSpan().StartLinePosition.Line)
            .ToList();

        reported.Distinct().Count().ShouldBe(3, "Each step must report its own line.");
        reported.ShouldBe(reported.OrderBy(l => l).ToList(), "and in source order.");
    }

    [Fact]
    public void ACallLocationIsAlwaysInsideTheCall()
    {
        var links = Walk("""
            class C
            {
                void Define(B flow) => flow.Step<A>();
            }
            """);

        var call = links.Single();

        // The member name's span, so it points at `Step<A>` rather than at `flow`.
        call.CallLocation.SourceSpan.Start.ShouldBeGreaterThan(
            call.Invocation.GetLocation().SourceSpan.Start);
    }

    [Fact]
    public void ALinkWithNoTypeArgumentsReportsAnEmptyList()
    {
        var links = Walk("""
            class C
            {
                void Define(B flow) => flow.Return(ctx => 1);
            }
            """);

        links.Single().TypeArguments.ShouldBeEmpty();
    }

    /// <summary>Walks as the analyzer does: with the builder parameter's name.</summary>
    private static IReadOnlyList<ChainLink> Walk(string source)
    {
        var define = Define(source);

        return FlowChainWalker.Walk(
            Body(define),
            define.ParameterList.Parameters[0].Identifier.ValueText);
    }

    private static SyntaxNode? Parse(string source) => Body(Define(source));

    private static SyntaxNode? Body(MethodDeclarationSyntax define)
        => (SyntaxNode?)define.ExpressionBody ?? define.Body;

    /// <summary>The single method named <c>Define</c> in the source.</summary>
    private static MethodDeclarationSyntax Define(string source) => CSharpSyntaxTree
        .ParseText(source)
        .GetRoot()
        .DescendantNodes()
        .OfType<MethodDeclarationSyntax>()
        .Single(m => m.Identifier.ValueText == "Define");
}
