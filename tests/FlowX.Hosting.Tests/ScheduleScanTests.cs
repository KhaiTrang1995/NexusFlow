using FlowX.Conformance.InMemory;
using FlowX.Hosting;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Hosting.Tests;

/// <summary>
/// What fires a schedule, what stops it firing twice, and what happens to a firing nobody
/// was there to take.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two silent failures, and both are tested here.</strong> A scheduler that never
/// fires and a scheduler that fires twice look identical from the outside — no exception, no
/// status code, no metric — and the second is the one a fleet produces by default, because
/// every node sweeps. So every test in this file asserts a <em>count</em> of journalled
/// instances rather than "something ran".
/// </para>
/// <para>
/// The stores are the conformance suite's reference implementations, for the reason
/// <see cref="TimerScanTests"/> gives: a store written for a test is a store nothing holds to
/// <c>JournalConformance</c>, and it is <c>StartAsync</c>'s refusal of a duplicate id that
/// this whole design rests on.
/// </para>
/// </remarks>
public sealed class ScheduleScanTests
{
    /// <summary>1970-01-01T00:00:00Z is a Thursday, and it is on the hour.</summary>
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static readonly CapabilityDescriptor Reconcile =
        CapabilityDescriptor.Create("ledger.reconcile", "1.0.0", isIdempotent: true);

    /// <summary>A sweep before the first occurrence starts nothing.</summary>
    [Fact]
    public async Task ASweepBeforeTheFirstOccurrenceFiresNothing()
    {
        var fixture = Fixture.Hourly();

        var report = await fixture.SweepAsync();

        report.Due.ShouldBe(0, "the hour the node started in has not turned over");
        report.Fired.ShouldBe(0);
        fixture.Journal.Instances.ShouldBeEmpty();
    }

    /// <summary>A sweep after an occurrence has passed starts the flow, once.</summary>
    [Fact]
    public async Task ASweepAfterAnOccurrenceFiresTheFlow()
    {
        var fixture = Fixture.Hourly();

        fixture.Clock.Advance(TimeSpan.FromHours(1));

        var report = await fixture.SweepAsync();

        report.Due.ShouldBe(1);
        report.Fired.ShouldBe(1);

        fixture.Journal.Instances.Count.ShouldBe(1);
        fixture.Journal.Instances[0].FlowId.ShouldBe("ledger.reconcile");
    }

    /// <summary>
    /// Ten nodes sweeping the same schedule at the same instant produce one instance.
    /// </summary>
    /// <remarks>
    /// <strong>This is the test the whole design exists for.</strong> There is no leader and no
    /// election: every node computes the same occurrence, derives the same instance id from it,
    /// and nine of the ten are refused — by the lease store while the winner holds it, and by
    /// the journal's primary key for ever afterwards.
    /// </remarks>
    [Fact]
    public async Task TenNodesSweepingOneOccurrenceProduceOneInstance()
    {
        var fixture = Fixture.Hourly();

        fixture.Clock.Advance(TimeSpan.FromHours(1));

        var reports = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(node => fixture.SweepAsNodeAsync("node-" + node).AsTask()));

        fixture.Journal.Instances.Count.ShouldBe(
            1, "ten nodes agreed on one occurrence, and an occurrence names one instance");

        reports.Sum(static r => r.Fired).ShouldBe(1);
        reports.Sum(static r => r.Contended).ShouldBe(9);
    }

    /// <summary>A node that sweeps again for the same occurrence starts nothing new.</summary>
    /// <remarks>
    /// The sweep runs on a ten-second interval and a schedule is hourly, so the ordinary case
    /// is a sweep with nothing to do. Correctness here does not depend on the node remembering
    /// what it fired: the memory only stops it asking the stores again.
    /// </remarks>
    [Fact]
    public async Task ASecondSweepForTheSameOccurrenceFiresNothing()
    {
        var fixture = Fixture.Hourly();

        fixture.Clock.Advance(TimeSpan.FromHours(1));

        await fixture.SweepAsync();
        var second = await fixture.SweepAsync();

        second.Fired.ShouldBe(0);
        fixture.Journal.Instances.Count.ShouldBe(1);
    }

    /// <summary>
    /// A node restarted after firing an occurrence does not fire it a second time.
    /// </summary>
    /// <remarks>
    /// The in-process memory is gone, so the node genuinely asks the stores again — and the
    /// journal is what refuses it. That is the half of the answer that survives a restart, a
    /// deployment and a lease that has long since expired.
    /// </remarks>
    [Fact]
    public async Task ARestartedNodeDoesNotRefireAnOccurrenceTheJournalAlreadyHolds()
    {
        var fixture = Fixture.Hourly();

        fixture.Clock.Advance(TimeSpan.FromHours(1));

        await fixture.SweepAsync();

        var restarted = fixture.Scan();

        var report = await restarted.RunOnceAsync(TestContext.Current.CancellationToken);

        report.Due.ShouldBe(1, "a fresh process has no memory of what it fired");
        report.Fired.ShouldBe(0);
        report.Contended.ShouldBe(1, "the journal already holds that occurrence's instance");

        fixture.Journal.Instances.Count.ShouldBe(1);
    }

    /// <summary>The instance a schedule starts carries the occurrence that started it.</summary>
    [Fact]
    public async Task TheInstanceCarriesTheOccurrenceThatFiredIt()
    {
        var fixture = Fixture.Hourly();

        fixture.Clock.Advance(TimeSpan.FromMinutes(90));

        await fixture.SweepAsync();

        fixture.Dispatcher.Inputs.Count.ShouldBe(1);
        fixture.Dispatcher.Inputs[0].ShouldBeOfType<ScheduledFire>()
            .OccurrenceAt.ShouldBe(T0.AddHours(1), "the occurrence, not the instant the sweep ran");
    }

    /// <summary>
    /// Three occurrences missed while the node was down produce one fire under the default.
    /// </summary>
    /// <remarks>
    /// <c>MissedFirePolicy.RunOnce</c> is the default, and it is the one that says "catch up,
    /// but do not stampede". The fire happens late rather than not at all, which is what
    /// at-least-once commits a schedule to.
    /// </remarks>
    [Fact]
    public async Task ThreeMissedOccurrencesFireOnceUnderTheDefault()
    {
        var fixture = Fixture.Hourly();

        fixture.Clock.Advance(TimeSpan.FromHours(3));

        var report = await fixture.SweepAsync();

        report.Due.ShouldBe(1);
        report.Fired.ShouldBe(1);

        fixture.Dispatcher.Inputs.Count.ShouldBe(1);
        fixture.Dispatcher.Inputs[0].ShouldBeOfType<ScheduledFire>()
            .OccurrenceAt.ShouldBe(T0.AddHours(3), "the most recent missed occurrence, not the oldest");
    }

    /// <summary>The same three under <c>RunAll</c> produce three.</summary>
    [Fact]
    public async Task ThreeMissedOccurrencesFireThreeTimesUnderRunAll()
    {
        var fixture = Fixture.Hourly(MissedFirePolicy.RunAll);

        fixture.Clock.Advance(TimeSpan.FromHours(3));

        var report = await fixture.SweepAsync();

        report.Fired.ShouldBe(3);

        fixture.Dispatcher.Inputs
            .Cast<ScheduledFire>()
            .Select(static fire => fire.OccurrenceAt)
            .ShouldBe([T0.AddHours(1), T0.AddHours(2), T0.AddHours(3)], ignoreOrder: true);
    }

    /// <summary>And under <c>Skip</c> they produce none.</summary>
    /// <remarks>
    /// The author asked for the work not to be done late. A sweep that fired it anyway would
    /// make the declaration a comment.
    /// </remarks>
    [Fact]
    public async Task ThreeMissedOccurrencesFireNothingUnderSkip()
    {
        var fixture = Fixture.Hourly(MissedFirePolicy.Skip);

        fixture.Clock.Advance(TimeSpan.FromHours(3));

        var report = await fixture.SweepAsync();

        report.Due.ShouldBe(0, "every occurrence is older than one sweep interval");
        report.Fired.ShouldBe(0);
        fixture.Journal.Instances.ShouldBeEmpty();
    }

    /// <summary>An occurrence older than the catch-up horizon is not fired.</summary>
    /// <remarks>
    /// The bound on at-least-once, and it is a declared number rather than an accident. A
    /// deployment that expects to survive a longer outage raises it.
    /// </remarks>
    [Fact]
    public async Task AnOccurrenceOlderThanTheHorizonIsNotFired()
    {
        var fixture = Fixture.Hourly();

        fixture.Options.ScheduleCatchUp = TimeSpan.FromMinutes(30);
        fixture.Clock.Advance(TimeSpan.FromHours(3));

        var report = await fixture.SweepAsync();

        report.Due.ShouldBe(0);
        fixture.Journal.Instances.ShouldBeEmpty();
    }

    /// <summary>A draining node fires nothing, so a shutdown finishes.</summary>
    [Fact]
    public async Task ADrainingNodeFiresNothing()
    {
        var fixture = Fixture.Hourly();

        fixture.Clock.Advance(TimeSpan.FromHours(1));

        await fixture.Host.DrainAsync(TestContext.Current.CancellationToken);

        (await fixture.SweepAsync()).Due.ShouldBe(0);
        fixture.Journal.Instances.ShouldBeEmpty();
    }

    /// <summary>A host with no schedules registered does not sweep at all.</summary>
    [Fact]
    public void AHostWithNoScheduleDoesNotSweep()
    {
        var fixture = Fixture.Hourly(schedules: new FlowScheduleCatalog());

        fixture.Scan().IsEnabled.ShouldBeFalse();
    }

    /// <summary>The constructor refuses a null collaborator rather than failing at sweep time.</summary>
    [Fact]
    public void EveryCollaboratorIsRequired()
    {
        var journal = new InMemoryFlowJournal();
        var durability = new FlowDurability(journal, new InMemoryLeaseStore());
        var options = new FlowXOptions { ApplicationName = "Tests", NodeName = "node" };
        var host = new FlowHost(new FlowEngine(SystemClock.Instance), options, durability);
        var schedules = new FlowScheduleCatalog();

        Should.Throw<ArgumentNullException>(
            () => new FlowScheduleScan(null!, schedules, options, SystemClock.Instance));
        Should.Throw<ArgumentNullException>(
            () => new FlowScheduleScan(host, null!, options, SystemClock.Instance));
        Should.Throw<ArgumentNullException>(
            () => new FlowScheduleScan(host, schedules, null!, SystemClock.Instance));
        Should.Throw<ArgumentNullException>(
            () => new FlowScheduleScan(host, schedules, options, null!));
    }

    /// <summary>An ephemeral flow cannot be scheduled, and the refusal is at registration.</summary>
    /// <remarks>
    /// Without a journal there is no primary key to refuse a second node's fire, so a schedule
    /// on an ephemeral flow fires once per node per occurrence — silently. Refusing at
    /// registration makes it a pod that never becomes ready.
    /// </remarks>
    [Fact]
    public void AnEphemeralFlowCannotCarryASchedule()
    {
        var plan = ExecutionPlan.Create(
            FlowDescriptor.Create("ledger.reconcile", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromMinutes(5)),
            StepGraph.Create([StepNode.ForCapability(0, Reconcile)]));

        Should.Throw<ArgumentException>(() => new FlowScheduleCatalog().Add(
            FlowSchedule.Create("ledger.reconcile", "1.0.0", "0 * * * *", "UTC", MissedFirePolicy.RunOnce),
            plan,
            new RecordingDispatcher()));
    }

    /// <summary>One node, one journal, and one hourly schedule registered on it.</summary>
    private sealed class Fixture
    {
        private Fixture(FlowScheduleCatalog schedules)
        {
            Options = new FlowXOptions
            {
                ApplicationName = "Tests",
                NodeName = "node",
                ShutdownDrainTimeout = TimeSpan.FromSeconds(5),
            };

            Durability = new FlowDurability(Journal, Leases);
            Host = new FlowHost(new FlowEngine(Clock), Options, Durability);
            Schedules = schedules;
        }

        public InMemoryFlowJournal Journal { get; } = new();

        public InMemoryLeaseStore Leases { get; } = new();

        public MovableClock Clock { get; } = new(T0);

        public FlowDurability Durability { get; }

        public FlowHost Host { get; }

        public FlowXOptions Options { get; }

        public FlowScheduleCatalog Schedules { get; }

        public RecordingDispatcher Dispatcher { get; } = new();

        private FlowScheduleScan? _resident;

        /// <summary>One step, on the hour, in UTC.</summary>
        public static Fixture Hourly(
            MissedFirePolicy missedFire = MissedFirePolicy.RunOnce,
            FlowScheduleCatalog? schedules = null)
        {
            var fixture = new Fixture(schedules ?? new FlowScheduleCatalog());

            if (schedules is null)
            {
                fixture.Schedules.Add(
                    FlowSchedule.Create("ledger.reconcile", "1.0.0", "0 * * * *", "UTC", missedFire),
                    ExecutionPlan.Create(
                        FlowDescriptor.Create(
                            "ledger.reconcile", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromMinutes(5)),
                        StepGraph.Create([StepNode.ForCapability(0, Reconcile)])),
                    fixture.Dispatcher);
            }

            return fixture;
        }

        /// <summary>A fresh sweep, as a node that has just started would have.</summary>
        public FlowScheduleScan Scan() => new(Host, Schedules, Options, Clock);

        /// <summary>The sweep this node keeps between ticks.</summary>
        public ValueTask<ScheduleScanReport> SweepAsync() =>
            (_resident ??= Scan()).RunOnceAsync(TestContext.Current.CancellationToken);

        /// <summary>A sweep belonging to a different node, over the same stores.</summary>
        public ValueTask<ScheduleScanReport> SweepAsNodeAsync(string nodeName)
        {
            var options = new FlowXOptions
            {
                ApplicationName = Options.ApplicationName,
                NodeName = nodeName,
                ScheduleCatchUp = Options.ScheduleCatchUp,
            };

            var host = new FlowHost(new FlowEngine(Clock), options, Durability);

            return new FlowScheduleScan(host, Schedules, options, Clock)
                .RunOnceAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A clock a test can wind forward, so an hour costs no wall-clock time.</summary>
    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        private readonly Lock _gate = new();
        private DateTimeOffset _now = start;

        public DateTimeOffset UtcNow
        {
            get
            {
                lock (_gate)
                {
                    return _now;
                }
            }
        }

        public void Advance(TimeSpan by)
        {
            lock (_gate)
            {
                _now += by;
            }
        }

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            Advance(delay);

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A dispatcher that answers success and remembers what it was started with.</summary>
    private sealed class RecordingDispatcher : IStepDispatcher
    {
        private readonly Lock _gate = new();
        private readonly List<object?> _inputs = [];

        public IReadOnlyList<object?> Inputs
        {
            get
            {
                lock (_gate)
                {
                    return [.. _inputs];
                }
            }
        }

        public JournalPayload DescribeInput(object? input)
        {
            lock (_gate)
            {
                _inputs.Add(input);
            }

            return JournalPayload.Empty;
        }

        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct) =>
            ValueTask.FromResult(StepOutcome.Success);

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
