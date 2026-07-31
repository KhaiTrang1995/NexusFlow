using FlowX.Conformance.InMemory;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// Lease acquisition, renewal and release — the half of ADR-0006 that WP-52 left as a
/// parameter and WP-55 makes a mechanism.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The stores are the reference implementations from the conformance suite.</strong>
/// <see cref="InMemoryLeaseStore"/> passes <c>LeaseStoreConformance</c> and
/// <see cref="InMemoryFlowJournal"/> passes <c>JournalConformance</c>, including
/// <c>TheFenceRisesOnAcquisitionNotOnTheFirstWrite</c>. Asserting against stores nothing
/// holds to those suites would prove that this code works with a double written to agree
/// with it.
/// </para>
/// <para>
/// <strong>The expiry assertions wait real milliseconds</strong>, for the reason the
/// conformance suite gives for the same choice: a fake clock in the lease store would make
/// them cheaper and would stop them being about expiry. The TTLs here are hundreds of
/// milliseconds rather than the thirty seconds a deployment uses.
/// </para>
/// </remarks>
public sealed class LeaseTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private const string NodeOne = "node-1";
    private const string NodeTwo = "node-2";

    /// <summary>A policy that expires quickly enough to observe, and renews well inside it.</summary>
    private static LeasePolicy Brief { get; } = new()
    {
        Ttl = TimeSpan.FromMilliseconds(400),
        RenewalInterval = TimeSpan.FromMilliseconds(50),
    };

    /// <summary>The same plan, re-declared <c>Durable</c>.</summary>
    private static ExecutionPlan Durable(ExecutionPlan plan) => ExecutionPlan.Create(
        FlowDescriptor.Create(
            plan.Flow.Id, plan.Flow.Version, ExecutionProfile.Durable, plan.Flow.Deadline),
        plan.Graph);

    private static async Task<DurableLease> AcquireAsync(
        ILeaseStore store, Guid instanceId, string node, LeasePolicy? policy = null)
    {
        var acquired = await DurableLease.AcquireAsync(
            store, instanceId, node, policy ?? LeasePolicy.Default, TestContext.Current.CancellationToken);

        acquired.IsSuccess.ShouldBeTrue(acquired.IsFailure ? acquired.Error.ToString() : null);

        return acquired.Value;
    }

    // ---------------------------------------------------------------------------------
    // Acquisition, and the fence it raises
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A fresh instance's opening fence is the token of the lease that opened it.
    /// </summary>
    /// <remarks>
    /// The half of "the fence rises on acquisition" that <c>FenceAsync</c> cannot serve: there
    /// is no row to raise a fence on yet, so the token travels on
    /// <see cref="IFlowJournal.StartAsync"/> instead. What matters is that the outcome is the
    /// same — there is no window in which the row exists at a fence below the lease that
    /// created it.
    /// </remarks>
    [Fact]
    public async Task AFreshInstanceOpensAtTheFenceOfTheLeaseThatStartedIt()
    {
        var journal = new InMemoryFlowJournal();
        var leases = new InMemoryLeaseStore();
        var instanceId = Guid.NewGuid();

        await using var lease = await AcquireAsync(leases, instanceId, NodeOne);

        var begun = await lease.BeginAsync(
            journal, Durable(Plans.FourStepSaga()), Plans.Invocation,
            cancellationToken: TestContext.Current.CancellationToken);

        begun.IsSuccess.ShouldBeTrue();

        var record = await journal.ReadInstanceAsync(instanceId, TestContext.Current.CancellationToken);

        record.Value.Fence.ShouldBe(lease.Token);
        lease.Token.Value.ShouldBe(1);
        begun.Value.Token.ShouldBe(lease.Token);
    }

    /// <summary>
    /// The fence rises when the next node acquires, before it reads or writes anything.
    /// </summary>
    /// <remarks>
    /// This is the window <c>IFlowJournal.FenceAsync</c> exists to close, and the reason
    /// <see cref="DurableLease"/> is the only way to reach a token: between node 2 winning
    /// the lease and its first commit, a zombie node 1 still holding token 1 would otherwise
    /// be writing to a journal whose highest seen token was still 1 — and its write would be
    /// accepted. Asserted by refusing that write while node 2 has not committed anything.
    /// </remarks>
    [Fact]
    public async Task TheFenceRisesWhenTheNextNodeAcquiresAndNotWhenItWrites()
    {
        var journal = new InMemoryFlowJournal();
        var leases = new InMemoryLeaseStore();
        var instanceId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        var first = await AcquireAsync(leases, instanceId, NodeOne);

        await first.BeginAsync(
            journal, Durable(Plans.FourStepSaga()), Plans.Invocation, cancellationToken: ct);

        // node-1 goes away cleanly, which is the fast path of the same handover a crash
        // takes the slow way round.
        (await first.ReleaseAsync(ct)).Value.ShouldBeTrue();

        await using var second = await AcquireAsync(leases, instanceId, NodeTwo);

        second.Token.ShouldBeGreaterThan(first.Token);

        var resumed = await second.ResumeAsync(journal, ct);

        resumed.IsSuccess.ShouldBeTrue();

        var record = await journal.ReadInstanceAsync(instanceId, ct);

        record.Value.Fence.ShouldBe(second.Token,
            "Acquisition raised it. Nothing has been committed under the new token yet.");

        var zombie = await journal.CommitAsync(
            new StepCommit
            {
                Key = StepKey.First(instanceId, 0),
                Token = first.Token,
                CapabilityId = "order.validate",
                CapabilityVersion = "1.0.0",
                Outcome = JournalOutcome.Success,
            },
            ct);

        zombie.IsFailure.ShouldBeTrue();
        zombie.Error.Code.ShouldBe("journal.fenced_out");
        zombie.Error.Category.ShouldBe(ErrorCategory.Forbidden,
            "Terminal: retrying with a token below the fence can never succeed.");
    }

    /// <summary>A live lease is refused to everyone else, including the node that holds it.</summary>
    [Fact]
    public async Task ALiveLeaseIsNotAcquiredTwice()
    {
        var leases = new InMemoryLeaseStore();
        var instanceId = Guid.NewGuid();

        await using var held = await AcquireAsync(leases, instanceId, NodeOne);

        var contender = await DurableLease.AcquireAsync(
            leases, instanceId, NodeTwo, LeasePolicy.Default, TestContext.Current.CancellationToken);

        contender.IsFailure.ShouldBeTrue();
        contender.Error.Code.ShouldBe("lease.held");
        contender.Error.Message.ShouldContain(NodeOne, Case.Sensitive,
            "An operator asking who has an instance must be told, not sent to a dashboard.");
    }

    // ---------------------------------------------------------------------------------
    // Renewal
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A lease outlives its own TTL for as long as its holder keeps renewing.
    /// </summary>
    /// <remarks>
    /// The whole point of renewal, and the reason a long step does not hand the instance to
    /// another node. What it cannot promise is stated where the mechanism is: a node paused
    /// past its TTL wakes up believing it still holds a lease that has been reissued, and
    /// what stops it is the fence, not this.
    /// </remarks>
    [Fact]
    public async Task AHeldLeaseIsRenewedPastTheTtlItWasIssuedWith()
    {
        var leases = new InMemoryLeaseStore();
        var instanceId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        // A generous TTL against a fast renewal, so the assertion is about renewal happening
        // rather than about how promptly a loaded CI agent schedules a timer.
        var policy = new LeasePolicy
        {
            Ttl = TimeSpan.FromSeconds(4),
            RenewalInterval = TimeSpan.FromMilliseconds(50),
        };

        await using var lease = await AcquireAsync(leases, instanceId, NodeOne, policy);

        var issued = lease.ExpiresAt;

        await WaitUntilAsync(() => lease.ExpiresAt > issued, TimeSpan.FromSeconds(2), ct);

        lease.IsHeld.ShouldBeTrue();
        lease.IsLost.ShouldBeFalse();
        lease.ExpiresAt.ShouldBeGreaterThan(issued);

        var live = await leases.ReadAsync(instanceId, ct);

        live.IsSuccess.ShouldBeTrue("The store still has it, held by this node.");
        live.Value.Token.ShouldBe(lease.Token, "A renewal extends the expiry and keeps the token.");
    }

    /// <summary>
    /// A renewal the store refuses is a lost lease, and the holder is told rather than left
    /// believing.
    /// </summary>
    /// <remarks>
    /// Observable and deliberately not a cancellation. Cancelling the execution would send the
    /// flow down the failure path, and the failure path compensates — which would run this
    /// node's undo effects against an instance another node is carrying forwards. The fence
    /// is what stops a lost lease, and it stops it at the write.
    /// </remarks>
    [Fact]
    public async Task ARenewalTheStoreRefusesIsObservedAsALostLease()
    {
        var leases = new RefusingLeaseStore();
        var instanceId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        await using var lease = await AcquireAsync(leases, instanceId, NodeOne, Brief);

        lease.IsLost.ShouldBeFalse();

        leases.RefuseRenewals = true;

        await WaitUntilAsync(() => lease.IsLost, Brief.Ttl * 4, ct);

        lease.IsLost.ShouldBeTrue();
        lease.IsHeld.ShouldBeFalse();
    }

    /// <summary>
    /// A store that is merely unreachable is retried while there is still time on the lease.
    /// </summary>
    /// <remarks>
    /// The journal contract's distinction, applied to the lease store: a refusal is a working
    /// store saying no and is definitive, an exception is a store that is gone and may be
    /// back before the lease lapses. Declaring the lease lost on the first blip would hand
    /// instances to other nodes every time a network hiccuped.
    /// </remarks>
    [Fact]
    public async Task AnUnreachableStoreIsRetriedRatherThanTreatedAsALostLease()
    {
        var leases = new RefusingLeaseStore();
        var instanceId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        // A TTL long enough that the retries under test happen well inside it. The point is
        // the distinction between a store that throws and one that refuses, not the expiry.
        var policy = new LeasePolicy
        {
            Ttl = TimeSpan.FromSeconds(8),
            RenewalInterval = TimeSpan.FromMilliseconds(50),
        };

        leases.ThrowOnRenewals = true;

        await using var lease = await AcquireAsync(leases, instanceId, NodeOne, policy);

        await WaitUntilAsync(() => leases.RenewAttempts >= 3, TimeSpan.FromSeconds(4), ct);

        leases.RenewAttempts.ShouldBeGreaterThanOrEqualTo(3, "It kept trying rather than giving up.");
        lease.IsLost.ShouldBeFalse("There was still time on the lease.");

        leases.ThrowOnRenewals = false;

        var issued = lease.ExpiresAt;

        await WaitUntilAsync(() => lease.ExpiresAt > issued, TimeSpan.FromSeconds(4), ct);

        lease.IsHeld.ShouldBeTrue("The store came back before the lease lapsed.");
        lease.ExpiresAt.ShouldBeGreaterThan(issued, "And the lease was renewed once it did.");
    }

    // ---------------------------------------------------------------------------------
    // Release
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Releasing hands the instance on immediately instead of after a TTL.
    /// </summary>
    /// <remarks>
    /// What makes a rolling update a non-event (<c>docs/11-Distributed-Runtime.md §7</c>).
    /// The successor's token is strictly greater, which is what makes the handover safe as
    /// well as fast.
    /// </remarks>
    [Fact]
    public async Task ReleasingHandsTheInstanceOnWithoutWaitingForExpiry()
    {
        var leases = new InMemoryLeaseStore();
        var instanceId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        var first = await AcquireAsync(leases, instanceId, NodeOne);

        (await first.ReleaseAsync(ct)).Value.ShouldBeTrue();
        first.IsHeld.ShouldBeFalse();

        await using var second = await AcquireAsync(leases, instanceId, NodeTwo);

        second.Token.ShouldBeGreaterThan(first.Token,
            "Never reset, not on release and not on expiry. A store that restarted the " +
            "sequence would hand a returning zombie the token the new owner is using.");
    }

    /// <summary>Releasing twice is not an error, and neither is disposing after releasing.</summary>
    /// <remarks>
    /// The ordinary path releases at the end of a flow and disposes in a <c>finally</c>, and
    /// a drain releases whatever is left. None of those should have to know which got there
    /// first.
    /// </remarks>
    [Fact]
    public async Task ReleaseIsIdempotent()
    {
        var leases = new InMemoryLeaseStore();
        var ct = TestContext.Current.CancellationToken;
        var lease = await AcquireAsync(leases, Guid.NewGuid(), NodeOne);

        (await lease.ReleaseAsync(ct)).Value.ShouldBeTrue();
        (await lease.ReleaseAsync(ct)).Value.ShouldBeFalse("There was nothing left to release.");

        await lease.DisposeAsync();
    }

    /// <summary>Disposing releases a lease nothing gave back.</summary>
    [Fact]
    public async Task DisposingReleasesALeaseThatIsStillHeld()
    {
        var leases = new InMemoryLeaseStore();
        var instanceId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        var lease = await AcquireAsync(leases, instanceId, NodeOne);

        await lease.DisposeAsync();

        (await leases.ReadAsync(instanceId, ct)).Error.Code.ShouldBe("lease.not_held");
    }

    /// <summary>Disposal does not throw over the top of a store that has gone away.</summary>
    /// <remarks>
    /// Disposal runs in a <c>finally</c>, usually while another failure is in flight.
    /// Replacing the error the caller was about to report with "the lease store is down"
    /// would lose the one that explains what happened — and an unreleased lease simply
    /// expires, which is the slower half of the same outcome.
    /// </remarks>
    [Fact]
    public async Task DisposingSwallowsAStoreThatHasGoneAway()
    {
        var leases = new RefusingLeaseStore();
        var lease = await AcquireAsync(leases, Guid.NewGuid(), NodeOne);

        leases.ThrowOnRelease = true;

        await lease.DisposeAsync();
    }

    /// <summary>A lease is refused for arguments no lease could be run on.</summary>
    [Fact]
    public async Task RejectsAPolicyNoLeaseCouldBeRunOn()
    {
        var leases = new InMemoryLeaseStore();
        var instanceId = Guid.NewGuid();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await DurableLease.AcquireAsync(
            leases, instanceId, NodeOne,
            new LeasePolicy { Ttl = TimeSpan.Zero }, TestContext.Current.CancellationToken));

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await DurableLease.AcquireAsync(
            leases, instanceId, NodeOne,
            new LeasePolicy { Ttl = TimeSpan.FromSeconds(1), RenewalInterval = TimeSpan.FromSeconds(1) },
            TestContext.Current.CancellationToken));

        await Should.ThrowAsync<ArgumentException>(async () => await DurableLease.AcquireAsync(
            leases, instanceId, "  ", LeasePolicy.Default, TestContext.Current.CancellationToken));

        await Should.ThrowAsync<ArgumentNullException>(async () => await DurableLease.AcquireAsync(
            null!, instanceId, NodeOne, LeasePolicy.Default, TestContext.Current.CancellationToken));
    }

    /// <summary><see cref="LeasePolicy.WithTtl"/> takes the margin §3 asks for.</summary>
    [Fact]
    public void APolicyBuiltFromATtlRenewsAtAThirdOfIt()
    {
        var policy = LeasePolicy.WithTtl(TimeSpan.FromSeconds(30));

        policy.Ttl.ShouldBe(TimeSpan.FromSeconds(30));
        policy.RenewalInterval.ShouldBe(TimeSpan.FromSeconds(10));

        LeasePolicy.Default.Ttl.ShouldBe(policy.Ttl);
        LeasePolicy.Default.RenewalInterval.ShouldBe(policy.RenewalInterval);

        Should.Throw<ArgumentOutOfRangeException>(() => LeasePolicy.WithTtl(TimeSpan.Zero));
    }

    // ---------------------------------------------------------------------------------
    // What a node that lost the lease does next
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A fenced-out node stops and does <em>not</em> run its compensations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The consequence of a lease lost mid-flight, and the one place where getting it wrong
    /// costs real effects rather than a wasted round trip. The instance belongs to another
    /// node now: that node holds the journal, the frontier and the only compensation stack
    /// that describes what the instance actually did. Unwinding here would be an undo racing
    /// a redo — this node refunding a payment the new owner is about to capture.
    /// </para>
    /// <para>
    /// What is <em>not</em> claimed: the step that was in flight when the fence rose still
    /// ran, and its effects stand. The fence stops writes, not effects.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFencedOutNodeStopsWithoutRunningItsCompensations()
    {
        var journal = new StolenJournal { StolenOnCommit = 3 };
        var leases = new InMemoryLeaseStore();
        var plan = Durable(Plans.FourStepSaga());
        var instanceId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        await using var lease = await AcquireAsync(leases, instanceId, NodeOne);

        var begun = await lease.BeginAsync(journal, plan, Plans.Invocation, cancellationToken: ct);

        begun.IsSuccess.ShouldBeTrue();

        var engine = new FlowEngine(new FakeClock(T0));
        var dispatcher = new RecordingDispatcher();

        var result = await engine.ExecuteAsync(plan, dispatcher, Plans.Invocation, begun.Value, ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("journal.fenced_out");

        dispatcher.Executed.ShouldBe([0, 1, 2],
            "Steps 0 and 1 committed. Step 2 ran, and its commit was the one refused — its " +
            "effect happened and no row records it, which is the honest limit ADR-0006 states.");

        dispatcher.Compensated.ShouldBeEmpty(
            "Steps 1 and 2 are compensable and both had run. Unwinding them here would run " +
            "this node's undo against an instance another node now owns and is carrying " +
            "forwards — an undo racing a redo.");

        result.Compensation.ShouldBe(CompensationOutcome.Abandoned,
            "There was undo work and this node was not the one to do it. Reporting " +
            "NotRequired would make a disowned saga look like a query.");

        var record = await journal.ReadInstanceAsync(instanceId, ct);

        record.Value.State.ShouldBe(FlowInstanceState.Running,
            "This node did not seal it either. The terminal state belongs to whoever holds " +
            "the instance now.");
    }

    /// <summary>An ordinary failure still compensates, exactly as it always did.</summary>
    /// <remarks>
    /// The control for the assertion above. "Do not compensate" is scoped to the two refusals
    /// that mean this node is no longer the writer; a business failure in a durable flow
    /// unwinds like any other, and a change that widened it would be a saga that silently
    /// stopped undoing things.
    /// </remarks>
    [Fact]
    public async Task AnOrdinaryFailureInADurableFlowStillCompensates()
    {
        var journal = new StolenJournal();
        var leases = new InMemoryLeaseStore();
        var plan = Durable(Plans.FourStepSaga());
        var ct = TestContext.Current.CancellationToken;

        await using var lease = await AcquireAsync(leases, Guid.NewGuid(), NodeOne);

        var begun = await lease.BeginAsync(journal, plan, Plans.Invocation, cancellationToken: ct);

        var engine = new FlowEngine(new FakeClock(T0));

        var dispatcher = new RecordingDispatcher()
            .FailAt(2, new Error("payment.declined", "declined", ErrorCategory.Conflict));

        var result = await engine.ExecuteAsync(plan, dispatcher, Plans.Invocation, begun.Value, ct);

        result.IsFailure.ShouldBeTrue();
        result.Compensation.ShouldBe(CompensationOutcome.Succeeded);
        dispatcher.Compensated.ShouldBe([1]);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan budget, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + budget;

        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10, ct);
        }
    }

    /// <summary>
    /// The reference lease store with the two failures a renewal has to tell apart.
    /// </summary>
    /// <remarks>
    /// A wrapper rather than a rewrite, so everything except the injected fault is behaviour
    /// <c>LeaseStoreConformance</c> holds the inner store to.
    /// </remarks>
    private sealed class RefusingLeaseStore : ILeaseStore
    {
        private readonly InMemoryLeaseStore _inner = new();

        private int _renewAttempts;

        /// <summary>A working store saying no: the lease is gone for good.</summary>
        public bool RefuseRenewals { get; set; }

        /// <summary>A store that is unreachable: it may be back before the lease lapses.</summary>
        public bool ThrowOnRenewals { get; set; }

        /// <summary>A store that is unreachable while a lease is being given up.</summary>
        public bool ThrowOnRelease { get; set; }

        public int RenewAttempts => Volatile.Read(ref _renewAttempts);

        public ValueTask<Result<FlowLease>> AcquireAsync(
            Guid instanceId, string ownerNode, TimeSpan ttl, CancellationToken cancellationToken) =>
            _inner.AcquireAsync(instanceId, ownerNode, ttl, cancellationToken);

        public ValueTask<Result<FlowLease>> RenewAsync(
            FlowLease lease, TimeSpan ttl, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _renewAttempts);

            if (ThrowOnRenewals)
            {
                throw new StoreUnreachableException();
            }

            return RefuseRenewals
                ? new ValueTask<Result<FlowLease>>(
                    Result.Fail<FlowLease>(DurabilityErrors.LeaseLost(lease.InstanceId, lease.Token)))
                : _inner.RenewAsync(lease, ttl, cancellationToken);
        }

        public ValueTask<Result<bool>> ReleaseAsync(FlowLease lease, CancellationToken cancellationToken) =>
            ThrowOnRelease
                ? throw new StoreUnreachableException()
                : _inner.ReleaseAsync(lease, cancellationToken);

        public ValueTask<Result<FlowLease>> ReadAsync(Guid instanceId, CancellationToken cancellationToken) =>
            _inner.ReadAsync(instanceId, cancellationToken);
    }

    /// <summary>
    /// The reference journal, with another node taking the instance over part-way through.
    /// </summary>
    /// <remarks>
    /// The takeover is performed the way a real one is — a higher token raising the fence
    /// through <see cref="IFlowJournal.FenceAsync"/> — rather than by editing state, so the
    /// refusal the first node then gets is the store's own and not one this double invented.
    /// </remarks>
    private sealed class StolenJournal : IFlowJournal
    {
        private readonly InMemoryFlowJournal _inner = new();
        private readonly Lock _gate = new();

        private int _commits;

        /// <summary>Which commit another node steals the instance before, counting from one.</summary>
        public int? StolenOnCommit { get; init; }

        public ValueTask<Result<FlowInstanceRecord>> StartAsync(
            FlowInstanceStart start, CancellationToken cancellationToken) =>
            _inner.StartAsync(start, cancellationToken);

        public ValueTask<Result<FencingToken>> FenceAsync(
            Guid instanceId, FencingToken token, CancellationToken cancellationToken) =>
            _inner.FenceAsync(instanceId, token, cancellationToken);

        public async ValueTask<Result<JournalStep>> CommitAsync(
            StepCommit commit, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(commit);

            int attempt;

            lock (_gate)
            {
                attempt = ++_commits;
            }

            if (attempt == StolenOnCommit)
            {
                await _inner
                    .FenceAsync(commit.Key.InstanceId, commit.Token.Next(), cancellationToken)
                    .ConfigureAwait(false);
            }

            return await _inner.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
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

    /// <summary>The store being unreachable, as opposed to working and saying no.</summary>
    private sealed class StoreUnreachableException()
        : InvalidOperationException("The lease store cannot be reached.");
}
