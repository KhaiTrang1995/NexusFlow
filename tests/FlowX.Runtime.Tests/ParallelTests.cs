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
    /// flow's shared state bag. Provoked on purpose rather than hoped for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This test fails when the guard is removed.</strong> That is the only claim
    /// worth making about a race test, and the previous version of this one could not make
    /// it: it wrote three keys per execution through a single reused engine, and after the
    /// first execution every one of those writes was an <em>overwrite</em>. An overwrite
    /// assigns one already-allocated slot; it cannot move an entry, relink a bucket or grow
    /// an array, so there was nothing left for two threads to corrupt and the test passed
    /// unguarded every time it was run. Two branches being in flight at once (which
    /// <c>BranchesOverlapWhenTheirStepsActuallyYield</c> does prove) is not the same as two
    /// threads colliding inside one dictionary operation.
    /// </para>
    /// <para>
    /// Three things had to change to make the collision reachable. <em>Insertion, not
    /// assignment</em> — every branch writes keys the bag has never seen, so each write runs
    /// the path that appends an entry, links a bucket and periodically reallocates both.
    /// <em>A fresh context per iteration</em> — a pooled context keeps its buckets across
    /// executions, so a new engine per iteration is what makes the writes inserts rather
    /// than overwrites. <em>A rendezvous</em> — the branches spin at a gate until the last
    /// one arrives, so they enter the write loop within nanoseconds of each other instead of
    /// whenever the thread pool happens to schedule them.
    /// </para>
    /// <para>
    /// The assertion is the invariant, not a crash: unguarded insertion usually loses an
    /// entry rather than throwing, so what is checked is that every key a branch wrote reads
    /// back with the value it wrote. <see cref="RendezvousDispatcher.Rendezvoused"/> is
    /// counted and asserted too — a run in which the branches never actually met would pass
    /// vacuously, which is exactly the defect this test was rewritten to remove.
    /// </para>
    /// <para>
    /// <strong>Measured, on a four-core machine.</strong> Guarded: twenty consecutive runs,
    /// forty passes, no failures, and the gate was met in 200 of 200 iterations at both
    /// branch counts — so the floor asserted below has a wide margin and is there for a
    /// loaded CI box, not for this one. With <c>_guarded</c> forced to <c>false</c> in
    /// <c>FlowExecutionContext.Initialise</c>: five consecutive runs, ten failures, no
    /// passes, every one of them inside the first two iterations. Both failure modes showed
    /// up — a key that was written and then simply was not in the bag, and an exception
    /// thrown from inside the dictionary and reported as <c>capability.unhandled</c>.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ConcurrentBranchWritesDoNotCorruptTheSharedStateBag(int branches)
    {
        const int iterations = 200;

        var plan = ForkOf(branches);
        var slotsPerBranch = Slots.Length / branches;
        var met = 0;

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            // A new engine per iteration, and therefore a new context: the pool would hand
            // back one whose dictionary already has buckets for every key, turning the
            // inserts this test depends on into assignments.
            var dispatcher = new RendezvousDispatcher(branches, slotsPerBranch);

            var result = await Engine().ExecuteAsync(
                plan, dispatcher, Plans.Invocation, "seed", dispatcher.CountUnreadableSlots, Ct);

            result.IsSuccess.ShouldBeTrue(
                $"Iteration {iteration} did not complete: {result.Error?.Code}. A step that " +
                "threw here is the state bag failing loudly rather than quietly.");

            result.Value.ShouldBe(0,
                $"Iteration {iteration} left {result.Value} of {Slots.Length} slots unreadable. " +
                "Every branch wrote keys no other branch touches, so each one must read back " +
                "with the value its own branch wrote.");

            dispatcher.UnreadableAfterWrite.ShouldBe(0,
                $"Iteration {iteration}: a branch could not read back a key it had just " +
                "written, which means a sibling's insert moved it or dropped it.");

            if (dispatcher.Rendezvoused)
            {
                met++;
            }
        }

        met.ShouldBeGreaterThan(iterations / 2,
            $"The branches met at the gate in only {met} of {iterations} iterations, so most " +
            "of this run never put two threads in the write loop at the same time and the " +
            "passes prove nothing.");
    }

    /// <summary>
    /// A fork of <paramref name="branches"/> single-step branches, joining at an emit.
    /// </summary>
    /// <remarks>
    /// Built rather than spelled out, unlike the plans in <c>Plans</c>: the branch count is
    /// the variable under test, and the shape is otherwise the same fork
    /// <see cref="Plans.Parallel"/> describes. The fork is step 0 so that the branches reach
    /// an almost-empty state bag — a dictionary that has to grow is a dictionary whose
    /// entries move.
    /// </remarks>
    private static ExecutionPlan ForkOf(int branches)
    {
        var targets = new int[branches];
        var steps = new List<StepNode>(branches + 2);

        for (var b = 0; b < branches; b++)
        {
            targets[b] = b + 1;
        }

        steps.Add(StepNode.ForParallel(0, targets, joinTarget: branches + 1, MergeStrategy.AllMustSucceed));

        for (var b = 0; b < branches; b++)
        {
            steps.Add(StepNode.ForCapability(b + 1, Plans.Validate));
        }

        steps.Add(StepNode.ForEmit(branches + 1, "order.screened"));

        return ExecutionPlan.Create(
            FlowDescriptor.Create("order.fork", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create(steps));
    }

    // ------------------------------------------------------------------ distinct keys

    /// <summary>One key in the state bag, reachable without reflection.</summary>
    /// <remarks>
    /// The bag is keyed by <see cref="Type"/> and reached through <c>Set&lt;T&gt;</c>, so a
    /// distinct key is a distinct type named at a distinct call site. These pairs are the
    /// call sites, held as delegates so a branch can pick one by index.
    /// </remarks>
    private readonly record struct SlotAccessor(
        Action<FlowContext, int> Write,
        Func<FlowContext, int, bool> ReadsBack);

    private static SlotAccessor Slot<TRow, TColumn>() => new(
        static (ctx, value) => ctx.Set(new Cell<TRow, TColumn>(value)),
        static (ctx, value) => ctx.TryGet<Cell<TRow, TColumn>>(out var cell) && cell.Value == value);

    /// <summary>
    /// Thirty-six distinct keys, from six tags paired up.
    /// </summary>
    /// <remarks>
    /// Pairing is only a naming device that avoids thirty-six declarations —
    /// <c>Cell&lt;Tag1, Tag2&gt;</c> and <c>Cell&lt;Tag2, Tag1&gt;</c> are two unrelated
    /// types and therefore two unrelated keys. Enough of them that a branch's writes span
    /// several of the dictionary's growth steps rather than fitting inside its initial
    /// capacity.
    /// </remarks>
    private static readonly SlotAccessor[] Slots =
    [
        Slot<Tag1, Tag1>(), Slot<Tag1, Tag2>(), Slot<Tag1, Tag3>(),
        Slot<Tag1, Tag4>(), Slot<Tag1, Tag5>(), Slot<Tag1, Tag6>(),
        Slot<Tag2, Tag1>(), Slot<Tag2, Tag2>(), Slot<Tag2, Tag3>(),
        Slot<Tag2, Tag4>(), Slot<Tag2, Tag5>(), Slot<Tag2, Tag6>(),
        Slot<Tag3, Tag1>(), Slot<Tag3, Tag2>(), Slot<Tag3, Tag3>(),
        Slot<Tag3, Tag4>(), Slot<Tag3, Tag5>(), Slot<Tag3, Tag6>(),
        Slot<Tag4, Tag1>(), Slot<Tag4, Tag2>(), Slot<Tag4, Tag3>(),
        Slot<Tag4, Tag4>(), Slot<Tag4, Tag5>(), Slot<Tag4, Tag6>(),
        Slot<Tag5, Tag1>(), Slot<Tag5, Tag2>(), Slot<Tag5, Tag3>(),
        Slot<Tag5, Tag4>(), Slot<Tag5, Tag5>(), Slot<Tag5, Tag6>(),
        Slot<Tag6, Tag1>(), Slot<Tag6, Tag2>(), Slot<Tag6, Tag3>(),
        Slot<Tag6, Tag4>(), Slot<Tag6, Tag5>(), Slot<Tag6, Tag6>(),
    ];

    private sealed record Cell<TRow, TColumn>(int Value);

    private sealed class Tag1;

    private sealed class Tag2;

    private sealed class Tag3;

    private sealed class Tag4;

    private sealed class Tag5;

    private sealed class Tag6;

    /// <summary>
    /// Holds every branch at a gate until the last one arrives, then has all of them insert
    /// their own block of keys at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct keys per branch on purpose — that is the shape FLOWX1013 requires and
    /// therefore the shape the runtime has to make work. Every branch still writes into
    /// <em>one</em> dictionary, which is where the race lives: two inserts that read the
    /// same free index write the same entry, and one of the two keys is then simply not in
    /// the bag.
    /// </para>
    /// <para>
    /// The gate spins rather than blocking on a <see cref="Barrier"/>. A barrier wakes its
    /// participants through the kernel, one at a time and microseconds apart, which is long
    /// enough for the first branch to finish its whole write loop before the second starts —
    /// the interleaving would then be no more deterministic than it was without a gate.
    /// </para>
    /// </remarks>
    private sealed class RendezvousDispatcher(int branches, int slotsPerBranch) : IStepDispatcher
    {
        /// <summary>Long enough that a busy pool still gets there; short enough to notice.</summary>
        private const int GateTimeoutMs = 2000;

        private int _arrived;
        private int _open;
        private int _passed;
        private int _unreadableAfterWrite;

        /// <summary>True when every branch reached the gate, so the writes really did overlap.</summary>
        public bool Rendezvoused => Volatile.Read(ref _passed) == branches;

        /// <summary>Keys a branch could not read back immediately after writing them.</summary>
        public int UnreadableAfterWrite => Volatile.Read(ref _unreadableAfterWrite);

        /// <summary>
        /// Counts the slots that do not read back with the value written, once the fork has
        /// joined and nothing is still running.
        /// </summary>
        /// <remarks>
        /// Passed to the engine as the flow's projection, because that is the only place the
        /// context can be read: it is pooled and reset the instant the engine returns.
        /// </remarks>
        public int CountUnreadableSlots(FlowContext ctx)
        {
            var lost = 0;

            for (var slot = 0; slot < Slots.Length; slot++)
            {
                if (slot < branches * slotsPerBranch && !Slots[slot].ReadsBack(ctx, slot))
                {
                    lost++;
                }
            }

            return lost;
        }

        public async ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
        {
            // Yield before anything else. Branches are started eagerly on the calling
            // thread, so a branch that waited at the gate before yielding would be waiting
            // for siblings the engine has not started yet.
            await Task.Yield();

            var branch = stepIndex - 1;

            if ((uint)branch >= (uint)branches)
            {
                return StepOutcome.Success;
            }

            if (WaitForSiblings())
            {
                Interlocked.Increment(ref _passed);
            }

            var first = branch * slotsPerBranch;

            for (var i = 0; i < slotsPerBranch; i++)
            {
                var slot = first + i;

                Slots[slot].Write(ctx, slot);

                // A read racing a sibling's insert can walk a bucket array that is being
                // replaced, so the read path is exercised here and not only at the join.
                if (!Slots[slot].ReadsBack(ctx, slot))
                {
                    Interlocked.Increment(ref _unreadableAfterWrite);
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

        /// <summary>
        /// Blocks until every branch has arrived, or gives up. Returns whether they met.
        /// </summary>
        /// <remarks>
        /// Giving up is not a failure: a thread pool that cannot supply one thread per
        /// branch is a property of the machine, not of the runtime under test. It is counted
        /// instead, and the test asserts that it stayed rare — an assertion that never met
        /// its own precondition is worse than no assertion.
        /// </remarks>
        private bool WaitForSiblings()
        {
            if (Interlocked.Increment(ref _arrived) == branches)
            {
                Volatile.Write(ref _open, 1);
                return true;
            }

            var deadline = Environment.TickCount64 + GateTimeoutMs;
            var spins = 0;

            while (Volatile.Read(ref _open) == 0)
            {
                if (++spins < 128)
                {
                    Thread.SpinWait(8);
                    continue;
                }

                spins = 0;

                if (Environment.TickCount64 > deadline)
                {
                    return false;
                }

                // Yield rather than spin forever: on a machine with fewer cores than
                // branches, the sibling this one is waiting for needs this core to run on.
                Thread.Yield();
            }

            return true;
        }
    }
}
