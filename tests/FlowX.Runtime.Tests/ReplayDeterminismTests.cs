using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// The replay contract, under test rather than stated:
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/06-Execution-Engine.md">06 §5</a>
/// says that replaying a completed durable instance must produce byte-identical step inputs
/// and identical control flow, and until this suite existed nothing compared a run against its
/// replay.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is risk R2's actual mitigation, and it is a different kind of thing from the
/// three analyzers WP-58 shipped.</strong> <c>FLOWX1007</c>–<c>FLOWX1009</c> forbid the
/// ordinary ways flow and capability code stops being deterministic, which is prevention.
/// Forbidding the ways a property can break is not the same as showing it holds — a
/// suppressed rule, a capability the compilation cannot see, and a defect in the capture
/// itself all pass a build and none of them pass this suite.
/// </para>
/// <para>
/// <strong>What is asserted is not "it did not throw".</strong> Every shape below runs twice:
/// once against the world, and once against the journal the first run wrote, with the second
/// run's clock a hundred days from the first's so that a value which was not replayed cannot
/// be mistaken for one that was. The two runs are then compared through both channels the
/// harness watches — every action the flow took, and every row the journal holds including the
/// full <c>NondeterminismCapture</c> — and any difference is reported by name. The tests below
/// that end in "AndTheHarnessSaysSo" exist to prove the comparison can fail: a determinism
/// gate that cannot go red is worth nothing, and this repository has removed several of
/// exactly that kind.
/// </para>
/// <para>
/// <strong>Three fidelity limits are pinned here rather than hidden.</strong> A <c>Parallel</c>
/// whose branches genuinely overlap does not replay, because one pooled context is shared by
/// every branch and a capture can land on a sibling's row; a compensation's ambient reads are
/// never captured at all; and the engine's own deadline check is not replayed, because the
/// step loop has no per-step hook a replay driver could use. Each has a test that goes red the
/// day the limit is fixed, which is the only kind of note about a known limit that survives.
/// </para>
/// </remarks>
public sealed class ReplayDeterminismTests
{
    private static readonly string[] ThreeLines = ["line-a", "line-b", "line-c"];

    private static readonly Error Declined =
        new("payment.declined", "issuer declined", ErrorCategory.Conflict);

    /// <summary>The same plan, re-declared <c>Durable</c>.</summary>
    /// <remarks>
    /// Rebuilt from an existing graph rather than written out again, for the reason
    /// <c>DurableSeamTests</c> does the same: a durable flow is not a different shape, it is
    /// the same shape with a different declaration. The corpus is the shapes the DSL already
    /// ships, replayed — not a set of flows written to be replayable.
    /// </remarks>
    private static ExecutionPlan Durable(ExecutionPlan plan) => ExecutionPlan.Create(
        FlowDescriptor.Create(
            plan.Flow.Id, plan.Flow.Version, ExecutionProfile.Durable, plan.Flow.Deadline),
        plan.Graph);

    /// <summary>A two-branch fork: <c>0 parallel(→1,2 join 3) · 1 reserve · 2 capture · 3 emit</c>.</summary>
    /// <remarks>
    /// Two rather than <c>Plans.Parallel</c>'s three, because the limit demonstrated on it is
    /// an exact interleaving between one branch and one sibling. A third branch running while
    /// the first is suspended would be a third participant in the ordering and the
    /// demonstration would stop being deterministic.
    /// </remarks>
    private static ExecutionPlan TwoBranchFork() => ExecutionPlan.Create(
        FlowDescriptor.Create(
            "order.screen", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForParallel(0, [1, 2], joinTarget: 3),
            StepNode.ForCapability(1, Plans.Reserve, Plans.Release),
            StepNode.ForCapability(2, Plans.Capture),
            StepNode.ForEmit(3, "order.screened"),
        ]));

    // ---------------------------------------------------------------------------------
    // The corpus: one journaled instance per shape that makes replay hard
    // ---------------------------------------------------------------------------------

    /// <summary>A straight line of four steps replays step for step.</summary>
    /// <remarks>
    /// The shape with nothing in it, first, because a corpus whose simplest member does not
    /// replay has nothing to say about its hardest.
    /// </remarks>
    [Fact]
    public async Task ALinearFlowReplaysStepForStep()
    {
        var report = await ReplayHarness.RunAsync(
            Durable(Plans.FourStepSaga()),
            () => new CorpusFlow("order.place")
                .Does(0, step => step.Wrote("validated"))
                .Does(1, step => step.Wrote("reserved"))
                .Does(2, step => step.Wrote("captured"))
                .Does(3, step => step.Wrote("emitted")),
            ct: TestContext.Current.CancellationToken);

        report.ShouldBeIdentical();

        report.Original.Instances.ShouldHaveSingleItem().Rows.Count.ShouldBe(
            4, "Four steps, four committed rows, and the replay committed the same four.");
    }

    /// <summary>
    /// A <c>When</c> takes the arm it took, and the arm is derived rather than recorded.
    /// </summary>
    /// <remarks>
    /// <strong>This is the shape ADR-0015's first amendment made load-bearing.</strong> The
    /// journal has no field for the branch taken; the amendment resolved that by re-evaluating
    /// the predicate against the restored state bag. So the predicate here reads a value a
    /// step wrote and the journal stored, and the replay reaches the same arm by computing it
    /// — which is the only way replay of control flow can be observed at all.
    /// </remarks>
    [Theory]
    [InlineData(1, "the Then arm")]
    [InlineData(0, "the Otherwise arm")]
    public async Task AConditionalReplaysTheArmItDerived(int route, string which)
    {
        var report = await ReplayHarness.RunAsync(
            Durable(Plans.Conditional()),
            () => new CorpusFlow("order.review")
                .Does(0, step => step.Routes(route))
                .Answers(1, ctx => ctx.Get<Ledger>().Route == 1),
            ct: TestContext.Current.CancellationToken);

        report.ShouldBeIdentical();

        report.Original.Trace.ShouldContain(
            $"order.review evaluate 1 -> {route == 1}", $"The corpus must actually take {which}.");
    }

    /// <summary>A <c>Switch</c> selects the arm it selected, from the restored state bag.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(-1)]
    public async Task ASwitchReplaysTheArmItSelected(int arm)
    {
        var report = await ReplayHarness.RunAsync(
            Durable(Plans.Switching()),
            () => new CorpusFlow("order.price")
                .Does(0, step => step.Routes(arm))
                .Selects(1, ctx => ctx.Get<Ledger>().Route),
            ct: TestContext.Current.CancellationToken);

        report.ShouldBeIdentical();
    }

    /// <summary>
    /// A <c>ForEach</c> replays every iteration, in its own scope, over the same collection.
    /// </summary>
    /// <remarks>
    /// Scope re-entry is what makes a loop hard to replay: the same step index commits once
    /// per element, so the capture for the second element must reach the second element and
    /// not the first. The bodies read the element, so a replay that re-entered the scopes in
    /// another order would write a different ledger on the first row where it differed.
    /// </remarks>
    [Fact]
    public async Task AForEachReplaysEveryIterationInItsOwnScope()
    {
        var report = await ReplayHarness.RunAsync(
            Durable(Plans.ForEach()),
            () => new CorpusFlow("order.reserve")
                .Iterates(1, ThreeLines)
                .Does(2, step => step.Wrote($"reserved {step.Context.Get<string>()}"))
                .Does(3, step => step.Wrote($"captured {step.Context.Get<string>()}")),
            ct: TestContext.Current.CancellationToken);

        report.ShouldBeIdentical();

        var scopes = report.Original.Instances[0].Rows
            .Where(row => !row.Key.Scope.IsRoot)
            .Select(row => row.Key.Scope.Text)
            .Distinct()
            .ToList();

        scopes.Count.ShouldBe(3, "Three elements, three iteration scopes, and the replay re-entered all three.");
    }

    /// <summary>A fork replays when its branches do not overlap.</summary>
    /// <remarks>
    /// <strong>The qualifier is the finding, not a hedge.</strong> A branch whose every step
    /// completes synchronously runs to the end before its sibling starts, so the shared context
    /// is only ever used by one branch at a time and every capture lands on the row of the step
    /// that made it. When the branches genuinely interleave that stops being true, and
    /// <see cref="AForkWhoseBranchesOverlapDoesNotReplayAndTheHarnessSaysSo"/> is what happens
    /// then.
    /// </remarks>
    [Fact]
    public async Task AParallelForkReplaysWhenItsBranchesDoNotOverlap()
    {
        var report = await ReplayHarness.RunAsync(
            Durable(Plans.Parallel()),
            () => new CorpusFlow("order.screen"),
            ct: TestContext.Current.CancellationToken);

        report.ShouldBeIdentical();
        report.Original.Instances[0].Rows.Count.ShouldBe(5);
    }

    /// <summary>A composed sub-flow replays, and so does the child's own instance.</summary>
    /// <remarks>
    /// A child is its own <c>flow_instance</c> row (ADR-0015's third commitment), so "the
    /// replay reproduced it" is a claim about two histories rather than one, and the harness
    /// compares every instance the run opened, in the order it opened them.
    /// </remarks>
    [Fact]
    public async Task ASubFlowReplaysTheChildItComposed()
    {
        var report = await ReplayHarness.RunAsync(
            Durable(Plans.Composing()),
            Composing,
            ct: TestContext.Current.CancellationToken);

        report.ShouldBeIdentical();

        report.Original.Instances.Count.ShouldBe(2, "The parent and the child are two instances.");
        report.Original.Instances[1].Record.FlowId.ShouldBe("order.fulfil");
    }

    /// <summary>A detached child replays, with the ordering it declined to declare left alone.</summary>
    [Fact]
    public async Task ADetachedSubFlowReplaysTheChildItStarted()
    {
        var report = await ReplayHarness.RunAsync(
            Durable(Plans.Composing(SubFlowMode.Detached)),
            Composing,
            order: TraceOrder.AsSet,
            ct: TestContext.Current.CancellationToken);

        report.ShouldBeIdentical();
        report.Original.Instances.Count.ShouldBe(2);
    }

    /// <summary>A flow that fails replays its failure, its unwind, and the rows for both.</summary>
    /// <remarks>
    /// The failure path is where a divergence costs most: a replay that succeeded where the
    /// original failed would describe compensations that never ran and effects that were never
    /// undone. The unwind's own rows are compared too, which is what makes this a test of
    /// <c>06 §7</c> rule 4's write half as well as of replay.
    /// </remarks>
    [Fact]
    public async Task AFailedFlowReplaysItsFailureAndItsUnwind()
    {
        var report = await ReplayHarness.RunAsync(
            Durable(Plans.FourStepSaga()),
            () => new CorpusFlow("order.place").Fails(2, Declined),
            ct: TestContext.Current.CancellationToken);

        report.ShouldBeIdentical();

        report.Original.Result.IsFailure.ShouldBeTrue();
        report.Original.Result.Error!.Code.ShouldBe("payment.declined");

        report.Original.Instances[0].Rows
            .Count(row => row.Outcome == JournalOutcome.Compensated)
            .ShouldBe(1, "Step 1 completed and is compensable; step 2 failed, so it is not on the stack.");
    }

    /// <summary>
    /// A flow that reads the clock, mints ids and draws from the generator gets all three back
    /// from the journal rather than from the world.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The shape the whole package exists for.</strong> The other members of the
    /// corpus prove that control flow and step inputs reproduce; this one proves that the
    /// non-determinism envelope is complete — that what a durable step is allowed to read from
    /// outside itself is exactly what <c>NondeterminismCapture</c> holds, and that handing it
    /// back reconstructs the run.
    /// </para>
    /// <para>
    /// The <c>Switch</c> selects on the number the generator drew, so the ambient read does not
    /// merely decorate the ledger — it decides which capability runs. A replay whose generator
    /// was not rebuilt from the captured seed takes a different arm and invokes a different
    /// capability, and the report says so on the row rather than in a payload.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFlowThatReadsAllThreeAmbientSourcesReplaysEveryOneOfThem()
    {
        var report = await ReplayHarness.RunAsync(
            Durable(Plans.Switching()),
            AmbientReader,
            ct: TestContext.Current.CancellationToken);

        report.ShouldBeIdentical();

        var opening = report.Original.Instances[0].Rows[0].Nondeterminism;

        opening.UtcNow.ShouldNotBeNull("Step 0 read the clock, so its row must carry the instant.");
        opening.NewIds.Count.ShouldBe(1, "Step 0 minted one id, so its row must carry one.");
        opening.RandomSeed.ShouldNotBeNull("Step 0 drew a number, so its row must carry the seed.");

        // Member by member rather than by record equality: NondeterminismCapture holds its
        // ids in an array, and a record's generated Equals compares an array by reference. Two
        // envelopes carrying the same ids in the same order would compare unequal, which would
        // have made this assertion fail for a reason that has nothing to do with replay.
        var replayed = report.Replayed.Instances[0].Rows[0].Nondeterminism;

        replayed.UtcNow.ShouldBe(opening.UtcNow, "The replayed step read the captured instant.");
        replayed.NewIds.ShouldBe(opening.NewIds, "The replayed step was given the captured ids, in order.");
        replayed.RandomSeed.ShouldBe(opening.RandomSeed, "The generator was rebuilt from the captured seed.");

        opening.UtcNow!.Value.ShouldBeLessThan(
            ReplayHarness.ReplayEpoch,
            "The replay's own clock is a hundred days later, so an instant from before it is " +
            "one that could only have come from the capture.");
    }

    // ---------------------------------------------------------------------------------
    // The harness can fail, and names what diverged
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A step that is not handed its capture re-reads the clock, and the harness catches it.
    /// </summary>
    /// <remarks>
    /// <strong>WP-61's exit criterion, exactly: an injected impurity — a captured clock read —
    /// caught by the test.</strong> The flow is unchanged and correct; what is withheld is the
    /// capture for one step, which is what a replay driver that missed a step would do. The
    /// hundred-day gap between the two runs' clocks is what makes the red unambiguous.
    /// </remarks>
    [Fact]
    public async Task AStepDeniedItsCaptureRereadsTheClockAndTheHarnessSaysSo()
    {
        var report = await ReplayHarness.RunAsync(
            Durable(Plans.Switching()),
            AmbientReader,
            onReplay: flow => flow.SkipPrimingAt = 0,
            ct: TestContext.Current.CancellationToken);

        report.ShouldHaveCaught("ctx.UtcNow", "read the clock");
    }

    /// <summary>A replay that mints one id more than the original is caught and named.</summary>
    /// <remarks>
    /// The capture is a list and the replay is a cursor over it, so reading past the end is the
    /// one divergence a naive driver produces silently: the extra read gets a fresh id, and
    /// nothing about the first two looks wrong. It is caught because a replayed step still
    /// records what it minted, so the extra id reaches the replayed row where the comparison
    /// sees it.
    /// </remarks>
    [Fact]
    public async Task AnExtraAmbientReadOnReplayIsCaughtAndNamed()
    {
        var report = await ReplayHarness.RunAsync(
            Durable(Plans.FourStepSaga()),
            () => new CorpusFlow("order.place").Does(0, step => step.NewId()),
            onReplay: flow => flow.Does(0, step =>
            {
                step.NewId();
                step.NewId();
            }),
            ct: TestContext.Current.CancellationToken);

        report.ShouldHaveCaught("ctx.NewId()");
    }

    /// <summary>A replay that takes a different arm is caught and named.</summary>
    /// <remarks>
    /// The divergence ADR-0015's first amendment created the exposure to: the branch is derived
    /// from the restored state, so a selector that is not a pure function of it silently
    /// reroutes the flow. <c>FLOWX1011</c> is what forbids that, it ships as a Warning, and it
    /// says nothing about capability bodies — so a runtime gate that notices is not redundant
    /// with it.
    /// </remarks>
    [Fact]
    public async Task AReplayThatSelectsADifferentArmIsCaughtAndNamed()
    {
        var report = await ReplayHarness.RunAsync(
            Durable(Plans.Switching()),
            () => new CorpusFlow("order.price")
                .Does(0, step => step.Routes(0))
                .Selects(1, ctx => ctx.Get<Ledger>().Route),
            onReplay: flow => flow.Selects(1, _ => 2),
            ct: TestContext.Current.CancellationToken);

        report.ShouldHaveCaught("select 1", "Control flow diverged");
    }

    /// <summary>
    /// A flow that mints an id outside its context cannot replay, and the harness says so.
    /// </summary>
    /// <remarks>
    /// The impurity <c>FLOWX1008</c> refuses at build time, reaching the runtime anyway —
    /// which it can, through a suppression, through a capability the compilation cannot see,
    /// or through a dependency. Nothing is injected into the replay here: the flow is impure in
    /// both runs, and the harness reports that no replay of it is possible.
    /// </remarks>
    [Fact]
    public async Task AFlowThatMintsAnAmbientIdCannotReplayAndTheHarnessSaysSo()
    {
        var report = await ReplayHarness.RunAsync(
            Durable(Plans.FourStepSaga()),
            () => new CorpusFlow("order.place").Does(1, step => step.AmbientId()),
            ct: TestContext.Current.CancellationToken);

        report.ShouldHaveCaught("Guid.NewGuid()", "FLOWX1008");
    }

    /// <summary>A flow that reads mutable static state cannot replay, and the harness says so.</summary>
    /// <remarks>
    /// The third ambient family, and the one with no envelope at all: a row records the clock,
    /// the ids and the seed, and nothing about a static. <c>FLOWX1009</c> is why.
    /// </remarks>
    [Fact]
    public async Task AFlowThatReadsAStaticCannotReplayAndTheHarnessSaysSo()
    {
        var report = await ReplayHarness.RunAsync(
            Durable(Plans.FourStepSaga()),
            () => new CorpusFlow("order.place").Does(1, step => step.AmbientCount()),
            ct: TestContext.Current.CancellationToken);

        report.ShouldHaveCaught("static counter", "FLOWX1009");
    }

    // ---------------------------------------------------------------------------------
    // Fidelity limits, pinned so that fixing one turns a test red
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Inside a fork, a branch's captured id is attributed to whichever sibling commits first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>ADR-0015 states this limit; this is it, measured.</strong> One pooled context is
    /// shared by every branch, so <c>TakeNondeterminism</c> at a branch's commit takes
    /// everything minted since the last commit — including a sibling's. The interleaving is
    /// pinned by a rendezvous rather than hoped for, so the demonstration is the same on every
    /// run: branch 1 mints an id and suspends, branch 2 mints its own and commits, and branch
    /// 2's row carries both while branch 1's carries none.
    /// </para>
    /// <para>
    /// <strong>This test goes red the day a per-branch context lands, and that is what it is
    /// for.</strong> WP-61 did not buy one — it is a change to the fork's context handling in
    /// the engine, not to the corpus — so what WP-61 bought instead is the measurement of what
    /// not having one costs. When the exactness arrives, this assertion is wrong and the note
    /// on <c>FlowExecutionContext.TakeNondeterminism</c> is too, and both should be deleted in
    /// the same commit.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AForkAttributesOneBranchsCapturedIdToItsSiblingsRow()
    {
        var plan = TwoBranchFork();

        var flow = new CorpusFlow("order.screen")
            .Does(1, step => step.NewId())
            .Does(2, step => step.NewId())
            .Rendezvous(index: 1, releasedBy: 2);

        var journal = new Conformance.InMemory.InMemoryFlowJournal();
        var begun = await DurableExecution.BeginAsync(
            journal, plan, Plans.Invocation, Guid.NewGuid(), new FencingToken(1),
            cancellationToken: TestContext.Current.CancellationToken);

        begun.IsSuccess.ShouldBeTrue();

        var result = await new FlowEngine(new DriftingClock(ReplayHarness.Epoch)).ExecuteAsync(
            plan, flow, Plans.Invocation, begun.Value, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();

        var frontier = await journal.ReadResumeFrontierAsync(
            begun.Value.InstanceId, TestContext.Current.CancellationToken);

        var suspended = frontier.Value.Committed.Single(row => row.Key.StepId == 1);
        var releasing = frontier.Value.Committed.Single(row => row.Key.StepId == 2);

        releasing.Nondeterminism.NewIds.Count.ShouldBe(
            2,
            "The releasing branch committed first, so it took everything minted since the last " +
            "commit — its own id and its suspended sibling's.");

        suspended.Nondeterminism.NewIds.ShouldBeEmpty(
            "The branch that actually minted the first id has no record of having minted " +
            "anything. This is ADR-0015's best-effort attribution, and it is why a fork needs " +
            "a per-branch context before a capture taken under one can be trusted.");
    }

    /// <summary>A fork whose branches overlap does not replay, and the harness says so.</summary>
    /// <remarks>
    /// <para>
    /// The consequence of the misattribution above, which is why the limit matters rather than
    /// merely existing. The replay hands each row back to the step it belongs to, faithfully —
    /// including the row that says the suspended branch minted nothing — so that branch mints a
    /// fresh id and the run diverges at the first thing it did.
    /// </para>
    /// <para>
    /// <strong>Red when fixed.</strong> With a per-branch context this fork replays and the
    /// expectation here inverts to <c>ShouldBeIdentical</c>. Until then, "a <c>Parallel</c>
    /// replays" is true only of forks whose branches do not overlap, and
    /// <see cref="AParallelForkReplaysWhenItsBranchesDoNotOverlap"/> is the honest statement of
    /// how much of the shape is covered.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AForkWhoseBranchesOverlapDoesNotReplayAndTheHarnessSaysSo()
    {
        var report = await ReplayHarness.RunAsync(
            TwoBranchFork(),
            () => new CorpusFlow("order.screen")
                .Does(1, step => step.NewId())
                .Does(2, step => step.NewId())
                .Rendezvous(index: 1, releasedBy: 2),
            ct: TestContext.Current.CancellationToken);

        report.ShouldHaveCaught("ctx.NewId()");
    }

    /// <summary>A compensation's ambient reads are captured by nothing, so they cannot replay.</summary>
    /// <remarks>
    /// <para>
    /// <strong>A gap in the envelope that no document names.</strong> The forward path takes a
    /// <c>NondeterminismCapture</c> at every step boundary; <c>CommitCompensationAsync</c> takes
    /// none, so an undo that reads <c>ctx.UtcNow</c> leaves no record of what it read. The
    /// journal cannot see this at all — a compensation row's envelope is empty either way —
    /// and it is caught only because the harness watches what the flow did as well as what the
    /// store holds, which is the reason it has two channels.
    /// </para>
    /// <para>
    /// <strong>Red when fixed.</strong> Giving a compensation row a capture makes this replay,
    /// and this expectation inverts.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ACompensationsAmbientReadsAreCapturedByNothingAndTheHarnessSaysSo()
    {
        var report = await ReplayHarness.RunAsync(
            Durable(Plans.FourStepSaga()),
            () => new CorpusFlow("order.place")
                .Fails(2, Declined)
                .Undoes(1, step => step.Now()),
            ct: TestContext.Current.CancellationToken);

        report.ShouldHaveCaught("ctx.UtcNow");

        report.Original.Instances[0].Rows
            .Where(row => row.Outcome == JournalOutcome.Compensated)
            .ShouldAllBe(
                row => row.Nondeterminism.IsEmpty,
                "A compensation row carries no capture, whatever the undo read.");
    }

    // ---------------------------------------------------------------------------------
    // The corpus flows the shapes above share
    // ---------------------------------------------------------------------------------

    /// <summary>A parent that composes one child, both journaled.</summary>
    private static CorpusFlow Composing() => new CorpusFlow("order.place")
        .Composes(
            1,
            Durable(Plans.Child()),
            new CorpusFlow("order.fulfil")
                .Does(0, step => step.Wrote($"fulfilled {step.Context.Get<string>()}")),
            "order-1");

    /// <summary>A flow whose control flow is decided by what it drew from <c>ctx.Random</c>.</summary>
    private static CorpusFlow AmbientReader() => new CorpusFlow("order.price")
        .Does(0, step =>
        {
            step.Wrote("validated");
            step.Now();
            step.NewId();
            step.Routes(step.Roll(3));
        })
        .Selects(1, ctx => ctx.Get<Ledger>().Route);
}
