using FlowX.Hosting;
using FlowX.Runtime;
using Npgsql;
using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// The three triggers that have no caller, starting flows in the right tenant against a real
/// database — and holding their work when they cannot name one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Before this, a tenanted deployment had exactly one usable trigger.</strong> Only HTTP
/// carries a caller, so only HTTP produced a principal; a change, a message and a cron occurrence
/// each reached <c>ClaimTenantResolver</c> with none and were refused with <c>tenant.required</c>
/// — at <see cref="TenantIsolation.Row"/>, which shipped, as much as at
/// <see cref="TenantIsolation.Schema"/>. What each of them does know is asserted here: a change
/// knows the schema and the instance row it was read from, a message knows the field its
/// publisher wrote, and a schedule knows the tenant it was fanned out for.
/// </para>
/// <para>
/// <strong>Both halves of isolation are asserted, and the second is the one that matters.</strong>
/// "Started in tenant A" is satisfied by a trigger that starts the flow in every tenant, so every
/// test here also names what must <em>not</em> have happened in tenant B.
/// </para>
/// </remarks>
public sealed class TenantedTriggerTests
{
    private const string TenantA = "acme";
    private const string TenantB = "globex";
    private const string Source = "order.placed";
    private const string Group = "projection";

    private static readonly CapabilityDescriptor Project =
        CapabilityDescriptor.Create("orders.project", "1.0.0", isIdempotent: true);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    // -----------------------------------------------------------------------------------
    // Change
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// A change staged in one tenant starts the observing flow in that tenant and in no other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The tenant comes from the emitting instance's row, which is the same key the
    /// table's own policy decides visibility through.</strong> <c>outbox_event</c> carries no
    /// <c>tenant_id</c> and 0008 says why; the feed joins <c>flow_instance</c> and reads it there,
    /// so "whose change is this" and "who may see this change" are one fact.
    /// </para>
    /// <para>
    /// At <see cref="TenantIsolation.Schema"/> the feed additionally fans out over the registry,
    /// and the assertion that the flow landed in the right schema is what proves the fan-out
    /// committed the right tenant's cursor as well as reading the right tenant's table.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AChangeStartsItsFlowInTheTenantItWasStagedIn(bool tenantSchemas)
    {
        await using var schema = await SchemaFor(tenantSchemas);

        var emitter = await schema.StageableInstanceAsync(TenantA, Cancellation);

        await schema.StageAsync(emitter, Source, "{}", TenantA, Cancellation);

        var fixture = ChangeFixture(schema, tenantSchemas);

        await AwaitVisibleAsync(fixture.Feed, Cancellation);

        var pass = await fixture.PassAsync();

        pass.Error.ShouldBeNull();
        pass.Started.ShouldBe(1, "the change named a tenant, so admission had one to resolve.");
        pass.Held.ShouldBe(0);

        (await ObservingInstancesAsync(schema, TenantA)).ShouldHaveSingleItem().ShouldBe(TenantA);

        (await ObservingInstancesAsync(schema, TenantB)).ShouldBeEmpty(
            "and nothing was started in the tenant the change did not come from. A trigger " +
            "that fired for every tenant would satisfy the assertion above and be the whole " +
            "isolation failure.");
    }

    /// <summary>
    /// A change whose emitting instance names no tenant leaves the cursor exactly where it was.
    /// </summary>
    /// <remarks>
    /// <strong>The cursor is the assertion, not the refusal.</strong> Refusing to start an
    /// untenanted flow on an isolating deployment was always right; committing the cursor
    /// afterwards is what turned it into silent data loss, and it is what stopped
    /// <c>AddFlowXPostgresChangeFeed</c> being allowed to fan out. The position is read out of
    /// <c>change_cursor</c> rather than off the scan's report, because the report is what the
    /// bug agreed with.
    /// </remarks>
    [Fact]
    public async Task AnUntenantedChangeDoesNotMoveTheCursor()
    {
        await using var schema = await SchemaFor(tenantSchemas: false);

        var emitter = await schema.StageableInstanceAsync(tenantId: null, Cancellation);

        await schema.StageAsync(emitter, Source, "{}", tenantId: null, Cancellation);

        var fixture = ChangeFixture(schema, tenantSchemas: false);

        await AwaitVisibleAsync(fixture.Feed, Cancellation);

        var before = await CursorRowsAsync(schema);

        var pass = await fixture.PassAsync();

        pass.Observed.ShouldBe(1, "the feed offered it; admission is what refused it.");
        pass.Started.ShouldBe(0);
        pass.Held.ShouldBe(1);

        (await ObservingInstancesAsync(schema, TenantA)).ShouldBeEmpty();

        (await CursorRowsAsync(schema)).ShouldBe(
            before,
            "and change_cursor is untouched. A committed cursor over a change no flow ran " +
            "loses the work with nothing anywhere recording that it did.");
    }

    // -----------------------------------------------------------------------------------
    // Bus
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// A claimed outbox row carries the tenant of the instance that emitted it, which is what a
    /// publisher writes onto the wire for a consumer to start a flow with.
    /// </summary>
    /// <remarks>
    /// <strong>Row isolation is the case worth having.</strong> At schema isolation the publisher
    /// already knew the tenant from the schema it claimed in; at row isolation it knew nothing at
    /// all, so a published message reached its consumer with no tenant and the consuming flow
    /// could not be admitted. The claim now joins the emitting instance, which is the only place
    /// the answer exists.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AClaimedEventCarriesTheTenantOfTheInstanceThatEmittedIt(bool tenantSchemas)
    {
        await using var schema = await SchemaFor(tenantSchemas);

        var emitter = await schema.StageableInstanceAsync(TenantA, Cancellation);

        await schema.StageAsync(emitter, Source, "{}", TenantA, Cancellation);

        var broker = new RecordingPublisher();
        var publisher = tenantSchemas
            ? new PostgresOutboxPublisher(schema.TenantStores!, broker)
            : new PostgresOutboxPublisher(schema.DataSource, broker);

        var pass = await publisher.PublishPendingAsync(Cancellation);

        pass.Failure.ShouldBeNull();
        pass.Published.ShouldBe(1);

        broker.Batch.ShouldHaveSingleItem().TenantId.ShouldBe(
            TenantA,
            "the tenant travels beside the body so a consumer can start the flow in it — never " +
            "inside the payload, which is what docs/16 §3 forbids.");
    }

    // -----------------------------------------------------------------------------------
    // Schedule
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// One occurrence of a per-tenant schedule fires once for each tenant, under an id of that
    /// tenant's own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The tenant is a term of the derived instance id, and without it the fan-out
    /// cannot work at all.</strong> Ten tenants sharing one id would have the first tenant's
    /// journal row refuse the other nine through the primary key that exists to refuse a second
    /// <em>node</em> — the mechanism working perfectly on the wrong subject.
    /// </para>
    /// <para>
    /// The directory is the deployment's declared list at row isolation and
    /// <c>tenant_schema</c> at schema isolation, and the sweep does not know which it got.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APerTenantOccurrenceFiresOncePerTenant(bool tenantSchemas)
    {
        await using var schema = await SchemaFor(tenantSchemas);

        if (tenantSchemas)
        {
            // Provisioning is what puts a tenant in the registry the directory reads.
            await schema.SourceFor(TenantA, Cancellation);
            await schema.SourceFor(TenantB, Cancellation);
        }

        var fixture = ScheduleFixture(schema, tenantSchemas, perTenant: true);

        var report = await fixture.SweepAsync();

        report.Fired.ShouldBe(2, "one firing per tenant, from one occurrence.");

        foreach (var tenant in new[] { TenantA, TenantB })
        {
            var expected = fixture.Schedule.InstanceIdFor(Occurrence, tenant);

            (await ScheduledInstancesAsync(schema, tenant)).ShouldHaveSingleItem().ShouldBe(
                expected,
                $"tenant {tenant}'s firing is under the id every node derives for it, with the " +
                "tenant among the terms.");
        }
    }

    /// <summary>
    /// A schedule that is not per-tenant fires nothing on a deployment that isolates, and writes
    /// no row in any tenant.
    /// </summary>
    /// <remarks>
    /// <strong>Refused rather than fired untenanted.</strong> There is no tenant such an
    /// occurrence could name, and admitting it would write an instance row no tenant can read
    /// back — the row-with-no-owner that row-level security exists to make unreachable. The
    /// repair is <c>PerTenant = true</c> on the declaration, which is why the failure is counted
    /// rather than swallowed.
    /// </remarks>
    [Fact]
    public async Task AScheduleThatNamesNoTenantFiresNothingWhereTheDeploymentIsolates()
    {
        await using var schema = await SchemaFor(tenantSchemas: false);

        var fixture = ScheduleFixture(schema, tenantSchemas: false, perTenant: false);

        var report = await fixture.SweepAsync();

        report.Due.ShouldBe(1, "the occurrence fell due; admission is what refused it.");
        report.Fired.ShouldBe(0);

        report.Failed.ShouldBe(
            1,
            "and it is counted. A refusal reported as Fired would put the sweep's healthiest " +
            "number on a schedule that has never run once.");

        (await ScheduledInstancesAsync(schema, TenantA)).ShouldBeEmpty();
        (await ScheduledInstancesAsync(schema, tenantId: null)).ShouldBeEmpty(
            "and no untenanted row either, which is the row nothing could ever read back.");
    }

    // -----------------------------------------------------------------------------------
    // Fixtures
    // -----------------------------------------------------------------------------------

    /// <summary>The occurrence every schedule test here fires: midnight, the day before now.</summary>
    private static readonly DateTimeOffset Occurrence =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ValueTask<PostgresTestSchema> SchemaFor(bool tenantSchemas) => tenantSchemas
        ? PostgresTestSchema.CreateWithTenantSchemasAsync(Cancellation)
        : PostgresTestSchema.CreateAsync(Cancellation);

    private static FlowXOptions OptionsFor(bool tenantSchemas) => new()
    {
        ApplicationName = "Tests",
        NodeName = "node-1",
        ShutdownDrainTimeout = TimeSpan.FromSeconds(5),
        TenantIsolation = tenantSchemas ? TenantIsolation.Schema : TenantIsolation.Row,
    };

    private static ExecutionPlan Plan() => ExecutionPlan.Create(
        FlowDescriptor.Create(
            "orders.project", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromMinutes(5)),
        StepGraph.Create([StepNode.ForCapability(0, Project)]));

    private static ChangeFixtureState ChangeFixture(PostgresTestSchema schema, bool tenantSchemas)
    {
        var options = OptionsFor(tenantSchemas);
        var durability = new FlowDurability(schema.Journal, schema.Leases);
        var host = new FlowHost(new FlowEngine(SystemClock.Instance), options, durability);

        var feed = tenantSchemas
            ? new PostgresChangeFeed(schema.TenantStores!)
            : new PostgresChangeFeed(schema.DataSource);

        var subscriptions = new FlowChangeCatalog().Add(
            new ChangeSubscription("orders.project", "1.0.0", Source, Group),
            Plan(),
            new SucceedingDispatcher());

        return new ChangeFixtureState(
            feed, new FlowChangeScan(host, subscriptions, feed, durability, options));
    }

    private sealed record ChangeFixtureState(PostgresChangeFeed Feed, FlowChangeScan Scan)
    {
        public ValueTask<ChangeScanReport> PassAsync() => Scan.RunOnceAsync(Cancellation);
    }

    private sealed record ScheduleFixtureState(FlowSchedule Schedule, FlowScheduleScan Scan)
    {
        public ValueTask<ScheduleScanReport> SweepAsync() => Scan.RunOnceAsync(Cancellation);
    }

    private static ScheduleFixtureState ScheduleFixture(
        PostgresTestSchema schema, bool tenantSchemas, bool perTenant)
    {
        var options = OptionsFor(tenantSchemas);

        if (!tenantSchemas)
        {
            options.Tenants.Add(TenantA);
            options.Tenants.Add(TenantB);
        }

        var durability = new FlowDurability(schema.Journal, schema.Leases);
        var host = new FlowHost(new FlowEngine(SystemClock.Instance), options, durability);

        var declared = FlowSchedule.Create(
            "orders.project", "1.0.0", "0 0 * * *", "UTC", MissedFirePolicy.RunOnce, perTenant);

        var catalogue = new FlowScheduleCatalog();

        catalogue.Add(declared, Plan(), new SucceedingDispatcher());

        ITenantDirectory directory = tenantSchemas
            ? new PostgresTenantDirectory(schema.TenantStores!)
            : new DeclaredTenantDirectory([.. options.Tenants]);

        // The node starts an hour before the occurrence and the sweep runs a minute after it, so
        // exactly one occurrence has fallen due — a sweep whose node started at the same instant
        // it swept has nothing behind it and fires nothing.
        var clock = new MovableClock(Occurrence.AddHours(-1));

        var scan = new FlowScheduleScan(host, catalogue, durability, options, clock, directory);

        clock.MoveTo(Occurrence.AddMinutes(1));

        return new ScheduleFixtureState(declared, scan);
    }

    /// <summary>Waits until the staged changes are below the feed's visibility barrier.</summary>
    /// <remarks>
    /// <c>pg_snapshot_xmin</c> is cluster-wide, so a transaction open anywhere in the database —
    /// including one belonging to a test running beside this — holds the barrier down. That is
    /// ADR-0048's stated latency floor, and it is why this waits rather than asserting that a
    /// committed change is immediately observable.
    /// </remarks>
    private static async ValueTask AwaitVisibleAsync(
        PostgresChangeFeed feed, CancellationToken cancellationToken)
    {
        var subscription = new ChangeSubscription("orders.project", "1.0.0", Source, Group);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var read = await feed.ReadAsync(subscription, 8, cancellationToken);

            if (read.IsSuccess && read.Value.Count > 0)
            {
                return;
            }

            await Task.Delay(25, cancellationToken);
        }

        throw new InvalidOperationException(
            "a change was staged and committed and is still below no barrier after 30 seconds.");
    }

    /// <summary>The tenants the observing flow's instance rows are recorded under.</summary>
    private static Task<IReadOnlyList<string?>> ObservingInstancesAsync(
        PostgresTestSchema schema, string tenantId) =>
        InstanceTenantsAsync(schema, tenantId, "orders.project");

    /// <summary>The ids of the scheduled flow's instances in one tenant's schema.</summary>
    private static async Task<IReadOnlyList<Guid>> ScheduledInstancesAsync(
        PostgresTestSchema schema, string? tenantId)
    {
        var source = await schema.SourceFor(tenantId, Cancellation);
        var ids = new List<Guid>();

        await using var connection = await source.OpenConnectionAsync(Cancellation);
        await using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT instance_id FROM flow_instance " +
            "WHERE flow_id = 'orders.project' " +
            "  AND tenant_id IS NOT DISTINCT FROM @tenant";

        command.Parameters.AddWithValue("tenant", (object?)tenantId ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(Cancellation);

        while (await reader.ReadAsync(Cancellation))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static async Task<IReadOnlyList<string?>> InstanceTenantsAsync(
        PostgresTestSchema schema, string tenantId, string flowId)
    {
        var source = await schema.SourceFor(tenantId, Cancellation);
        var tenants = new List<string?>();

        await using var connection = await source.OpenConnectionAsync(Cancellation);
        await using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT tenant_id FROM flow_instance WHERE flow_id = @flow AND tenant_id = @tenant";

        command.Parameters.AddWithValue("flow", flowId);
        command.Parameters.AddWithValue("tenant", tenantId);

        await using var reader = await command.ExecuteReaderAsync(Cancellation);

        while (await reader.ReadAsync(Cancellation))
        {
            tenants.Add(
                await reader.IsDBNullAsync(0, Cancellation) ? null : reader.GetString(0));
        }

        return tenants;
    }

    /// <summary>Every cursor row, as a position an assertion can compare before and after.</summary>
    private static async Task<IReadOnlyList<string>> CursorRowsAsync(PostgresTestSchema schema)
    {
        var rows = new List<string>();

        await using var connection = await schema.DataSource.OpenConnectionAsync(Cancellation);
        await using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT subscription_id::text, position_xid::text, position_seq " +
            "FROM change_cursor ORDER BY subscription_id";

        await using var reader = await command.ExecuteReaderAsync(Cancellation);

        while (await reader.ReadAsync(Cancellation))
        {
            rows.Add($"{reader.GetString(0)}:{reader.GetString(1)}:{reader.GetInt64(2)}");
        }

        return rows;
    }

    /// <summary>A clock a test moves, so a day costs no wall-clock time.</summary>
    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        private long _ticks = start.UtcTicks;

        public DateTimeOffset UtcNow =>
            new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        /// <summary>Puts the clock at an instant.</summary>
        public void MoveTo(DateTimeOffset now) => Interlocked.Exchange(ref _ticks, now.UtcTicks);

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    /// <summary>A dispatcher whose one step succeeds, so a started flow reaches a terminal row.</summary>
    private sealed class SucceedingDispatcher : IStepDispatcher
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

    /// <summary>A broker that takes everything and remembers what it was handed.</summary>
    private sealed class RecordingPublisher : IEventPublisher
    {
        public IReadOnlyList<OutboxRecord> Batch { get; private set; } = [];

        public ValueTask<Result<int>> PublishAsync(
            IReadOnlyList<OutboxRecord> batch, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(batch);

            Batch = [.. Batch, .. batch];

            return ValueTask.FromResult(Result.Ok(batch.Count));
        }
    }
}
