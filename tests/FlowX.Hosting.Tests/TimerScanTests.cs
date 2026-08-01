using FlowX.Conformance.InMemory;
using FlowX.Hosting;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Hosting.Tests;

/// <summary>
/// What wakes a parked instance, and what it does not.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The property under test is that there is no third resume path.</strong>
/// <see cref="FlowTimerScan"/> finds instances whose recorded instant has passed and hands
/// each one to <c>FlowHost.ResumeAsync</c> — the same call <see cref="FlowRecoveryScan"/>
/// makes and the same one <c>FlowHost.SignalAsync</c> is. The sweep decides <em>which</em>
/// instances to look at again; the engine decides whether the wait each one is standing at is
/// over, because the engine is what holds the plan that declared the duration.
/// </para>
/// <para>
/// The stores are the conformance suite's reference implementations, for the reason
/// <c>DurableSeamTests</c> gives: a store written for a test is a store nothing holds to
/// <c>JournalConformance</c>, and fencing is the first thing it would get wrong.
/// </para>
/// </remarks>
public sealed class TimerScanTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static readonly CapabilityDescriptor Validate =
        CapabilityDescriptor.Create("order.validate", "1.0.0", isIdempotent: true);

    /// <summary>A sweep before the wait is due leaves the instance exactly where it was.</summary>
    /// <remarks>
    /// The candidate set is filtered at the store on an instant the instance chose, so a
    /// parked instance whose wait is still running is never fetched, never leased and never
    /// resumed. That is what makes a ten-second sweep affordable against a table of week-long
    /// waits.
    /// </remarks>
    [Fact]
    public async Task ASweepBeforeTheWaitIsDueFindsNothing()
    {
        var fixture = await Fixture.ParkedAsync();

        var report = await fixture.SweepAsync();

        report.Examined.ShouldBe(0, "an hour has not passed");
        report.Woken.ShouldBe(0);

        fixture.Dispatcher.Executed.ShouldBeEmpty();
        (await fixture.StateAsync()).ShouldBe(FlowInstanceState.Suspended);
    }

    /// <summary>A sweep after it is due wakes the instance and it runs on.</summary>
    /// <remarks>
    /// Through <c>FlowHost.ResumeAsync</c> carrying no signal, which is exactly what a
    /// recovery scan issues. The instance was parked at a timer rather than at a signal, so
    /// what satisfies it is the clock the engine reads when it arrives back at the node.
    /// </remarks>
    [Fact]
    public async Task ASweepAfterItIsDueWakesTheInstanceAndItRunsOn()
    {
        var fixture = await Fixture.ParkedAsync();

        fixture.Clock.Advance(TimeSpan.FromHours(1));

        var report = await fixture.SweepAsync();

        report.Examined.ShouldBe(1);
        report.Woken.ShouldBe(1);

        fixture.Dispatcher.Executed.ShouldBe(
            [1, 2],
            "the timer was dispatched — which only happens once it has come due — and the " +
            "step after it ran. Step 0 is stepped over because its row is committed.");

        (await fixture.StateAsync()).ShouldBe(FlowInstanceState.Completed);
    }

    /// <summary>A woken instance carries no wait afterwards.</summary>
    /// <remarks>
    /// The difference between a timer that fires once and one that fires on every sweep for
    /// the rest of the instance's retention window.
    /// </remarks>
    [Fact]
    public async Task AWokenInstanceCarriesNoWaitAfterwards()
    {
        var fixture = await Fixture.ParkedAsync();

        fixture.Clock.Advance(TimeSpan.FromHours(1));

        await fixture.SweepAsync();

        (await fixture.SweepAsync()).Examined.ShouldBe(0, "it finished, so nothing is due");
    }

    /// <summary>
    /// A candidate pinned to a version this node does not carry is left for one that does.
    /// </summary>
    /// <remarks>
    /// Taking a lease this node could not use would deny the instance to a node that can, for
    /// a whole TTL, on every sweep — and it would be counted as woken, which is the number an
    /// operator watches to find out that an escalation is not happening.
    /// </remarks>
    [Fact]
    public async Task ACandidateThisNodeCannotRunIsLeftAlone()
    {
        var fixture = await Fixture.ParkedAsync(catalogue: new FlowCatalog());

        fixture.Clock.Advance(TimeSpan.FromHours(1));

        var report = await fixture.SweepAsync();

        report.Examined.ShouldBe(1);
        report.NotRunnable.ShouldBe(1);
        report.Woken.ShouldBe(0);

        (await fixture.StateAsync()).ShouldBe(FlowInstanceState.Suspended);
    }

    /// <summary>A host whose journal cannot answer the query does not sweep.</summary>
    /// <remarks>
    /// Not a failure. Durable flows still run on that host and still park; what does not
    /// happen is that anything comes back for them, so a <c>.Delay</c> waits for whatever else
    /// resumes the instance and an expired <c>.OnTimeout</c> never fires. A deployment that
    /// declined <c>ITimerIndex</c> and writes flows with timers in them has made two decisions
    /// that disagree, and <see cref="FlowTimerScan.IsEnabled"/> is where it finds out.
    /// </remarks>
    [Fact]
    public async Task AHostWhoseJournalCannotBeSweptDoesNotSweep()
    {
        var fixture = await Fixture.ParkedAsync(withTimerIndex: false);

        fixture.Durability.CanWake.ShouldBeFalse();

        var scan = fixture.Scan();

        scan.IsEnabled.ShouldBeFalse();
        (await scan.RunOnceAsync(TestContext.Current.CancellationToken))
            .ShouldBe(TimerScanReport.Nothing);
    }

    /// <summary>A draining node stops sweeping, so a shutdown finishes.</summary>
    /// <remarks>
    /// Waking an instance is executing it, and a drain that kept picking up new work would
    /// never end — the same reason <see cref="FlowRecoveryScan"/> checks the same flag.
    /// </remarks>
    [Fact]
    public async Task ADrainingNodeSweepsNothing()
    {
        var fixture = await Fixture.ParkedAsync();

        fixture.Clock.Advance(TimeSpan.FromHours(1));

        await fixture.Host.DrainAsync(TestContext.Current.CancellationToken);

        (await fixture.SweepAsync()).Examined.ShouldBe(0);
    }

    /// <summary>The constructor refuses a null collaborator rather than failing at sweep time.</summary>
    [Fact]
    public void EveryCollaboratorIsRequired()
    {
        var journal = new InMemoryFlowJournal();
        var durability = new FlowDurability(
            journal, new InMemoryLeaseStore(), timerIndex: new InMemoryTimerIndex(journal));

        var options = new FlowXOptions { ApplicationName = "Tests", NodeName = "node" };
        var host = new FlowHost(new FlowEngine(SystemClock.Instance), options, durability);
        var catalogue = new FlowCatalog();

        Should.Throw<ArgumentNullException>(
            () => new FlowTimerScan(null!, catalogue, durability, options, SystemClock.Instance));
        Should.Throw<ArgumentNullException>(
            () => new FlowTimerScan(host, null!, durability, options, SystemClock.Instance));
        Should.Throw<ArgumentNullException>(
            () => new FlowTimerScan(host, catalogue, null!, options, SystemClock.Instance));
        Should.Throw<ArgumentNullException>(
            () => new FlowTimerScan(host, catalogue, durability, null!, SystemClock.Instance));
        Should.Throw<ArgumentNullException>(
            () => new FlowTimerScan(host, catalogue, durability, options, null!));
    }

    /// <summary>One node, one journal, and one instance parked at a timer in it.</summary>
    private sealed class Fixture
    {
        private Fixture(bool withTimerIndex, FlowCatalog catalogue)
        {
            Options = new FlowXOptions
            {
                ApplicationName = "Tests",
                NodeName = "node",
                ShutdownDrainTimeout = TimeSpan.FromSeconds(5),
            };

            Durability = new FlowDurability(
                Journal,
                Leases,
                timerIndex: withTimerIndex ? new InMemoryTimerIndex(Journal) : null);

            Host = new FlowHost(new FlowEngine(Clock), Options, Durability);
            Catalogue = catalogue;
        }

        public InMemoryFlowJournal Journal { get; } = new();

        public InMemoryLeaseStore Leases { get; } = new();

        public MovableClock Clock { get; } = new(T0);

        public FlowDurability Durability { get; }

        public FlowHost Host { get; }

        public FlowXOptions Options { get; }

        public FlowCatalog Catalogue { get; }

        public RecordingDispatcher Dispatcher { get; } = new();

        public Guid InstanceId { get; private set; }

        /// <summary>Validate, wait an hour, validate again.</summary>
        private static ExecutionPlan DelayingPlan() => ExecutionPlan.Create(
            FlowDescriptor.Create("order.place", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromDays(30)),
            StepGraph.Create([
                StepNode.ForCapability(0, Validate),
                StepNode.ForDelay(1, TimeSpan.FromHours(1)),
                StepNode.ForCapability(2, Validate),
            ]));

        /// <summary>Runs the flow up to its timer and leaves it parked there.</summary>
        public static async Task<Fixture> ParkedAsync(
            bool withTimerIndex = true,
            FlowCatalog? catalogue = null)
        {
            var plan = DelayingPlan();
            var fixture = new Fixture(withTimerIndex, catalogue ?? new FlowCatalog());
            var ct = TestContext.Current.CancellationToken;

            if (catalogue is null)
            {
                fixture.Catalogue.Add(plan, fixture.Dispatcher);
            }

            var result = await fixture.Host.RunAsync(
                plan,
                new RecordingDispatcher(),
                new FlowInvocation("corr", "order-1"),
                ct);

            result.IsSuspended.ShouldBeTrue("the flow reached its timer");

            fixture.InstanceId = result.InstanceId!.Value;

            return fixture;
        }

        public FlowTimerScan Scan() => new(Host, Catalogue, Durability, Options, Clock);

        public ValueTask<TimerScanReport> SweepAsync() =>
            Scan().RunOnceAsync(TestContext.Current.CancellationToken);

        public async ValueTask<FlowInstanceState> StateAsync() =>
            (await Journal.ReadInstanceAsync(InstanceId, TestContext.Current.CancellationToken))
            .Value.State;
    }

    /// <summary>A clock a test can wind forward, so a wait costs no wall-clock time.</summary>
    /// <remarks>
    /// The whole reason a durable timer is testable at all: the instance records an instant
    /// and the engine compares it against a clock, so an hour-long wait is one assignment
    /// rather than an hour.
    /// </remarks>
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

    /// <summary>A dispatcher that answers success and remembers which indices it was asked for.</summary>
    private sealed class RecordingDispatcher : IStepDispatcher
    {
        private readonly Lock _gate = new();
        private readonly List<int> _executed = [];

        public IReadOnlyList<int> Executed
        {
            get
            {
                lock (_gate)
                {
                    return [.. _executed];
                }
            }
        }

        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
        {
            lock (_gate)
            {
                _executed.Add(stepIndex);
            }

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
}
