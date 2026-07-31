using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// A fork as the engine sees it: a <see cref="StepKind.Parallel"/> carrying one target
/// per branch and a join, with each branch executed as a sub-range of the same flat step
/// array.
/// </summary>
/// <remarks>
/// <para>
/// The tests are shaped around the two claims that are easy to get wrong and impossible to
/// notice from a passing smoke test. First, that <em>every</em> branch runs — a fork that
/// silently executed one arm would look identical to a switch and pass any assertion about
/// the join. Second, that a cancelled sibling's completed work is still compensated:
/// cancellation is not a reason to leak a reservation, and <c>CompensateAsync</c>
/// deliberately ignores cancellation so that it cannot become one.
/// </para>
/// <para>
/// <c>Executed</c> is asserted as a set rather than a sequence wherever branches can
/// overlap. Completion order across branches is not deterministic and a test that pinned
/// it would be asserting the thread pool's behaviour rather than the engine's.
/// </para>
/// </remarks>
public sealed class ParallelTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static FlowEngine Engine() => new(new FakeClock(T0));

    private static readonly Error Declined =
        new("payment.declined", "declined", ErrorCategory.Conflict);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ every branch runs

    [Fact]
    public async Task EveryBranchRunsAndControlResumesAtTheJoin()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await Engine().ExecuteAsync(
            Plans.Parallel(MergeStrategy.AllMustSucceed), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeTrue();
        dispatcher.Executed.Order().ToList().ShouldBe([0, 2, 3, 4, 5],
            "Steps 2, 3 and 4 are the three branches and every one of them must run; step 5 " +
            "is the join. A fork that ran only one branch would be a switch wearing a " +
            "different name.");
        result.CompletedSteps.ShouldBe(5);
    }

    [Fact]
    public async Task ABranchIsARangeRatherThanASingleStep()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await Engine().ExecuteAsync(
            Plans.ParallelWithMultiStepBranches(), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeTrue();
        dispatcher.Executed.Order().ToList().ShouldBe([1, 2, 3, 4, 5],
            "Branch 0 owns [1, 3) and branch 1 owns [3, 5); the range bound is what ends a " +
            "branch, which is why a fork needs no closing jumps where a switch does.");
    }

    [Fact]
    public async Task BranchesOverlapWhenTheirStepsActuallyYield()
    {
        // The claim being tested is narrow and worth stating exactly: branches are started
        // eagerly on the calling thread, so branches whose steps all complete synchronously
        // finish one after another and never overlap. Only a step that yields lets a
        // sibling in — which is the case a fork exists for, since a fork is for I/O.
        var dispatcher = new RecordingDispatcher().YieldAt(2).YieldAt(3).YieldAt(4);

        var result = await Engine().ExecuteAsync(
            Plans.Parallel(), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeTrue();
        dispatcher.PeakConcurrency.ShouldBeGreaterThan(1,
            "Three yielding branches never had more than one step in flight, so they ran " +
            "sequentially and the fork bought nothing.");
    }

    [Fact]
    public async Task SynchronousBranchesStillProduceTheRightAnswerWithoutOverlapping()
    {
        var dispatcher = new RecordingDispatcher();

        var result = await Engine().ExecuteAsync(Plans.Parallel(), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeTrue();
        dispatcher.PeakConcurrency.ShouldBe(1,
            "Nothing yielded, so nothing overlapped. Correctness must not depend on it.");
    }

    // ------------------------------------------------------------------ AllMustSucceed

    [Fact]
    public async Task AllMustSucceedFailsTheFlowWithTheBranchesOwnError()
    {
        var dispatcher = new RecordingDispatcher().FailAt(3, Declined);

        var result = await Engine().ExecuteAsync(
            Plans.Parallel(MergeStrategy.AllMustSucceed), dispatcher, Plans.Invocation, Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("payment.declined",
            "There is exactly one failure, so it is the reason. Replacing it with a count " +
            "would throw away what a responder needs.");
        dispatcher.Executed.ShouldNotContain(5, "The join must not run when the merge failed.");
    }

    [Fact]
    public async Task AllMustSucceedCancelsItsSiblingsOnTheFirstFailure()
    {
        // Branch 0 fails immediately; branches 1 and 2 yield, so they are still in flight
        // when the linked token is cancelled.
        var dispatcher = new RecordingDispatcher()
            .FailAt(2, Declined)
            .YieldAt(3)
            .YieldAt(4);

        var result = await Engine().ExecuteAsync(
            Plans.Parallel(MergeStrategy.AllMustSucceed), dispatcher, Plans.Invocation, Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("payment.declined",
            "A sibling cancelled by this failure reports flow.cancelled, and that must not " +
            "displace the failure that caused it.");
    }

    /// <summary>
    /// The reconciliation the work package asked for: a cancelled sibling has completed
    /// work, and cancellation is not a reason to leave it dangling.
    /// </summary>
    /// <remarks>
    /// Branch 0's step 1 is compensable and completes; branch 1's step 3 then fails, which
    /// cancels branch 0's step 2 mid-flight. The reservation branch 0 already took must
    /// still be released — <c>CompensateAsync</c> runs under
    /// <see cref="CancellationToken.None"/> precisely so that the token which stopped the
    /// work cannot also stop the undoing of it.
    /// </remarks>
    [Fact]
    public async Task WorkACancelledSiblingHadAlreadyCompletedIsStillCompensated()
    {
        var dispatcher = new RecordingDispatcher()
            .FailAt(3, Declined)
            .YieldAt(2);

        var result = await Engine().ExecuteAsync(
            Plans.ParallelWithMultiStepBranches(MergeStrategy.AllMustSucceed),
            dispatcher,
            Plans.Invocation,
            Ct);

        result.IsFailure.ShouldBeTrue();
        result.Compensation.ShouldBe(CompensationOutcome.Succeeded);
        dispatcher.Compensated.ShouldContain(1,
            "Step 1 completed inside a branch that was later cancelled. Its compensation is " +
            "exactly the dangling state a saga exists to prevent.");
    }

    [Fact]
    public async Task NothingIsStillRunningWhenTheForkReturns()
    {
        // The pooled context is the reason this matters: a branch still writing after the
        // engine has moved on would eventually write into the next flow's context, which
        // may belong to another tenant.
        var dispatcher = new RecordingDispatcher()
            .FailAt(2, Declined)
            .YieldAt(3)
            .YieldAt(4);

        await Engine().ExecuteAsync(
            Plans.Parallel(MergeStrategy.AllMustSucceed), dispatcher, Plans.Invocation, Ct);

        var seen = dispatcher.Executed.Count;
        await Task.Delay(50, Ct);

        dispatcher.Executed.Count.ShouldBe(seen,
            "A step started after the fork returned means a branch outlived its own flow.");
    }

    // ------------------------------------------------------------------ AllSettled

    [Fact]
    public async Task AllSettledContinuesPastAFailedBranch()
    {
        var dispatcher = new RecordingDispatcher().FailAt(3, Declined);

        var result = await Engine().ExecuteAsync(
            Plans.Parallel(MergeStrategy.AllSettled), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeTrue("AllSettled collects outcomes; it does not fail the flow.");
        dispatcher.Executed.ShouldContain(5, "The join runs, which is the whole point of AllSettled.");
    }

    [Fact]
    public async Task AllSettledCancelsNothingAndRunsEveryBranchToTheEnd()
    {
        var dispatcher = new RecordingDispatcher().FailAt(2, Declined);

        await Engine().ExecuteAsync(
            Plans.Parallel(MergeStrategy.AllSettled), dispatcher, Plans.Invocation, Ct);

        dispatcher.Executed.Order().ToList().ShouldBe([0, 2, 3, 4, 5],
            "A failure under AllSettled is information, not a signal to stop the siblings.");
    }

    [Fact]
    public async Task AllSettledPublishesEachBranchesOutcomeInDeclarationOrder()
    {
        // Read through the projection rather than off the context afterwards: the context
        // is pooled and reset the instant the engine returns, so the projection is the only
        // place a caller can see what a flow left behind.
        var dispatcher = new RecordingDispatcher().FailAt(3, Declined);

        var result = await Engine().ExecuteAsync(
            Plans.Parallel(MergeStrategy.AllSettled),
            dispatcher,
            Plans.Invocation,
            "seed",
            static ctx => ctx.Get<ParallelOutcome>(),
            Ct);

        result.IsSuccess.ShouldBeTrue();

        var outcome = result.Value;

        outcome.StepIndex.ShouldBe(1);
        outcome.BranchCount.ShouldBe(3);
        outcome.SucceededCount.ShouldBe(2);
        outcome.AllSucceeded.ShouldBeFalse();
        outcome.AllFailed.ShouldBeFalse();

        outcome.ErrorFor(0).ShouldBeNull();
        outcome.ErrorFor(1)!.Code.ShouldBe("payment.declined",
            "Branch 1 is the one that failed, and the index is declaration order — not the " +
            "order the branches happened to finish in.");
        outcome.ErrorFor(2).ShouldBeNull();
    }

    // ------------------------------------------------------------------ FirstSuccess, Quorum

    [Fact]
    public async Task FirstSuccessSucceedsWhenOneBranchDoes()
    {
        var dispatcher = new RecordingDispatcher()
            .FailAt(2, Declined)
            .FailAt(3, Declined);

        var result = await Engine().ExecuteAsync(
            Plans.Parallel(MergeStrategy.FirstSuccess), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeTrue("Branch 2 succeeded, which is all FirstSuccess asks for.");
        dispatcher.Executed.ShouldContain(5);
    }

    [Fact]
    public async Task FirstSuccessFailsWithACountWhenNoBranchSucceeds()
    {
        var dispatcher = new RecordingDispatcher()
            .FailAt(2, Declined)
            .FailAt(3, Declined)
            .FailAt(4, Declined);

        var result = await Engine().ExecuteAsync(
            Plans.Parallel(MergeStrategy.FirstSuccess), dispatcher, Plans.Invocation, Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("flow.merge_not_satisfied");
        result.Error.Data!["required"].ShouldBe(1);
        result.Error.Data!["succeeded"].ShouldBe(0);
        result.Error.Data!["branches"].ShouldBe(3);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public async Task QuorumIsMetWhenEnoughBranchesSucceed(int required, bool expectedSuccess)
    {
        var dispatcher = new RecordingDispatcher().FailAt(3, Declined);

        var result = await Engine().ExecuteAsync(
            Plans.Parallel(MergeStrategy.Quorum(required)), dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBe(expectedSuccess,
            $"Two of three branches succeeded and the quorum was {required}.");
    }

    [Fact]
    public async Task AQuorumLargerThanTheBranchCountFailsRatherThanHanging()
    {
        // MergeStrategy cannot check this — it does not know how many branches there are.
        // The engine can, and reporting it as a failed merge is better than waiting for a
        // fourth branch that does not exist.
        var dispatcher = new RecordingDispatcher();

        var result = await Engine().ExecuteAsync(
            Plans.Parallel(MergeStrategy.Quorum(4)), dispatcher, Plans.Invocation, Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("flow.merge_not_satisfied");
        result.Error.Data!["succeeded"].ShouldBe(3);
    }

    // ------------------------------------------------------------------ shared context

    /// <summary>
    /// The failure mode this whole design is arranged around: concurrent writes to the
    /// flow's shared state bag.
    /// </summary>
    /// <remarks>
    /// A <c>Dictionary&lt;Type, object&gt;</c> written from two threads does not throw
    /// reliably — it corrupts, and the corruption surfaces later as a missing entry or an
    /// infinite loop in a bucket chain. So this hammers the write path from every branch,
    /// repeatedly, and asserts the flow still completes and the bag still reads back.
    /// <para>
    /// <strong>Read this as a smoke test, not as proof that the lock is load-bearing.</strong>
    /// An earlier version of this comment claimed it fails within a handful of iterations
    /// when <c>_guarded</c> is forced false. It does not: it was run eight times unguarded —
    /// including a variant inserting twelve extra distinct keys per branch per iteration to
    /// force dictionary resizes — and passed every time on a four-core machine. Two branches
    /// being in flight at once (which <c>BranchesOverlapWhenTheirStepsActuallyYield</c> does
    /// prove) is not the same as two threads colliding inside one dictionary operation, and
    /// the window for that is narrow.
    /// </para>
    /// <para>
    /// The lock stays regardless, and not because this test asks for it: concurrent mutation
    /// of a <c>Dictionary</c> is unsafe by contract, and a race that is merely hard to
    /// provoke is worse than one that is easy — it reaches production instead of CI. What is
    /// missing is a test that can actually fail, which needs deterministic interleaving
    /// rather than more iterations.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ConcurrentBranchWritesDoNotCorruptTheSharedStateBag()
    {
        var engine = Engine();
        var plan = Plans.Parallel(MergeStrategy.AllSettled);

        for (var iteration = 0; iteration < 200; iteration++)
        {
            var dispatcher = new ContextHammeringDispatcher();

            var result = await engine.ExecuteAsync(plan, dispatcher, Plans.Invocation, Ct);

            result.IsSuccess.ShouldBeTrue();
            dispatcher.Failure.ShouldBeNull(
                $"Iteration {iteration} saw the state bag misbehave: {dispatcher.Failure}");
        }
    }

    /// <summary>Writes and reads a distinct slot per branch, as hard as it can.</summary>
    /// <remarks>
    /// Distinct slots on purpose — that is the shape FLOWX1013 requires and therefore the
    /// shape the runtime has to make work. Every branch still writes into <em>one</em>
    /// dictionary, which is where the race lives.
    /// </remarks>
    private sealed class ContextHammeringDispatcher : IStepDispatcher
    {
        public string? Failure { get; private set; }

        public async ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
        {
            await Task.Yield();

            for (var i = 0; i < 200; i++)
            {
                switch (stepIndex)
                {
                    case 2: ctx.Set(new SlotA(i)); break;
                    case 3: ctx.Set(new SlotB(i)); break;
                    case 4: ctx.Set(new SlotC(i)); break;
                    default: break;
                }

                // Reads race a resize just as writes do, so both are exercised.
                if (stepIndex is 2 or 3 or 4 && !ctx.TryGet<SlotA>(out _) && i > 0 && stepIndex == 2)
                {
                    Failure = "A slot written by this branch could not be read back.";
                }
            }

            return StepOutcome.Success;
        }

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx) => true;

        /// <inheritdoc />
        /// <remarks>This double declares no iteration, so the engine never asks it for one.</remarks>
        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to begin.");

        /// <inheritdoc />
        public FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to enter.");

        public int Select(int stepIndex, FlowContext ctx) => -1;

        private sealed record SlotA(int Value);

        private sealed record SlotB(int Value);

        private sealed record SlotC(int Value);
    }
}
