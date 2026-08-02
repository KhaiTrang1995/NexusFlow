using FlowX.Hosting;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// The whole path, against a real database: a host declaring row isolation, a real journal, a
/// real lease store, and one tenant trying to take over another tenant's instance.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists when <c>TenantIsolationTests</c> already asserts the store.</strong>
/// That file proves the adapter refuses; this proves the runtime <em>asks it to</em>. The two
/// fail independently and the second is the one the defect was made of: every piece of the
/// store side — the <c>tenant_id</c> column, its index, the query's tenant filter — has been
/// present since migration <c>0001</c>, correct and unreached, because nothing above passed
/// the tenant down. A green store suite proved nothing about that then and would prove nothing
/// about it now.
/// </para>
/// <para>
/// <strong>Resuming is the verb under test.</strong> A read returns rows; a resume takes a
/// lease, raises a fence and runs the flow — real effects, under an owner that is now this
/// caller. <c>FlowHost.ResumeAsync</c> is the single door a signal, a recovery scan and a
/// timer sweep all pass through, so refusing there refuses all three.
/// </para>
/// </remarks>
public sealed class TenantHostIsolationTests
{
    private const string TenantA = "acme";
    private const string TenantB = "globex";

    private static readonly CapabilityDescriptor Validate =
        CapabilityDescriptor.Create("order.validate", "1.0.0", isIdempotent: true);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// A host resuming an instance under one tenant cannot pick up an instance belonging to
    /// another, in either direction.
    /// </summary>
    /// <remarks>
    /// The instance is left exactly as a dead node leaves one, so this is the state a recovery
    /// scan and a timer sweep both find. What is asserted is that supplying the wrong tenant
    /// does not take it over — and, crucially, that supplying the right one does, so the
    /// refusal is isolation rather than a resume that is simply broken.
    /// </remarks>
    [Fact]
    public async Task AHostCannotResumeAnInstanceBelongingToAnotherTenant()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var durability = new FlowDurability(schema.Journal, schema.Leases, schema.RecoveryIndex);

        durability.CanIsolateTenants.ShouldBeTrue(
            "the Postgres journal binds a connection to a tenant, so a host on it gets the " +
            "database's wall as well as its own refusal.");

        var ofA = await AbandonAsync(schema, TenantA);
        var ofB = await AbandonAsync(schema, TenantB);

        var host = NewHost(durability);
        var registration = new FlowRegistration(Plan(), new CountingDispatcher());

        var aTakesB = await host.ResumeAsync(ofB, registration, TenantA, Cancellation);
        var bTakesA = await host.ResumeAsync(ofA, registration, TenantB, Cancellation);

        aTakesB.IsFailure.ShouldBeTrue(
            "a host resuming as tenant A took over tenant B's instance. It now holds the " +
            "lease on somebody else's flow and is about to run its remaining steps.");

        bTakesA.IsFailure.ShouldBeTrue(
            "and the same the other way. Both directions, because an isolation that holds " +
            "for the tenant a test arranges first holds by accident.");

        var aTakesA = await host.ResumeAsync(ofA, registration, TenantA, Cancellation);

        aTakesA.IsSuccess.ShouldBeTrue(
            "tenant A must still recover its own instance. Without this the two assertions " +
            "above would pass against a resume that never works at all.");
    }

    /// <summary>
    /// A recovery scan resumes each candidate under the tenant its own row names, so a
    /// multi-tenant deployment recovers every tenant and mixes none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The scan is not a tenant, and that is the point.</strong> It is node-wide
    /// platform work with no claims and nobody asking for anything, so it legitimately picks up
    /// both tenants' abandoned instances — refusing them would make a node restart an outage
    /// for everyone. What it must not do is run one tenant's instance on a connection that can
    /// reach another's rows, and it avoids that by carrying <c>AbandonedInstance.TenantId</c>
    /// through to the resume.
    /// </para>
    /// <para>
    /// So the assertion is that both are recovered <em>and</em> both complete: a scan that had
    /// scoped them wrongly would fail to read the frontier and report zero resumed, which is
    /// the failure this would catch.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARecoveryScanResumesEachTenantsInstanceUnderItsOwnTenant()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var durability = new FlowDurability(schema.Journal, schema.Leases, schema.RecoveryIndex);

        var ofA = await AbandonAsync(schema, TenantA);
        var ofB = await AbandonAsync(schema, TenantB);

        var options = Options();
        var host = NewHost(durability, options);
        var catalog = new FlowCatalog().Add(Plan(), new CountingDispatcher());
        var scan = new FlowRecoveryScan(host, catalog, durability, options, SystemClock.Instance);

        var report = await scan.RunOnceAsync(Cancellation);

        report.Error.ShouldBeNull();
        report.Resumed.ShouldBe(
            2,
            "both tenants' abandoned instances were found and taken over. A scan that had " +
            "resumed them unscoped, or scoped them to the wrong tenant, would report fewer.");

        var recordA = await schema.Journal.ReadInstanceAsync(ofA, Cancellation);
        var recordB = await schema.Journal.ReadInstanceAsync(ofB, Cancellation);

        recordA.Value.State.ShouldBe(FlowInstanceState.Completed);
        recordB.Value.State.ShouldBe(FlowInstanceState.Completed);

        recordA.Value.TenantId.ShouldBe(TenantA);
        recordB.Value.TenantId.ShouldBe(
            TenantB,
            "each instance kept its own tenant through the takeover. A resume that wrote " +
            "under the scan's idea of a tenant rather than the instance's would show here.");
    }

    /// <summary>
    /// A deployment declaring <see cref="TenantIsolation.None"/> does not scope its journal,
    /// even when the rows it is reading carry a tenant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The declared level is the switch, and the presence of a tenant is not.</strong>
    /// That distinction is easy to lose, and losing it is a live regression rather than a
    /// theoretical one: <c>HttpTriggerReader</c> populates <c>FlowInvocation.TenantId</c> from
    /// claims on every deployment, isolating or not, and a journal's rows may carry a tenant
    /// written long before any of this existed. A host that scoped whenever it saw one would
    /// silently start binding connections and assuming a restricted role because its tokens
    /// happen to carry a <c>tid</c> claim.
    /// </para>
    /// <para>
    /// The arrangement discriminates rather than merely passing: the wrong tenant is supplied
    /// for the instance. A host that isolates would scope to it, find nothing, and refuse. A
    /// host that declares <see cref="TenantIsolation.None"/> must ignore it entirely — which is
    /// what "single-tenant deployments pay nothing" means when it is written as a test rather
    /// than as a sentence.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASingleTenantDeploymentDoesNotScopeItsJournalAtAll()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var durability = new FlowDurability(schema.Journal, schema.Leases, schema.RecoveryIndex);
        var ofA = await AbandonAsync(schema, TenantA);

        var options = Options();
        options.TenantIsolation = TenantIsolation.None;

        var host = NewHost(durability, options);
        var registration = new FlowRegistration(Plan(), new CountingDispatcher());

        var resumed = await host.ResumeAsync(ofA, registration, TenantB, Cancellation);

        resumed.IsSuccess.ShouldBeTrue(
            "a deployment that declares no isolation must behave exactly as it did before " +
            "this feature existed — including ignoring a tenant it was handed. Had the host " +
            "scoped on the tenant's presence rather than on the declared level, this would " +
            "have bound the connection to the wrong tenant and refused.");
    }

    // -----------------------------------------------------------------------------------
    // Fixtures
    // -----------------------------------------------------------------------------------

    private static FlowHost NewHost(FlowDurability durability, FlowXOptions? options = null) =>
        new(new FlowEngine(SystemClock.Instance), options ?? Options(), durability);

    private static FlowXOptions Options() => new()
    {
        ApplicationName = "Sample.App",
        NodeName = "node-1",
        ShutdownDrainTimeout = TimeSpan.FromSeconds(5),
        TenantIsolation = TenantIsolation.Row,
    };

    private static ExecutionPlan Plan() => ExecutionPlan.Create(
        FlowDescriptor.Create(
            "order.place", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromMinutes(5)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForCapability(1, Validate),
        ]));

    /// <summary>
    /// Plays out a node dying half-way through one tenant's flow, and leaves the journal
    /// exactly as it would be found.
    /// </summary>
    private static async Task<Guid> AbandonAsync(PostgresTestSchema schema, string tenantId)
    {
        var instance = Guid.CreateVersion7();

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

        // Opened through a journal scoped to the tenant, because that is how the host opens
        // one. Opening it unscoped would arrange a row no production path can produce.
        var journal = schema.Journal.ForTenant(tenantId);

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
            Cancellation);

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
