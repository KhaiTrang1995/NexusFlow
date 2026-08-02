using Shouldly;
using Xunit;

namespace FlowX.Core.Tests;

/// <summary>
/// The step graph is the compiled shape of a flow body. Its invariants exist so the
/// engine's step loop can be a plain indexed walk with no bounds checking and no
/// null handling — the cost of a malformed graph is paid once, at construction.
/// </summary>
public sealed class StepGraphTests
{
    private static StepNode Step(int index, CapabilityDescriptor capability, CapabilityDescriptor? compensation = null)
        => StepNode.ForCapability(index, capability, compensation);

    [Fact]
    public void BuildsTheThreeStepPlanWithOneCompensation()
    {
        // This is the P0 target shape, and the exit criterion of WP-2.
        var graph = StepGraph.Create([
            Step(0, Fixtures.ValidateOrder),
            Step(1, Fixtures.ReserveInventory, Fixtures.ReleaseInventory),
            Step(2, Fixtures.CapturePayment),
        ]);

        graph.Count.ShouldBe(3);
        graph[1].IsCompensable.ShouldBeTrue();
        graph[0].IsCompensable.ShouldBeFalse();
        graph[2].Capability.ShouldBe(Fixtures.CapturePayment);
    }

    [Fact]
    public void RejectsAnEmptyGraph()
        => Should.Throw<InvalidFlowPlanException>(() => StepGraph.Create([]));

    [Fact]
    public void RejectsDuplicateIndices()
    {
        var error = Should.Throw<InvalidFlowPlanException>(() => StepGraph.Create([
            Step(0, Fixtures.ValidateOrder),
            Step(0, Fixtures.ReserveInventory),
        ]));

        error.Message.ShouldContain("0");
    }

    [Fact]
    public void RejectsNonContiguousIndices()
    {
        // A gap means the generator emitted a step it then dropped. Catching it here
        // turns a silent skipped-step bug into a build failure.
        Should.Throw<InvalidFlowPlanException>(() => StepGraph.Create([
            Step(0, Fixtures.ValidateOrder),
            Step(2, Fixtures.ReserveInventory),
        ]));
    }

    [Fact]
    public void RejectsIndicesNotStartingAtZero()
        => Should.Throw<InvalidFlowPlanException>(() => StepGraph.Create([Step(1, Fixtures.ValidateOrder)]));

    [Fact]
    public void AcceptsStepsSuppliedOutOfOrderAndNormalisesThem()
    {
        var graph = StepGraph.Create([
            Step(2, Fixtures.CapturePayment),
            Step(0, Fixtures.ValidateOrder),
            Step(1, Fixtures.ReserveInventory),
        ]);

        graph.Steps.Select(static s => s.Index).ShouldBe([0, 1, 2]);
        graph[0].Capability.ShouldBe(Fixtures.ValidateOrder);
    }

    [Fact]
    public void ACompensationMustDifferFromTheStepItCompensates()
    {
        var error = Should.Throw<InvalidFlowPlanException>(
            () => Step(0, Fixtures.ReserveInventory, Fixtures.ReserveInventory));

        error.Message.ShouldContain("inventory.reserve");
        // A step that compensates itself would run the same effect twice on the
        // failure path — the opposite of an undo.
    }

    [Fact]
    public void EmitStepsCarryAnEventTypeAndNoCapability()
    {
        var step = StepNode.ForEmit(0, "order.placed");

        step.Kind.ShouldBe(StepKind.Emit);
        step.EventType.ShouldBe("order.placed");
        step.Capability.ShouldBeNull();
        step.IsCompensable.ShouldBeFalse();
    }

    [Fact]
    public void ABranchCarriesOnlyItsFalseTarget()
    {
        // The true path needs no target: the `then` block is laid out immediately after
        // the branch, so taking it is the ordinary next index.
        var branch = StepNode.ForBranch(0, falseTarget: 3);

        branch.Kind.ShouldBe(StepKind.Branch);
        branch.Target.ShouldBe(3);
        branch.IsControlTransfer.ShouldBeTrue();
        branch.Capability.ShouldBeNull();
        branch.IsCompensable.ShouldBeFalse();
    }

    [Fact]
    public void AnOrdinaryStepHasNoTarget()
    {
        Step(0, Fixtures.ValidateOrder).Target.ShouldBeNull();
        Step(0, Fixtures.ValidateOrder).IsControlTransfer.ShouldBeFalse();
        StepNode.ForEmit(0, "order.placed").Target.ShouldBeNull();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    [InlineData(0)]
    public void ATargetMustPointForward(int target)
    {
        // 2 is backwards, 1 is a self-loop, 0 is further backwards. None of the three is
        // something `When` can express, so all three are layout bugs — and each would
        // make the engine's step loop run forever rather than fail.
        Should.Throw<InvalidFlowPlanException>(() => StepNode.ForBranch(2, target))
            .Message.ShouldContain("forward");

        Should.Throw<InvalidFlowPlanException>(() => StepNode.ForJump(2, target));
    }

    [Fact]
    public void ATargetMayBeOnePastTheLastStepBecauseThatEndsTheFlow()
    {
        // The layout of a `When` written at the tail of a chain: the false path has
        // nowhere to go but out.
        var graph = StepGraph.Create([
            Step(0, Fixtures.ValidateOrder),
            StepNode.ForBranch(1, falseTarget: 3),
            Step(2, Fixtures.CapturePayment),
        ]);

        graph[1].Target.ShouldBe(3);
    }

    [Fact]
    public void RejectsATargetPastTheEndOfTheGraph()
    {
        // The factory cannot catch this — it does not know how many steps there will be.
        // Left unchecked it surfaces as an IndexOutOfRangeException from the middle of a
        // flow, after some of its steps have already run.
        var error = Should.Throw<InvalidFlowPlanException>(() => StepGraph.Create([
            Step(0, Fixtures.ValidateOrder),
            StepNode.ForBranch(1, falseTarget: 4),
            Step(2, Fixtures.CapturePayment),
        ]));

        error.Message.ShouldContain("4");
        error.Message.ShouldContain("3");
    }

    [Fact]
    public void RejectsAJumpTargetPastTheEndOfTheGraph()
    {
        Should.Throw<InvalidFlowPlanException>(() => StepGraph.Create([
            Step(0, Fixtures.ValidateOrder),
            StepNode.ForJump(1, target: 9),
        ]));
    }

    [Fact]
    public void AcceptsTheFullConditionalLayout()
    {
        // The shape the emitter produces for
        // `.Step<A>().When(p, t => t.Step<B>()).Otherwise(o => o.Step<C>()).Step<D>()`.
        var graph = StepGraph.Create([
            Step(0, Fixtures.ValidateOrder),
            StepNode.ForBranch(1, falseTarget: 4),
            Step(2, Fixtures.ReserveInventory),
            StepNode.ForJump(3, target: 5),
            Step(4, Fixtures.CapturePayment),
            Step(5, Fixtures.ValidateOrder),
        ]);

        graph.Count.ShouldBe(6);
        graph[1].Target.ShouldBe(4);
        graph[3].Target.ShouldBe(5);
    }

    [Fact]
    public void ASwitchCarriesATargetPerCaseAndOneForTheMiss()
    {
        var node = StepNode.ForSwitch(0, [1, 3], defaultTarget: 5);

        node.Kind.ShouldBe(StepKind.Switch);
        node.CaseTargets.ShouldBe([1, 3]);
        node.Target.ShouldBe(5, "The default target is where a value matching no case goes.");
        node.IsControlTransfer.ShouldBeTrue();
        node.Capability.ShouldBeNull();
        node.IsCompensable.ShouldBeFalse();
    }

    [Fact]
    public void ANonSwitchHasNoCaseTargets()
    {
        Step(0, Fixtures.ValidateOrder).CaseTargets.ShouldBeEmpty();
        StepNode.ForBranch(0, falseTarget: 1).CaseTargets.ShouldBeEmpty();
    }

    [Fact]
    public void RejectsASwitchWithNoCases()
    {
        // Nothing to select between, so it would always take its default — an
        // unconditional transfer wearing the costume of a decision.
        Should.Throw<InvalidFlowPlanException>(() => StepNode.ForSwitch(0, [], defaultTarget: 1))
            .Message.ShouldContain("no cases");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    [InlineData(0)]
    public void ACaseTargetMustPointForward(int target)
    {
        // Same three shapes as a branch's, and the same reason: `Case` can only skip
        // steps, never repeat them, so any of these is a layout bug that would make the
        // step loop run forever.
        Should.Throw<InvalidFlowPlanException>(() => StepNode.ForSwitch(2, [target], defaultTarget: 3))
            .Message.ShouldContain("forward");

        Should.Throw<InvalidFlowPlanException>(() => StepNode.ForSwitch(2, [3], defaultTarget: target))
            .Message.ShouldContain("forward");
    }

    [Fact]
    public void RejectsACaseTargetPastTheEndOfTheGraph()
    {
        // The factory cannot catch this — it does not know how many steps there will be.
        // Checking only the default target would leave the proof holding for the arm
        // nobody takes and not for the arms they do.
        var error = Should.Throw<InvalidFlowPlanException>(() => StepGraph.Create([
            Step(0, Fixtures.ValidateOrder),
            StepNode.ForSwitch(1, [2, 9], defaultTarget: 3),
            Step(2, Fixtures.CapturePayment),
        ]));

        error.Message.ShouldContain("9");
        error.Message.ShouldContain("3");
    }

    [Fact]
    public void AcceptsTheFullSwitchLayout()
    {
        // The shape the emitter produces for
        // `.Switch(s).Case(a, b => b.Step<B>()).Case(c, b => b.Step<C>()).Default(b => b.Step<D>()).Step<E>()`.
        var graph = StepGraph.Create([
            Step(0, Fixtures.ValidateOrder),
            StepNode.ForSwitch(1, [2, 4], defaultTarget: 6),
            Step(2, Fixtures.ReserveInventory),
            StepNode.ForJump(3, target: 7),
            Step(4, Fixtures.CapturePayment),
            StepNode.ForJump(5, target: 7),
            Step(6, Fixtures.ValidateOrder),
            Step(7, Fixtures.CapturePayment),
        ]);

        graph.Count.ShouldBe(8);
        graph[1].CaseTargets.ShouldBe([2, 4]);
        graph[1].Target.ShouldBe(6);
    }

    [Fact]
    public void ASwitchWithNoDefaultTargetsTheJoinWhichMayEndTheFlow()
    {
        // The layout of a `Switch` written at the tail of a chain with no `Default`: a
        // value matching nothing has nowhere to go but out. One past the last step is the
        // only out-of-range target the graph permits, and this is the shape it exists for.
        var graph = StepGraph.Create([
            Step(0, Fixtures.ValidateOrder),
            StepNode.ForSwitch(1, [2], defaultTarget: 3),
            Step(2, Fixtures.CapturePayment),
        ]);

        graph[1].Target.ShouldBe(3);
    }

    // ------------------------------------------------------------------ parallel

    [Fact]
    public void AParallelCarriesATargetPerBranchAndOneForTheJoin()
    {
        var node = StepNode.ForParallel(0, [1, 3], joinTarget: 5, MergeStrategy.AllSettled);

        node.Kind.ShouldBe(StepKind.Parallel);
        node.BranchTargets.ShouldBe([1, 3]);
        node.Target.ShouldBe(5, "The join is where control resumes once the merge is satisfied.");
        node.Merge.ShouldBe(MergeStrategy.AllSettled);
        node.CaseTargets.ShouldBeEmpty("Branch targets are not case targets; they mean the opposite thing.");
        node.IsCompensable.ShouldBeFalse();
    }

    [Fact]
    public void AParallelDefaultsToTheStrictestMerge()
    {
        // default(MergeStrategy) is AllMustSucceed. A default that tolerated a failed
        // branch would be the wrong way round: silence should not buy leniency.
        StepNode.ForParallel(0, [1, 2], joinTarget: 3).Merge.ShouldBe(MergeStrategy.AllMustSucceed);
    }

    [Fact]
    public void AcceptsTheFullParallelLayout()
    {
        // The shape the emitter produces for
        // `.Step<A>().Parallel(p => p.Branch<B>().Branch(x => x.Step<C>().Step<D>()), merge).Step<E>()`.
        // No closing jumps: a branch's range ends where the next branch begins, so a jump
        // to the join would be a step that only restates the range bound.
        var graph = StepGraph.Create([
            Step(0, Fixtures.ValidateOrder),
            StepNode.ForParallel(1, [2, 3], joinTarget: 5),
            Step(2, Fixtures.ReserveInventory),
            Step(3, Fixtures.CapturePayment),
            Step(4, Fixtures.ValidateOrder),
            Step(5, Fixtures.CapturePayment),
        ]);

        graph.Count.ShouldBe(6);
        graph[1].BranchTargets.ShouldBe([2, 3]);
        graph[1].Target.ShouldBe(5);
    }

    [Fact]
    public void AParallelMayJoinOnePastTheLastStepBecauseThatEndsTheFlow()
    {
        var graph = StepGraph.Create([
            StepNode.ForParallel(0, [1, 2], joinTarget: 3),
            Step(1, Fixtures.ReserveInventory),
            Step(2, Fixtures.CapturePayment),
        ]);

        graph[0].Target.ShouldBe(3);
    }

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 3 })]
    public void RejectsAParallelWithFewerThanTwoBranches(int[] targets)
    {
        // One branch is a sequence wearing a costume: it would buy a linked token, a task
        // array and an await for work that happens in exactly one order anyway. The
        // generator lays such a declaration out inline, so reaching here is a layout bug.
        Should.Throw<InvalidFlowPlanException>(
                () => StepNode.ForParallel(2, targets, joinTarget: 9))
            .Message.ShouldContain("two");
    }

    [Theory]
    [InlineData(new[] { 3, 3 })]
    [InlineData(new[] { 5, 4 })]
    public void RejectsBranchTargetsThatDoNotStrictlyAscend(int[] targets)
    {
        // Equal targets give a branch an empty range, and a branch that runs no steps
        // still counts towards a quorum — which is a silent way to turn Quorum(2) into
        // Quorum(1). Descending targets give two branches an overlapping range, so the
        // same step would run twice and be compensated twice.
        Should.Throw<InvalidFlowPlanException>(
                () => StepNode.ForParallel(2, targets, joinTarget: 9))
            .Message.ShouldContain("ascending");
    }

    [Fact]
    public void RejectsABranchTargetThatDoesNotPointForward()
        => Should.Throw<InvalidFlowPlanException>(
                () => StepNode.ForParallel(4, [2, 6], joinTarget: 9))
            .Message.ShouldContain("ascending");

    [Fact]
    public void RejectsAJoinThatIsNotPastTheLastBranch()
    {
        // The last branch owns [lastTarget, join). A join at or before the last target
        // gives it an empty range, so its steps would never run at all while the merge
        // still waited for it.
        Should.Throw<InvalidFlowPlanException>(
                () => StepNode.ForParallel(0, [1, 3], joinTarget: 3))
            .Message.ShouldContain("past its last branch");
    }

    [Fact]
    public void RejectsABranchTargetPastTheEndOfTheGraph()
    {
        // Only the graph knows the length. Left unchecked, this is not a wrong answer but
        // an IndexOutOfRangeException thrown from a thread-pool thread halfway through a
        // fork — the worst place in the runtime to discover a layout bug.
        var error = Should.Throw<InvalidFlowPlanException>(() => StepGraph.Create([
            StepNode.ForParallel(0, [1, 9], joinTarget: 10),
            Step(1, Fixtures.ReserveInventory),
            Step(2, Fixtures.CapturePayment),
        ]));

        // The branch target is named, not the join. A fork's join is by construction its
        // largest target, so a message that always named the join would never name the
        // block that is actually wrong.
        error.Message.ShouldContain("9");
    }

    [Fact]
    public void RejectsAParallelJoinPastTheEndOfTheGraph()
        => Should.Throw<InvalidFlowPlanException>(() => StepGraph.Create([
            StepNode.ForParallel(0, [1, 2], joinTarget: 7),
            Step(1, Fixtures.ReserveInventory),
            Step(2, Fixtures.CapturePayment),
        ]));

    [Fact]
    public void EveryTargetOfAParallelStillPointsForwardSoTheLoopTerminates()
    {
        // The termination proof does not weaken for a fork: each branch is a forward-only
        // walk over a bounded sub-range of the same array, and the ranges are disjoint.
        var graph = StepGraph.Create([
            StepNode.ForParallel(0, [1, 3], joinTarget: 5),
            Step(1, Fixtures.ReserveInventory),
            StepNode.ForJump(2, target: 3),
            Step(3, Fixtures.CapturePayment),
            Step(4, Fixtures.ValidateOrder),
            Step(5, Fixtures.ValidateOrder),
        ]);

        foreach (var step in graph.Steps)
        {
            foreach (var target in step.BranchTargets)
            {
                target.ShouldBeGreaterThan(step.Index);
                target.ShouldBeLessThanOrEqualTo(graph.Count);
            }
        }
    }

    [Fact]
    public void AParallelDescribesItselfWithItsBranchesAndItsMerge()
        => StepNode.ForParallel(1, [2, 3], joinTarget: 4, MergeStrategy.Quorum(2))
            .ToString()
            .ShouldBe("[1] parallel 2, 3 (Quorum(2)), join 4");

    [Fact]
    public void APlanKnowsWhetherItForksSoTheRuntimeNeverHasToScan()
    {
        var linear = ExecutionPlan.Create(
            FlowDescriptor.Create("order.linear", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(1)),
            StepGraph.Create([Step(0, Fixtures.ValidateOrder)]));

        var forking = ExecutionPlan.Create(
            FlowDescriptor.Create("order.forking", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(1)),
            StepGraph.Create([
                StepNode.ForParallel(0, [1, 2], joinTarget: 3),
                Step(1, Fixtures.ReserveInventory),
                Step(2, Fixtures.CapturePayment),
            ]));

        linear.HasParallel.ShouldBeFalse(
            "A flow that never forks must not pay for the guarding a forking one needs.");
        forking.HasParallel.ShouldBeTrue();
    }

    // --------------------------------------------------------------------------- ForEach

    private static ForEachOptions Bounded(int max = 1, bool continueOnError = false) => new()
    {
        MaxDegreeOfParallelism = max,
        ContinueOnError = continueOnError,
    };

    private static ExecutionPlan LoopPlan(StepNode loop) => ExecutionPlan.Create(
        FlowDescriptor.Create("order.loop", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(1)),
        StepGraph.Create([loop, Step(1, Fixtures.ReserveInventory)]));

    [Fact]
    public void AForEachCarriesItsJoinAndItsBoundsAndNothingElse()
    {
        var loop = StepNode.ForEach(1, joinTarget: 4, Bounded(4, continueOnError: true));

        loop.Kind.ShouldBe(StepKind.ForEach);
        loop.Target.ShouldBe(4);
        loop.MaxDegreeOfParallelism.ShouldBe(4);
        loop.ContinueOnError.ShouldBeTrue();

        loop.BranchTargets.ShouldBeEmpty(
            "The body has no target of its own: it is the span from the node to the join, " +
            "and storing where it starts would be a second copy of a fact the layout " +
            "already fixes.");
        loop.CaseTargets.ShouldBeEmpty();
    }

    [Fact]
    public void AnOrdinaryStepIsBoundedAtOneSoNothingElsePaysForConcurrency()
        => Step(0, Fixtures.ValidateOrder).MaxDegreeOfParallelism.ShouldBe(1);

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void RejectsAForEachWithNoBody(int joinOffset)
        // A loop that runs nothing per element still evaluates its selector and still
        // costs an iteration each time. The generator lays such a declaration out as
        // nothing at all, so this rejects a layout bug rather than something an author
        // can write.
        => Should.Throw<InvalidFlowPlanException>(
            () => StepNode.ForEach(2, joinTarget: 2 + joinOffset, Bounded()));

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void RejectsANonPositiveConcurrencyBound(int max)
        => Should.Throw<InvalidFlowPlanException>(
            () => StepNode.ForEach(0, joinTarget: 2, Bounded(max)));

    [Fact]
    public void TheConcurrencyBoundIsCappedByTheRuntimeRatherThanRefused()
    {
        // "Bounded by construction: MaxDegreeOfParallelism is required and capped by the
        // runtime" — 08-Flow-Definition.md §3.4. An author asking for a thousand concurrent
        // reservations has asked for something reasonable that this runtime will not do,
        // and failing the build over it would be refusing to run a correct flow. The plan
        // therefore carries the bound that will actually be honoured, so reading it back
        // tells the truth.
        StepNode.ForEach(0, joinTarget: 2, Bounded(1000))
            .MaxDegreeOfParallelism.ShouldBe(StepNode.MaxIterationConcurrency);
    }

    [Fact]
    public void TheCapIsAConstantRatherThanAPropertyOfTheBuildMachine()
        // Derived from Environment.ProcessorCount it would compile to a different graph on
        // a laptop and in CI, and the manifest would stop being reproducible.
        => StepNode.MaxIterationConcurrency.ShouldBe(64);

    [Fact]
    public void AcceptsTheFullForEachLayout()
    {
        // 0 validate · 1 foreach(body 2..4, join 4) · 2 reserve · 3 capture · 4 validate.
        // No closing jump: the body's end is the join, and the engine re-enters the span
        // rather than falling out of it.
        var graph = StepGraph.Create([
            Step(0, Fixtures.ValidateOrder),
            StepNode.ForEach(1, joinTarget: 4, Bounded(2)),
            Step(2, Fixtures.ReserveInventory, Fixtures.ReleaseInventory),
            Step(3, Fixtures.CapturePayment),
            Step(4, Fixtures.ValidateOrder),
        ]);

        graph.Count.ShouldBe(5);
        graph[1].Target.ShouldBe(4);
    }

    [Fact]
    public void AForEachMayJoinOnePastTheLastStepBecauseThatEndsTheFlow()
    {
        var graph = StepGraph.Create([
            StepNode.ForEach(0, joinTarget: 2, Bounded()),
            Step(1, Fixtures.ReserveInventory),
        ]);

        graph[0].Target.ShouldBe(graph.Count);
    }

    [Fact]
    public void RejectsAForEachJoinPastTheEndOfTheGraph()
        // Only the graph knows how long it is, so this cannot be caught in the factory —
        // and left unchecked it is an IndexOutOfRangeException thrown from the middle of a
        // flow, after some of its elements have already been processed.
        => Should.Throw<InvalidFlowPlanException>(() => StepGraph.Create([
            StepNode.ForEach(0, joinTarget: 5, Bounded()),
            Step(1, Fixtures.ReserveInventory),
        ]));

    [Fact]
    public void EveryPassOverAForEachBodyStillTerminatesForTheOldReason()
    {
        // The forward-target rule proves each pass terminates; it does not prove the loop
        // over passes does, because the body is deliberately re-entered. What bounds that
        // is the element count, read once before the first pass — see IterationSource.
        var graph = StepGraph.Create([
            StepNode.ForEach(0, joinTarget: 4, Bounded()),
            StepNode.ForBranch(1, falseTarget: 3),
            Step(2, Fixtures.ReserveInventory),
            Step(3, Fixtures.CapturePayment),
            Step(4, Fixtures.ValidateOrder),
        ]);

        foreach (var step in graph.Steps)
        {
            if (step.Target is { } target)
            {
                target.ShouldBeGreaterThan(step.Index);
                target.ShouldBeLessThanOrEqualTo(graph.Count);
            }
        }
    }

    [Fact]
    public void AForEachDescribesItselfWithItsBodyAndItsBound()
        => StepNode.ForEach(1, joinTarget: 4, Bounded(4, continueOnError: true))
            .ToString()
            .ShouldBe("[1] foreach 2..4 (max 4, continue on error)");

    [Fact]
    public void APlanKnowsWhetherAnIterationCanBeReachedByTwoThreads()
    {
        // The question is not "does the flow loop" but "can two threads reach the context".
        // A loop that runs one element at a time cannot, so it keeps the unguarded fast
        // path exactly as a conditional does — which is what keeps budget B2 a hard zero
        // for everything that does not actually fork.
        LoopPlan(StepNode.ForEach(0, joinTarget: 2, Bounded())).HasParallel.ShouldBeFalse();
        LoopPlan(StepNode.ForEach(0, joinTarget: 2, Bounded(2))).HasParallel.ShouldBeTrue();
    }

    // ------------------------------------------------------------------------- sub-flows

    [Fact]
    public void ASubFlowCarriesTheChildsIdentityAndNothingElse()
    {
        var composition = StepNode.ForSubFlow(1, "order.fulfil");

        composition.Kind.ShouldBe(StepKind.SubFlow);
        composition.SubFlowId.ShouldBe("order.fulfil");
        composition.Mode.ShouldBe(SubFlowMode.Inline);

        composition.Target.ShouldBeNull(
            "A sub-flow names another flow's plan, not a position in this array. It is the " +
            "one composite kind with no target at all.");
        composition.CaseTargets.ShouldBeEmpty();
        composition.BranchTargets.ShouldBeEmpty();
        composition.Capability.ShouldBeNull();
        composition.IsControlTransfer.ShouldBeFalse(
            "It does work — a whole flow of it — so it is not a transfer.");
    }

    [Fact]
    public void AnInlineSubFlowIsCompensableWithoutNamingACompensation()
    {
        // It has none of its own. What it may have to undo is whatever the *child*
        // completed, which the parent's plan cannot know — the child is compiled separately
        // and may live in another assembly. Reporting true is what makes the parent build a
        // compensation stack at all, and without it the child's completed work would be
        // silently unrecoverable.
        StepNode.ForSubFlow(0, "order.fulfil").IsCompensable.ShouldBeTrue();
        StepNode.ForSubFlow(0, "order.fulfil").Compensation.ShouldBeNull();

        StepNode.ForSubFlow(0, "order.fulfil", SubFlowMode.Detached).IsCompensable.ShouldBeFalse(
            "A detached child's lifecycle is its own, so the parent failing says nothing " +
            "about it and there is nothing for the parent's unwind to do.");
    }

    [Fact]
    public void APlanKnowsWhetherItComposesAnotherFlow()
    {
        var composing = ExecutionPlan.Create(
            FlowDescriptor.Create("order.place", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(1)),
            StepGraph.Create([StepNode.ForSubFlow(0, "order.fulfil"), Step(1, Fixtures.CapturePayment)]));

        composing.HasSubFlow.ShouldBeTrue();
        composing.HasCompensation.ShouldBeTrue(
            "The composition alone makes the flow compensable.");
        composing.HasParallel.ShouldBeFalse(
            "A child runs on its own context, so composing one does not make two threads " +
            "reach this flow's — which is what keeps the state bag unguarded and budget B2 " +
            "a hard zero here.");

        LoopPlan(StepNode.ForEach(0, joinTarget: 2, Bounded())).HasSubFlow.ShouldBeFalse();
    }

    [Fact]
    public void ASubFlowDoesNotAffectTheTerminationProofForThisGraph()
    {
        // Every other kind is a statement about this array, so the forward-target rule
        // covers it. A sub-flow has no target, so there is nothing here to prove — what
        // bounds the *composition* graph is FLOWX1021 at build time and the engine's
        // nesting cap at run time, and neither is a property of one graph.
        var graph = StepGraph.Create([
            StepNode.ForBranch(0, falseTarget: 3),
            StepNode.ForSubFlow(1, "order.fulfil"),
            StepNode.ForJump(2, target: 4),
            StepNode.ForSubFlow(3, "order.notify", SubFlowMode.Detached),
            Step(4, Fixtures.CapturePayment),
        ]);

        graph.Count.ShouldBe(5);

        foreach (var step in graph.Steps)
        {
            if (step.Target is { } target)
            {
                target.ShouldBeGreaterThan(step.Index);
                target.ShouldBeLessThanOrEqualTo(graph.Count);
            }
        }
    }

    [Fact]
    public void ASubFlowMustNameAWellFormedFlowIdentity()
        // The id reaches the manifest and a rendered diagram, where a reader matches it
        // against the child's own entry. An id that is not <domain>.<verb> would match
        // nothing and could not be followed.
        => Should.Throw<ArgumentException>(() => StepNode.ForSubFlow(0, "fulfil"));

    [Fact]
    public void AwaitCompletionCannotBeBuiltIntoAPlan()
    {
        // FLOWX1026 refuses it at build time; this refuses it in the one place a plan can
        // be built by hand. Neither degenerate form is honest: running it inline changes the
        // parent's deadline and failure semantics, and skipping it drops business logic.
        var thrown = Should.Throw<InvalidFlowPlanException>(
            () => StepNode.ForSubFlow(0, "order.fulfil", SubFlowMode.AwaitCompletion));

        thrown.Message.ShouldContain("journal");
    }

    [Fact]
    public void ASubFlowDescribesItselfWithTheChildAndTheMode()
        => StepNode.ForSubFlow(2, "order.fulfil", SubFlowMode.Detached)
            .ToString()
            .ShouldBe("[2] subflow order.fulfil (Detached)");

    /// <summary>A poll's satisfied path must lie past the attempt it polls with.</summary>
    /// <remarks>
    /// The forward-target rule read one index further out. A target of <c>index + 1</c> points
    /// <em>at</em> the body, so a satisfied poll would land on the step that satisfied it and
    /// run it again — which is the loop the rule exists to make unrepresentable, wearing a
    /// legal-looking number.
    /// </remarks>
    [Fact]
    public void APollsSatisfiedPathMustLiePastItsAttempt() =>
        Should.Throw<InvalidFlowPlanException>(
            () => StepNode.ForPoll(0, Backoff.Exponential("PT5S", "PT5M"), TimeSpan.FromHours(4), 1))
            .Message.ShouldContain("past the attempt");

    /// <summary>A poll with no gap between attempts is refused.</summary>
    /// <remarks>
    /// A zero interval parks the instance on an instant already in the past, so every sweep
    /// finds it due and the flow spends its whole budget hot-looping against somebody else's
    /// service — which is the shape polling exists to replace.
    /// </remarks>
    [Fact]
    public void APollWithNoGapBetweenAttemptsIsRefused() =>
        Should.Throw<InvalidFlowPlanException>(
            () => StepNode.ForPoll(0, Backoff.Exponential(TimeSpan.Zero, TimeSpan.Zero), TimeSpan.FromHours(4)))
            .Message.ShouldContain("no gap");

    /// <summary>A poll describes itself with its body, its schedule and its budget.</summary>
    [Fact]
    public void APollDescribesItselfWithItsBodyAndItsBudget() =>
        StepNode.ForPoll(1, Backoff.Exponential("PT5S", "PT5M"), TimeSpan.FromHours(4), 5)
            .ToString()
            .ShouldBe("[1] poll 2 every 00:00:05..00:05:00 for 04:00:00, else 3, satisfied 5");

    [Fact]
    public void TheGraphIsImmutableOnceBuilt()
    {
        var steps = new List<StepNode> { Step(0, Fixtures.ValidateOrder) };
        var graph = StepGraph.Create(steps);

        steps.Add(Step(1, Fixtures.CapturePayment));

        graph.Count.ShouldBe(1, "The graph must copy its input, not alias it.");
    }
}
