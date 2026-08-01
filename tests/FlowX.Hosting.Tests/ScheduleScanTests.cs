using FlowX.Conformance.InMemory;
using FlowX.Hosting;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Hosting.Tests;

/// <summary>
/// What fires a schedule, what stops it firing twice, and what happens to a firing nobody was
/// there to take.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two silent failures, and both are tested here.</strong> A scheduler that never fires
/// and a scheduler that fires twice look identical from the outside — no exception, no status
/// code, no metric — and the second is the one a fleet produces by default, because every node
/// sweeps. So the tests here assert a <em>count</em> of journalled instances rather than that
/// something ran.
/// </para>
/// <para>
/// The stores are the conformance suite's reference implementations, for the reason
/// <see cref="TimerScanTests"/> gives: a store written for a test is a store nothing holds to
/// <c>JournalConformance</c>, and it is <c>StartAsync</c>'s refusal of a duplicate id that this
/// whole design rests on.
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
    /// <para>
    /// <strong>This is the test the whole design exists for.</strong> There is no leader and no
    /// election: every node computes the same occurrence and derives the same instance id from
    /// it, so nine of the ten are refused — by the lease store while the winner holds it, and by
    /// the journal's primary key afterwards.
    /// </para>
    /// <para>
    /// <strong>Two numbers are asserted and a third is deliberately not.</strong> One instance
    /// and one <c>Fired</c> are invariants. <c>Contended</c> is not nine: a node whose sweep
    /// starts after the winner has committed sees the occurrence already fired and reports
    /// nothing due at all, which is the same answer arriving one layer earlier. Pinning it to
    /// nine would be pinning a race.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TenNodesSweepingOneOccurrenceProduceOneInstance()
    {
        var fixture = Fixture.Hourly();
        var nodes = fixture.Nodes(10);

        fixture.Clock.Advance(TimeSpan.FromHours(1));

        var reports = await Task.WhenAll(
            nodes.Select(node => node.RunOnceAsync(TestContext.Current.CancellationToken).AsTask()));

        fixture.Journal.Instances.Count.ShouldBe(
            1, "ten nodes agreed on one occurrence, and an occurrence names one instance");

        reports.Sum(static r => r.Fired).ShouldBe(1);
    }

    /// <summary>
    /// Two nodes starting one instance id produce one instance, and the loser is told why.
    /// </summary>
    /// <remarks>
    /// The mechanism underneath the sweep, asserted without a race in it. This is what makes
    /// "fires once" survive a lease that has expired, a node that restarted, and a deployment
    /// that happened in between: the primary key is permanent where a lease is not.
    /// </remarks>
    [Fact]
    public async Task TwoNodesStartingOneInstanceIdProduceOneInstance()
    {
        var fixture = Fixture.Hourly();
        var registration = fixture.Schedules.Registrations[0];
        var occurrence = T0.AddHours(1);
        var instanceId = registration.Schedule.InstanceIdFor(occurrence);
        var ct = TestContext.Current.CancellationToken;

        var first = await fixture.Host.RunAsync(
            registration.Flow.Plan,
            registration.Flow.Dispatcher,
            new FlowInvocation("corr", "one"),
            registration.Schedule.FireFor(occurrence),
            instanceId,
            ct);

        var second = await fixture.Host.RunAsync(
            registration.Flow.Plan,
            registration.Flow.Dispatcher,
            new FlowInvocation("corr", "one"),
            registration.Schedule.FireFor(occurrence),
            instanceId,
            ct);

        first.IsSuccess.ShouldBeTrue();
        second.IsFailure.ShouldBeTrue();
        second.Error!.Code.ShouldBe(DurabilityErrors.InstanceExistsCode);

        fixture.Journal.Instances.Count.ShouldBe(1);
    }

    /// <summary>A node that sweeps again for the same occurrence starts nothing new.</summary>
    /// <remarks>
    /// The sweep runs on a ten-second interval and a schedule is hourly, so the ordinary case is
    /// a sweep with nothing to do. Correctness does not depend on the node remembering what it
    /// fired: the memory only stops it asking the stores again.
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
    /// The in-process memory is gone, so the node genuinely asks the journal — and the journal
    /// is what tells it where to resume from.
    /// </remarks>
    [Fact]
    public async Task ARestartedNodeDoesNotRefireAnOccurrenceTheJournalAlreadyHolds()
    {
        var fixture = Fixture.Hourly();

        fixture.Clock.Advance(TimeSpan.FromHours(1));

        await fixture.SweepAsync();

        var restarted = fixture.Scan();
        var report = await restarted.RunOnceAsync(TestContext.Current.CancellationToken);

        report.Due.ShouldBe(0, "the journal already holds that occurrence's instance");
        report.Fired.ShouldBe(0);

        fixture.Journal.Instances.Count.ShouldBe(1);
    }

    /// <summary>
    /// A restarted node catches up what fell due while nothing was running.
    /// </summary>
    /// <remarks>
    /// <strong>This is what at-least-once commits a schedule to, and it is the case a node's own
    /// memory cannot answer.</strong> The node that fired 01:00 is gone; the node that comes
    /// back at 03:40 has never seen this schedule. It walks back from 03:00, finds 01:00 in the
    /// journal, and knows that 02:00 and 03:00 are missed rather than ancient.
    /// </remarks>
    [Fact]
    public async Task ARestartedNodeCatchesUpWhatFellDueWhileNothingWasRunning()
    {
        var fixture = Fixture.Hourly(MissedFirePolicy.RunAll);

        fixture.Clock.Advance(TimeSpan.FromHours(1));
        await fixture.SweepAsync();

        // The fleet is gone for two hours and forty minutes, and comes back as a process that
        // has never seen this schedule.
        fixture.Clock.Advance(TimeSpan.FromMinutes(160));

        var report = await fixture.Scan().RunOnceAsync(TestContext.Current.CancellationToken);

        report.Fired.ShouldBe(2);

        fixture.Dispatcher.Inputs
            .Cast<ScheduledFire>()
            .Select(static fire => fire.OccurrenceAt)
            .ShouldBe([T0.AddHours(1), T0.AddHours(2), T0.AddHours(3)], ignoreOrder: true);
    }

    /// <summary>
    /// A schedule the journal has never fired starts from the node's own start, not the horizon.
    /// </summary>
    /// <remarks>
    /// <strong>The other half of the catch-up decision, and the one a bare horizon gets
    /// wrong.</strong> A first deployment at half past under a day-long horizon would otherwise
    /// replay every occurrence since yesterday — work nobody missed, because nobody was ever
    /// going to do it. Absence of evidence in the journal is what tells the two cases apart.
    /// </remarks>
    [Fact]
    public async Task ANewScheduleDoesNotReplayTheHorizon()
    {
        var fixture = Fixture.Hourly(startedAt: T0.AddMinutes(30));

        var first = await fixture.SweepAsync();

        first.Due.ShouldBe(0, "nothing in the journal, so there is nothing to catch up on");
        fixture.Journal.Instances.ShouldBeEmpty();

        fixture.Clock.Advance(TimeSpan.FromMinutes(30));

        var second = await fixture.SweepAsync();

        second.Fired.ShouldBe(1, "and it starts firing from its next occurrence");

        fixture.Dispatcher.Inputs[0].ShouldBeOfType<ScheduledFire>()
            .OccurrenceAt.ShouldBe(T0.AddHours(1));
    }

    /// <summary>The instance a schedule starts carries the occurrence that started it.</summary>
    [Fact]
    public async Task TheInstanceCarriesTheOccurrenceThatFiredIt()
    {
        var fixture = Fixture.Hourly();

        fixture.Clock.Advance(TimeSpan.FromMinutes(90));

        await fixture.SweepAsync();

        fixture.Dispatcher.Inputs.Count.ShouldBe(1);

        var fire = fixture.Dispatcher.Inputs[0].ShouldBeOfType<ScheduledFire>();

        fire.OccurrenceAt.ShouldBe(T0.AddHours(1), "the occurrence, not the instant the sweep ran");
        fire.Cron.ShouldBe("0 * * * *");
        fire.TimeZone.ShouldBe("UTC");
    }

    /// <summary>
    /// Three occurrences missed while the node was down produce one firing under the default.
    /// </summary>
    /// <remarks>
    /// <c>MissedFirePolicy.RunOnce</c> is the default, and it is the one that says "catch up, but
    /// do not stampede". The firing happens late rather than not at all, which is what
    /// at-least-once commits a schedule to.
    /// </remarks>
    [Fact]
    public async Task ThreeMissedOccurrencesFireOnceUnderTheDefault()
    {
        var fixture = Fixture.Hourly();

        fixture.Clock.Advance(TimeSpan.FromMinutes(210));

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

        fixture.Clock.Advance(TimeSpan.FromMinutes(210));

        var report = await fixture.SweepAsync();

        report.Fired.ShouldBe(3);

        fixture.Dispatcher.Inputs
            .Cast<ScheduledFire>()
            .Select(static fire => fire.OccurrenceAt)
            .ShouldBe([T0.AddHours(1), T0.AddHours(2), T0.AddHours(3)], ignoreOrder: true);
    }

    /// <summary>And under <c>Skip</c> they produce none.</summary>
    /// <remarks>
    /// The author asked for the work not to be done late. A sweep that fired it anyway would make
    /// the declaration a comment.
    /// </remarks>
    [Fact]
    public async Task ThreeMissedOccurrencesFireNothingUnderSkip()
    {
        var fixture = Fixture.Hourly(MissedFirePolicy.Skip);

        fixture.Clock.Advance(TimeSpan.FromMinutes(210));

        var report = await fixture.SweepAsync();

        report.Due.ShouldBe(0, "the newest occurrence is half an hour old, which is not fresh");
        report.Fired.ShouldBe(0);
        fixture.Journal.Instances.ShouldBeEmpty();
    }

    /// <summary>An occurrence <c>Skip</c> is still fresh enough for does fire.</summary>
    /// <remarks>
    /// <c>Skip</c> is about work that is <em>late</em>, not about work that is due. A sweep
    /// arriving five seconds after the occurrence has missed nothing.
    /// </remarks>
    [Fact]
    public async Task AFreshOccurrenceFiresUnderSkip()
    {
        var fixture = Fixture.Hourly(MissedFirePolicy.Skip);

        fixture.Clock.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(5)));

        (await fixture.SweepAsync()).Fired.ShouldBe(1);
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
        fixture.Clock.Advance(TimeSpan.FromMinutes(210));

        var report = await fixture.SweepAsync();

        report.Due.ShouldBe(0, "the newest occurrence fell outside a half-hour horizon");
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
        Fixture.Hourly(schedules: new FlowScheduleCatalog()).Scan().IsEnabled.ShouldBeFalse();
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
            () => new FlowScheduleScan(null!, schedules, durability, options, SystemClock.Instance));
        Should.Throw<ArgumentNullException>(
            () => new FlowScheduleScan(host, null!, durability, options, SystemClock.Instance));
        Should.Throw<ArgumentNullException>(
            () => new FlowScheduleScan(host, schedules, null!, options, SystemClock.Instance));
        Should.Throw<ArgumentNullException>(
            () => new FlowScheduleScan(host, schedules, durability, null!, SystemClock.Instance));
        Should.Throw<ArgumentNullException>(
            () => new FlowScheduleScan(host, schedules, durability, options, null!));
    }

    /// <summary>An ephemeral flow cannot be scheduled, and the refusal is at registration.</summary>
    /// <remarks>
    /// Without a journal there is no primary key to refuse a second node's firing, so a schedule
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

    /// <summary>An expression the host cannot read is refused at registration, not at the sweep.</summary>
    [Fact]
    public void AnUnreadableExpressionIsRefusedAtRegistration()
    {
        Should.Throw<ArgumentException>(() => FlowSchedule.Create(
            "ledger.reconcile", "1.0.0", "every night please", "UTC", MissedFirePolicy.RunOnce));
    }

    /// <summary>One node, one journal, and one hourly schedule registered on it.</summary>
    private sealed class Fixture
    {
        private FlowScheduleScan? _resident;

        private Fixture(FlowScheduleCatalog schedules, DateTimeOffset startedAt)
        {
            Options = new FlowXOptions
            {
                ApplicationName = "Tests",
                NodeName = "node",
                ShutdownDrainTimeout = TimeSpan.FromSeconds(5),
            };

            Clock = new MovableClock(startedAt);
            Durability = new FlowDurability(Journal, Leases);
            Host = new FlowHost(new FlowEngine(Clock), Options, Durability);
            Schedules = schedules;
        }

        public InMemoryFlowJournal Journal { get; } = new();

        public InMemoryLeaseStore Leases { get; } = new();

        public MovableClock Clock { get; }

        public FlowDurability Durability { get; }

        public FlowHost Host { get; }

        public FlowXOptions Options { get; }

        public FlowScheduleCatalog Schedules { get; }

        public RecordingDispatcher Dispatcher { get; } = new();

        /// <summary>One step, on the hour, in UTC.</summary>
        /// <remarks>
        /// The node's own sweep is built here rather than lazily, because a sweep records when
        /// its node started and uses it as the floor for a schedule the journal has never fired.
        /// Building it after the clock had been wound forward would be a node that started in the
        /// future.
        /// </remarks>
        public static Fixture Hourly(
            MissedFirePolicy missedFire = MissedFirePolicy.RunOnce,
            FlowScheduleCatalog? schedules = null,
            DateTimeOffset? startedAt = null)
        {
            var fixture = new Fixture(schedules ?? new FlowScheduleCatalog(), startedAt ?? T0);

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

            fixture._resident = fixture.Scan();

            return fixture;
        }

        /// <summary>A sweep as a process that has just started would have it.</summary>
        public FlowScheduleScan Scan() => new(Host, Schedules, Durability, Options, Clock);

        /// <summary>The sweep this node keeps between ticks.</summary>
        public ValueTask<ScheduleScanReport> SweepAsync() =>
            _resident!.RunOnceAsync(TestContext.Current.CancellationToken);

        /// <summary>A fleet of nodes over one set of stores, all started now.</summary>
        public IReadOnlyList<FlowScheduleScan> Nodes(int count) =>
        [
            .. Enumerable.Range(0, count).Select(node =>
            {
                var options = new FlowXOptions
                {
                    ApplicationName = Options.ApplicationName,
                    NodeName = "node-" + node.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };

                return new FlowScheduleScan(
                    new FlowHost(new FlowEngine(Clock), options, Durability),
                    Schedules,
                    Durability,
                    options,
                    Clock);
            }),
        ];
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
