using FlowX.Conformance;
using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// Schema-per-tenant isolation against a real database: what a tenant's journal reaches, what
/// it cannot, and what the two node-wide sweeps see once the rows stop being in one table.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every assertion is in the negative and every one is made in both directions</strong>,
/// for <see cref="TenantIsolationTests"/>'s reason: an isolation that holds only for the tenant
/// a test happens to arrange first is an isolation that holds by accident.
/// </para>
/// <para>
/// <strong>What is different here is where the failure would come from.</strong> At row level a
/// leak is a policy that did not apply. At schema level it is a connection that pointed
/// somewhere else — which is not a refusal but a successful read of the wrong rows, and it would
/// arrive through a pool rather than through a query. So the pooling assertion below is not a
/// detail of this file; it is the property the level rests on.
/// </para>
/// </remarks>
public sealed class TenantSchemaIsolationTests
{
    private const string TenantA = "acme";
    private const string TenantB = "globex";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// Each tenant's rows are in a schema of that tenant's own, and neither schema is the
    /// control schema.
    /// </summary>
    /// <remarks>
    /// The premise everything else in this file rests on, asserted rather than assumed. Were
    /// both tenants writing into one schema the level would be row isolation under another
    /// name — and every negative assertion below would still pass, because row isolation also
    /// refuses.
    /// </remarks>
    [Fact]
    public async Task EachTenantsRowsAreInASchemaOfItsOwn()
    {
        await using var schema = await PostgresTestSchema.CreateWithTenantSchemasAsync(Cancellation);

        await schema.AbandonAsync(FlowInstanceState.Running, TimeSpan.Zero, TenantA, Cancellation);
        await schema.AbandonAsync(FlowInstanceState.Running, TimeSpan.Zero, TenantB, Cancellation);

        var stores = schema.TenantStores.ShouldNotBeNull();

        stores.SchemaFor(TenantA).ShouldNotBe(
            stores.SchemaFor(TenantB),
            "two tenants sharing a schema share every row in it, and no policy below would " +
            "notice: each connection would be reading what its own connection string pointed at.");

        (await CountAsync(schema, TenantA)).ShouldBe(1);
        (await CountAsync(schema, TenantB)).ShouldBe(1);

        (await ScalarAsync(schema, "SELECT count(*) FROM flow_instance")).ShouldBe(
            0L,
            "the control schema holds the leases, the ledger and the tenant registry, and no " +
            "instance at all. A row that landed there would be one no tenant's journal reads.");
    }

    /// <summary>
    /// A journal scoped to one tenant cannot read another tenant's instance row, in either
    /// direction.
    /// </summary>
    /// <remarks>
    /// Refused twice over, and the two refusals are independent. The connection resolves to a
    /// schema that does not hold the row at all; and were the schema somehow right, the row's
    /// <c>tenant_id</c> is somebody else's and migration <c>0008</c>'s policy — which is live
    /// inside every tenant schema, because every tenant schema is migrated — hides it.
    /// </remarks>
    [Fact]
    public async Task ATenantCannotReadAnotherTenantsInstance()
    {
        await using var schema = await PostgresTestSchema.CreateWithTenantSchemasAsync(Cancellation);

        var ofA = await schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.Zero, TenantA, Cancellation);

        var ofB = await schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.Zero, TenantB, Cancellation);

        var journalOfA = schema.Journal.ForTenant(TenantA);
        var journalOfB = schema.Journal.ForTenant(TenantB);

        var aReadsB = await journalOfA.ReadInstanceAsync(ofB, Cancellation);
        var bReadsA = await journalOfB.ReadInstanceAsync(ofA, Cancellation);

        aReadsB.IsFailure.ShouldBeTrue("tenant A read tenant B's instance row.");
        bReadsA.IsFailure.ShouldBeTrue("tenant B read tenant A's instance row.");

        aReadsB.Error.Code.ShouldBe(DurabilityErrors.InstanceNotFoundCode);
        bReadsA.Error.Code.ShouldBe(DurabilityErrors.InstanceNotFoundCode);

        (await journalOfA.ReadInstanceAsync(ofA, Cancellation)).IsSuccess.ShouldBeTrue(
            "a tenant must still reach its own instance. An isolation that refuses everybody " +
            "is not an isolation, it is an outage.");

        (await journalOfB.ReadInstanceAsync(ofB, Cancellation)).IsSuccess.ShouldBeTrue(
            "and the same the other way.");
    }

    /// <summary>
    /// A journal scoped to one tenant cannot read another tenant's committed steps, which is
    /// where the payloads actually are.
    /// </summary>
    /// <remarks>
    /// Asserted separately from the instance row because <c>flow_step</c> is reached through a
    /// foreign key rather than through a tenant column. An isolation that covered the row naming
    /// the tenant and not the rows holding the data would protect the label and leak the
    /// contents.
    /// </remarks>
    [Fact]
    public async Task ATenantCannotReadAnotherTenantsCommittedSteps()
    {
        await using var schema = await PostgresTestSchema.CreateWithTenantSchemasAsync(Cancellation);

        var ofA = await schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.Zero, TenantA, Cancellation);

        var ofB = await schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.Zero, TenantB, Cancellation);

        var ownFrontier = await schema.Journal
            .ForTenant(TenantB)
            .ReadResumeFrontierAsync(ofB, Cancellation);

        ownFrontier.IsSuccess.ShouldBeTrue(
            "the arrangement itself must be sound: tenant B's instance really does have a " +
            "committed step for tenant A to fail to see.");

        ownFrontier.Value.Committed.ShouldNotBeEmpty();

        var aReadsB = await schema.Journal
            .ForTenant(TenantA)
            .ReadResumeFrontierAsync(ofB, Cancellation);

        var bReadsA = await schema.Journal
            .ForTenant(TenantB)
            .ReadResumeFrontierAsync(ofA, Cancellation);

        aReadsB.IsFailure.ShouldBeTrue(
            "tenant A read the frontier of tenant B's instance — every capability result that " +
            "instance has committed, which is the payload data rather than merely the fact " +
            "that the instance exists.");

        bReadsA.IsFailure.ShouldBeTrue("and the same the other way.");
    }

    /// <summary>
    /// A tenant cannot commit a step onto another tenant's instance, in either direction.
    /// </summary>
    /// <remarks>
    /// The write half. A history another tenant can append to is not this tenant's history, and
    /// the refusal has to leave nothing behind — a refused commit that still wrote the row would
    /// be the worse half of the same defect.
    /// </remarks>
    [Fact]
    public async Task ATenantCannotCommitOntoAnotherTenantsInstance()
    {
        await using var schema = await PostgresTestSchema.CreateWithTenantSchemasAsync(Cancellation);

        var ofA = await schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.Zero, TenantA, Cancellation);

        var ofB = await schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.Zero, TenantB, Cancellation);

        var aOntoB = await CommitAsync(schema, TenantA, ofB);
        var bOntoA = await CommitAsync(schema, TenantB, ofA);

        aOntoB.IsFailure.ShouldBeTrue("tenant A appended a step to tenant B's instance.");
        bOntoA.IsFailure.ShouldBeTrue("and the same the other way.");

        var frontier = await schema.Journal.ForTenant(TenantB).ReadResumeFrontierAsync(ofB, Cancellation);

        frontier.Value.Committed.ShouldAllBe(
            step => step.Key.StepId != 7,
            "the refused commit must have written nothing at all.");
    }

    /// <summary>
    /// A pooled connection never carries one tenant's schema to the next borrower — because no
    /// connection is ever borrowed by two tenants.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the assertion the level rests on.</strong> A stale <c>flowx.tenant_id</c>
    /// makes the next borrower see nothing, which is loud. A stale <c>search_path</c> makes it
    /// see a full table of somebody else's rows, which is not — so the arrangement here forces
    /// the reuse rather than hoping for it: tenant A reads first so that a connection is
    /// returned to a pool warm, and tenant B reads immediately afterwards.
    /// </para>
    /// <para>
    /// The pools are disjoint, so B cannot draw the connection A returned; what this pins is
    /// that they really are disjoint, which is a property of how
    /// <c>PostgresTenantStores</c> builds them and would be quietly lost by anyone who
    /// "simplified" it into one data source with a <c>SET search_path</c> per borrow.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task APooledConnectionNeverCarriesATenantsSchemaToTheNextBorrower()
    {
        await using var schema = await PostgresTestSchema.CreateWithTenantSchemasAsync(Cancellation);

        var ofA = await schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.Zero, TenantA, Cancellation);

        var ofB = await schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.Zero, TenantB, Cancellation);

        var journalOfA = schema.Journal.ForTenant(TenantA);
        var journalOfB = schema.Journal.ForTenant(TenantB);

        for (var round = 0; round < 4; round++)
        {
            (await journalOfA.ReadInstanceAsync(ofA, Cancellation)).IsSuccess.ShouldBeTrue(
                "tenant A must reach its own row on a warm pool as well as a cold one.");

            (await journalOfB.ReadInstanceAsync(ofA, Cancellation)).IsFailure.ShouldBeTrue(
                "tenant B borrowed immediately after tenant A and reached tenant A's row. The " +
                "schema travelled on a pooled connection, which is the failure this level " +
                "exists to make impossible rather than unlikely.");

            (await journalOfB.ReadInstanceAsync(ofB, Cancellation)).IsSuccess.ShouldBeTrue(
                "and tenant B must still reach its own.");

            (await journalOfA.ReadInstanceAsync(ofB, Cancellation)).IsFailure.ShouldBeTrue(
                "and the same the other way, alternating, so that neither order is the one " +
                "that happens to work.");
        }

        (await schema.Journal.ReadInstanceAsync(ofA, Cancellation)).IsFailure.ShouldBeTrue(
            "the unscoped journal reads the control schema, which holds no instances at all. " +
            "Had it inherited a tenant's search_path it would have found one.");

        // And the same property asked of the connection directly rather than through a read,
        // because a read can be refused for the right answer by the wrong mechanism: the row
        // policy would hide another tenant's rows even from a connection that had wandered into
        // their schema. current_schema() is the only thing that distinguishes the two.
        var stores = schema.TenantStores.ShouldNotBeNull();

        for (var round = 0; round < 4; round++)
        {
            (await CurrentSchemaAsync(schema, TenantA)).ShouldBe(stores.SchemaFor(TenantA));
            (await CurrentSchemaAsync(schema, TenantB)).ShouldBe(
                stores.SchemaFor(TenantB),
                "a connection borrowed for tenant B immediately after one was returned for " +
                "tenant A resolved to tenant A's schema. Every read through it would have " +
                "succeeded, against the wrong rows.");
            (await CurrentSchemaAsync(schema, tenantId: null)).ShouldBe(
                schema.Options.Schema,
                "and the control pool, which the sweeps and the lease store share, must never " +
                "acquire a tenant's schema at all.");
        }
    }

    /// <summary>
    /// A recovery scan finds every tenant's abandoned instances, and a scan scoped to one tenant
    /// finds only that tenant's — in either direction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The fan-out is asserted first because its absence is silent.</strong> Under schema
    /// isolation the control schema's <c>flow_instance</c> is empty, so a sweep that had not been
    /// fanned out would find nothing, report nothing and leave every abandoned instance in the
    /// deployment stranded — a green suite and a platform that never recovers.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARecoveryScanReachesEveryTenantAndNoTenantReachesAnother()
    {
        await using var schema = await PostgresTestSchema.CreateWithTenantSchemasAsync(Cancellation);

        var ofA = await schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.FromHours(1), TenantA, Cancellation);

        var ofB = await schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.FromHours(1), TenantB, Cancellation);

        var everyone = await ScanAsync(schema, tenantId: null);

        everyone.ShouldContain(ofA);
        everyone.ShouldContain(
            ofB,
            "a node-wide recovery scan must reach every tenant's schema. One that reached only " +
            "the control schema would report an empty backlog for ever.");

        var forA = await ScanAsync(schema, TenantA);
        var forB = await ScanAsync(schema, TenantB);

        forA.ShouldContain(ofA);
        forA.ShouldNotContain(
            ofB,
            "a recovery scan scoped to tenant A offered tenant B's instance as a candidate. It " +
            "would then take a lease on it and resume somebody else's flow.");

        forB.ShouldContain(ofB);
        forB.ShouldNotContain(ofA, "and the same the other way.");
    }

    /// <summary>
    /// A timer sweep finds every tenant's due instances, and a sweep scoped to one tenant finds
    /// only that tenant's — in either direction.
    /// </summary>
    /// <remarks>
    /// Asserted separately from the recovery scan rather than assumed to follow from it: they
    /// are two queries over disjoint sets of rows served by different partial indexes, fanned
    /// out by two different classes. One of them being right says nothing about the other.
    /// </remarks>
    [Fact]
    public async Task ATimerSweepReachesEveryTenantAndNoTenantReachesAnother()
    {
        await using var schema = await PostgresTestSchema.CreateWithTenantSchemasAsync(Cancellation);

        var ofA = await ParkAsync(schema, TenantA);
        var ofB = await ParkAsync(schema, TenantB);

        var everyone = await SweepAsync(schema, tenantId: null);

        everyone.ShouldContain(ofA);
        everyone.ShouldContain(
            ofB,
            "a node-wide timer sweep must reach every tenant's schema. One that reached only " +
            "the control schema would leave every parked instance in the deployment asleep.");

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

    /// <summary>
    /// A tenant nobody provisioned has its schema created and migrated the first time it is
    /// used, and is then visible to the sweeps.
    /// </summary>
    /// <remarks>
    /// <strong>This is the migration answer, asserted rather than described.</strong> A tenant
    /// arriving at run time is the ordinary case for a SaaS platform, so the alternative to
    /// provisioning here is a level that cannot be used without a control plane this repository
    /// does not ship. What the assertion pins is the second half, which is easier to get wrong:
    /// a schema that is created but not registered is a schema the node-wide sweeps never visit,
    /// and that failure is invisible until an instance in it is abandoned.
    /// </remarks>
    [Fact]
    public async Task ATenantSeenForTheFirstTimeIsProvisionedAndThenSwept()
    {
        await using var schema = await PostgresTestSchema.CreateWithTenantSchemasAsync(Cancellation);

        var stores = schema.TenantStores.ShouldNotBeNull();

        (await stores.KnownTenantsAsync(Cancellation)).ShouldBeEmpty(
            "nothing has been provisioned yet, and the registry must not invent a tenant.");

        var arrival = "newcomer-" + Guid.NewGuid().ToString("n")[..8];

        var instance = await schema.AbandonAsync(
            FlowInstanceState.Running, TimeSpan.FromHours(1), arrival, Cancellation);

        (await stores.KnownTenantsAsync(Cancellation)).ShouldContain(
            arrival,
            "the tenant's schema was created and migrated, and the registry is what makes it " +
            "reachable by a node-wide sweep. A schema nobody recorded is a tenant whose " +
            "abandoned work no node ever finds.");

        (await ScanAsync(schema, tenantId: null)).ShouldContain(
            instance,
            "and the sweep must actually reach it, which is the only thing the registry is for.");

        (await schema.Journal.ForTenant(arrival).ReadInstanceAsync(instance, Cancellation))
            .IsSuccess.ShouldBeTrue("the migrated schema must be usable, not merely present.");
    }

    /// <summary>
    /// Two tenants whose ids clean up to the same identifier still get two schemas.
    /// </summary>
    /// <remarks>
    /// A collision here is not a bug that returns an error — it is two tenants sharing every row
    /// they own, with each connection reading exactly what its own connection string pointed at
    /// and no policy anywhere with a reason to object. Which is why the derived name carries a
    /// digest of the tenant id and not merely a sanitised copy of it.
    /// </remarks>
    [Theory]
    [InlineData("acme", "Acme")]
    [InlineData("acme", "a-c-m-e")]
    [InlineData("acme/1", "acme:1")]
    [InlineData("tenant-with-a-very-long-identifier-one", "tenant-with-a-very-long-identifier-two")]
    public async Task TenantsThatSlugToTheSameNameStillGetTwoSchemas(string left, string right)
    {
        await using var schema = await PostgresTestSchema.CreateWithTenantSchemasAsync(Cancellation);

        var stores = schema.TenantStores.ShouldNotBeNull();

        var ofLeft = stores.SchemaFor(left);
        var ofRight = stores.SchemaFor(right);

        ofLeft.ShouldNotBe(ofRight, $"'{left}' and '{right}' were given the same schema.");

        ofLeft.Length.ShouldBeLessThanOrEqualTo(
            63,
            "PostgreSQL truncates a longer identifier silently, which would collapse two " +
            "schemas into one at exactly the length where nobody is looking.");

        ofRight.Length.ShouldBeLessThanOrEqualTo(63);
    }

    /// <summary>Which instances a recovery scan is offered.</summary>
    private static async Task<IReadOnlyList<Guid>> ScanAsync(
        PostgresTestSchema schema,
        string? tenantId)
    {
        var listed = await schema.RecoveryScan.ListAbandonedAsync(
            new AbandonedInstanceQuery
            {
                IdleBefore = DateTimeOffset.UtcNow.AddMinutes(-1),
                TenantId = tenantId,
            },
            Cancellation);

        listed.IsSuccess.ShouldBeTrue(listed.IsFailure ? listed.Error.ToString() : string.Empty);

        return [.. listed.Value.Select(candidate => candidate.InstanceId)];
    }

    /// <summary>Which instances a timer sweep is offered.</summary>
    private static async Task<IReadOnlyList<Guid>> SweepAsync(
        PostgresTestSchema schema,
        string? tenantId)
    {
        var listed = await schema.TimerSweep.ListDueAsync(
            new DueInstanceQuery
            {
                DueBefore = DateTimeOffset.UtcNow.AddMinutes(1),
                TenantId = tenantId,
            },
            Cancellation);

        listed.IsSuccess.ShouldBeTrue(listed.IsFailure ? listed.Error.ToString() : string.Empty);

        return [.. listed.Value.Select(candidate => candidate.InstanceId)];
    }

    /// <summary>Starts an instance for a tenant and parks it at a wait that is already due.</summary>
    private static async Task<Guid> ParkAsync(PostgresTestSchema schema, string tenantId)
    {
        var instance = Guid.CreateVersion7();
        var journal = schema.Writer(tenantId);

        var started = await journal.StartAsync(
            new FlowInstanceStart
            {
                InstanceId = instance,
                FlowId = "offer.accept",
                FlowVersion = "1.0.0",
                TenantId = tenantId,
                Token = new FencingToken(1),
            },
            Cancellation);

        started.IsSuccess.ShouldBeTrue(started.IsFailure ? started.Error.ToString() : string.Empty);

        var parked = await journal.CompleteAsync(
            instance,
            new FencingToken(1),
            FlowInstanceState.Suspended,
            JournalPayload.Empty,
            new FlowWake(StepScope.Root, 0, DateTimeOffset.UtcNow.AddSeconds(-30)),
            Cancellation);

        parked.IsSuccess.ShouldBeTrue(parked.IsFailure ? parked.Error.ToString() : string.Empty);

        return instance;
    }

    /// <summary>One tenant's attempt to append a step to an instance.</summary>
    private static ValueTask<Result<JournalStep>> CommitAsync(
        PostgresTestSchema schema,
        string tenantId,
        Guid instance) =>
        schema.Journal.ForTenant(tenantId).CommitAsync(
            new StepCommit
            {
                Key = StepKey.First(instance, 7),
                Token = new FencingToken(1),
                CapabilityId = "order.validate",
                CapabilityVersion = "1.0.0",
                Outcome = JournalOutcome.Success,
                State = FlowInstanceState.Running,
            },
            Cancellation);

    /// <summary>Which schema a connection borrowed for this tenant actually resolves to.</summary>
    private static async Task<string?> CurrentSchemaAsync(
        PostgresTestSchema schema,
        string? tenantId)
    {
        var source = await schema.SourceFor(tenantId, Cancellation);

        await using var connection = await source.OpenConnectionAsync(Cancellation);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT current_schema()";

        return (string?)await command.ExecuteScalarAsync(Cancellation);
    }

    /// <summary>How many instance rows are in one tenant's schema.</summary>
    private static async Task<long> CountAsync(PostgresTestSchema schema, string tenantId)
    {
        var source = await schema.SourceFor(tenantId, Cancellation);

        await using var connection = await source.OpenConnectionAsync(Cancellation);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM flow_instance";

        return (long)(await command.ExecuteScalarAsync(Cancellation))!;
    }

    /// <summary>Reads one value from the control schema.</summary>
    private static async Task<object?> ScalarAsync(PostgresTestSchema schema, string sql) =>
        await schema.ScalarAsync(sql, Cancellation);
}
