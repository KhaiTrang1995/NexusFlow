using FlowX.Conformance.InMemory;
using FlowX.Hosting;
using FlowX.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// The whole path at schema level, against a real database: a host declaring
/// <see cref="TenantIsolation.Schema"/>, a real journal over per-tenant pools, a real lease
/// store, and the two node-wide sweeps that have to reach across schemas without mixing them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists when <see cref="TenantSchemaIsolationTests"/> already asserts the
/// store.</strong> That file proves the adapter keeps two tenants apart; this proves the
/// runtime asks it to, and that the two sweeps still work once the rows have left the one table
/// they used to share. The second half is where schema isolation fails silently: a sweep that
/// found nothing would report an empty backlog for ever and no assertion about the store would
/// notice.
/// </para>
/// </remarks>
public sealed class TenantSchemaHostIsolationTests
{
    private const string TenantA = "acme";
    private const string TenantB = "globex";

    private static readonly CapabilityDescriptor Validate =
        CapabilityDescriptor.Create("order.validate", "1.0.0", isIdempotent: true);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// A host resuming under one tenant cannot pick up an instance belonging to another, in
    /// either direction.
    /// </summary>
    /// <remarks>
    /// The instance is left exactly as a dead node leaves one, so this is the state a recovery
    /// scan and a timer sweep both find. Supplying the right tenant must still take it over, so
    /// that the refusal is isolation rather than a resume that is simply broken.
    /// </remarks>
    [Fact]
    public async Task AHostCannotResumeAnInstanceBelongingToAnotherTenant()
    {
        await using var schema = await PostgresTestSchema.CreateWithTenantSchemasAsync(Cancellation);

        var durability = Durability(schema);

        durability.IsolationEnforced.ShouldBe(
            TenantIsolation.Schema,
            "the journal was given per-tenant pools, so it enforces the level the host declares.");

        var ofA = await AbandonAsync(schema, TenantA);
        var ofB = await AbandonAsync(schema, TenantB);

        var host = NewHost(durability);
        var registration = new FlowRegistration(Plan(), new CountingDispatcher());

        var aTakesB = await host.ResumeAsync(ofB, registration, TenantA, Cancellation);
        var bTakesA = await host.ResumeAsync(ofA, registration, TenantB, Cancellation);

        aTakesB.IsFailure.ShouldBeTrue(
            "a host resuming as tenant A took over tenant B's instance. It now holds the lease " +
            "on somebody else's flow and is about to run its remaining steps.");

        bTakesA.IsFailure.ShouldBeTrue(
            "and the same the other way, because an isolation that holds for the tenant a test " +
            "arranges first holds by accident.");

        (await host.ResumeAsync(ofA, registration, TenantA, Cancellation)).IsSuccess.ShouldBeTrue(
            "tenant A must still recover its own instance.");

        (await host.ResumeAsync(ofB, registration, TenantB, Cancellation)).IsSuccess.ShouldBeTrue(
            "and tenant B its own.");
    }

    /// <summary>
    /// A recovery scan reaches every tenant's schema and resumes each instance under the tenant
    /// its own row names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The failure this catches is the one schema isolation introduces.</strong> The
    /// scan is node-wide platform work with no claims and nobody asking for anything, so it
    /// legitimately picks up both tenants' abandoned instances — but the table it used to read
    /// is now empty, and every row is in a schema of somebody's own. A scan that had not been
    /// fanned out would report zero examined, zero resumed and no error at all.
    /// </para>
    /// <para>
    /// Both instances completing is what says the fan-out also scoped correctly: a candidate
    /// resumed under the wrong tenant cannot read its own frontier and is counted as failed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARecoveryScanReachesEveryTenantsSchemaAndResumesUnderItsOwnTenant()
    {
        await using var schema = await PostgresTestSchema.CreateWithTenantSchemasAsync(Cancellation);

        var durability = Durability(schema);

        var ofA = await AbandonAsync(schema, TenantA);
        var ofB = await AbandonAsync(schema, TenantB);

        var options = Options();
        var host = NewHost(durability, options);
        var catalog = new FlowCatalog().Add(Plan(), new CountingDispatcher());

        var report = await new FlowRecoveryScan(host, catalog, durability, options, SystemClock.Instance)
            .RunOnceAsync(Cancellation);

        report.Error.ShouldBeNull();
        report.Resumed.ShouldBe(
            2,
            "both tenants' abandoned instances were found, in two different schemas, and taken " +
            "over. A scan that swept only the control schema would report zero and say nothing.");

        var recordA = await schema.Journal.ForTenant(TenantA).ReadInstanceAsync(ofA, Cancellation);
        var recordB = await schema.Journal.ForTenant(TenantB).ReadInstanceAsync(ofB, Cancellation);

        recordA.Value.State.ShouldBe(FlowInstanceState.Completed);
        recordB.Value.State.ShouldBe(FlowInstanceState.Completed);

        recordA.Value.TenantId.ShouldBe(TenantA);
        recordB.Value.TenantId.ShouldBe(
            TenantB,
            "each instance kept its own tenant through the takeover, and stayed in its own " +
            "schema while doing it.");
    }

    /// <summary>
    /// A timer sweep reaches every tenant's schema and wakes each parked instance under the
    /// tenant its own row names.
    /// </summary>
    /// <remarks>
    /// Asserted separately from the recovery scan rather than assumed to follow from it: they
    /// are two sweeps over disjoint sets of rows, fanned out by two different classes. A parked
    /// instance nobody wakes is worse than an abandoned one nobody recovers — the offer is not
    /// withdrawn and the reminder is not sent — and at this level it fails without an error.
    /// </remarks>
    [Fact]
    public async Task ATimerSweepReachesEveryTenantsSchemaAndWakesUnderItsOwnTenant()
    {
        await using var schema = await PostgresTestSchema.CreateWithTenantSchemasAsync(Cancellation);

        var durability = Durability(schema);

        var ofA = await ParkAsync(schema, TenantA);
        var ofB = await ParkAsync(schema, TenantB);

        var options = Options();
        var host = NewHost(durability, options);
        var catalog = new FlowCatalog().Add(Plan(), new CountingDispatcher());

        var report = await new FlowTimerScan(host, catalog, durability, options, SystemClock.Instance)
            .RunOnceAsync(Cancellation);

        report.Error.ShouldBeNull();
        report.Woken.ShouldBe(
            2,
            "both tenants' parked instances came due, in two different schemas, and were woken. " +
            "A sweep over the control schema alone would leave every one of them asleep.");

        var recordA = await schema.Journal.ForTenant(TenantA).ReadInstanceAsync(ofA, Cancellation);
        var recordB = await schema.Journal.ForTenant(TenantB).ReadInstanceAsync(ofB, Cancellation);

        recordA.Value.State.ShouldBe(FlowInstanceState.Completed);
        recordB.Value.State.ShouldBe(FlowInstanceState.Completed);
    }

    /// <summary>
    /// A host declaring <see cref="TenantIsolation.Schema"/> over a store that only filters rows
    /// is refused, rather than served the weaker level under the stronger name.
    /// </summary>
    /// <remarks>
    /// <strong>This is the "not silently downgraded" requirement made enforceable.</strong> The
    /// two journals are the same class and present the same seam; the only thing that tells them
    /// apart is what the store says it enforces. Without this check a deployment that forgot one
    /// line of configuration would keep every tenant's rows in one schema while its configuration
    /// said each had its own, and nothing anywhere would report a problem.
    /// </remarks>
    [Fact]
    public async Task AHostDeclaringSchemaOverARowStoreIsRefused()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var durability = new FlowDurability(schema.Journal, schema.Leases, schema.RecoveryIndex);

        durability.IsolationEnforced.ShouldBe(
            TenantIsolation.Row,
            "a journal built without per-tenant pools filters rows and nothing more.");

        var options = Options();

        var refusal = Should.Throw<InvalidOperationException>(() => NewHost(durability, options));

        refusal.Message.Contains(nameof(TenantIsolation.Schema), StringComparison.Ordinal)
            .ShouldBeTrue($"the refusal must name the level that was asked for.\n{refusal.Message}");

        refusal.Message
            .Contains(nameof(PostgresJournalOptions.TenantSchemas), StringComparison.Ordinal)
            .ShouldBeTrue(
                "and the registration that repairs it, because the operator's mistake is a " +
                $"missing line of configuration rather than a wrong level.\n{refusal.Message}");
    }

    /// <summary>
    /// Turning tenant schemas on swaps every node-wide loop for its fan-out, and makes the one
    /// that cannot fan out refuse instead of observing an empty table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No server is needed and none is asked for: the data source is built lazily and nothing
    /// here opens a connection. The question is which service types the container ended up with,
    /// which is answerable offline.
    /// </para>
    /// <para>
    /// <strong>The change feed's refusal is the half worth having, and its reason is not the
    /// one ADR-0051 §4 gave.</strong> The publisher fans out — a claim was always confined to one
    /// table — so "it claims rows and advances a position" cannot be what disqualifies a loop.
    /// What disqualifies the feed is delivery: a change observed in a tenant's schema has to
    /// start a flow in that tenant, and a change scan carries no principal to be admitted with.
    /// </para>
    /// </remarks>
    [Fact]
    public void TurningTenantSchemasOnFansOutEveryLoopExceptTheOneThatCannotDeliver()
    {
        var services = new ServiceCollection();

        services.AddFlowX(options =>
        {
            options.ApplicationName = "Sample.App";
            options.TenantIsolation = TenantIsolation.Schema;
        });

        services.AddFlowXPostgres(
            "Host=localhost;Database=postgres;Username=postgres",
            new PostgresJournalOptions
            {
                TenantSchemas = new TenantSchemaOptions { IsEnabled = true },
            });

        services.AddSingleton<IEventPublisher>(new RecordingEventPublisher());
        services.AddFlowXPostgresOutbox();
        services.AddFlowXPostgresChangeFeed();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IRecoveryIndex>().ShouldBeOfType<PostgresTenantRecoveryIndex>(
            "the control schema holds no instances, so the single-schema query would sweep " +
            "correctly, find nothing, and strand every abandoned instance in the deployment.");

        provider.GetRequiredService<ITimerIndex>().ShouldBeOfType<PostgresTenantTimerIndex>(
            "and the same for parked instances, whose failure mode is a business one.");

        provider.GetRequiredService<IFlowJournal>()
            .ShouldBeOfType<PostgresFlowJournal>()
            .Isolation.ShouldBe(
                TenantIsolation.Schema,
                "and the journal must report the level it now enforces, because that is what " +
                "stops a host declaring more than its store delivers.");

        provider.GetRequiredService<PostgresOutboxPublisher>().ShouldNotBeNull(
            "the publisher drains every tenant's outbox at this level. Refusing it left a " +
            "deployment that isolates by schema with no way to publish at all.");

        var refused = Should.Throw<InvalidOperationException>(
            () => provider.GetRequiredService<IChangeFeed>());

        refused.Message.ShouldContain(
            "tenant.required",
            Case.Sensitive,
            "the refusal must name the decision that is missing rather than restate that the " +
            $"control schema is empty, or the next reader repeats the analysis.\n{refused.Message}");
    }

    // -----------------------------------------------------------------------------------
    // Fixtures
    // -----------------------------------------------------------------------------------

    private static FlowDurability Durability(PostgresTestSchema schema) =>
        new(schema.Journal, schema.Leases, schema.RecoveryScan, schema.TimerSweep);

    private static FlowHost NewHost(FlowDurability durability, FlowXOptions? options = null) =>
        new(new FlowEngine(SystemClock.Instance), options ?? Options(), durability);

    private static FlowXOptions Options() => new()
    {
        ApplicationName = "Sample.App",
        NodeName = "node-1",
        ShutdownDrainTimeout = TimeSpan.FromSeconds(5),
        TenantIsolation = TenantIsolation.Schema,
    };

    private static ExecutionPlan Plan() => ExecutionPlan.Create(
        FlowDescriptor.Create(
            "order.place", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromMinutes(5)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForCapability(1, Validate),
        ]));

    /// <summary>
    /// Plays out a node dying half-way through one tenant's flow, in that tenant's schema.
    /// </summary>
    private static async Task<Guid> AbandonAsync(PostgresTestSchema schema, string tenantId)
    {
        var instance = Guid.CreateVersion7();
        var journal = schema.Journal.ForTenant(tenantId);

        var lease = await DurableLease.AcquireAsync(
            schema.Leases,
            instance,
            "dead-node",
            new LeasePolicy
            {
                Ttl = TimeSpan.FromSeconds(30),
                RenewalInterval = TimeSpan.FromSeconds(10),
            },
            Cancellation);

        lease.IsSuccess.ShouldBeTrue(lease.IsFailure ? lease.Error.ToString() : string.Empty);

        var begun = await lease.Value.BeginAsync(
            journal,
            Plan(),
            new FlowInvocation("corr-1", instance.ToString(), tenantId),
            cancellationToken: Cancellation);

        begun.IsSuccess.ShouldBeTrue(begun.IsFailure ? begun.Error.ToString() : string.Empty);

        var committed = await journal.CommitAsync(
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

        await schema.ExecuteAsync(
            $"""
             UPDATE flow_instance
                SET updated_at = now() - interval '1 hour'
              WHERE instance_id = '{instance}'
             """,
            tenantId,
            Cancellation);

        return instance;
    }

    /// <summary>
    /// Parks one tenant's instance at a wait that is already due, in that tenant's schema.
    /// </summary>
    private static async Task<Guid> ParkAsync(PostgresTestSchema schema, string tenantId)
    {
        var instance = Guid.CreateVersion7();
        var journal = schema.Journal.ForTenant(tenantId);

        var lease = await DurableLease.AcquireAsync(
            schema.Leases,
            instance,
            "parking-node",
            new LeasePolicy
            {
                Ttl = TimeSpan.FromSeconds(30),
                RenewalInterval = TimeSpan.FromSeconds(10),
            },
            Cancellation);

        lease.IsSuccess.ShouldBeTrue(lease.IsFailure ? lease.Error.ToString() : string.Empty);

        var begun = await lease.Value.BeginAsync(
            journal,
            Plan(),
            new FlowInvocation("corr-1", instance.ToString(), tenantId),
            cancellationToken: Cancellation);

        begun.IsSuccess.ShouldBeTrue(begun.IsFailure ? begun.Error.ToString() : string.Empty);

        var parked = await journal.CompleteAsync(
            instance,
            lease.Value.Token,
            FlowInstanceState.Suspended,
            JournalPayload.Empty,
            new FlowWake(StepScope.Root, 0, DateTimeOffset.UtcNow.AddSeconds(-30)),
            Cancellation);

        parked.IsSuccess.ShouldBeTrue(parked.IsFailure ? parked.Error.ToString() : string.Empty);

        await lease.Value.DisposeAsync();

        return instance;
    }

    /// <summary>A dispatcher that runs every step successfully and records nothing.</summary>
    private sealed class CountingDispatcher : IStepDispatcher
    {
        public ValueTask<StepOutcome> ExecuteAsync(
            int stepIndex, FlowContext ctx, CancellationToken ct) =>
            ValueTask.FromResult(StepOutcome.Success);

        public ValueTask<StepOutcome> CompensateAsync(
            int stepIndex, FlowContext ctx, CancellationToken ct) =>
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
