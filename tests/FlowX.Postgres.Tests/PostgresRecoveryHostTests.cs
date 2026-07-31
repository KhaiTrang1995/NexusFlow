using FlowX.Hosting;
using FlowX.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// The property the whole adapter change exists for: a host whose journal is PostgreSQL
/// finds an instance a dead node left running and finishes it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Everything below this line was already true, and the fleet still lost work.</strong>
/// WP-53 shipped a journal and a lease store that pass their conformance suites; WP-55
/// shipped the sweep and the takeover. What connected them was an <see cref="IRecoveryIndex"/>
/// that only a test double in another assembly implemented, and
/// <c>FlowXServiceCollectionExtensions</c> resolves that interface optionally on purpose — so
/// a Postgres host fenced correctly, journaled correctly, and never once looked for another
/// node's abandoned work. Nothing failed. That is why this test is here rather than only the
/// unit-level ones: every part being right is what the previous state already had.
/// </para>
/// <para>
/// <strong>The stores are the real ones and the scan is the real one.</strong> The only
/// simulation is time — the dead node's row is moved into the past instead of the test
/// waiting out a lease TTL — and the death itself, which is a lease that is never renewed.
/// </para>
/// </remarks>
public sealed class PostgresRecoveryHostTests
{
    private const string DeadNode = "node-1";
    private const string LiveNode = "node-2";

    private static readonly CapabilityDescriptor Validate =
        CapabilityDescriptor.Create("order.validate", "1.0.0", isIdempotent: true);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// A node sweeps, finds the instance the other node died holding, and runs it to
    /// completion from the step after the last one that committed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The committed prefix is skipped and the rest runs, which is the point of a journal
    /// being a history rather than a checkpoint: step 0's effect happened, so re-running it
    /// would be a second charge to a customer, and step 1 has no row, so as far as anything
    /// durable is concerned it never ran.
    /// </para>
    /// <para>
    /// The fence reaching 2 is the safety half. The dead node is not merely gone — if it
    /// came back mid-step it would be refused, because the instance has been shown a higher
    /// token than the one that node holds.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AHostWithAPostgresJournalRecoversAnAbandonedInstance()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var durability = new FlowDurability(schema.Journal, schema.Leases, schema.RecoveryIndex);

        durability.CanScan.ShouldBeTrue(
            "the Postgres adapter answers the recovery query, so a host on it sweeps.");

        var instance = await AbandonAsync(schema);

        var options = Options(LiveNode);
        var host = new FlowHost(new FlowEngine(SystemClock.Instance), options, durability);
        var dispatcher = new CountingDispatcher();
        var catalog = new FlowCatalog().Add(Plan(), dispatcher);

        var scan = new FlowRecoveryScan(host, catalog, durability, options, SystemClock.Instance);

        scan.IsEnabled.ShouldBeTrue();

        var report = await scan.RunOnceAsync(Cancellation);

        report.Error.ShouldBeNull();
        report.Examined.ShouldBe(1);
        report.NotRunnable.ShouldBe(0);
        report.Contended.ShouldBe(0);
        report.Resumed.ShouldBe(1, "this node took the instance over and ran it.");

        dispatcher.Executed.ShouldBe(
            [1],
            "step 0 committed before the node died, so its effect happened and it is skipped. " +
            "Step 1 has no row, so as far as the journal is concerned it never ran.");

        var record = await schema.Journal.ReadInstanceAsync(instance, Cancellation);

        record.Value.State.ShouldBe(FlowInstanceState.Completed);
        record.Value.Fence.Value.ShouldBe(2, "the second lease issued for this instance.");

        host.HeldLeases.ShouldBe(0);

        (await schema.Leases.ReadAsync(instance, Cancellation)).Error.Code.ShouldBe(
            "lease.not_held",
            "released at the end of the resumed flow, so the next node does not wait a TTL.");
    }

    /// <summary>
    /// A healthy instance is not swept, and the sweep costs one query to establish it.
    /// </summary>
    /// <remarks>
    /// The other half of the same behaviour, and the one that decides what a fleet costs at
    /// rest. Without the staleness filter every node would fetch every live instance on every
    /// sweep and be refused a lease on each — correct, and a self-inflicted load test against
    /// the journal.
    /// </remarks>
    [Fact]
    public async Task AnInstanceWithALiveOwnerIsNotSwept()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var durability = new FlowDurability(schema.Journal, schema.Leases, schema.RecoveryIndex);

        await AbandonAsync(schema, idle: false);

        var options = Options(LiveNode);
        var host = new FlowHost(new FlowEngine(SystemClock.Instance), options, durability);
        var dispatcher = new CountingDispatcher();
        var catalog = new FlowCatalog().Add(Plan(), dispatcher);

        var report = await new FlowRecoveryScan(host, catalog, durability, options, SystemClock.Instance)
            .RunOnceAsync(Cancellation);

        report.ShouldBe(
            RecoveryScanReport.Nothing,
            "its row was written inside the idle window, so it never reached the candidate set.");

        dispatcher.Executed.ShouldBeEmpty();
    }

    /// <summary>
    /// An instance pinned to a version this node does not carry is left for a node that
    /// does.
    /// </summary>
    /// <remarks>
    /// The case the staleness ordering exists for, reached through the real stores. Taking a
    /// lease on an instance this node cannot run would deny it to a node that can, for a
    /// whole TTL, on every sweep — so the candidate is examined, counted and left alone with
    /// no acquisition at all.
    /// </remarks>
    [Fact]
    public async Task AnInstancePinnedToAnUncarriedVersionIsExaminedAndLeft()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var durability = new FlowDurability(schema.Journal, schema.Leases, schema.RecoveryIndex);

        var instance = await AbandonAsync(schema, version: "2.0.0");

        var options = Options(LiveNode);
        var host = new FlowHost(new FlowEngine(SystemClock.Instance), options, durability);
        var catalog = new FlowCatalog().Add(Plan("1.0.0"), new CountingDispatcher());

        var report = await new FlowRecoveryScan(host, catalog, durability, options, SystemClock.Instance)
            .RunOnceAsync(Cancellation);

        report.Examined.ShouldBe(1);
        report.NotRunnable.ShouldBe(1);
        report.Resumed.ShouldBe(0);

        (await schema.Leases.ReadAsync(instance, Cancellation)).Error.Code.ShouldBe(
            "lease.not_held",
            "and no lease was taken on it, so the node that carries 2.0.0 can have it now " +
            "rather than a TTL from now.");
    }

    /// <summary>
    /// Calling <c>AddFlowXPostgres</c> is what wires the scan, and the option is what
    /// declines it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No server is needed and none is asked for: the data source is built lazily and
    /// nothing here opens a connection. The question is which service types the container
    /// ended up with, and that is answerable offline.
    /// </para>
    /// <para>
    /// The negative half is the one worth having. <c>FlowXOptions</c> says a deployment that
    /// does not want to sweep should "leave the journal without an IRecoveryIndex", so the
    /// opt-out has to remain reachable through the bundled extension method rather than only
    /// through hand-wiring the three stores.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddFlowXPostgresRegistersTheRecoveryIndexUnlessItIsDeclined(bool registerIndex)
    {
        var services = new ServiceCollection();

        services.AddFlowX(options =>
        {
            options.ApplicationName = "Sample.App";
            options.NodeName = LiveNode;
        });

        services.AddFlowXPostgres(
            "Host=localhost;Database=postgres;Username=postgres",
            new PostgresJournalOptions { RegisterRecoveryIndex = registerIndex });

        using var provider = services.BuildServiceProvider();

        provider.GetService<IFlowJournal>().ShouldBeOfType<PostgresFlowJournal>();
        provider.GetService<ILeaseStore>().ShouldBeOfType<PostgresLeaseStore>();

        if (registerIndex)
        {
            provider.GetService<IRecoveryIndex>().ShouldBeOfType<PostgresRecoveryIndex>(
                "a Postgres host sweeps by default, because not sweeping is what it did " +
                "before anyone chose it.");
        }
        else
        {
            provider.GetService<IRecoveryIndex>().ShouldBeNull(
                "and a deployment that has decided not to sweep can still say so.");
        }

        provider.GetRequiredService<FlowHost>().IsDurabilityConfigured.ShouldBeTrue();
    }

    // -----------------------------------------------------------------------------------
    // Fixture
    // -----------------------------------------------------------------------------------

    /// <summary>A two-step durable flow.</summary>
    private static ExecutionPlan Plan(string version = "1.0.0") => ExecutionPlan.Create(
        FlowDescriptor.Create("order.place", version, ExecutionProfile.Durable, TimeSpan.FromMinutes(5)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForCapability(1, Validate),
        ]));

    private static FlowXOptions Options(string node) => new()
    {
        ApplicationName = "Sample.App",
        NodeName = node,
        ShutdownDrainTimeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>
    /// Plays out a node dying half-way through a flow, and leaves the journal exactly as it
    /// would be found.
    /// </summary>
    /// <param name="schema">The schema under test.</param>
    /// <param name="version">The version the instance is pinned to for its whole life.</param>
    /// <param name="idle">
    /// Whether to move the row into the past. False leaves it as a healthy instance whose
    /// owner wrote it a moment ago.
    /// </param>
    /// <remarks>
    /// The lease is taken, the instance is opened under it, one step commits, and then the
    /// lease is given up without the instance being finished — which is the state a
    /// <c>SIGKILL</c> reaches a TTL later, with the difference that a test does not have to
    /// wait for the TTL.
    /// </remarks>
    private static async Task<Guid> AbandonAsync(
        PostgresTestSchema schema,
        string version = "1.0.0",
        bool idle = true)
    {
        var instance = Guid.NewGuid();
        var plan = Plan(version);

        var lease = await DurableLease.AcquireAsync(
            schema.Leases,
            instance,
            DeadNode,
            new LeasePolicy { Ttl = TimeSpan.FromSeconds(30), RenewalInterval = TimeSpan.FromSeconds(10) },
            Cancellation);

        lease.IsSuccess.ShouldBeTrue(lease.IsFailure ? lease.Error.ToString() : string.Empty);

        var begun = await lease.Value.BeginAsync(
            schema.Journal,
            plan,
            new FlowInvocation("corr-1", instance.ToString(), "acme"),
            cancellationToken: Cancellation);

        begun.IsSuccess.ShouldBeTrue(begun.IsFailure ? begun.Error.ToString() : string.Empty);

        var committed = await schema.Journal.CommitAsync(
            new StepCommit
            {
                Key = StepKey.First(instance, 0),
                Token = lease.Value.Token,
                CapabilityId = "order.validate",
                CapabilityVersion = "1.0.0",
                Outcome = JournalOutcome.Success,
                State = FlowInstanceState.Running,
            },
            Cancellation);

        committed.IsSuccess.ShouldBeTrue(
            committed.IsFailure ? committed.Error.ToString() : string.Empty);

        // The lease goes; the instance does not. A crash reaches the same place a TTL later.
        await lease.Value.DisposeAsync();

        if (idle)
        {
            await schema.ExecuteAsync(
                $"""
                 UPDATE flow_instance
                    SET updated_at = now() - interval '1 hour'
                  WHERE instance_id = '{instance}'
                 """,
                Cancellation);
        }

        return instance;
    }

    /// <summary>A dispatcher that records the step indices it was asked to run.</summary>
    private sealed class CountingDispatcher : IStepDispatcher
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

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This double runs plans with no branch step.");

        public int Select(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This double runs plans with no switch step.");

        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to begin.");

        public FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to enter.");
    }
}
