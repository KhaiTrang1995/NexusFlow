using System.Security.Claims;
using FlowX.Conformance.InMemory;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Hosting.Tests;

/// <summary>
/// The noisy neighbour: one tenant with a backlog, one without, and what the platform does to
/// the second one's latency.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every test here that only showed a mechanism refusing would prove nothing.</strong>
/// A rate limiter that returns 429 is evidence that a rate limiter exists, not that a quiet
/// tenant got served — and quality goal <strong>Q8</strong> is a statement about the quiet
/// tenant. So the two load-bearing tests in this file are a matched pair: the first drives two
/// tenants at unequal rates against the sweep as it was and asserts the quiet one <em>never
/// runs</em>; the second changes one setting and asserts it runs on the first sweep, while the
/// noisy tenant is still draining. Neither is meaningful without the other.
/// </para>
/// <para>
/// <strong>The starvation is real and it is not in the engine.</strong> Both sweeps ask their
/// index for the oldest work in the table, bounded by a page size, and spend a bounded number
/// of slots on what comes back. A tenant whose backlog is longer than the page therefore owns
/// every row of every page: the quiet tenant's instance is not served late, it is never
/// fetched. That is why the fix is in two halves — <c>PerTenantLimit</c> on the query, so the
/// page contains both tenants, and <see cref="TenantFairShare"/> over the page, so the slots do
/// not all go to the tenant that filled it.
/// </para>
/// </remarks>
public sealed class TenantFairnessTests
{
    private const string Noisy = "acme";
    private const string Quiet = "globex";

    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static readonly CapabilityDescriptor Validate =
        CapabilityDescriptor.Create("order.validate", "1.0.0", isIdempotent: true);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    // -----------------------------------------------------------------------------------
    // The starvation, and then its absence
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// Without fairness, a tenant whose backlog fills the page takes every slot and the quiet
    /// tenant is never woken at all.
    /// </summary>
    /// <remarks>
    /// <strong>This test asserts a defect, and deleting it would hide the point of the
    /// feature.</strong> Forty parked instances for one tenant and three for another, a page of
    /// sixteen and four slots a sweep: the page is the sixteen oldest rows, all of which belong
    /// to the noisy tenant, so the quiet tenant's three instances are not merely at the back of
    /// the queue — the sweep has never seen them. Five sweeps is twenty slots spent, half the
    /// noisy backlog drained, and not one of them went to the tenant that was waiting.
    /// </remarks>
    [Fact]
    public async Task ANoisyTenantTakesEverySlotAndTheQuietOneIsNeverWoken()
    {
        var fixture = await Fixture.LoadedAsync(fair: false);

        for (var sweep = 0; sweep < 5; sweep++)
        {
            await fixture.SweepAsync();
        }

        (await fixture.WokenAsync(Noisy)).ShouldBe(
            20, "four slots a sweep, five sweeps, every one of them the noisy tenant's");

        (await fixture.WokenAsync(Quiet)).ShouldBe(
            0,
            "the quiet tenant parked three instances and after five sweeps none has run. This " +
            "is not a slow queue — its rows were never in a page, because the page is the " +
            "oldest work in the table and the noisy tenant owns all of it. Q8 is a statement " +
            "about this number.");
    }

    /// <summary>
    /// With fairness, the quiet tenant is woken on the first sweep, while the noisy tenant is
    /// still tens of instances from draining.
    /// </summary>
    /// <remarks>
    /// <strong>The same arrangement, the same page size, the same four slots.</strong> What
    /// changed is that the query caps what one tenant may occupy of the page, so the quiet
    /// tenant's rows are fetched, and <see cref="TenantFairShare"/> interleaves the page's
    /// tenants so a slot reaches them. The noisy tenant still progresses — this is fair
    /// queueing and not a penalty — which the second assertion is here to hold: a "fairness"
    /// mechanism that fixed the quiet tenant's latency by stopping the noisy one would have
    /// moved the outage rather than removed it.
    /// </remarks>
    [Fact]
    public async Task TheQuietTenantIsWokenOnTheFirstSweepAndTheNoisyOneStillProgresses()
    {
        var fixture = await Fixture.LoadedAsync(fair: true);

        await fixture.SweepAsync();

        (await fixture.WokenAsync(Quiet)).ShouldBeGreaterThan(
            0,
            "one setting changed and the tenant that could not get a slot in five sweeps got " +
            "one in the first.");

        (await fixture.WokenAsync(Noisy)).ShouldBeGreaterThan(
            0, "fair queueing shares the slots; it does not take them away from the busy tenant");

        // Three more sweeps: the quiet tenant's whole backlog is gone and the noisy tenant's
        // is still most of what it was. That is what "bounded latency for the quiet tenant"
        // means when the system as a whole is saturated.
        for (var sweep = 0; sweep < 3; sweep++)
        {
            await fixture.SweepAsync();
        }

        (await fixture.WokenAsync(Quiet)).ShouldBe(
            Fixture.QuietInstances, "the quiet tenant's backlog drains in four sweeps");

        (await fixture.WokenAsync(Noisy)).ShouldBeLessThan(
            Fixture.NoisyInstances,
            "the noisy tenant is still working through a backlog it created, which is correct");
    }

    /// <summary>
    /// A tenant with a declared weight of three is served three candidates to another's one.
    /// </summary>
    /// <remarks>
    /// The <em>weighted</em> half of weighted fair queueing, and the reason plain round robin is
    /// not enough: an enterprise plan and a free plan share a node, and the free tenant's share
    /// has to be small without ever being zero. Asserted on the ordering rather than through a
    /// sweep, because a sweep's slot count would hide the ratio inside its own rounding.
    /// </remarks>
    [Fact]
    public void AWeightedTenantTakesItsWeightOfEachRound()
    {
        string[] page = [Noisy, Noisy, Noisy, Noisy, Quiet, Quiet];

        var order = TenantFairShare.Order(
            page,
            static tenant => tenant,
            tenant => tenant == Noisy ? 3 : 1,
            offset: 0);

        order[..4].Select(i => page[i]).ShouldBe(
            [Noisy, Noisy, Noisy, Quiet],
            "three of the noisy tenant's, then one of the quiet tenant's — the declared ratio, " +
            "in the first round rather than averaged over the page");

        order.Length.ShouldBe(page.Length, "every candidate is reordered and none is dropped");
        order.Distinct().Count().ShouldBe(page.Length, "and none is duplicated");
    }

    // -----------------------------------------------------------------------------------
    // Admission control: the three stage-1 bounds
    // -----------------------------------------------------------------------------------

    /// <summary>A tenant that has spent its permits is refused before a flow instance exists.</summary>
    /// <remarks>
    /// <c>docs/16 §4</c> puts every limit at stage 1 — "before authentication, before any
    /// allocation, before any journal write" — because "rejecting expensively is how rate
    /// limiting becomes the DoS". <c>CompletedSteps</c> is what asserts it: a refusal after the
    /// first step is a call that was admitted and stopped.
    /// </remarks>
    [Fact]
    public async Task ATenantOverItsRateIsRefusedBeforeAnyStepRuns()
    {
        var host = NewHost(fairness => fairness.PermitsPerWindow = 1);
        var dispatcher = new CountingDispatcher();

        var first = await host.RunAsync(Plan(), dispatcher, AsTenant(Noisy), Cancellation);
        var second = await host.RunAsync(Plan(), dispatcher, AsTenant(Noisy), Cancellation);

        first.IsSuccess.ShouldBeTrue("the first call is inside the budget");

        second.Error!.Code.ShouldBe(TenantErrors.RateLimitedCode);
        second.Error.Category.ShouldBe(
            ErrorCategory.Unavailable,
            "the caller is entitled to the call and is not getting it now, which is a 429 and " +
            "not a 403 — the one refusal in TenantErrors that is worth retrying");

        dispatcher.Calls.ShouldBe(1, "the refused call dispatched nothing");
    }

    /// <summary>One tenant's spent rate does not refuse another's call.</summary>
    /// <remarks>
    /// The bucket is keyed by tenant, which is the whole of what makes this fairness rather than
    /// a global limit — <c>docs/16 §9</c> lists "global-only rate limiting" as the anti-pattern
    /// whose consequence is "one tenant starves all". Without this assertion the previous test
    /// would pass just as well against a limiter keyed by nothing.
    /// </remarks>
    [Fact]
    public async Task ASpentTenantDoesNotRefuseAnotherTenantsCall()
    {
        var host = NewHost(fairness => fairness.PermitsPerWindow = 1);

        await host.RunAsync(Plan(), new CountingDispatcher(), AsTenant(Noisy), Cancellation);

        var noisy = await host.RunAsync(Plan(), new CountingDispatcher(), AsTenant(Noisy), Cancellation);
        var quiet = await host.RunAsync(Plan(), new CountingDispatcher(), AsTenant(Quiet), Cancellation);

        noisy.IsFailure.ShouldBeTrue("the noisy tenant has spent its permit");
        quiet.IsSuccess.ShouldBeTrue(
            "and the quiet tenant has spent none of its own. A limiter that refused this call " +
            "would be a global limit wearing a per-tenant declaration.");
    }

    /// <summary>A quota refusal is a different code from a rate-limit refusal.</summary>
    /// <remarks>
    /// The two are the same token bucket over the same store under different keys, so folding
    /// them into one refusal would have been defensible and is still wrong: one is repaired by
    /// backing off and the other by buying more, and an operator reading one code for both has
    /// to guess which.
    /// </remarks>
    [Fact]
    public async Task AnExhaustedQuotaIsReportedAsAQuotaAndNotAsARate()
    {
        var host = NewHost(fairness =>
        {
            fairness.QuotaPerWindow = 1;
            fairness.QuotaWindow = TimeSpan.FromHours(1);
        });

        await host.RunAsync(Plan(), new CountingDispatcher(), AsTenant(Noisy), Cancellation);

        var refused = await host.RunAsync(Plan(), new CountingDispatcher(), AsTenant(Noisy), Cancellation);

        refused.Error!.Code.ShouldBe(TenantErrors.QuotaExhaustedCode);
    }

    /// <summary>A tenant at its concurrency bound is shed rather than queued.</summary>
    /// <remarks>
    /// <c>docs/16 §4</c>: "429 · shed early, do not queue". The second call returns while the
    /// first is still executing, which is the observable difference between shedding and
    /// queueing — a queue would have made it wait for the slot it was refused.
    /// </remarks>
    [Fact]
    public async Task ATenantAtItsBulkheadIsShedAndNotQueued()
    {
        var host = NewHost(fairness => fairness.MaxConcurrency = 1);
        var blocking = new BlockingDispatcher();

        var held = host.RunAsync(Plan(), blocking, AsTenant(Noisy), Cancellation);

        await blocking.Entered.Task.WaitAsync(Cancellation);

        var shed = await host.RunAsync(Plan(), new CountingDispatcher(), AsTenant(Noisy), Cancellation);

        shed.Error!.Code.ShouldBe(TenantErrors.SaturatedCode);

        var quiet = await host.RunAsync(Plan(), new CountingDispatcher(), AsTenant(Quiet), Cancellation);

        quiet.IsSuccess.ShouldBeTrue(
            "the pools are per tenant, so a full one holds no slot the other tenant needs");

        blocking.Release();
        await held;

        var afterwards = await host.RunAsync(Plan(), new CountingDispatcher(), AsTenant(Noisy), Cancellation);

        afterwards.IsSuccess.ShouldBeTrue(
            "the slot came back. A permit leaked on the completion path is a tenant that " +
            "becomes permanently saturated — the starvation this exists to prevent, arriving " +
            "through its own mechanism.");
    }

    /// <summary>A continuation is exempt from the bounds, and that is not a bypass.</summary>
    /// <remarks>
    /// A sweep resuming an instance is finishing work this platform admitted and charged for
    /// once. Refusing it for want of a permit would not be backpressure — it would abandon a
    /// saga halfway with its compensations unrun, and a node restart would become an outage.
    /// What bounds a continuation is the sweep that issues it.
    /// </remarks>
    [Fact]
    public async Task AContinuationIsNotChargedForTheTenantsSpentBudget()
    {
        // Exactly enough permits to park the noisy tenant's backlog, so its budget is spent by
        // the arrangement rather than by a contrivance.
        var fixture = await Fixture.LoadedAsync(fair: true, permitsPerWindow: Fixture.NoisyInstances);

        var refused = await fixture.Host.RunAsync(
            Fixture.EphemeralPlan(), new CountingDispatcher(), AsTenant(Noisy), Cancellation);

        refused.Error!.Code.ShouldBe(TenantErrors.RateLimitedCode, "the budget really is spent");

        await fixture.SweepAsync();

        (await fixture.WokenAsync(Noisy)).ShouldBeGreaterThan(
            0,
            "and a sweep still resumed the instances that budget already paid for. Refusing " +
            "them would abandon sagas halfway rather than apply backpressure.");
    }

    /// <summary>
    /// A single-tenant deployment consults no limiter, whatever fairness would have said.
    /// </summary>
    /// <remarks>
    /// <strong>B2 stays a hard zero, and this is what says so rather than a paragraph.</strong>
    /// The host branches on <see cref="TenantIsolation.None"/> before it builds admission
    /// control at all, so the store is not merely unconsulted — there is nothing to consult it.
    /// A recording limiter is the only way to assert an absence like this from the outside.
    /// </remarks>
    [Fact]
    public async Task ASingleTenantDeploymentNeverReachesTheLimiter()
    {
        var limiter = new RecordingLimiter();

        var host = new FlowHost(
            new FlowEngine(new FixedClock()),
            new FlowXOptions { ApplicationName = "Tests", NodeName = "node" },
            durability: null,
            tenants: null,
            limiter);

        var outcome = await host.RunAsync(
            Plan(), new CountingDispatcher(), new FlowInvocation("corr", "idem"), Cancellation);

        outcome.IsSuccess.ShouldBeTrue();
        limiter.Keys.ShouldBeEmpty(
            "TenantIsolation.None pays nothing for a feature it did not ask for");
    }

    /// <summary>The rate limit and the quota are two buckets, not one.</summary>
    /// <remarks>
    /// They are the same mechanism over the same store, so it would be easy to spend them
    /// against one key and easy not to notice: the deployment would then have a single budget
    /// whose window was whichever setting was written last. The keys are what keeps them two.
    /// </remarks>
    [Fact]
    public async Task TheRateAndTheQuotaAreSpentAgainstSeparateBuckets()
    {
        var limiter = new RecordingLimiter();

        var host = NewHost(
            fairness =>
            {
                fairness.PermitsPerWindow = 10;
                fairness.QuotaPerWindow = 100;
            },
            limiter);

        await host.RunAsync(Plan(), new CountingDispatcher(), AsTenant(Noisy), Cancellation);

        limiter.Keys.Distinct().Count().ShouldBe(2, "one bucket for the rate, one for the quota");
        limiter.Keys.ShouldAllBe(key => key.Contains(Noisy, StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------------------
    // Fixtures
    // -----------------------------------------------------------------------------------

    private static FlowHost NewHost(Action<TenantFairness> configure, IRateLimiterStore? limiter = null)
    {
        var options = new FlowXOptions
        {
            ApplicationName = "Tests",
            NodeName = "node",
            TenantIsolation = TenantIsolation.Row,
        };

        configure(options.Fairness);

        return new FlowHost(
            new FlowEngine(new FixedClock()),
            options,
            durability: null,
            tenants: null,
            limiter ?? new InMemoryRateLimiter());
    }

    private static ExecutionPlan Plan() => ExecutionPlan.Create(
        FlowDescriptor.Create(
            "order.place", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromMinutes(5)),
        StepGraph.Create([StepNode.ForCapability(0, Validate)]));

    private static FlowInvocation AsTenant(string tenantId) =>
        new("corr", Guid.NewGuid().ToString(), Principal: PrincipalFor(tenantId));

    private static ClaimsPrincipal PrincipalFor(string tenantId) =>
        new(new ClaimsIdentity([new Claim("tid", tenantId)], "test"));

    /// <summary>
    /// Two tenants' parked instances, arranged so that one owns every page a sweep can fetch.
    /// </summary>
    /// <remarks>
    /// The noisy tenant's instances are parked first and the clock moves a second between each,
    /// so its wake instants are strictly older than the quiet tenant's. That is the arrangement
    /// the store's <c>ORDER BY wake_at</c> turns into starvation, and it is the ordinary shape
    /// of a tenant that has simply been busier for longer.
    /// </remarks>
    private sealed class Fixture
    {
        public const int NoisyInstances = 40;
        public const int QuietInstances = 3;

        private const int PageSize = 16;
        private const int Slots = 4;

        private readonly List<Guid> _noisy = [];
        private readonly List<Guid> _quiet = [];

        private Fixture(bool fair, int permitsPerWindow)
        {
            Options = new FlowXOptions
            {
                ApplicationName = "Tests",
                NodeName = "node",
                TenantIsolation = TenantIsolation.Row,
                TimerScanBatchSize = PageSize,
                MaxConcurrentRecoveries = Slots,
            };

            if (fair)
            {
                // One setting, and it is the whole difference between the two tests above.
                Options.Fairness.PerTenantScanShare = Slots;
            }

            Options.Fairness.PermitsPerWindow = permitsPerWindow;

            Durability = new FlowDurability(Journal, Leases, timerIndex: new InMemoryTimerIndex(Journal));

            Host = new FlowHost(
                new FlowEngine(Clock), Options, Durability, tenants: null, new InMemoryRateLimiter());
        }

        public InMemoryFlowJournal Journal { get; } = new();

        public InMemoryLeaseStore Leases { get; } = new();

        public MovableClock Clock { get; } = new(T0);

        public FlowDurability Durability { get; }

        public FlowHost Host { get; }

        public FlowXOptions Options { get; }

        public FlowCatalog Catalogue { get; } = new();

        /// <summary>Validate, wait an hour, validate again — parked at the timer.</summary>
        private static ExecutionPlan DelayingPlan() => ExecutionPlan.Create(
            FlowDescriptor.Create("order.place", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromDays(30)),
            StepGraph.Create([
                StepNode.ForCapability(0, Validate),
                StepNode.ForDelay(1, TimeSpan.FromHours(1)),
                StepNode.ForCapability(2, Validate),
            ]));

        /// <summary>A one-step flow, for asserting what admission does rather than what a sweep does.</summary>
        public static ExecutionPlan EphemeralPlan() => ExecutionPlan.Create(
            FlowDescriptor.Create(
                "order.check", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromMinutes(5)),
            StepGraph.Create([StepNode.ForCapability(0, Validate)]));

        public static async Task<Fixture> LoadedAsync(bool fair, int permitsPerWindow = 0)
        {
            var plan = DelayingPlan();
            var fixture = new Fixture(fair, permitsPerWindow);

            fixture.Catalogue.Add(plan, new CountingDispatcher());

            await fixture.ParkAsync(plan, Noisy, NoisyInstances, fixture._noisy);
            await fixture.ParkAsync(plan, Quiet, QuietInstances, fixture._quiet);

            // Every wait is now over, so the whole backlog is due at once — the state a sweep
            // finds after an outage, and the only state in which fairness is measurable.
            fixture.Clock.Advance(TimeSpan.FromHours(2));

            return fixture;
        }

        public ValueTask<TimerScanReport> SweepAsync() =>
            new FlowTimerScan(Host, Catalogue, Durability, Options, Clock)
                .RunOnceAsync(TestContext.Current.CancellationToken);

        /// <summary>How many of one tenant's parked instances have finished.</summary>
        public async ValueTask<int> WokenAsync(string tenantId)
        {
            var woken = 0;

            foreach (var instanceId in tenantId == Noisy ? _noisy : _quiet)
            {
                var record = await Journal.ReadInstanceAsync(instanceId, Cancellation);

                if (record.Value.State != FlowInstanceState.Suspended)
                {
                    woken++;
                }
            }

            return woken;
        }

        private async Task ParkAsync(ExecutionPlan plan, string tenantId, int count, List<Guid> into)
        {
            for (var i = 0; i < count; i++)
            {
                var result = await Host.RunAsync(
                    plan, new CountingDispatcher(), AsTenant(tenantId), Cancellation);

                result.IsSuspended.ShouldBeTrue("the flow reached its timer");

                into.Add(result.InstanceId!.Value);

                // A second between parks, so the page's ordering is unambiguous rather than
                // resting on how a store breaks a tie.
                Clock.Advance(TimeSpan.FromSeconds(1));
            }
        }
    }

    /// <summary>A limiter that admits everything and remembers which buckets it was asked for.</summary>
    private sealed class RecordingLimiter : IRateLimiterStore
    {
        private readonly Lock _gate = new();
        private readonly List<string> _keys = [];

        public IReadOnlyList<string> Keys
        {
            get
            {
                lock (_gate)
                {
                    return [.. _keys];
                }
            }
        }

        public ValueTask<Result<RateLimitVerdict>> TryAcquireAsync(
            string key, int permits, TimeSpan window, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _keys.Add(key);
            }

            return new(Result.Ok(new RateLimitVerdict(true, permits, TimeSpan.Zero)));
        }
    }

    /// <summary>
    /// A token bucket in one process — legitimate here and nowhere else.
    /// </summary>
    /// <remarks>
    /// <c>ADR-0040</c> refuses to <em>ship</em> a process-local limiter, because n nodes would
    /// admit n times the declared rate behind a declaration that reads as a deployment-wide
    /// bound. A single-process test has n = 1, and what is under test here is what the host does
    /// with a verdict rather than how a store reaches one — <c>RateLimiterConformance</c> owns
    /// the second question and holds the two shipped stores to it.
    /// </remarks>
    private sealed class InMemoryRateLimiter : IRateLimiterStore
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, int> _spent = new(StringComparer.Ordinal);

        public ValueTask<Result<RateLimitVerdict>> TryAcquireAsync(
            string key, int permits, TimeSpan window, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _spent.TryGetValue(key, out var spent);

                if (spent >= permits)
                {
                    return new(Result.Ok(new RateLimitVerdict(false, 0, window)));
                }

                _spent[key] = spent + 1;

                return new(Result.Ok(
                    new RateLimitVerdict(true, permits - spent - 1, TimeSpan.Zero)));
            }
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => T0;
    }

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            UtcNow += delay;

            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingDispatcher : IStepDispatcher
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);

            return ValueTask.FromResult(StepOutcome.Success);
        }

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct) =>
            ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This double runs plans with no branch step.");

        public int Select(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This double runs plans with no switch step.");

        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to begin.");

        public FlowContext EnterIteration(
            int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to enter.");
    }

    /// <summary>A dispatcher that holds its step open until the test lets it go.</summary>
    private sealed class BlockingDispatcher : IStepDispatcher
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public async ValueTask<StepOutcome> ExecuteAsync(
            int stepIndex, FlowContext ctx, CancellationToken ct)
        {
            Entered.TrySetResult();

            await _release.Task.ConfigureAwait(false);

            return StepOutcome.Success;
        }

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct) =>
            ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This double runs plans with no branch step.");

        public int Select(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This double runs plans with no switch step.");

        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to begin.");

        public FlowContext EnterIteration(
            int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to enter.");
    }
}
