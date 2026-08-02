using FlowX.Conformance.InMemory;
using FlowX.Hosting;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Hosting.Tests;

/// <summary>
/// What starts a flow from an observed change, what stops one change starting two flows, and
/// what a subscription does when it cannot make progress.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two silent failures, and both are tested here</strong> — <see cref="BusScanTests"/>'s
/// framing, and the second one arrives by a different route. A change feed re-offers a change
/// when the cursor was not committed past it, which is a crash rather than a broker's contract,
/// so these tests assert a <em>count</em> of journalled instances rather than that something ran.
/// </para>
/// <para>
/// The stores are the conformance suite's reference implementations, for
/// <see cref="BusScanTests"/>'s reason: it is <c>StartAsync</c>'s refusal of a duplicate id that
/// the whole design rests on. The feed is a recording double here and a real PostgreSQL in
/// <c>tests/Ecommerce.Tests/ChangeStartsAFlowTests</c>.
/// </para>
/// </remarks>
public sealed class ChangeScanTests
{
    private static readonly CapabilityDescriptor Project =
        CapabilityDescriptor.Create("orders.project", "1.0.0", isIdempotent: true);

    private const string Source = "order.placed";

    private const string Group = "projection";

    /// <summary>A pass over a feed with nothing new starts nothing.</summary>
    [Fact]
    public async Task APassOverAnEmptyFeedStartsNothing()
    {
        var fixture = Fixture.Create();

        var report = await fixture.PassAsync();

        report.Observed.ShouldBe(0);
        report.Started.ShouldBe(0);
        fixture.Journal.Instances.ShouldBeEmpty();
    }

    /// <summary>One staged change starts the flow, and hands it the change.</summary>
    [Fact]
    public async Task OneChangeStartsTheFlow()
    {
        var fixture = Fixture.Create();

        fixture.Feed.Stage(Source);

        var report = await fixture.PassAsync();

        report.Observed.ShouldBe(1);
        report.Started.ShouldBe(1);

        fixture.Journal.Instances.Count.ShouldBe(1);
        fixture.Journal.Instances[0].FlowId.ShouldBe("orders.project");

        var message = fixture.Dispatcher.Inputs[0].ShouldBeOfType<BusMessage>();

        message.Type.ShouldBe(Source);
        message.Topic.ShouldBe(Source, "the feed fills the topic from the subscription's source.");
    }

    /// <summary>
    /// A deployment that isolates by tenant starts no flow from a change, and moves its cursor
    /// past the change anyway.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is why <c>AddFlowXPostgresChangeFeed</c> is still refused at
    /// <see cref="TenantIsolation.Schema"/>, and the refusal cites it.</strong> The blocker is
    /// not the cursor — <c>change_cursor</c> is keyed by subscription and one row per tenant
    /// schema is the same key in a different table — it is that a change scan carries no
    /// principal, so <c>ClaimTenantResolver</c> refuses every invocation under an isolating
    /// deployment whether it names a tenant or not. Nothing about schemas causes this: it is
    /// asserted at <see cref="TenantIsolation.Row"/>, which shipped, and is the same at every
    /// level above <see cref="TenantIsolation.None"/>.
    /// </para>
    /// <para>
    /// <strong>The second assertion is the one that makes fanning the feed out worse than
    /// refusing it.</strong> <c>tenant.required</c> is not among the dispositions that hold, so
    /// the pass reads the refusal as progress and commits past a change no flow ever ran. A
    /// per-tenant feed would do that to every change of every tenant, silently — where the
    /// refusal leaves the rows in the table.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AChangeStartsNoFlowWhereTheDeploymentIsolatesByTenant()
    {
        var fixture = Fixture.Create(isolation: TenantIsolation.Row);

        fixture.Feed.Stage(Source);

        var report = await fixture.PassAsync();

        report.Observed.ShouldBe(1, "the feed offered the change; admission is what refused it.");

        fixture.Journal.Instances.ShouldBeEmpty(
            "a change scan carries no claims, so an isolating deployment refuses the start " +
            "with tenant.required. There is no principal for it to carry and no continuation " +
            "it could claim to be — a continuation also skips step authorisation, which a " +
            "flow that is starting must not.");

        fixture.Feed.Committed.ShouldHaveSingleItem(
            "and the cursor moved past it. The refusal is classified as progress, so a feed " +
            "fanned out across tenant schemas would advance every tenant's cursor over work " +
            "that never happened.");
    }

    /// <summary>The instance the change started is the one every node derives for it.</summary>
    [Fact]
    public async Task TheInstanceIsNamedByTheChange()
    {
        var fixture = Fixture.Create();

        var eventId = fixture.Feed.Stage(Source);

        await fixture.PassAsync();

        fixture.Journal.Instances[0].InstanceId.ShouldBe(
            ChangeIdentity.InstanceIdFor("orders.project", "1.0.0", Source, Group, eventId),
            "a minted id would pass every other test in this file and deduplicate nothing.");
    }

    /// <summary>
    /// A change re-offered because the cursor never moved starts one flow, not two.
    /// </summary>
    /// <remarks>
    /// <strong>The test the whole design exists for.</strong> The feed commits its cursor after
    /// the flows have run, so the window between the two writes is real; this is what a node that
    /// died inside it leaves behind.
    /// </remarks>
    [Fact]
    public async Task AReofferedChangeStartsNoSecondFlow()
    {
        var fixture = Fixture.Create();

        fixture.Feed.Stage(Source);
        fixture.Feed.SuppressCommits = true;

        (await fixture.PassAsync()).Started.ShouldBe(1);

        var second = await fixture.PassAsync();

        second.Observed.ShouldBe(1, "the cursor never moved, so the feed offers it again.");
        second.Started.ShouldBe(0);
        second.Deduplicated.ShouldBe(1);

        fixture.Journal.Instances.Count.ShouldBe(
            1, "one change names one instance, however many times it is offered.");
    }

    /// <summary>The cursor is committed once, after the flows, at the last change run.</summary>
    [Fact]
    public async Task TheCursorIsCommittedAfterTheFlowsHaveRun()
    {
        var fixture = Fixture.Create();

        fixture.Feed.Stage(Source);
        fixture.Feed.Stage(Source);

        var report = await fixture.PassAsync();

        report.Started.ShouldBe(2);

        fixture.Feed.Committed.ShouldHaveSingleItem().ShouldBe(
            "2", "one commit per pass, naming the last change the pass finished with.");

        fixture.Feed.CommitsBeforeAnyFlow.ShouldBe(
            0, "a cursor committed before the flows ran would be at-most-once, and would lose " +
               "every change a crash landed in the middle of.");
    }

    /// <summary>
    /// A flow that failed as a value is progress; the cursor moves past it.
    /// </summary>
    /// <remarks>
    /// ADR-0007 makes a business failure a <c>Result</c>, so a flow that ran, compensated and
    /// wrote a terminal row has happened. Holding the cursor would stop the subscription for ever
    /// over an instance the primary key now refuses.
    /// </remarks>
    [Fact]
    public async Task AFlowThatFailedAsAValueDoesNotHoldTheCursor()
    {
        var fixture = Fixture.Create(failing: true);

        fixture.Feed.Stage(Source);

        var report = await fixture.PassAsync();

        report.Started.ShouldBe(1);
        report.Held.ShouldBe(0);

        fixture.Feed.Committed.ShouldHaveSingleItem();
    }

    /// <summary>
    /// A change that reached no recorded outcome holds the cursor, and holds everything behind
    /// it.
    /// </summary>
    /// <remarks>
    /// The lease on the instance is taken by somebody else, so the run is refused with
    /// <c>lease.held</c> and nothing describes the change. Continuing to the next change would run
    /// <em>n+1</em> of a key before <em>n</em>, and would leave a cursor that cannot be committed
    /// past either.
    /// </remarks>
    [Fact]
    public async Task AChangeThatJournalledNothingHoldsTheCursorAndEverythingBehindIt()
    {
        var fixture = Fixture.Create();

        var first = fixture.Feed.Stage(Source);

        fixture.Feed.Stage(Source);

        var taken = await fixture.Leases.AcquireAsync(
            ChangeIdentity.InstanceIdFor("orders.project", "1.0.0", Source, Group, first),
            "another-node",
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);

        taken.IsSuccess.ShouldBeTrue();

        var report = await fixture.PassAsync();

        report.Observed.ShouldBe(1, "the pass stopped at the change it could not record.");
        report.Held.ShouldBe(1);
        report.Started.ShouldBe(0);

        fixture.Feed.Committed.ShouldBeEmpty(
            "nothing was finished with, so the cursor has nowhere to move to.");

        fixture.Journal.Instances.ShouldBeEmpty();
    }

    /// <summary>A subscription another node is reading is left to that node.</summary>
    [Fact]
    public async Task ASubscriptionAnotherNodeHoldsIsLeftAlone()
    {
        var fixture = Fixture.Create();

        fixture.Feed.Stage(Source);

        var taken = await fixture.Leases.AcquireAsync(
            ChangeIdentity.SubscriptionLeaseIdFor("orders.project", "1.0.0", Source, Group),
            "another-node",
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);

        taken.IsSuccess.ShouldBeTrue();

        var report = await fixture.PassAsync();

        report.Contended.ShouldBe(1);
        report.Observed.ShouldBe(0, "a cursor is a single-reader structure (ADR-0048).");
        fixture.Journal.Instances.ShouldBeEmpty();
    }

    /// <summary>A host with a subscription and no journal does not observe at all.</summary>
    /// <remarks>
    /// The silent failure <c>FlowChangeCatalog.Add</c> refuses at registration, restated at the
    /// scan: without a primary key to refuse a re-read, a subscription would start a flow per
    /// pass until the cursor happened to commit.
    /// </remarks>
    [Fact]
    public void AHostWithNoJournalIsNotEnabled()
    {
        var options = new FlowXOptions { ApplicationName = "Tests", NodeName = "node" };
        var host = new FlowHost(new FlowEngine(SystemClock.Instance), options);

        var subscriptions = new FlowChangeCatalog().Add(
            new ChangeSubscription("orders.project", "1.0.0", Source, Group),
            Plan(ExecutionProfile.Durable),
            new RecordingDispatcher());

        var durability = new FlowDurability(new InMemoryFlowJournal(), new InMemoryLeaseStore());

        var scan = new FlowChangeScan(
            host, subscriptions, new RecordingChangeFeed(() => 0), durability, options);

        scan.IsEnabled.ShouldBeFalse(
            "the host was built with no durability, so nothing journals an instance and nothing " +
            "would refuse a re-read.");
    }

    /// <summary>An ephemeral flow is refused at registration, not started and deduplicated.</summary>
    [Fact]
    public void AnEphemeralFlowIsRefused()
    {
        var refusal = Should.Throw<ArgumentException>(() => new FlowChangeCatalog().Add(
            new ChangeSubscription("orders.project", "1.0.0", Source, Group),
            Plan(ExecutionProfile.Ephemeral),
            new RecordingDispatcher()));

        refusal.Message.ShouldContain("Durable");
    }

    /// <summary>
    /// A flow that emits the very type it observes is refused at registration.
    /// </summary>
    /// <remarks>
    /// The cycle ADR-0047 decision 3 refuses. Every instance in the loop is legitimately distinct,
    /// so nothing downstream can tell it from work — which is why it is refused rather than
    /// detected.
    /// </remarks>
    [Fact]
    public void AFlowThatEmitsWhatItObservesIsRefused()
    {
        var plan = ExecutionPlan.Create(
            FlowDescriptor.Create(
                "orders.project", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromMinutes(5)),
            StepGraph.Create([StepNode.ForCapability(0, Project), StepNode.ForEmit(1, Source)]));

        var refusal = Should.Throw<ArgumentException>(() => new FlowChangeCatalog().Add(
            new ChangeSubscription("orders.project", "1.0.0", Source, Group),
            plan,
            new RecordingDispatcher()));

        refusal.Message.ShouldContain("observes 'order.placed' and emits it");
    }

    /// <summary>A flow that emits a different type is registered.</summary>
    /// <remarks>
    /// The other half of the rule, and the one that stops it being "a change-triggered flow may
    /// not emit". Observing one type and emitting another is a projection pipeline, which is the
    /// ordinary shape.
    /// </remarks>
    [Fact]
    public void AFlowThatEmitsSomethingElseIsRegistered()
    {
        var plan = ExecutionPlan.Create(
            FlowDescriptor.Create(
                "orders.project", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromMinutes(5)),
            StepGraph.Create([StepNode.ForCapability(0, Project), StepNode.ForEmit(1, "order.projected")]));

        new FlowChangeCatalog()
            .Add(new ChangeSubscription("orders.project", "1.0.0", Source, Group), plan, new RecordingDispatcher())
            .Count
            .ShouldBe(1);
    }

    private static ExecutionPlan Plan(ExecutionProfile profile) => ExecutionPlan.Create(
        FlowDescriptor.Create("orders.project", "1.0.0", profile, TimeSpan.FromMinutes(5)),
        StepGraph.Create([StepNode.ForCapability(0, Project)]));

    /// <summary>One node's stores, catalogue and feed.</summary>
    private sealed class Fixture
    {
        private FlowChangeScan? _resident;

        private Fixture(bool failing, TenantIsolation isolation)
        {
            Options = new FlowXOptions
            {
                ApplicationName = "Tests",
                NodeName = "node",
                ShutdownDrainTimeout = TimeSpan.FromSeconds(5),
                TenantIsolation = isolation,
            };

            Durability = new FlowDurability(Journal, Leases);
            Host = new FlowHost(new FlowEngine(SystemClock.Instance), Options, Durability);
            Dispatcher = new RecordingDispatcher(failing);

            // The journal's instance count is "how many flows have run", read by the feed so that
            // "the cursor is committed after the flows" is asserted against the store rather than
            // against a counter the test increments.
            Feed = new RecordingChangeFeed(() => Journal.Instances.Count);
        }

        public InMemoryFlowJournal Journal { get; } = new();

        public InMemoryLeaseStore Leases { get; } = new();

        public RecordingChangeFeed Feed { get; }

        public FlowDurability Durability { get; }

        public FlowHost Host { get; }

        public FlowXOptions Options { get; }

        public FlowChangeCatalog Subscriptions { get; } = new();

        public RecordingDispatcher Dispatcher { get; }

        public static Fixture Create(
            bool failing = false, TenantIsolation isolation = TenantIsolation.None)
        {
            var fixture = new Fixture(failing, isolation);

            fixture.Subscriptions.Add(
                new ChangeSubscription("orders.project", "1.0.0", Source, Group),
                Plan(ExecutionProfile.Durable),
                fixture.Dispatcher);

            fixture._resident = new FlowChangeScan(
                fixture.Host, fixture.Subscriptions, fixture.Feed, fixture.Durability, fixture.Options);

            return fixture;
        }

        public ValueTask<ChangeScanReport> PassAsync() =>
            _resident!.RunOnceAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A feed over a list, with a cursor, that records what it was told and when.
    /// </summary>
    /// <remarks>
    /// The position is the change's ordinal, rendered as a string, because the contract says a
    /// position is opaque above the plugin — a double whose position had structure would let a
    /// defect in the scan pass here and fail against PostgreSQL.
    /// </remarks>
    private sealed class RecordingChangeFeed(Func<int> flowsRun) : IChangeFeed
    {
        private readonly List<BusMessage> _changes = [];
        private readonly List<string> _committed = [];
        private int _cursor;

        /// <summary>Stops the cursor moving, which is what a crash mid-pass leaves behind.</summary>
        public bool SuppressCommits { get; set; }

        /// <summary>Every position committed, in order.</summary>
        public IReadOnlyList<string> Committed => _committed;

        /// <summary>How many commits happened before any flow had run.</summary>
        public int CommitsBeforeAnyFlow { get; private set; }

        public string Feed => "recording";

        /// <summary>Stages one change, and returns the identity it carries.</summary>
        public Guid Stage(string type)
        {
            var eventId = Guid.NewGuid();

            _changes.Add(new BusMessage(eventId, type, type, "1.0.0", "instance-1", "{}"));

            return eventId;
        }

        public ValueTask<Result<IReadOnlyList<ObservedChange>>> ReadAsync(
            ChangeSubscription subscription, int max, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(subscription);

            var offered = _changes
                .Skip(_cursor)
                .Take(max)
                .Select((change, index) => new ObservedChange(
                    change,
                    new ChangePosition((_cursor + index + 1).ToString(
                        System.Globalization.CultureInfo.InvariantCulture))))
                .ToList();

            return ValueTask.FromResult(Result.Ok<IReadOnlyList<ObservedChange>>(offered));
        }

        public ValueTask<Result<bool>> CommitAsync(
            ChangeSubscription subscription,
            ChangePosition position,
            CancellationToken cancellationToken)
        {
            if (flowsRun() == 0)
            {
                CommitsBeforeAnyFlow++;
            }

            if (SuppressCommits)
            {
                return ValueTask.FromResult(Result.Ok(false));
            }

            _committed.Add(position.Value);
            _cursor = int.Parse(position.Value, System.Globalization.CultureInfo.InvariantCulture);

            return ValueTask.FromResult(Result.Ok(true));
        }
    }

    /// <summary>
    /// A dispatcher that remembers what it was started with and, on request, fails as a value.
    /// </summary>
    /// <remarks>
    /// <see cref="BusScanTests"/>'s double and its reason: ADR-0007 makes a business failure a
    /// <c>Result</c>, so a scan that treated only <c>IsSuccess</c> as progress would pass every
    /// test here except the one that uses this mode — and would stop a subscription for ever over
    /// one declined order in production.
    /// </remarks>
    private sealed class RecordingDispatcher(bool failing = false) : IStepDispatcher
    {
        private static readonly Error Declined =
            new("orders.declined", "The order was not projected.", ErrorCategory.Validation);

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
            ValueTask.FromResult(failing ? StepOutcome.Failed(Declined) : StepOutcome.Success);

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
