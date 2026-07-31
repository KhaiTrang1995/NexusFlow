using FlowX.Conformance.InMemory;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// WP-57: a compensation that fails is retried under its own policy set and, if it exhausts,
/// ends as <see cref="FlowInstanceState.CompensationFailed"/> with an alert — not as a silent
/// loss.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is a slice, not the policy engine.</strong> One policy executes at run time —
/// a retry declared at <see cref="PolicyStage.Consistency"/>, around the compensating
/// dispatch — and nothing else does. No timeout is armed, no breaker opens, no cache is
/// consulted, and the forward path executes no policy at all. That is what keeps P4 P4 while
/// giving P2's unwind the one thing it cannot do without: the ability to survive a transient
/// failure, which is the ordinary case.
/// </para>
/// <para>
/// <strong>Why the fixed order is honoured rather than bypassed.</strong> ADR-0011 makes the
/// order a safety property because executing a later stage without an earlier one is the
/// recurring incident. The only stage executed here is the <em>last</em>, so there is no
/// earlier stage it could have skipped. <see cref="PolicyChain"/> remains the single ordering
/// mechanism.
/// </para>
/// </remarks>
public sealed class CompensationPolicyTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
    private static readonly FencingToken First = new(1);
    private static readonly FencingToken Second = new(2);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Error Declined = new("payment.declined", "declined", ErrorCategory.Conflict);

    private static readonly Error BrokerDown =
        new("inventory.release_failed", "broker down", ErrorCategory.Unavailable);

    private static readonly Error Rejected =
        new("inventory.release_rejected", "the ledger says no", ErrorCategory.Validation);

    /// <summary>The same plan, re-declared <c>Durable</c>.</summary>
    private static ExecutionPlan Durable(ExecutionPlan plan) => ExecutionPlan.Create(
        FlowDescriptor.Create(
            plan.Flow.Id, plan.Flow.Version, ExecutionProfile.Durable, plan.Flow.Deadline),
        plan.Graph);

    /// <summary>A compensation policy chain, resolved against the capability it will retry.</summary>
    private static PolicyChain Retrying(int attempts, ErrorCategory[]? retryOn = null) =>
        PolicyChain.Create(
            PolicySet.Named("undo").CompensationRetry(
                attempts,
                Backoff.Exponential(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(40)),
                retryOn),
            Plans.Release);

    /// <summary>
    /// <c>0 reserve (undo: release) · 1 capture (undo: refund) · 2 validate</c>, with the
    /// retry declared on step 0's undo only.
    /// </summary>
    /// <remarks>
    /// Two compensable steps rather than one, so an exhausted undo can be shown not to
    /// abandon the one behind it — the best-effort rule, which the retry must not quietly
    /// replace with fail-fast.
    /// </remarks>
    private static ExecutionPlan Saga(PolicyChain? compensationPolicies = null) => ExecutionPlan.Create(
        FlowDescriptor.Create("order.undo", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Plans.Reserve, Plans.Release, compensationPolicies: compensationPolicies),
            StepNode.ForCapability(1, Plans.Capture, Plans.Refund),
            StepNode.ForCapability(2, Plans.Validate),
        ]));

    // ---------------------------------------------------------------------------------
    // The retry itself
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task ACompensationThatFailsOnceSucceedsOnItsRetry()
    {
        var clock = new FakeClock(T0);
        var engine = new FlowEngine(clock);

        var dispatcher = new RecordingDispatcher()
            .FailAt(2, Declined)
            .FailCompensationForAttempts(0, attempts: 1, BrokerDown);

        var result = await engine.ExecuteAsync(Saga(Retrying(5)), dispatcher, Plans.Invocation, Ct);

        result.Compensation.ShouldBe(CompensationOutcome.Succeeded,
            "A broker that was down for one call and up for the next is the ordinary case. " +
            "Reporting that as an unrecoverable inconsistency would page somebody for a " +
            "blip the platform could have absorbed.");

        dispatcher.Compensated.ShouldBe([1, 0, 0],
            "Strict reverse, then the retry of the one that failed. The retry does not " +
            "reorder the unwind: it repeats one entry in place.");
    }

    [Fact]
    public async Task WithoutADeclaredPolicyACompensationIsAttemptedExactlyOnce()
    {
        var engine = new FlowEngine(new FakeClock(T0));

        var dispatcher = new RecordingDispatcher()
            .FailAt(2, Declined)
            .FailCompensationAt(0, BrokerDown);

        var result = await engine.ExecuteAsync(Saga(), dispatcher, Plans.Invocation, Ct);

        result.Compensation.ShouldBe(CompensationOutcome.PartiallyFailed);

        dispatcher.Compensated.ShouldBe([1, 0],
            "Nothing was declared, so nothing is retried. A runtime that retried every undo " +
            "five times whether or not anybody asked would be the policy engine arriving " +
            "early and undeclared — which is exactly what P4 exists to do properly.");
    }

    [Fact]
    public async Task ARetryReusesTheFlowsIdempotencyKey()
    {
        var engine = new FlowEngine(new FakeClock(T0));
        var keys = new List<string>();

        var dispatcher = new RecordingDispatcher
        {
            OnCompensate = ctx => keys.Add(ctx.IdempotencyKey),
        };

        dispatcher.FailAt(2, Declined).FailCompensationForAttempts(0, attempts: 2, BrokerDown);

        await engine.ExecuteAsync(Saga(Retrying(5)), dispatcher, Plans.Invocation, Ct);

        keys.Count.ShouldBe(4);

        keys.Distinct().ShouldHaveSingleItem();
        keys[0].ShouldBe(Plans.Invocation.IdempotencyKey,
            "docs/10-Policy-Framework.md §5: a retry never uses a fresh idempotency key. " +
            "Attempt 2 presents the same key as attempt 1, which is what makes downstream " +
            "deduplication work — and a compensation is the one place a duplicate is a " +
            "second real reversal.");
    }

    [Fact]
    public async Task ATerminalCategoryIsNotRetried()
    {
        var engine = new FlowEngine(new FakeClock(T0));

        var dispatcher = new RecordingDispatcher()
            .FailAt(2, Declined)
            .FailCompensationAt(0, Rejected);

        var result = await engine.ExecuteAsync(Saga(Retrying(5)), dispatcher, Plans.Invocation, Ct);

        result.Compensation.ShouldBe(CompensationOutcome.PartiallyFailed);

        dispatcher.Compensated.ShouldBe([1, 0],
            "A Validation failure is the downstream saying the request was wrong, not that " +
            "it was busy. Five attempts at it burn the budget an operator could have spent " +
            "on the transient ones and delay the alert by the whole backoff.");
    }

    [Fact]
    public async Task AnExhaustedCompensationDoesNotAbandonTheOnesBehindIt()
    {
        var engine = new FlowEngine(new FakeClock(T0));

        var plan = Saga(Retrying(3));

        var dispatcher = new RecordingDispatcher()
            .FailAt(2, Declined)
            .FailCompensationAt(0, BrokerDown);

        var result = await engine.ExecuteAsync(plan, dispatcher, Plans.Invocation, Ct);

        result.Compensation.ShouldBe(CompensationOutcome.PartiallyFailed);

        dispatcher.Compensated.ShouldBe([1, 0, 0, 0],
            "Step 1's refund ran first and step 0's release was attempted three times. " +
            "Best-effort survives the retry: exhausting one undo must not abandon the " +
            "others, because a failed refund is no reason to also leak the reservation.");
    }

    [Fact]
    public async Task TheBackoffIsFullJitterAndGrowsWithTheAttempt()
    {
        var clock = new FakeClock(T0);
        var engine = new FlowEngine(clock);

        var dispatcher = new RecordingDispatcher()
            .FailAt(2, Declined)
            .FailCompensationAt(0, BrokerDown);

        await engine.ExecuteAsync(Saga(Retrying(4)), dispatcher, Plans.Invocation, Ct);

        clock.Delays.Count.ShouldBe(3, "Four attempts are separated by three waits.");

        // Full jitter draws below the ceiling rather than at it, so the assertion is the
        // envelope: 10 ms x 2^attempt, capped at 40 ms.
        clock.Delays[0].ShouldBeLessThanOrEqualTo(TimeSpan.FromMilliseconds(20));
        clock.Delays[1].ShouldBeLessThanOrEqualTo(TimeSpan.FromMilliseconds(40));
        clock.Delays[2].ShouldBeLessThanOrEqualTo(TimeSpan.FromMilliseconds(40));

        clock.Delays.ShouldAllBe(static d => d >= TimeSpan.Zero);
    }

    [Fact]
    public async Task ARetryNeverOutlivesTheDeadline()
    {
        var clock = new FakeClock(T0);
        var engine = new FlowEngine(clock);

        // A budget that leaves room for the steps and for the first undo, and none at all
        // for a backoff — so the retry is refused rather than armed.
        var plan = ExecutionPlan.Create(
            FlowDescriptor.Create(
                "order.undo", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromMilliseconds(5)),
            Saga(Retrying(5)).Graph);

        var dispatcher = new RecordingDispatcher()
            .FailAt(2, Declined)
            .FailCompensationAt(0, BrokerDown);

        var result = await engine.ExecuteAsync(plan, dispatcher, Plans.Invocation, Ct);

        result.Compensation.ShouldBe(CompensationOutcome.PartiallyFailed);

        dispatcher.Compensated.ShouldBe([1, 0],
            "docs/10-Policy-Framework.md §5: the elapsed time plus the planned backoff is " +
            "subtracted before the next attempt is armed. The first attempt still runs — an " +
            "undo is cleanup and a spent budget is no reason to skip it — but a wait that " +
            "would outlive the flow is not entered.");

        clock.Delays.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------------------------
    // The alert
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task AnExhaustedCompensationRaisesExactlyOneAlertNamingWhatFailed()
    {
        var alerts = new RecordingAlertSink();
        var engine = new FlowEngine(new FakeClock(T0), alerts: alerts);

        var dispatcher = new RecordingDispatcher()
            .FailAt(2, Declined)
            .FailCompensationAt(0, BrokerDown);

        await engine.ExecuteAsync(Saga(Retrying(3)), dispatcher, Plans.Invocation, Ct);

        var alert = alerts.Raised.ShouldHaveSingleItem();

        alert.FlowId.ShouldBe("order.undo");
        alert.StepIndex.ShouldBe(0);
        alert.CompensationId.ShouldBe("inventory.release");
        alert.Attempts.ShouldBe(3);
        alert.CorrelationId.ShouldBe(Plans.Invocation.CorrelationId);
        alert.TenantId.ShouldBe(Plans.Invocation.TenantId);
        alert.Error.Code.ShouldBe("inventory.release_failed",
            "The alert carries the last failure, because that is what the operator has to " +
            "go and fix before `flowx replay --from` can do anything.");
    }

    [Fact]
    public async Task ASuccessfulUnwindRaisesNoAlert()
    {
        var alerts = new RecordingAlertSink();
        var engine = new FlowEngine(new FakeClock(T0), alerts: alerts);

        var dispatcher = new RecordingDispatcher()
            .FailAt(2, Declined)
            .FailCompensationForAttempts(0, attempts: 2, BrokerDown);

        await engine.ExecuteAsync(Saga(Retrying(5)), dispatcher, Plans.Invocation, Ct);

        alerts.Raised.ShouldBeEmpty(
            "An alert that fires for a blip the platform absorbed is an alert people learn " +
            "to close without reading.");
    }

    [Fact]
    public async Task AnAlertSinkThatThrowsDoesNotDerailTheRestOfTheUnwind()
    {
        var engine = new FlowEngine(new FakeClock(T0), alerts: new ThrowingAlertSink());

        var plan = ExecutionPlan.Create(
            FlowDescriptor.Create("order.undo", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([
                StepNode.ForCapability(0, Plans.Reserve, Plans.Release),
                StepNode.ForCapability(1, Plans.Capture, Plans.Refund),
                StepNode.ForCapability(2, Plans.Validate),
            ]));

        var dispatcher = new RecordingDispatcher()
            .FailAt(2, Declined)
            .FailCompensationAt(1, BrokerDown);

        var result = await engine.ExecuteAsync(plan, dispatcher, Plans.Invocation, Ct);

        result.Compensation.ShouldBe(CompensationOutcome.PartiallyFailed);

        dispatcher.Compensated.ShouldBe([1, 0],
            "The sink is somebody else's code on the failure path. A broken pager must not " +
            "leak the inventory reservation the unwind was in the middle of releasing.");
    }

    // ---------------------------------------------------------------------------------
    // The journal: rows for compensating steps, and CompensationFailed
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task ACompensatedStepGetsItsOwnJournalRow()
    {
        var journal = new CountingJournal();
        var plan = Durable(Saga());
        var instanceId = Guid.NewGuid();
        var run = await BeginAsync(journal, plan, instanceId);

        var engine = new FlowEngine(new FakeClock(T0));

        await engine.ExecuteAsync(
            plan, new RecordingDispatcher().FailAt(2, Declined), Plans.Invocation, run, Ct);

        var rows = await RowsAsync(journal, instanceId);

        var undone = rows.Where(static r => r.Outcome == JournalOutcome.Compensated).ToList();

        undone.Select(static r => r.Key.StepId).ShouldBe([1, 0],
            "docs/06-Execution-Engine.md §7 rule 4: compensation is itself journaled. Until " +
            "now the journal recorded only what ran forward and said nothing about what had " +
            "been undone, so a crash mid-unwind lost it.");

        undone.Select(static r => r.CapabilityId).ShouldBe(["payment.refund", "inventory.release"],
            "The row names what actually ran. Recording the forward capability would make " +
            "the undo indistinguishable from the step it reverses.");

        rows.Count(static r => r.Key.StepId == 0).ShouldBe(2,
            "One forward row and one compensation row, at different attempt numbers — an " +
            "append-only table cannot overwrite, and the two are different facts about the " +
            "same step.");
    }

    [Fact]
    public async Task AnExhaustedCompensationLeavesTheInstanceCompensationFailed()
    {
        var journal = new CountingJournal();
        var alerts = new RecordingAlertSink();
        var plan = Durable(Saga(Retrying(2)));
        var instanceId = Guid.NewGuid();
        var run = await BeginAsync(journal, plan, instanceId);

        var engine = new FlowEngine(new FakeClock(T0), alerts: alerts);

        var dispatcher = new RecordingDispatcher()
            .FailAt(2, Declined)
            .FailCompensationAt(0, BrokerDown);

        var result = await engine.ExecuteAsync(plan, dispatcher, Plans.Invocation, run, Ct);

        result.Compensation.ShouldBe(CompensationOutcome.PartiallyFailed);

        var instance = await journal.ReadInstanceAsync(instanceId, Ct);

        instance.Value.State.ShouldBe(FlowInstanceState.CompensationFailed,
            "The one terminal state with no automatic resolution — docs/11 §8's last row. " +
            "It was reachable in code and reached by nothing; this is the test that row's " +
            "exit criterion asks for.");

        journal.StatesSeen.ShouldContain(FlowInstanceState.Compensating,
            "The instance says what it is doing while it does it. An operator watching a " +
            "saga unwind must not have to infer it from the absence of new step rows.");

        alerts.Raised.ShouldHaveSingleItem().InstanceId.ShouldBe(instanceId,
            "An alert with no instance id is an alert nobody can act on: the runbook is " +
            "`flowx replay --instance <id> --from <step>`.");

        var rows = await RowsAsync(journal, instanceId);

        rows.Count(static r => r.Key.StepId == 0 && r.Outcome == JournalOutcome.Failure).ShouldBe(2,
            "Both failed undo attempts are recorded. The attempt history is what makes the " +
            "exhaustion legible after the fact rather than a single row that says 'no'.");
    }

    [Fact]
    public async Task AResumedInstanceDoesNotRepeatACompensationThatAlreadyCommitted()
    {
        // Commits 1-2 are steps 0 and 1; commit 3 is step 2's failure; commit 4 is step 1's
        // undo; the node dies writing step 0's, having already run it.
        var journal = new CountingJournal { DiesOnCommit = 5 };
        var plan = Durable(Saga());
        var instanceId = Guid.NewGuid();
        var run = await BeginAsync(journal, plan, instanceId);

        var engine = new FlowEngine(new FakeClock(T0));
        var crashed = new RecordingDispatcher().FailAt(2, Declined);

        await Should.ThrowAsync<NodeDiedException>(
            () => engine.ExecuteAsync(plan, crashed, Plans.Invocation, run, Ct).AsTask());

        crashed.Compensated.ShouldBe([1, 0], "Both undos ran; only the first was written down.");

        journal.DiesOnCommit = null;

        var resumed = await DurableExecution.ResumeAsync(journal, instanceId, Second, Ct);

        resumed.IsSuccess.ShouldBeTrue();

        var recovered = new RecordingDispatcher().FailAt(2, Declined);

        var finished = await engine.ExecuteAsync(plan, recovered, Plans.Invocation, resumed.Value, Ct);

        finished.IsFailure.ShouldBeTrue();

        recovered.Executed.ShouldBe([2], "Steps 0 and 1 committed, so they are stepped over.");

        recovered.Compensated.ShouldBe([0],
            "Step 1's undo committed, so the resumed instance must not refund the same " +
            "payment twice. Step 0's did not, so as far as the journal is concerned it never " +
            "happened and it runs again — the same honest limit ADR-0006 states for a " +
            "forward effect that landed before its commit.");
    }

    // ---------------------------------------------------------------------------------
    // The sub-flow boundary: what this package does and does not close
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A resumed parent still does not rebuild a completed child's compensation stack, and
    /// this is the test that says so out loud.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Not closed here, and the reason is a contract rather than an oversight.</strong>
    /// Rebuilding the child's stack needs the child's instance id, and the only way to get it
    /// from the parent is to ask "which instances exist under this parent" —
    /// <see cref="IFlowJournal"/> deliberately does not answer that (it is a recovery scan's
    /// query, which is why <see cref="IRecoveryIndex"/> was split out), and widening the
    /// journal contract would oblige every store to serve a query some deployments never run.
    /// </para>
    /// <para>
    /// So ADR-0015's statement stands unweakened, and this pins the behaviour it describes so
    /// that whoever does close it breaks a test rather than discovering the gap in production.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AResumedParentStillDoesNotRebuildASkippedChildsCompensationStack()
    {
        // The commits are: 1 the parent's step 0, 2 and 3 the child's own two steps, 4 the
        // parent's row for the composition — and the node dies writing the fifth, which is
        // step 2. So the composition is committed, which is what makes the resumed loop skip
        // it rather than compose the child again.
        var journal = new CountingJournal { DiesOnCommit = 5 };

        // 0 validate · 1 subflow(order.fulfil) · 2 capture (undo: refund) · 3 emit — the
        // `A · child(X, Y) · B` shape, with the child's X compensable.
        var plan = Durable(Plans.Composing());
        var child = Durable(Plans.Child());
        var instanceId = Guid.NewGuid();
        var run = await BeginAsync(journal, plan, instanceId);

        var engine = new FlowEngine(new FakeClock(T0));

        var trace = new List<string>();
        var childDispatcher = new RecordingDispatcher().As("child", trace);
        var parent = new RecordingDispatcher().As("parent", trace);

        parent.ComposeAt(1, child, childDispatcher, "input");

        // The node dies sealing the composition, after the child has completed its own
        // instance and left a compensation pending.
        await Should.ThrowAsync<NodeDiedException>(
            () => engine.ExecuteAsync(plan, parent, Plans.Invocation, run, Ct).AsTask());

        journal.DiesOnCommit = null;

        var resumed = await DurableExecution.ResumeAsync(journal, instanceId, Second, Ct);
        var recoveredTrace = new List<string>();
        var recoveredChild = new RecordingDispatcher().As("child", recoveredTrace);
        var recoveredParent = new RecordingDispatcher().As("parent", recoveredTrace);

        recoveredParent.ComposeAt(1, child, recoveredChild, "input");
        recoveredParent.FailAt(3, Declined);

        var finished = await engine.ExecuteAsync(
            plan, recoveredParent, Plans.Invocation, resumed.Value, Ct);

        finished.IsFailure.ShouldBeTrue();

        recoveredChild.Composed.ShouldBeEmpty(
            "The composition committed, so the resumed loop steps over it. Composing the " +
            "child again would re-run its effects, which is what resumption exists to avoid.");

        recoveredParent.Compensated.ShouldBe([2],
            "`A · child(X, Y) · B` should unwind B, Y, X, A. What it unwinds after a resume " +
            "is B alone.");

        recoveredChild.Compensated.ShouldBeEmpty(
            "The gap ADR-0015 records, unchanged and now covered: the parent recorded the " +
            "composition as one entry bound to the child's context, and that context died " +
            "with the node. Closing it needs a 'which instances are under this parent' query " +
            "the journal contract deliberately does not have — it is a recovery scan's, which " +
            "is why IRecoveryIndex was split out — and widening the journal would oblige " +
            "every store to serve a query some deployments never run.");
    }

    // ---------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------

    private static async Task<DurableExecution> BeginAsync(
        IFlowJournal journal, ExecutionPlan plan, Guid instanceId)
    {
        var begun = await DurableExecution.BeginAsync(
            journal, plan, Plans.Invocation, instanceId, First, input: null, Ct);

        begun.IsSuccess.ShouldBeTrue(begun.IsFailure ? begun.Error.ToString() : null);

        return begun.Value;
    }

    private static async Task<IReadOnlyList<JournalStep>> RowsAsync(IFlowJournal journal, Guid instanceId)
    {
        var frontier = await journal.ReadResumeFrontierAsync(instanceId, Ct);

        frontier.IsSuccess.ShouldBeTrue();

        return frontier.Value.Committed;
    }

    /// <summary>Collects the alerts the engine raises, so a test can read them back.</summary>
    private sealed class RecordingAlertSink : ICompensationAlertSink
    {
        public List<CompensationAlert> Raised { get; } = [];

        public void CompensationExhausted(in CompensationAlert alert) => Raised.Add(alert);
    }

    /// <summary>A pager that is itself broken, which is a thing that happens at 2 a.m.</summary>
    private sealed class ThrowingAlertSink : ICompensationAlertSink
    {
        public void CompensationExhausted(in CompensationAlert alert) =>
            throw new InvalidOperationException("The alerting pipeline is down.");
    }

    /// <summary>
    /// The reference journal, plus a commit counter so a node can be killed on a chosen
    /// write, and a note of every instance state a commit asked for.
    /// </summary>
    /// <remarks>
    /// Delegation rather than reimplementation, for the reason <c>DurableSeamTests</c> gives:
    /// the semantics under test are <see cref="InMemoryFlowJournal"/>'s, which passes
    /// <c>JournalConformance</c>. A store written for this file would get the fencing check
    /// wrong first, and that is what the whole seam rests on.
    /// </remarks>
    private sealed class CountingJournal : IFlowJournal
    {
        private readonly InMemoryFlowJournal _inner = new();
        private readonly Lock _gate = new();

        private int _commits;

        /// <summary>Which commit the node dies on, counting from one, or null to survive.</summary>
        public int? DiesOnCommit { get; set; }

        /// <summary>Every instance state a commit carried, in commit order.</summary>
        public List<FlowInstanceState> StatesSeen { get; } = [];

        public ValueTask<Result<FlowInstanceRecord>> StartAsync(
            FlowInstanceStart start, CancellationToken cancellationToken) =>
            _inner.StartAsync(start, cancellationToken);

        public ValueTask<Result<FencingToken>> FenceAsync(
            Guid instanceId, FencingToken token, CancellationToken cancellationToken) =>
            _inner.FenceAsync(instanceId, token, cancellationToken);

        public ValueTask<Result<JournalStep>> CommitAsync(
            StepCommit commit, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(commit);

            int attempt;

            lock (_gate)
            {
                attempt = ++_commits;

                if (commit.State is { } state)
                {
                    StatesSeen.Add(state);
                }
            }

            return attempt == DiesOnCommit
                ? throw new NodeDiedException()
                : _inner.CommitAsync(commit, cancellationToken);
        }

        public ValueTask<Result<FlowInstanceRecord>> CompleteAsync(
            Guid instanceId,
            FencingToken token,
            FlowInstanceState state,
            JournalPayload stateBag,
            CancellationToken cancellationToken) =>
            _inner.CompleteAsync(instanceId, token, state, stateBag, cancellationToken);

        public ValueTask<Result<FlowInstanceRecord>> ReadInstanceAsync(
            Guid instanceId, CancellationToken cancellationToken) =>
            _inner.ReadInstanceAsync(instanceId, cancellationToken);

        public ValueTask<Result<ResumeFrontier>> ReadResumeFrontierAsync(
            Guid instanceId, CancellationToken cancellationToken) =>
            _inner.ReadResumeFrontierAsync(instanceId, cancellationToken);

        public ValueTask<Result<IReadOnlyList<OutboxRecord>>> ReadOutboxAsync(
            Guid instanceId, CancellationToken cancellationToken) =>
            _inner.ReadOutboxAsync(instanceId, cancellationToken);
    }
}
