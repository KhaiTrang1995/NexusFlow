using FlowX.Conformance.InMemory;
using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// The outbox publisher at <see cref="TenantIsolation.Schema"/>, against a real database: one
/// loop, every tenant's table, and no tenant able to see or stall another.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this replaces is a refusal.</strong> <c>ADR-0051 §4</c> refused
/// <c>AddFlowXPostgresOutbox</c> at this level on the grounds that a loop which claims rows and
/// advances a position is a different contract once per tenant. It is not: a claim was always
/// confined to one <c>outbox_event</c>, so per-tenant tables make the <c>SKIP LOCKED</c> and the
/// per-key hold answer the same question over less data. What genuinely had to be built is the
/// three things the single-schema loop got for free — the tenant set, the visiting order, and
/// what happens when one tenant's database says no.
/// </para>
/// <para>
/// Every assertion below is made in both directions. An isolation property that holds for the
/// tenant the test arranged first holds by accident.
/// </para>
/// </remarks>
public sealed class TenantSchemaOutboxTests
{
    private const string TenantA = "acme";
    private const string TenantB = "globex";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// An event staged by one tenant is published carrying that tenant, and never appears under
    /// the other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The tenant on the record is the assertion, not decoration.</strong>
    /// <c>outbox_event</c> has no <c>tenant_id</c> column — at this level the tenant <em>is</em>
    /// the schema the row was claimed from — so without
    /// <see cref="OutboxRecord.TenantId"/> a broker draining ten tenants receives one
    /// undifferentiated stream and no consumer downstream can tell whose event it has.
    /// </para>
    /// <para>
    /// A single pass drains both, which is the other half: a fan-out that visited one tenant per
    /// pass would pass the isolation assertion and leave every other tenant a poll interval
    /// behind for each tenant ahead of it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnEventStagedByOneTenantIsPublishedForThatTenantAndNeverForTheOther()
    {
        await using var schema = await PostgresTestSchema.CreateWithTenantSchemasAsync(Cancellation);

        var broker = new RecordingEventPublisher();
        var publisher = new PostgresOutboxPublisher(schema.TenantStores!, broker);

        await StageAsync(schema, TenantA, "order-a", ["order.placed"]);
        await StageAsync(schema, TenantB, "order-b", ["order.shipped"]);

        var pass = await publisher.PublishPendingAsync(Cancellation);

        pass.Failure.ShouldBeNull();
        pass.Published.ShouldBe(
            2,
            "one pass drains every registered tenant. A publisher left pointing at the control " +
            "schema would claim nothing, report success, and publish nothing for ever.");

        var ofA = broker.Delivered.Where(e => e.TenantId == TenantA).ToList();
        var ofB = broker.Delivered.Where(e => e.TenantId == TenantB).ToList();

        ofA.Select(e => e.Type).ShouldBe(["order.placed"]);
        ofB.Select(e => e.Type).ShouldBe(["order.shipped"]);

        broker.Delivered.ShouldAllBe(
            e => e.TenantId == TenantA || e.TenantId == TenantB,
            "every claimed row is tagged with the schema it came out of, because that is the " +
            "only place the tenant exists once the row is read.");

        ofA.ShouldAllBe(
            e => e.Type != "order.shipped",
            "tenant A was credited with tenant B's event, which means the fan-out read B's " +
            "schema through A's pool.");

        ofB.ShouldAllBe(e => e.Type != "order.placed", "and the same the other way.");

        (await Pending(schema, TenantA)).ShouldBe(0L);
        (await Pending(schema, TenantB)).ShouldBe(
            0L, "and each tenant's rows were marked in its own schema's transaction.");
    }

    /// <summary>
    /// Per-<c>partition_key</c> staging order survives the fan-out, in each tenant separately.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The batch is one, which is what makes this an ordering test rather than a
    /// coincidence.</strong> Three events on one key are claimed one at a time, so the only
    /// thing that can produce staging order across three passes is
    /// <c>OutboxSql.ClaimPending</c>'s hold on a key with an older pending sibling — evaluated
    /// inside one tenant's schema, over that tenant's rows and nobody else's.
    /// </para>
    /// <para>
    /// Both tenants use the <em>same</em> key string on purpose. At
    /// <see cref="TenantIsolation.Row"/> they shared a table and the hold serialised them
    /// against each other, which was an accident of colocation rather than a promise; here the
    /// two sequences are independent and both are in order.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task PerPartitionKeyOrderHoldsInsideEachTenantAfterTheFanOut()
    {
        await using var schema = await PostgresTestSchema.CreateWithTenantSchemasAsync(Cancellation);

        var broker = new RecordingEventPublisher();

        var publisher = new PostgresOutboxPublisher(
            schema.TenantStores!, broker, new PostgresOutboxOptions { BatchSize = 1 });

        const string SharedKey = "cart-1";

        await StageAsync(schema, TenantA, SharedKey, ["a.one", "a.two", "a.three"]);
        await StageAsync(schema, TenantB, SharedKey, ["b.one", "b.two", "b.three"]);

        for (var pass = 0; pass < 6; pass++)
        {
            (await publisher.PublishPendingAsync(Cancellation)).Failure.ShouldBeNull();
        }

        Types(broker, TenantA).ShouldBe(
            ["a.one", "a.two", "a.three"],
            "an event was overtaken by its own key's successor inside one tenant's schema, " +
            "which is the guarantee ADR-0018 makes and the fan-out must not spend.");

        Types(broker, TenantB).ShouldBe(["b.one", "b.two", "b.three"]);
    }

    /// <summary>
    /// A tenant whose outbox cannot be reached is reported and stepped over; every other tenant
    /// is still drained on the same pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the property the two sweeps do not have.</strong>
    /// <c>PostgresTenantRecoveryIndex</c> and <c>PostgresTenantTimerIndex</c> return the first
    /// tenant's failure and abandon the rest of the page, which is survivable for a sweep that
    /// runs again in a second. A publisher is a drain: a tenant that fails first in the ordering
    /// would hold every tenant behind it for as long as the outage lasted, and at-least-once
    /// would become at-least-once-eventually-for-whoever-sorts-early.
    /// </para>
    /// <para>
    /// The failure is arranged by dropping one tenant's table rather than by stopping the
    /// server, because a stopped server is not one tenant failing — it is the fan-out having
    /// nothing to fan out to, and the registry read would fail first.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATenantWhoseOutboxCannotBeReachedDoesNotStopTheOthersPublishing()
    {
        await using var schema = await PostgresTestSchema.CreateWithTenantSchemasAsync(Cancellation);

        var broker = new RecordingEventPublisher();
        var publisher = new PostgresOutboxPublisher(schema.TenantStores!, broker);

        await StageAsync(schema, TenantA, "order-a", ["order.placed"]);
        await StageAsync(schema, TenantB, "order-b", ["order.shipped"]);

        await schema.ExecuteAsync("DROP TABLE outbox_event CASCADE", TenantB, Cancellation);

        var pass = await publisher.PublishPendingAsync(Cancellation);

        pass.Published.ShouldBe(
            1,
            "tenant A's event was published on the same pass that met tenant B's broken " +
            "schema. A fan-out that let the exception out would have drained nobody.");

        Types(broker, TenantA).ShouldBe(["order.placed"]);

        pass.Failure.ShouldNotBeNull(
            "and the tenant that failed is reported rather than swallowed — a drain that " +
            "silently skips a tenant is the failure this level exists to remove.");

        pass.Failure!.Message.ShouldContain(
            TenantB, Case.Sensitive, "the refusal must name which tenant stopped draining.");
    }

    /// <summary>
    /// A tenant provisioned after the publisher was built is drained by the next pass.
    /// </summary>
    /// <remarks>
    /// The registry is re-read every pass for exactly this: a tenant this node has never served
    /// may have been provisioned by another node between two polls, and its events are as due as
    /// anybody's. A cached tenant list would leave that tenant's outbox filling until something
    /// restarted the process.
    /// </remarks>
    [Fact]
    public async Task ATenantProvisionedAfterThePublisherWasBuiltIsDrainedByTheNextPass()
    {
        await using var schema = await PostgresTestSchema.CreateWithTenantSchemasAsync(Cancellation);

        var broker = new RecordingEventPublisher();
        var publisher = new PostgresOutboxPublisher(schema.TenantStores!, broker);

        await StageAsync(schema, TenantA, "order-a", ["order.placed"]);

        (await publisher.PublishPendingAsync(Cancellation)).Published.ShouldBe(1);

        // The tenant arrives now — provisioned, migrated and registered by its first journal
        // call, which is what a node meeting a new customer does.
        await StageAsync(schema, TenantB, "order-b", ["order.shipped"]);

        var second = await publisher.PublishPendingAsync(Cancellation);

        second.Failure.ShouldBeNull();
        second.Published.ShouldBe(
            1,
            "the pass read the registry again and found a tenant that did not exist when the " +
            "publisher was constructed.");

        Types(broker, TenantB).ShouldBe(["order.shipped"]);
    }

    /// <summary>
    /// A deployment with no tenants yet publishes nothing and reports no failure.
    /// </summary>
    /// <remarks>
    /// The first minute of every schema-isolated deployment, and the state a polling loop is in
    /// until the first customer arrives. It has to be an empty pass rather than an error,
    /// because an error here would put a line in the log every poll interval for ever.
    /// </remarks>
    [Fact]
    public async Task ADeploymentWithNoTenantsYetIsAnEmptyPassRatherThanAFailure()
    {
        await using var schema = await PostgresTestSchema.CreateWithTenantSchemasAsync(Cancellation);

        var pass = await new PostgresOutboxPublisher(
            schema.TenantStores!, new RecordingEventPublisher()).PublishPendingAsync(Cancellation);

        pass.ShouldBe(OutboxPass.Empty);
    }

    // -----------------------------------------------------------------------------------
    // Fixtures
    // -----------------------------------------------------------------------------------

    /// <summary>The types delivered for one tenant, in delivery order.</summary>
    private static IReadOnlyList<string> Types(RecordingEventPublisher broker, string tenantId) =>
        [.. broker.Delivered.Where(e => e.TenantId == tenantId).Select(static e => e.Type)];

    /// <summary>How many of one tenant's staged events are still pending.</summary>
    private static async Task<object?> Pending(PostgresTestSchema schema, string tenantId)
    {
        var source = await schema.SourceFor(tenantId, Cancellation);

        await using var connection = await source.OpenConnectionAsync(Cancellation);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM outbox_event WHERE published_at IS NULL";

        return await command.ExecuteScalarAsync(Cancellation);
    }

    /// <summary>
    /// Stages events on a fresh instance inside one tenant's schema, in one step commit.
    /// </summary>
    /// <remarks>
    /// Through the tenant-scoped journal rather than raw SQL, for <c>OutboxPublisherTests</c>'s
    /// reason and one more that belongs to this level: writing the row by hand would prove the
    /// publisher can read a schema, not that the journal puts a tenant's events there.
    /// </remarks>
    private static async Task StageAsync(
        PostgresTestSchema schema,
        string tenantId,
        string partitionKey,
        string[] types)
    {
        var instance = Guid.NewGuid();
        var journal = schema.Journal.ForTenant(tenantId);

        var lease = await schema.Leases.AcquireAsync(
            instance, "test-node", TimeSpan.FromMinutes(5), Cancellation);

        lease.IsSuccess.ShouldBeTrue(lease.IsFailure ? lease.Error.ToString() : string.Empty);

        var started = await journal.StartAsync(
            new FlowInstanceStart
            {
                InstanceId = instance,
                FlowId = "order.place",
                FlowVersion = "1.0.0",
                TenantId = tenantId,
                Token = lease.Value.Token,
            },
            Cancellation);

        started.IsSuccess.ShouldBeTrue(started.IsFailure ? started.Error.ToString() : string.Empty);

        var committed = await journal.CommitAsync(
            new StepCommit
            {
                Key = StepKey.First(instance, 0),
                Token = lease.Value.Token,
                CapabilityId = "inventory.reserve",
                CapabilityVersion = "2.1.0",
                Outcome = JournalOutcome.Success,
                Outbox =
                [
                    .. types.Select(type => new OutboxWrite
                    {
                        Type = type,
                        SchemaVersion = "1.0.0",
                        PartitionKey = partitionKey,
                    }),
                ],
            },
            Cancellation);

        committed.IsSuccess.ShouldBeTrue(
            committed.IsFailure ? committed.Error.ToString() : string.Empty);
    }
}
