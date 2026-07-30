using System.Collections.Generic;
using System.Linq;
using FlowX.Compiler.Model;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// The layout arithmetic of a fork, in the model layer where it is decided.
/// </summary>
/// <remarks>
/// <para>
/// A fork looks like a switch and lays out differently in one respect that matters:
/// <strong>no closing jumps</strong>. A case block needs one because the arms sit
/// adjacently and a taken arm would fall into the next; a branch does not, because the
/// engine runs each branch as a bounded range and the next branch's target <em>is</em> the
/// bound.
/// </para>
/// <para>
/// Getting that wrong is not a wrong answer, it is a race: a branch whose range overran
/// into its sibling's would run the sibling's steps a second time, concurrently, and
/// compensate them twice. Which is why every number below is asserted rather than assumed.
/// </para>
/// </remarks>
public sealed class ParallelModelTests
{
    private static StepModel Step(int index) =>
        StepModel.Capability(index, "Sample.C" + index, "sample.c" + index, "1.0.0", isIdempotent: true);

    private static ParallelBranchModel Branch(params StepModel[] steps) => new(steps);

    private static StepModel Fork(
        int index,
        IReadOnlyList<ParallelBranchModel> branches,
        string merge = "MergeStrategy.AllMustSucceed",
        string? kind = "AllMustSucceed") =>
        StepModel.Parallel(index, branches, merge, kind);

    [Fact]
    public void EachBranchTargetsTheFirstStepOfItsOwnBlock()
    {
        // 1 fork · 2 branch0 · 3,4 branch1 · 5 join
        var node = Fork(1, [Branch(Step(2)), Branch(Step(3), Step(4))]);

        node.Branches.Select(b => b.Target).ShouldBe([2, 3],
            "A branch target that pointed one step late would silently drop that step out " +
            "of the flow, and a target that pointed early would run a sibling's step twice.");

        node.JoinIndex.ShouldBe(5);
        node.NextIndex.ShouldBe(5, "A fork occupies everything up to its join.");
    }

    [Fact]
    public void NoBranchIsClosedByAJump()
    {
        // The whole difference from a switch, stated as an absence. A branch's range ends
        // where the next branch begins; a jump saying the same thing would cost an index
        // the graph then has to account for, and would have to be skipped by the range
        // bound anyway.
        var node = Fork(1, [Branch(Step(2)), Branch(Step(3))]);

        node.JumpIndex.ShouldBeNull();
        node.JoinIndex.ShouldBe(4, "Two single-step branches occupy 2 and 3; the join is 4.");
    }

    [Fact]
    public void TheJoinMayBeOnePastTheLastStepWhenTheForkEndsTheFlow()
    {
        var node = Fork(0, [Branch(Step(1)), Branch(Step(2))]);

        node.JoinIndex.ShouldBe(3);
    }

    [Fact]
    public void ANestedConditionalInsideABranchIsCountedByItsOwnJoin()
    {
        // The reason NextIndex exists at all: a branch does not know how many indices the
        // shapes inside it occupy, and asking each one where it ends is what lets branching
        // nest without every enclosing block re-deriving the layout.
        var inner = StepModel.Condition(3, "ctx => true", then: [Step(4)], otherwise: [Step(6)]);
        var node = Fork(1, [Branch(Step(2)), Branch(inner)]);

        inner.JoinIndex.ShouldBe(7, "branch · then · jump · otherwise");
        node.JoinIndex.ShouldBe(7);
        node.Branches[1].Target.ShouldBe(3);
    }

    [Fact]
    public void SelfAndNestedReachesEveryStepInsideABranch()
    {
        // Everything that reads a flow's capabilities — the descriptors, the dispatcher's
        // switch, the manifest's capability list — reads SelfAndNested. A branch's steps
        // missing from it means a capability invoked inside a fork is invisible to all
        // three, and the generated dispatcher throws at run time on an index it has no
        // case for.
        var node = Fork(1, [Branch(Step(2)), Branch(Step(3), Step(4))]);

        node.SelfAndNested.Select(s => s.Index).ShouldBe([1, 2, 3, 4]);
    }

    [Fact]
    public void TheMergeExpressionIsCopiedVerbatimAndItsKindIsReadSeparately()
    {
        // Two fields on purpose. The expression reaches the generated plan, so a strategy
        // written as `MergeStrategy.Quorum(RequiredChecks)` compiles as the author wrote
        // it; the kind reaches the manifest, which publishes structure and never values.
        var node = Fork(1, [Branch(Step(2)), Branch(Step(3))], "MergeStrategy.Quorum(RequiredChecks)", "Quorum");

        node.MergeExpression.ShouldBe("MergeStrategy.Quorum(RequiredChecks)");
        node.MergeKindName.ShouldBe("Quorum");
    }

    [Fact]
    public void AnUnreadableMergeStillCompilesAndSimplyHasNoName()
    {
        // A strategy chosen through a variable or a helper. The plan is still exactly
        // right, because the plan copies the expression; only the manifest's label is
        // lost, and it is omitted rather than guessed.
        var node = Fork(1, [Branch(Step(2)), Branch(Step(3))], "_defaults.Merge", kind: null);

        node.MergeExpression.ShouldBe("_defaults.Merge");
        node.MergeKindName.ShouldBeNull();
    }

    [Fact]
    public void AnEmptyBranchOccupiesNoIndicesAndGetsNoTarget()
    {
        // FlowAnalyzer drops these before the model sees them and StepNode.ForParallel
        // rejects the layout they would produce — a branch that runs no steps would still
        // count towards a quorum. This pins the arithmetic anyway, because a factory that
        // handed an empty branch the join as its target would produce a graph that passes
        // validation and merges wrongly.
        var node = Fork(1, [Branch(Step(2)), Branch(), Branch(Step(3))]);

        node.Branches[1].Target.ShouldBe(0, "Never assigned, because there is nothing to point at.");
        node.JoinIndex.ShouldBe(4);
    }

    [Fact]
    public void AForkIsNotAConditionalOrASwitchAndCarriesNeithersData()
    {
        var node = Fork(1, [Branch(Step(2)), Branch(Step(3))]);

        node.Kind.ShouldBe(StepKindModel.Parallel);
        node.Then.ShouldBeEmpty();
        node.Otherwise.ShouldBeEmpty();
        node.Cases.ShouldBeEmpty();
        node.Predicate.ShouldBeNull();
        node.Selector.ShouldBeNull();
    }
}
