using FlowX.Conformance;
using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// Row-level tenant isolation against a real database: what a tenant-scoped journal can
/// reach, and — the half that matters — what it cannot.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every assertion here is written in the negative, and that is the point.</strong> A
/// test that tenant <em>A</em> can read <em>A</em>'s instance proves only that the adapter
/// still works; it is true of the unisolated journal this file was written to replace, and it
/// would have passed on every commit since migration <c>0001</c>. What has to be proved is
/// that <em>A</em> cannot reach <em>B</em>, and it is proved in both directions rather than
/// once — an isolation that holds only for the tenant a test happens to arrange first is an
/// isolation that holds by accident.
/// </para>
/// <para>
/// <strong>The refusal comes from the database, not from this process.</strong> The scoped
/// journal assumes a role that does not bypass row-level security and sets
/// <c>flowx.tenant_id</c> on the connection; the policy migration <c>0006</c> installs then
/// decides. So an assertion here fails if the policy is missing, if the role is wrong, if
/// <c>FORCE ROW LEVEL SECURITY</c> was omitted, or if the runtime forgot to set the scope —
/// which is four ways of shipping this defect a second time, all of them caught by the same
/// line.
/// </para>
/// </remarks>
public sealed class TenantIsolationTests
{
    private const string TenantA = "acme";
    private const string TenantB = "globex";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// A journal scoped to one tenant cannot read another tenant's instance row, in either
    /// direction.
    /// </summary>
    /// <remarks>
    /// <see cref="DurabilityErrors.InstanceNotFound"/> rather than a refusal of its own,
    /// because that is what the database's answer honestly is: under the policy the row is not
    /// there. Inventing a distinct "forbidden" here would tell the caller that the instance
    /// exists and belongs to somebody else, which is the information disclosure
    /// <c>docs/15-Security.md §3</c> refuses — and it would require reading the row to say so.
    /// </remarks>
    [Fact]
    public async Task ATenantCannotReadAnotherTenantsInstance()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var ofA = await schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.Zero, TenantA, Cancellation);

        var ofB = await schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.Zero, TenantB, Cancellation);

        var journalOfA = schema.Journal.ForTenant(TenantA);
        var journalOfB = schema.Journal.ForTenant(TenantB);

        var aReadsB = await journalOfA.ReadInstanceAsync(ofB, Cancellation);
        var bReadsA = await journalOfB.ReadInstanceAsync(ofA, Cancellation);

        aReadsB.IsFailure.ShouldBeTrue(
            "tenant A read tenant B's instance row. This is the defect the whole feature " +
            "exists to remove: the id is a guessable handle, and nothing above the database " +
            "was ever going to stop a caller that holds one.");

        bReadsA.IsFailure.ShouldBeTrue(
            "tenant B read tenant A's instance row. Asserted separately from the other " +
            "direction because an isolation that holds one way holds by accident.");

        aReadsB.Error.Code.ShouldBe(DurabilityErrors.InstanceNotFoundCode);
        bReadsA.Error.Code.ShouldBe(DurabilityErrors.InstanceNotFoundCode);

        var aReadsA = await journalOfA.ReadInstanceAsync(ofA, Cancellation);

        aReadsA.IsSuccess.ShouldBeTrue(
            "a tenant must still reach its own instance. An isolation that refuses " +
            "everybody is not an isolation, it is an outage.");
    }

    /// <summary>
    /// A journal scoped to one tenant cannot read another tenant's committed steps, which is
    /// where the payloads actually are.
    /// </summary>
    /// <remarks>
    /// Asserted separately from the instance row because <c>flow_step</c> carries no
    /// <c>tenant_id</c> of its own and is reached by a different policy — one that joins back
    /// to <c>flow_instance</c>. An isolation that covered the row naming the tenant and not the
    /// rows holding the data would protect the label and leak the contents.
    /// </remarks>
    [Fact]
    public async Task ATenantCannotReadAnotherTenantsCommittedSteps()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var ofB = await schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.Zero, TenantB, Cancellation);

        var unscoped = await schema.Journal.ReadResumeFrontierAsync(ofB, Cancellation);

        unscoped.IsSuccess.ShouldBeTrue(
            "the arrangement itself must be sound: tenant B's instance really does have a " +
            "committed step for tenant A to fail to see.");

        unscoped.Value.Committed.ShouldNotBeEmpty();

        var aReadsB = await schema.Journal
            .ForTenant(TenantA)
            .ReadResumeFrontierAsync(ofB, Cancellation);

        aReadsB.IsFailure.ShouldBeTrue(
            "tenant A read the frontier of tenant B's instance — every capability result " +
            "that instance has committed, which is the payload data itself and not merely " +
            "the fact that the instance exists.");
    }

    /// <summary>
    /// A journal scoped to one tenant cannot resume another tenant's instance: the fence it
    /// would raise is refused, so no step is ever reached.
    /// </summary>
    /// <remarks>
    /// <strong>Resuming is the dangerous verb, not reading.</strong> A read returns rows; a
    /// resume runs the flow — real payments, real messages — under a lease this caller now
    /// holds. <c>FenceAsync</c> is the first write a takeover makes, and it failing is what
    /// stops the takeover before <c>ReadResumeFrontierAsync</c> is ever called.
    /// </remarks>
    [Fact]
    public async Task ATenantCannotFenceAnotherTenantsInstanceAndSoCannotResumeIt()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var ofA = await schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.Zero, TenantA, Cancellation);

        var ofB = await schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.Zero, TenantB, Cancellation);

        var aFencesB = await schema.Journal
            .ForTenant(TenantA)
            .FenceAsync(ofB, new FencingToken(99), Cancellation);

        var bFencesA = await schema.Journal
            .ForTenant(TenantB)
            .FenceAsync(ofA, new FencingToken(99), Cancellation);

        aFencesB.IsFailure.ShouldBeTrue(
            "tenant A raised the fence on tenant B's instance, which is the first write of a " +
            "takeover and would have made A the writer of B's flow.");

        bFencesA.IsFailure.ShouldBeTrue(
            "tenant B raised the fence on tenant A's instance. Both directions, because an " +
            "isolation that holds one way holds by accident.");

        aFencesB.Error.Code.ShouldBe(DurabilityErrors.InstanceNotFoundCode);
        bFencesA.Error.Code.ShouldBe(DurabilityErrors.InstanceNotFoundCode);
    }

    /// <summary>
    /// A journal scoped to one tenant cannot append a step to another tenant's instance.
    /// </summary>
    /// <remarks>
    /// The write half. A refused read leaks data; a permitted write corrupts somebody else's
    /// history — it would put a row in tenant B's append-only journal that tenant B did not
    /// cause and cannot account for, at a sequence B's own next commit then collides with.
    /// </remarks>
    [Fact]
    public async Task ATenantCannotCommitAStepToAnotherTenantsInstance()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var ofB = await schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.Zero, TenantB, Cancellation);

        var written = await schema.Journal.ForTenant(TenantA).CommitAsync(
            new StepCommit
            {
                Key = StepKey.First(ofB, 1),
                Token = new FencingToken(1),
                CapabilityId = "payment.capture",
                CapabilityVersion = "1.0.0",
                Outcome = JournalOutcome.Success,
            },
            Cancellation);

        written.IsFailure.ShouldBeTrue(
            "tenant A appended a step to tenant B's instance. An append-only history that " +
            "another tenant can append to is not tenant B's history.");

        var frontier = await schema.Journal.ReadResumeFrontierAsync(ofB, Cancellation);

        frontier.Value.Committed.ShouldAllBe(
            step => step.Key.StepId != 1,
            "the refused commit must have written nothing at all. A refusal that still left " +
            "the row behind would be the worse half of the same defect.");
    }

    /// <summary>
    /// A scoped connection returned to the pool does not carry its tenant into the next
    /// borrower.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The failure mode this pins is invisible and total.</strong> The scope is two
    /// session settings on a pooled physical connection. If one survived the return to the
    /// pool, the next unscoped borrower — the recovery scan, the outbox publisher, the
    /// retention sweeper — would silently inherit it and quietly stop seeing most of the
    /// table, or see one tenant's rows while believing itself node-wide.
    /// </para>
    /// <para>
    /// The arrangement forces the reuse rather than hoping for it: a scoped read is performed
    /// first, so its connection is returned dirty, and the unscoped read that follows draws
    /// from a pool whose only idle connection is that one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AScopedConnectionDoesNotCarryItsTenantBackIntoThePool()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var ofA = await schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.Zero, TenantA, Cancellation);

        var ofB = await schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.Zero, TenantB, Cancellation);

        await schema.Journal.ForTenant(TenantA).ReadInstanceAsync(ofA, Cancellation);

        var unscopedA = await schema.Journal.ReadInstanceAsync(ofA, Cancellation);
        var unscopedB = await schema.Journal.ReadInstanceAsync(ofB, Cancellation);

        unscopedA.IsSuccess.ShouldBeTrue(
            "the unscoped journal reaches every row in the schema, and a connection that had " +
            "been scoped and released must be indistinguishable from one that never was.");

        unscopedB.IsSuccess.ShouldBeTrue(
            "tenant B's row is the one that proves it: had the previous scope survived the " +
            "return to the pool, this read would see nothing while reporting no error.");
    }

    /// <summary>
    /// A recovery scan restricted to one tenant is offered no candidate belonging to another,
    /// in either direction.
    /// </summary>
    /// <remarks>
    /// <c>AbandonedInstanceQuery.TenantId</c> has existed since the index was written and
    /// nothing in the runtime ever set it — the gap <c>docs/16</c>'s warning box names as "the
    /// runtime never passes the tenant filter the store would accept". This is the store half
    /// of closing it; <c>FlowRecoveryScan</c> passing the candidate's tenant to the resume is
    /// the other.
    /// </remarks>
    [Fact]
    public async Task ARecoveryScanScopedToOneTenantSeesNoOtherTenantsInstances()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var ofA = await schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.FromHours(1), TenantA, Cancellation);

        var ofB = await schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.FromHours(1), TenantB, Cancellation);

        var forA = await ScanAsync(schema, TenantA);
        var forB = await ScanAsync(schema, TenantB);

        forA.ShouldContain(ofA);
        forA.ShouldNotContain(
            ofB,
            "a recovery scan scoped to tenant A offered tenant B's instance as a candidate. " +
            "The scan would then take a lease on it and resume somebody else's flow.");

        forB.ShouldContain(ofB);
        forB.ShouldNotContain(
            ofA,
            "and the same the other way, which is the direction a single-tenant arrangement " +
            "would never have exercised.");
    }

    /// <summary>
    /// A timer sweep restricted to one tenant is offered no due instance belonging to another,
    /// in either direction.
    /// </summary>
    /// <remarks>
    /// Asserted separately from the recovery scan rather than assumed to follow from it: the
    /// two are different queries over disjoint sets of rows, served by different partial
    /// indexes, and <c>PostgresTimerIndex</c> composes its tenant predicate in its own
    /// statement. One of them being scoped says nothing about the other.
    /// </remarks>
    [Fact]
    public async Task ATimerSweepScopedToOneTenantSeesNoOtherTenantsDueInstances()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var ofA = await ParkAsync(schema, TenantA);
        var ofB = await ParkAsync(schema, TenantB);

        var forA = await SweepAsync(schema, TenantA);
        var forB = await SweepAsync(schema, TenantB);

        forA.ShouldContain(ofA);
        forA.ShouldNotContain(
            ofB,
            "a timer sweep scoped to tenant A found tenant B's parked instance due. It would " +
            "then wake somebody else's flow onto this node.");

        forB.ShouldContain(ofB);
        forB.ShouldNotContain(ofA, "and the same the other way.");
    }

    /// <summary>Which instances a recovery scan for one tenant is offered.</summary>
    private static async Task<IReadOnlyList<Guid>> ScanAsync(
        PostgresTestSchema schema,
        string tenantId)
    {
        var listed = await schema.RecoveryIndex.ListAbandonedAsync(
            new AbandonedInstanceQuery
            {
                IdleBefore = DateTimeOffset.UtcNow.AddMinutes(-1),
                TenantId = tenantId,
            },
            Cancellation);

        return [.. listed.Value.Select(candidate => candidate.InstanceId)];
    }

    /// <summary>Which instances a timer sweep for one tenant is offered.</summary>
    private static async Task<IReadOnlyList<Guid>> SweepAsync(
        PostgresTestSchema schema,
        string tenantId)
    {
        var listed = await new PostgresTimerIndex(schema.DataSource).ListDueAsync(
            new DueInstanceQuery
            {
                DueBefore = DateTimeOffset.UtcNow.AddMinutes(1),
                TenantId = tenantId,
            },
            Cancellation);

        return [.. listed.Value.Select(candidate => candidate.InstanceId)];
    }

    /// <summary>Starts an instance for a tenant and parks it at a wait that is already due.</summary>
    private static async Task<Guid> ParkAsync(PostgresTestSchema schema, string tenantId)
    {
        var instance = Guid.CreateVersion7();

        await schema.Journal.StartAsync(
            new FlowInstanceStart
            {
                InstanceId = instance,
                FlowId = "offer.accept",
                FlowVersion = "1.0.0",
                TenantId = tenantId,
                Token = new FencingToken(1),
            },
            Cancellation);

        await schema.Journal.CompleteAsync(
            instance,
            new FencingToken(1),
            FlowInstanceState.Suspended,
            JournalPayload.Empty,
            new FlowWake(StepScope.Root, 0, DateTimeOffset.UtcNow.AddSeconds(-30)),
            Cancellation);

        return instance;
    }
}
