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

    [Fact]
    public void DescendsIntoTheThenAndOtherwiseBlocksOfAConditional()
    {
        // The nested chains are arguments, not receivers, so unwinding the outer chain
        // never reaches them. A walker that stopped at the outer level would model a
        // conditional whose branches are both empty — and the flow would compile to one
        // that does nothing whichever way it goes.
        var links = Walk("""
            class C
            {
                void Define(B flow) => flow
                    .Step<A>()
                    .When(ctx => ctx.Get<R>().Value > 80, high => high
                        .Step<B>()
                        .Step<C>())
                    .Otherwise(low => low
                        .Step<D>())
                    .Step<E>();
            }
            """);

        links.Select(l => l.MethodName).ShouldBe(["Step", "When", "Otherwise", "Step"]);

        var when = links[1];
        var otherwise = links[2];

        FlowChainWalker.WalkBlock(when, 1)
            .Select(l => l.TypeArguments[0].ToString())
            .ShouldBe(["B", "C"], "The `then` block is the second argument of `When`.");

        FlowChainWalker.WalkBlock(otherwise, 0)
            .Select(l => l.TypeArguments[0].ToString())
            .ShouldBe(["D"]);
    }

    [Fact]
    public void DescendsIntoABlockBodiedBranchLambda()
    {
        var links = Walk("""
            class C
            {
                void Define(B flow) => flow
                    .When(ctx => true, high =>
                    {
                        high.Step<A>().Step<B>();
                    });
            }
            """);

        FlowChainWalker.WalkBlock(links.Single(), 1)
            .Select(l => l.MethodName)
            .ShouldBe(["Step", "Step"]);
    }

    [Fact]
    public void DescendsIntoAConditionalNestedInsideAnotherOne()
    {
        var links = Walk("""
            class C
            {
                void Define(B flow) => flow
                    .When(ctx => true, outer => outer
                        .When(ctx => false, inner => inner.Step<A>()));
            }
            """);

        var outer = FlowChainWalker.WalkBlock(links.Single(), 1).Single();

        outer.MethodName.ShouldBe("When");
        FlowChainWalker.WalkBlock(outer, 1).Single().TypeArguments[0].ToString().ShouldBe("A");
    }

    [Fact]
    public void AProjectionLambdaIsNotMistakenForABranchBlock()
    {
        // `ctx => ctx.Get<Order>()` is an invocation rooted at an identifier — structurally
        // the same shape as `then => then.Step<A>()`. Only the lambda's own parameter name
        // tells them apart, and without that test a `.Return(...)` clause would read as a
        // one-step branch named `Get`.
        var links = Walk("""
            class C
            {
                void Define(B flow) => flow.When(ctx => true, ctx => ctx.Get<Order>());
            }
            """);

        FlowChainWalker.WalkBlock(links.Single(), 1).Single().MethodName.ShouldBe("Get");

        // Same lambda, walked as if it belonged to a different builder: rejected.
        FlowChainWalker.Walk(
            links.Single().Invocation.ArgumentList.Arguments[1].Expression,
            "somethingElse").ShouldBeEmpty();
    }

    [Fact]
    public void DescendsIntoTheCaseAndDefaultBlocksOfASwitch()
    {
        // Same shape as a conditional's blocks and the same hazard: the nested chains are
        // arguments, not receivers, so unwinding the outer chain never reaches them. A
        // walker that stopped at the outer level would model a switch whose every arm is
        // empty — a flow that does nothing whichever value it sees.
        var links = Walk("""
            class C
            {
                void Define(B flow) => flow
                    .Step<A>()
                    .Switch(ctx => ctx.Get<O>().Channel)
                    .Case(Channel.Retail, retail => retail
                        .Step<B>())
                    .Case(Channel.Wholesale, wholesale => wholesale
                        .Step<C>()
                        .Step<D>())
                    .Default(rest => rest
                        .Step<E>())
                    .Step<F>();
            }
            """);

        links.Select(l => l.MethodName).ShouldBe(
            ["Step", "Switch", "Case", "Case", "Default", "Step"]);

        FlowChainWalker.WalkBlock(links[2], 1)
            .Select(l => l.TypeArguments[0].ToString())
            .ShouldBe(["B"], "A case's block is the second argument of `Case`.");

        FlowChainWalker.WalkBlock(links[3], 1)
            .Select(l => l.TypeArguments[0].ToString())
            .ShouldBe(["C", "D"]);

        FlowChainWalker.WalkBlock(links[4], 0)
            .Select(l => l.TypeArguments[0].ToString())
            .ShouldBe(["E"], "A `Default` block is its only argument.");
    }

    [Fact]
    public void ACaseValueIsNotMistakenForItsBlock()
    {
        // `.Case(Channel.Retail, …)` puts an expression where `When` puts a predicate.
        // Walking argument 0 as if it were a block must yield nothing rather than reading
        // the value as a chain.
        var links = Walk("""
            class C
            {
                void Define(B flow) => flow
                    .Switch(ctx => ctx.Get<O>().Channel)
                    .Case(Channel.Retail, retail => retail.Step<B>());
            }
            """);

        FlowChainWalker.WalkBlock(links[1], 0).ShouldBeEmpty();
    }

    [Fact]
    public void DescendsIntoASwitchNestedInsideACaseOfAnotherOne()
    {
        var links = Walk("""
            class C
            {
                void Define(B flow) => flow
                    .Switch(ctx => ctx.Get<O>().Channel)
                    .Case(Channel.Retail, retail => retail
                        .Switch(ctx => ctx.Get<O>().Tier)
                        .Case(Tier.Gold, gold => gold.Step<A>()));
            }
            """);

        var inner = FlowChainWalker.WalkBlock(links[1], 1);

        inner.Select(l => l.MethodName).ShouldBe(["Switch", "Case"]);
        FlowChainWalker.WalkBlock(inner[1], 1).Single().TypeArguments[0].ToString().ShouldBe("A");
    }

    [Fact]
    public void DescendsIntoABlockBodiedCaseLambda()
    {
        var links = Walk("""
            class C
            {
                void Define(B flow) => flow
                    .Switch(ctx => ctx.Get<O>().Channel)
                    .Case(Channel.Retail, retail =>
                    {
                        retail.Step<A>().Step<B>();
                    });
            }
            """);

        FlowChainWalker.WalkBlock(links[1], 1)
            .Select(l => l.MethodName)
            .ShouldBe(["Step", "Step"]);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    public void AnArgumentThatIsNotThereYieldsNoLinks(int argumentIndex)
    {
        var links = Walk("""
            class C
            {
                void Define(B flow) => flow.When(ctx => true, then => then.Step<A>());
            }
            """);

        FlowChainWalker.WalkBlock(links.Single(), argumentIndex).ShouldBeEmpty();
    }

    [Fact]
    public void AnArgumentThatIsNotALambdaYieldsNoLinks()
    {
        // A method group or a stored Action is legal C# whose body is not visible here.
        // Returning nothing means the flow models an empty branch; throwing would take
        // the whole build down.
        var links = Walk("""
            class C
            {
                void Define(B flow) => flow.When(ctx => true, HandleHigh);
            }
            """);

        FlowChainWalker.WalkBlock(links.Single(), 1).ShouldBeEmpty();
    }

    [Fact]
    public void WalkBlockToleratesANullLink()
        => FlowChainWalker.WalkBlock(null!, 0).ShouldBeEmpty();

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
