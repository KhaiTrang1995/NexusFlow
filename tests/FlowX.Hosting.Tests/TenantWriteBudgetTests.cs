using System.Security.Claims;
using FlowX.Conformance.InMemory;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Hosting.Tests;

/// <summary>
/// <c>docs/16 §4</c>'s sixth fairness mechanism: what one tenant's journal writes cost, who
/// decides the total, and what happens to the tenant that has spent it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Three claims, and each one is what makes the shape defensible rather than merely
/// present.</strong> That the budget is <em>shared</em> — every row spent is a row a shared
/// bucket gave up, so a fleet cannot write n × the declared rate, which is the whole of
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0040-a-rate-limit-is-shared-or-it-is-not-a-rate-limit.md">ADR-0040</a>.
/// That it is <em>amortised</em> — a block of credit is drawn once and spent many times, so the
/// mechanism does not put a round trip in front of every write it protects. And that exhaustion
/// <em>paces</em> rather than refuses, because a refused commit halfway through a durable flow
/// abandons a saga rather than applying backpressure.
/// </para>
/// <para>
/// The load-bearing test is the last one: a tenant refused by a mechanism proves the mechanism
/// exists, and quality goal <strong>Q8</strong> is a statement about the <em>other</em> tenant.
/// </para>
/// </remarks>
public sealed class TenantWriteBudgetTests
{
    private const string Noisy = "acme";
    private const string Quiet = "globex";

    /// <summary>Start, one step commit, complete — the rows a one-step durable flow writes.</summary>
    private const int RowsPerFlow = 3;

    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static readonly CapabilityDescriptor Validate =
        CapabilityDescriptor.Create("order.validate", "1.0.0", isIdempotent: true);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// A flow's rows are paid for by one draw against the shared bucket, not by one round trip
    /// each.
    /// </summary>
    /// <remarks>
    /// <strong>This is the reason the mechanism is buildable at all.</strong> Spending a shared
    /// budget per commit would put a limiter call in front of every journal write — doubling the
    /// latency of the thing being protected — which is why the sixth mechanism was left unbuilt
    /// while the other five shipped. A block covers the whole flow, so the cost is one round trip
    /// amortised over <c>JournalWriteBlock</c> rows and an interlocked decrement for the rest.
    /// </remarks>
    [Fact]
    public async Task AFlowsRowsAreBoughtWithOneDrawAndNotOnePerRow()
    {
        var fixture = new Fixture(writesPerWindow: 32, block: 32);

        var outcome = await fixture.RunAsync(Noisy, Cancellation);

        outcome.IsSuccess.ShouldBeTrue(outcome.Error?.ToString() ?? string.Empty);

        fixture.Limiter.Draws.ShouldBe(
            1,
            $"a flow writing {RowsPerFlow} rows cost one draw. A budget spent per commit would " +
            $"have cost {RowsPerFlow}, which is a round trip in front of every write it exists " +
            "to protect.");

        // The same flow with a block of one, which is what a per-commit shared budget is. Its
        // draw count is the row count, so the assertion above is amortisation rather than a
        // mechanism that quietly stopped charging after the first row.
        var unamortised = new Fixture(writesPerWindow: 32, block: 1);

        (await unamortised.RunAsync(Noisy, Cancellation)).IsSuccess.ShouldBeTrue();

        unamortised.Limiter.Draws.ShouldBe(RowsPerFlow);
    }

    /// <summary>Every row spent came out of the one shared bucket, under the tenant's own key.</summary>
    /// <remarks>
    /// ADR-0040's argument is that a budget each node keeps for itself is the declared limit
    /// multiplied by the replica count. A block draw is not that: the block is removed from the
    /// shared bucket before any of it can be spent, so the fleet's total is the bucket's and only
    /// the <em>unspent</em> remainder varies with the node count. This asserts the draw really
    /// reaches the store, and that it is keyed by the tenant rather than folded into the
    /// admission bucket.
    /// </remarks>
    [Fact]
    public async Task TheBlockIsDrawnFromTheSharedBucketUnderTheTenantsOwnKey()
    {
        var fixture = new Fixture(writesPerWindow: 32, block: 32);

        await fixture.RunAsync(Noisy, Cancellation);

        fixture.Limiter.Keys.Count.ShouldBe(1);

        fixture.Limiter.Keys[0].Contains(Noisy, StringComparison.Ordinal).ShouldBeTrue(
            "the budget is keyed by the tenant, or it is a global limit wearing a per-tenant " +
            $"declaration.\n{fixture.Limiter.Keys[0]}");

        fixture.Limiter.Keys[0].Contains("writes", StringComparison.Ordinal).ShouldBeTrue(
            "and by a bucket of its own, because rows and calls are different units: folding " +
            "them into the admission bucket would make a tenant's plan limit depend on how " +
            $"loop-heavy its flows happen to be.\n{fixture.Limiter.Keys[0]}");
    }

    /// <summary>
    /// A tenant that has spent its budget waits for credit and then commits, rather than having
    /// its flow refused mid-way.
    /// </summary>
    /// <remarks>
    /// <strong>The distinction the other five mechanisms do not have to draw.</strong> Those are
    /// spent at admission, where a refusal costs a caller nothing that had started. A journal
    /// write happens inside a durable flow that already holds a lease and an instance row, so
    /// refusing one is not backpressure — it strands a saga with its compensations unrun, which
    /// is the reason <c>FlowHost</c> already exempts a continuation from the stage-1 bounds.
    /// </remarks>
    [Fact]
    public async Task AnExhaustedTenantIsPacedAndThenCommits()
    {
        // A block covers exactly one flow, so the second one finds the bucket empty on its very
        // first row — which is the state this test is about.
        var fixture = new Fixture(writesPerWindow: RowsPerFlow, block: RowsPerFlow, blocksPerTenant: 2);

        (await fixture.RunAsync(Noisy, Cancellation)).IsSuccess.ShouldBeTrue("the first flow is inside the budget");

        // The bucket is empty for exactly one poll, and the wait it causes is satisfied rather
        // than left open: a window turning over, played out without spending one.
        fixture.Limiter.RefuseNext(1);
        fixture.Clock.Release();

        var paced = await fixture.RunAsync(Noisy, Cancellation);

        paced.IsSuccess.ShouldBeTrue(
            "the flow completed. A write budget that refused the commit would have ended a " +
            "durable flow halfway, which is abandonment rather than backpressure.\n" +
            (paced.Error?.ToString() ?? string.Empty));

        fixture.Clock.Waits.ShouldBe(
            1,
            "and it completed by waiting. Without this the assertion above would pass just as " +
            "well against a budget that was never enforced at all.");
    }

    /// <summary>
    /// One tenant blocked on an exhausted write budget does not hold up another tenant's
    /// commits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The mechanism's whole purpose, and the only test here that states it.</strong>
    /// The noisy tenant is left genuinely stuck — its bucket has nothing left and this fixture's
    /// limiter never refills — so if the budgets were shared between tenants, or if the pacing
    /// held a resource the second tenant needed, the quiet flow would never return.
    /// </para>
    /// <para>
    /// It asserts the quiet tenant <em>succeeds</em> rather than that it is fast: a wall-clock
    /// assertion would be a flake, and "completed while the other tenant is still blocked" is
    /// the stronger claim anyway.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ANoisyTenantsExhaustedWriteBudgetDoesNotStopAQuietTenantCommitting()
    {
        // One block per tenant and one flow per block, so the noisy tenant's second flow finds a
        // bucket that has nothing left and no window that will ever turn over.
        var fixture = new Fixture(
            writesPerWindow: RowsPerFlow, block: RowsPerFlow, blocksPerTenant: 1);

        (await fixture.RunAsync(Noisy, Cancellation)).IsSuccess.ShouldBeTrue("the noisy tenant spends its budget");

        using var stuck = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);

        var blocked = fixture.RunAsync(Noisy, stuck.Token);

        try
        {
            var quiet = await fixture.RunAsync(Quiet, Cancellation);

            quiet.IsSuccess.ShouldBeTrue(
                "the quiet tenant has spent none of its own budget, and the noisy tenant's " +
                "exhaustion is not its to pay for. A budget keyed by anything but the tenant " +
                "would be a global limit wearing a per-tenant declaration.\n" +
                (quiet.Error?.ToString() ?? string.Empty));

            blocked.IsCompleted.ShouldBeFalse(
                "and the noisy tenant is still waiting, so this was measured under load rather " +
                "than after it drained");
        }
        finally
        {
            await stuck.CancelAsync();

            try
            {
                await blocked;
            }
            catch (OperationCanceledException)
            {
                // The paced flow was released by the cancellation this test supplied, which is
                // the only thing that bounds the wait. Nothing to assert about it.
            }
        }
    }

    /// <summary>
    /// A deployment that declares no write budget gets the store's own journal, not a decorator
    /// that charges nothing.
    /// </summary>
    /// <remarks>
    /// <strong>Structural rather than careful, which is the bargain <c>ExecutionPlan</c>'s flags
    /// strike one layer down.</strong> There is no branch on the commit path to be predicted and
    /// no field to read: a host that did not ask for this hands the engine the journal the store
    /// gave it. The same holds for an untenanted execution, because a per-tenant budget with no
    /// tenant has nothing to key on.
    /// </remarks>
    [Fact]
    public void AnUndeclaredBudgetLeavesTheJournalItWasGiven()
    {
        var journal = new InMemoryFlowJournal();

        var undeclared = new TenantWriteBudget(
            new TenantFairness(), new RecordingLimiter(), SystemClock.Instance);

        undeclared.Bind(journal, Noisy).ShouldBeSameAs(
            journal,
            "no budget declared, so there is nothing to charge and nothing to wrap");

        var declared = new TenantWriteBudget(
            new TenantFairness { JournalWritesPerWindow = 32, JournalWriteBlock = 32 },
            new RecordingLimiter(),
            SystemClock.Instance);

        declared.Bind(journal, tenantId: null).ShouldBeSameAs(
            journal,
            "and a per-tenant budget with no tenant has no key to spend under");

        declared.Bind(journal, Noisy).ShouldNotBeSameAs(
            journal,
            "but a declared budget on a resolved tenant really does charge, or the two " +
            "assertions above would hold against a mechanism that never runs");
    }

    // -----------------------------------------------------------------------------------
    // Fixtures
    // -----------------------------------------------------------------------------------

    /// <summary>A host with a real journal, a real lease store and a countable budget.</summary>
    private sealed class Fixture
    {
        private readonly InMemoryFlowJournal _journal = new();
        private readonly InMemoryLeaseStore _leases = new();
        private readonly FlowHost _host;

        public Fixture(int writesPerWindow, int block, int blocksPerTenant = int.MaxValue)
        {
            var options = new FlowXOptions
            {
                ApplicationName = "Tests",
                NodeName = "node",
                TenantIsolation = TenantIsolation.Row,
            };

            options.Fairness.JournalWritesPerWindow = writesPerWindow;
            options.Fairness.JournalWriteBlock = block;
            options.Fairness.JournalWriteWindow = TimeSpan.FromMinutes(5);

            Limiter = new RecordingLimiter(blocksPerTenant);

            _host = new FlowHost(
                new FlowEngine(Clock),
                options,
                new FlowDurability(_journal, _leases),
                tenants: null,
                Limiter,
                Clock);
        }

        public RecordingLimiter Limiter { get; }

        public CountingClock Clock { get; } = new(T0);

        public Task<FlowExecutionResult> RunAsync(string tenantId, CancellationToken ct) =>
            _host.RunAsync(
                Plan(),
                new PassingDispatcher(),
                new FlowInvocation(
                    "corr",
                    Guid.NewGuid().ToString(),
                    Principal: new ClaimsPrincipal(
                        new ClaimsIdentity([new Claim("tid", tenantId)], "test"))),
                ct)
                .AsTask();

        private static ExecutionPlan Plan() => ExecutionPlan.Create(
            FlowDescriptor.Create(
                "order.place", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromMinutes(5)),
            StepGraph.Create([StepNode.ForCapability(0, Validate)]));
    }

    /// <summary>
    /// A limiter that grants a fixed number of blocks per key and then refuses for ever.
    /// </summary>
    /// <remarks>
    /// No refill, so a tenant that has spent its blocks stays spent for the length of the test.
    /// That is what makes "the quiet tenant still commits" a claim about the keys being separate
    /// rather than about a window happening to turn over at the right moment.
    /// </remarks>
    private sealed class RecordingLimiter : IRateLimiterStore
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, int> _granted = new(StringComparer.Ordinal);
        private readonly int _blocksPerKey;
        private int _refusals;

        public RecordingLimiter(int blocksPerKey = int.MaxValue) => _blocksPerKey = blocksPerKey;

        /// <summary>Every key a draw was attempted against, in order.</summary>
        public List<string> Keys { get; } = [];

        /// <summary>How many draws were admitted.</summary>
        public int Draws { get; private set; }

        /// <summary>Refuses the next <paramref name="calls"/> draws, whatever the key.</summary>
        /// <remarks>An empty bucket that is about to refill, scripted rather than waited for.</remarks>
        public void RefuseNext(int calls)
        {
            lock (_gate)
            {
                _refusals = calls;
            }
        }

        public ValueTask<Result<RateLimitVerdict>> TryAcquireAsync(
            string key, int permits, TimeSpan window, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Keys.Add(key);

                if (_refusals > 0)
                {
                    _refusals--;

                    return new(Result.Ok(new RateLimitVerdict(false, 0, window)));
                }

                var taken = _granted.GetValueOrDefault(key);

                if (taken >= _blocksPerKey)
                {
                    return new(Result.Ok(new RateLimitVerdict(false, 0, window)));
                }

                _granted[key] = taken + 1;
                Draws++;

                return new(Result.Ok(new RateLimitVerdict(true, permits - taken - 1, TimeSpan.Zero)));
            }
        }
    }

    /// <summary>
    /// A clock whose waits end when the test says the bucket refilled, and never on their own.
    /// </summary>
    /// <remarks>
    /// A wait that returned immediately would turn a paced caller into a spin against the
    /// limiter, and one that advanced <see cref="UtcNow"/> would let a node's credit expiry
    /// release it without the shared bucket having granted anything. So a paced caller stays
    /// paced until <see cref="Release"/> or its cancellation token, which is exactly the two
    /// things that end a real one.
    /// </remarks>
    private sealed class CountingClock(DateTimeOffset start) : IClock
    {
        private readonly TaskCompletionSource _refilled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _waits;

        public DateTimeOffset UtcNow { get; } = start;

        /// <summary>How many times a caller was paced.</summary>
        public int Waits => Volatile.Read(ref _waits);

        /// <summary>Says the bucket has refilled, so paced callers may carry on.</summary>
        public void Release() => _refilled.TrySetResult();

        public async ValueTask DelayAsync(
            TimeSpan delay, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _waits);

            await _refilled.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class PassingDispatcher : IStepDispatcher
    {
        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct) =>
            ValueTask.FromResult(StepOutcome.Success);

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct) =>
            ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This plan has no branch step.");

        public int Select(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This plan has no switch step.");

        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This plan has no iteration.");

        public FlowContext EnterIteration(
            int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException("This plan has no iteration.");
    }
}
