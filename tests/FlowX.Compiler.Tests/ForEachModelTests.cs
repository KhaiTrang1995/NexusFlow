using System.Collections.Generic;
using System.Linq;
using FlowX.Compiler.Model;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// The layout arithmetic of an iteration, in the model layer where it is decided.
/// </summary>
/// <remarks>
/// <para>
/// The simplest of the four branching shapes to lay out and the only one whose block runs
/// more than once. One body, starting at the very next index, with no closing jump and no
/// per-block target — so the only number is the join, and it comes from the body.
/// </para>
/// <para>
/// The property worth asserting is the one that is easy to assume and hard to notice: the
/// body appears in the array <em>once</em>, however many elements the collection turns out
/// to hold. A generator that unrolled it would produce a plan whose size depended on the
/// data, which the compiler cannot know and the manifest must not claim.
/// </para>
/// </remarks>
public sealed class ForEachModelTests
{
    private const string Options = "new ForEachOptions { MaxDegreeOfParallelism = 4 }";

    private static StepModel Step(int index) =>
        StepModel.Capability(index, "Sample.C" + index, "sample.c" + index, "1.0.0", isIdempotent: true);

    private static StepModel Loop(int index, params StepModel[] body) => StepModel.ForEach(
        index,
        "ctx => ctx.Get<ValidatedOrder>().Lines",
        "Sample.Contracts.OrderLine",
        body,
        Options,
        selectorLocation: "/src/Flows/Reserve.cs:11",
        location: "/src/Flows/Reserve.cs:10");

    [Fact]
    public void TheBodyStartsAtTheVeryNextIndexAndTheJoinComesFromItsEnd()
    {
        // 1 foreach · 2,3 body · 4 join
        var node = Loop(1, Step(2), Step(3));

        node.Kind.ShouldBe(StepKindModel.ForEach);
        node.Body.Select(s => s.Index).ShouldBe([2, 3]);
        node.JoinIndex.ShouldBe(4);
        node.NextIndex.ShouldBe(4, "A loop occupies everything up to its join.");
    }

    [Fact]
    public void TheBodyIsLaidOutOnceHoweverManyElementsThereTurnOutToBe()
    {
        // The count is data and the compiler has none. Unrolling would make the plan, the
        // manifest and a rendered diagram all depend on the size of a collection that does
        // not exist until run time.
        var node = Loop(0, Step(1), Step(2));

        node.SelfAndNested.Count().ShouldBe(3, "The loop and its two body steps. Not six, not nine.");
    }

    [Fact]
    public void TheBodyIsNotClosedByAJump()
    {
        // The same absence a fork's branches have, for a related reason: the body's end is
        // the join, and the engine re-enters the span rather than falling out of it. A jump
        // would cost an index that says nothing the layout does not already say.
        Loop(1, Step(2)).JumpIndex.ShouldBeNull();
    }

    [Fact]
    public void TheJoinMayBeOnePastTheLastStepWhenTheLoopEndsTheFlow()
        => Loop(0, Step(1)).JoinIndex.ShouldBe(2);

    [Fact]
    public void ANestedConditionalInsideTheBodyIsCountedByItsOwnJoin()
    {
        // The reason NextIndex exists: a body does not know how many indices the shapes
        // inside it occupy, and asking each one where it ends is what lets branching nest
        // without every enclosing block re-deriving the layout.
        var inner = StepModel.Condition(2, "ctx => true", then: [Step(3)], otherwise: [Step(5)]);
        var node = Loop(1, inner);

        inner.JoinIndex.ShouldBe(6, "branch · then · jump · otherwise");
        node.JoinIndex.ShouldBe(6);
    }

    [Fact]
    public void ANestedLoopIsNumberedInTheSameFlatSpace()
    {
        var inner = Loop(2, Step(3));
        var outer = Loop(1, inner);

        outer.JoinIndex.ShouldBe(4);
        outer.Body.Single().Kind.ShouldBe(StepKindModel.ForEach);
        outer.SelfAndNested.Select(s => s.Index).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void SelfAndNestedReachesEveryStepInsideTheBody()
    {
        // Everything that used to read a flow's top-level steps has to read this instead,
        // or a capability invoked inside a loop is invisible to the dispatcher and to the
        // manifest's capability list.
        var node = Loop(1, Step(2), Step(3));

        node.SelfAndNested.Select(s => s.Index).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void TheSelectorAndTheOptionsAreCarriedVerbatim()
    {
        // Both reach the generated plan and neither reaches the manifest. Reconstructing
        // an arbitrary C# expression means re-rendering every form the language has and
        // being wrong on the first one nobody thought of.
        var node = Loop(1, Step(2));

        node.Selector.ShouldBe("ctx => ctx.Get<ValidatedOrder>().Lines");
        node.OptionsExpression.ShouldBe(Options);
        node.ItemTypeName.ShouldBe("Sample.Contracts.OrderLine");
        node.SelectorLocation.ShouldBe("/src/Flows/Reserve.cs:11");
    }

    [Fact]
    public void AnEmptyBodyOccupiesNoIndicesAtAll()
    {
        // FlowAnalyzer drops such a declaration before it reaches the emitter, and
        // StepNode.ForEach rejects the layout it would produce. The model still has to
        // produce a coherent join, because a half-typed buffer can reach it.
        var node = StepModel.ForEach(3, "ctx => ctx.Input.Lines", "Sample.Line", [], Options);

        node.JoinIndex.ShouldBe(4);
        node.NextIndex.ShouldBe(4);
    }

    [Fact]
    public void TheLoopIsTheOnlyKindWhoseBlockIsNotAlsoABranchOrACase()
    {
        // Body is deliberately its own property. A loop has exactly one block and it means
        // something none of the others do — "this runs repeatedly" — so folding it into
        // Branches would make every reader of a model ambiguous about which it was holding.
        var node = Loop(1, Step(2));

        node.Branches.ShouldBeEmpty();
        node.Cases.ShouldBeEmpty();
        node.Then.ShouldBeEmpty();
        node.Otherwise.ShouldBeEmpty();
        node.Default.ShouldBeEmpty();
    }
}
