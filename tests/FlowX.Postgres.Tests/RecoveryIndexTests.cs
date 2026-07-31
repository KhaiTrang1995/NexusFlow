using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// The recovery scan's query against a real table: what it returns, what it refuses to
/// return, in what order, and by which plan.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every row these tests examine is written by the adapter.</strong> The one thing
/// raw SQL does here is move <c>updated_at</c> into the past, because staleness is the
/// premise of the whole query and the alternative is a test that sleeps for a lease TTL.
/// Nothing else is hand-written: states are reached through <c>CommitAsync</c> and
/// <c>CompleteAsync</c>, so a test cannot pass against a row shape the journal does not
/// produce.
/// </para>
/// <para>
/// <strong>The skip behaviour is <see cref="PostgresTestDatabase"/>'s and is inherited, not
/// re-implemented.</strong> No connection string configured is a skip carrying a reason; a
/// connection string configured with no server behind it is a failure. A test here that
/// caught the second and skipped would be exactly the vacuous green
/// <c>DatabaseAvailabilityTests</c> exists to prevent.
/// </para>
/// </remarks>
public sealed class RecoveryIndexTests
{
    private const string FlowId = "order.place";
    private const string FlowVersion = "1.2.0";

    /// <summary>An hour, which is longer than any lease TTL a sweep would use.</summary>
    private const int LongIdle = 3600;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    // -----------------------------------------------------------------------------------
    // What is a candidate
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// An unfinished instance nobody has written to for longer than a lease lives is a
    /// candidate, and it arrives with what the scan needs to decide about it.
    /// </summary>
    /// <remarks>
    /// The four fields matter individually. <c>FlowId</c> and <c>FlowVersion</c> are what the
    /// catalogue is asked about, and a node that does not carry that exact version must leave
    /// the instance alone — so a version read back wrong would have a node resuming an
    /// instance against a plan it was never pinned to. <c>UpdatedAt</c> is the staleness the
    /// ordering is built on.
    /// </remarks>
    [Fact]
    public async Task AStaleUnfinishedInstanceIsACandidate()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var instance = await AbandonAsync(schema, FlowInstanceState.Running, LongIdle, tenantId: "acme");

        var listed = await schema.RecoveryIndex.ListAbandonedAsync(Query(), Cancellation);

        listed.IsSuccess.ShouldBeTrue(
            $"the query answers or it throws; there is nothing here to be refused. " +
            $"{(listed.IsFailure ? listed.Error.ToString() : string.Empty)}");

        var candidate = listed.Value.ShouldHaveSingleItem();

        candidate.InstanceId.ShouldBe(instance);
        candidate.FlowId.ShouldBe(FlowId);
        candidate.FlowVersion.ShouldBe(
            FlowVersion,
            "the version an instance is pinned to for its whole life. A node that does not " +
            "carry it must skip the instance rather than resume it against another plan.");
        candidate.TenantId.ShouldBe("acme");
        candidate.State.ShouldBe(FlowInstanceState.Running);
        candidate.UpdatedAt.ShouldBeLessThan(DateTimeOffset.UtcNow.AddMinutes(-30));
    }

    /// <summary>An instance in the middle of unwinding is still work a dead node left.</summary>
    /// <remarks>
    /// <c>Compensating</c> is not terminal: an unwinding flow is still writing rows, and its
    /// compensations are the rows it writes. Dropping it from the candidate set would leave
    /// half-undone sagas as the one thing recovery never finishes, which is the case the
    /// whole compensation model exists for.
    /// </remarks>
    [Fact]
    public async Task ACompensatingInstanceIsACandidate()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var instance = await AbandonAsync(schema, FlowInstanceState.Compensating, LongIdle);

        var listed = await schema.RecoveryIndex.ListAbandonedAsync(Query(), Cancellation);

        listed.Value.ShouldHaveSingleItem().InstanceId.ShouldBe(instance);
    }

    /// <summary>
    /// An instance written to recently is filtered out at the store, not fetched and
    /// discarded.
    /// </summary>
    /// <remarks>
    /// The first and cheapest of the anti-stampede measures. In steady state a healthy fleet
    /// writes every live instance's row at every step boundary, so the query returns nothing
    /// and a sweep costs one indexed read — rather than every node pulling every live
    /// instance and being refused a lease on each.
    /// </remarks>
    [Fact]
    public async Task AnInstanceWrittenToRecentlyIsNotACandidate()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        await AbandonAsync(schema, FlowInstanceState.Running, idleSeconds: 0);

        var listed = await schema.RecoveryIndex.ListAbandonedAsync(Query(), Cancellation);

        listed.Value.ShouldBeEmpty(
            "its row was touched inside the idle window, so it either has a live owner or is " +
            "inside a single step outliving its own lease — and only acquisition settles the " +
            "second.");
    }

    /// <summary>
    /// Committing a step is what takes an instance back out of the candidate set.
    /// </summary>
    /// <remarks>
    /// The previous test asserts the filter; this asserts that the journal actually moves the
    /// column the filter reads. They are different claims and the second is the one that
    /// could quietly become false — a commit path that stopped touching <c>updated_at</c>
    /// would leave every live instance permanently stale, and every node in the fleet would
    /// spend every sweep trying to steal work that is running perfectly well.
    /// </remarks>
    [Fact]
    public async Task AStepBoundaryTakesAnInstanceOutOfTheCandidateSet()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var instance = await AbandonAsync(schema, FlowInstanceState.Running, LongIdle);

        (await schema.RecoveryIndex.ListAbandonedAsync(Query(), Cancellation))
            .Value.ShouldHaveSingleItem();

        var committed = await schema.Journal.CommitAsync(
            new StepCommit
            {
                Key = StepKey.First(instance, 1),
                Token = new FencingToken(1),
                CapabilityId = "order.validate",
                CapabilityVersion = "1.0.0",
                Outcome = JournalOutcome.Success,
            },
            Cancellation);

        committed.IsSuccess.ShouldBeTrue(
            committed.IsFailure ? committed.Error.ToString() : string.Empty);

        (await schema.RecoveryIndex.ListAbandonedAsync(Query(), Cancellation))
            .Value.ShouldBeEmpty("the step boundary moved updated_at to now().");
    }

    /// <summary>
    /// An instance that has finished is never a candidate, however long ago it finished.
    /// </summary>
    /// <remarks>
    /// The four terminal states. Returning one would have a node take a lease on a flow that
    /// is over, read its frontier, find nothing to run and give the lease back — every
    /// sweep, for as long as retention keeps the row, which for a failed instance is a
    /// hundred and eighty days.
    /// </remarks>
    [Theory]
    [InlineData(FlowInstanceState.Completed)]
    [InlineData(FlowInstanceState.Failed)]
    [InlineData(FlowInstanceState.TimedOut)]
    [InlineData(FlowInstanceState.CompensationFailed)]
    public async Task ATerminalInstanceIsNeverACandidate(FlowInstanceState state)
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        await AbandonAsync(schema, state, LongIdle);

        (await schema.RecoveryIndex.ListAbandonedAsync(Query(), Cancellation))
            .Value.ShouldBeEmpty($"'{state}' is terminal; there is nothing left to take over.");
    }

    /// <summary>
    /// A suspended instance is unfinished and is still not a candidate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one exclusion that is a judgement rather than a definition, so it is asserted
    /// rather than left to the reader of an <c>IN</c> list. <c>Suspended</c> means "waiting
    /// for a signal, a timer or a child flow": no node holds it, so no node died holding it,
    /// and it is stale by design rather than by accident.
    /// </para>
    /// <para>
    /// Returning it would be actively harmful, not merely wasteful. A sweep would resume a
    /// flow that is parked, fence out whatever eventually delivers the signal it is parked
    /// for, and — because the instance goes straight back to being stale — do it again on
    /// every sweep for as long as it waits. The reference implementation the contract is read
    /// against draws the same three states.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASuspendedInstanceIsNotACandidate()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        await AbandonAsync(schema, FlowInstanceState.Suspended, LongIdle);

        (await schema.RecoveryIndex.ListAbandonedAsync(Query(), Cancellation))
            .Value.ShouldBeEmpty(
                "a suspended instance is waiting, not abandoned. Nobody holds it, so nobody " +
                "died holding it.");
    }

    /// <summary>An empty answer is the ordinary answer and is not an error.</summary>
    [Fact]
    public async Task ASweepThatFindsNothingSucceedsWithAnEmptyList()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var listed = await schema.RecoveryIndex.ListAbandonedAsync(Query(), Cancellation);

        listed.IsSuccess.ShouldBeTrue("an empty backlog is a healthy fleet, not a fault.");
        listed.Value.ShouldBeEmpty();
    }

    // -----------------------------------------------------------------------------------
    // Order and page
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// Candidates come back oldest first.
    /// </summary>
    /// <remarks>
    /// The contract's anti-starvation property, and the reason the ordering column is
    /// staleness rather than id. An instance pinned to a flow version no deployed node
    /// carries can never be resumed; ordered by id it would hold the head of every page for
    /// ever and nothing behind it would be reached. Ordered by staleness it is returned, and
    /// so is everything behind it — and every instance that is resumed has its row touched
    /// and moves to the back, so the page advances.
    /// </remarks>
    [Fact]
    public async Task CandidatesComeBackOldestFirst()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var newest = await AbandonAsync(schema, FlowInstanceState.Running, idleSeconds: 300);
        var oldest = await AbandonAsync(schema, FlowInstanceState.Running, idleSeconds: 9000);
        var middle = await AbandonAsync(schema, FlowInstanceState.Running, idleSeconds: 1800);

        var listed = await schema.RecoveryIndex.ListAbandonedAsync(Query(), Cancellation);

        listed.Value.Select(static candidate => candidate.InstanceId).ShouldBe(
            [oldest, middle, newest],
            "ordered by staleness, oldest first. They were written in a different order on " +
            "purpose, so an ordering that happens to follow insertion cannot pass this.");
    }

    /// <summary>
    /// The page size bounds what comes back, and what it keeps is the oldest.
    /// </summary>
    /// <remarks>
    /// Both halves matter. A scan that pulled an unbounded backlog would turn one node's
    /// recovery into every node's memory pressure; a bounded page that kept an arbitrary
    /// subset would lose the ordering the anti-starvation argument rests on.
    /// </remarks>
    [Fact]
    public async Task ThePageSizeBoundsWhatComesBackAndKeepsTheOldest()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var oldest = await AbandonAsync(schema, FlowInstanceState.Running, idleSeconds: 9000);
        var second = await AbandonAsync(schema, FlowInstanceState.Running, idleSeconds: 7000);

        await AbandonAsync(schema, FlowInstanceState.Running, idleSeconds: 5000);
        await AbandonAsync(schema, FlowInstanceState.Running, idleSeconds: 3000);

        var listed = await schema.RecoveryIndex.ListAbandonedAsync(Query(limit: 2), Cancellation);

        listed.Value.Select(static candidate => candidate.InstanceId).ShouldBe([oldest, second]);
    }

    /// <summary>Asking for at most nothing is answered with nothing.</summary>
    /// <remarks>
    /// A limit of zero or below is a caller's arithmetic rather than a store fault, and it
    /// has an obvious answer. Passing it through would make PostgreSQL raise on a negative
    /// <c>LIMIT</c>, turning a slip into the one thing an exception from this adapter is
    /// supposed to mean: the store could not be reached.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ALimitOfZeroOrLessAsksForNothing(int limit)
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        await AbandonAsync(schema, FlowInstanceState.Running, LongIdle);

        var listed = await schema.RecoveryIndex.ListAbandonedAsync(Query(limit: limit), Cancellation);

        listed.IsSuccess.ShouldBeTrue("and it is an answer, not a refusal.");
        listed.Value.ShouldBeEmpty();
    }

    /// <summary>A scan scoped to one tenant sees only that tenant.</summary>
    /// <remarks>
    /// Nothing in the runtime sets this yet — <c>FlowRecoveryScan</c> sweeps every tenant a
    /// node serves — but a node sharded by tenant is what the parameter exists for, and a
    /// filter nothing exercises is a filter that is wrong the first time it is used.
    /// </remarks>
    [Fact]
    public async Task ASweepScopedToOneTenantSeesOnlyThatTenant()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var mine = await AbandonAsync(schema, FlowInstanceState.Running, LongIdle, tenantId: "acme");

        await AbandonAsync(schema, FlowInstanceState.Running, LongIdle, tenantId: "globex");
        await AbandonAsync(schema, FlowInstanceState.Running, LongIdle);

        var listed = await schema.RecoveryIndex.ListAbandonedAsync(
            Query(tenantId: "acme"), Cancellation);

        listed.Value.ShouldHaveSingleItem().InstanceId.ShouldBe(mine);

        var everyone = await schema.RecoveryIndex.ListAbandonedAsync(Query(), Cancellation);

        everyone.Value.Count.ShouldBe(3, "an unscoped sweep sees every tenant, and the untenanted row.");
    }

    // -----------------------------------------------------------------------------------
    // The plan
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// The query is answered by an ordered index scan, not by reading the table and sorting
    /// it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the test the migration exists for.</strong> A partial index is only
    /// usable when the planner can prove the query's predicate implies the index's, and the
    /// ordering is only free when the ordering column leads the index. Both halves are silent
    /// when they break: the query keeps returning the right rows, by reading every instance
    /// ever written and top-N sorting it, on every node, on every sweep. Measured on 200 000
    /// rows that is a parallel sequential scan touching 1 748 buffers where the index scan
    /// touches 4.
    /// </para>
    /// <para>
    /// The statement explained is the one the adapter issues, taken from
    /// <see cref="PostgresRecoveryIndex.Statement"/> rather than transcribed here. A copy
    /// would go on passing after the original had drifted, which is the entire regression
    /// this is written to catch.
    /// </para>
    /// <para>
    /// Enough rows are inserted for the planner to have a choice worth making. Against a
    /// handful of rows a sequential scan is genuinely cheaper and choosing it would be
    /// correct, so a plan assertion on a small table asserts nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheQueryIsServedByAnIndexAndNotBySortingTheTable()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        await schema.ExecuteAsync(
            """
            INSERT INTO flow_instance (instance_id, flow_id, flow_version, state, fence, updated_at)
            SELECT gen_random_uuid(), 'order.place', '1.2.0',
                   (ARRAY['Pending', 'Running', 'Suspended', 'Compensating', 'Completed', 'Failed'])[1 + (i % 6)],
                   1,
                   now() - (i || ' seconds')::interval
              FROM generate_series(1, 20000) i;
            ANALYZE flow_instance;
            """,
            Cancellation);

        var plan = await ExplainAsync(schema, Cancellation);

        plan.ShouldContain(
            "flow_instance_abandoned_idx",
            Case.Sensitive,
            $"the sweep must be one indexed read. Plan was:{Environment.NewLine}{plan}");

        plan.ShouldNotContain(
            "Seq Scan",
            Case.Sensitive,
            $"a sequential scan means the partial index's predicate no longer covers the " +
            $"query's. Plan was:{Environment.NewLine}{plan}");

        plan.ShouldNotContain(
            "Sort",
            Case.Sensitive,
            "a sort means the ordering column no longer leads the index, so every candidate " +
            "is read before the page is cut. That is the cost the ordering was supposed to " +
            $"be free of. Plan was:{Environment.NewLine}{plan}");
    }

    /// <summary>The migration that carries the index is applied by the migrator.</summary>
    /// <remarks>
    /// The plan test above would fail without it, but it would fail by reporting a sequential
    /// scan, which reads as a query problem rather than as a schema that is one version
    /// behind. This says which.
    /// </remarks>
    [Fact]
    public async Task TheAbandonedIndexIsPartOfTheMigratedSchema()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var definition = await schema.ScalarAsync(
            "SELECT indexdef FROM pg_indexes WHERE indexname = 'flow_instance_abandoned_idx'",
            Cancellation) as string;

        definition.ShouldNotBeNull(
            "0003_index_abandoned_instances.sql is listed in PostgresMigrator.Migrations and " +
            "the fixture migrates to the latest version.");

        definition!.ShouldContain(
            "updated_at",
            Case.Sensitive,
            "staleness leads the index, which is what makes the ordering free.");
    }

    // -----------------------------------------------------------------------------------
    // The handshake with the lease store
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// The index says "looks abandoned"; the lease store decides, and losing is a skip.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The division of labour the interface's remarks describe, checked against the two real
    /// stores rather than assumed. The index reads <c>flow_instance</c> and knows nothing
    /// about leases — they live in another table here and could live in another technology —
    /// so the most it can say is that a row is unfinished and cold.
    /// </para>
    /// <para>
    /// One node wins the candidate and fences it; the other is told <c>lease.held</c>, which
    /// is a skip and not a failure. That asymmetry is what stops a fleet queueing behind one
    /// instance: the loser moves to the next candidate rather than retrying this one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task LosingTheRaceForACandidateIsASkipAndNotAFailure()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var instance = await AbandonAsync(schema, FlowInstanceState.Running, LongIdle);

        var candidate = (await schema.RecoveryIndex.ListAbandonedAsync(Query(), Cancellation))
            .Value.ShouldHaveSingleItem();

        candidate.InstanceId.ShouldBe(instance);

        var winner = await schema.Leases.AcquireAsync(
            candidate.InstanceId, "node-2", TimeSpan.FromMinutes(5), Cancellation);

        winner.IsSuccess.ShouldBeTrue(
            "the dead node's lease was never renewed, so it has lapsed and the row is free. " +
            $"{(winner.IsFailure ? winner.Error.ToString() : string.Empty)}");

        winner.Value.Token.Value.ShouldBe(2, "the second lease issued for this instance.");

        var loser = await schema.Leases.AcquireAsync(
            candidate.InstanceId, "node-3", TimeSpan.FromMinutes(5), Cancellation);

        loser.IsFailure.ShouldBeTrue();
        loser.Error.Code.ShouldBe(
            "lease.held",
            "which the sweep counts as contention and skips. A retry here would be a fleet " +
            "queueing behind one instance.");

        var fenced = await schema.Journal.FenceAsync(
            candidate.InstanceId, winner.Value.Token, Cancellation);

        fenced.IsSuccess.ShouldBeTrue(
            $"and the winner raises the fence before it writes anything. " +
            $"{(fenced.IsFailure ? fenced.Error.ToString() : string.Empty)}");

        var zombie = await schema.Journal.CommitAsync(
            new StepCommit
            {
                Key = StepKey.First(candidate.InstanceId, 5),
                Token = new FencingToken(1),
                CapabilityId = "order.validate",
                CapabilityVersion = "1.0.0",
                Outcome = JournalOutcome.Success,
            },
            Cancellation);

        zombie.IsFailure.ShouldBeTrue();
        zombie.Error.Code.ShouldBe(
            "journal.fenced_out",
            "the node the sweep took the instance from cannot write to it any more, which " +
            "is what makes taking it over safe rather than merely fast.");
    }

    // -----------------------------------------------------------------------------------
    // Fixture
    // -----------------------------------------------------------------------------------

    /// <summary>The query a sweep would issue, with a lease TTL's worth of idleness.</summary>
    private static AbandonedInstanceQuery Query(int limit = 64, string? tenantId = null) => new()
    {
        IdleBefore = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30),
        Limit = limit,
        TenantId = tenantId,
    };

    /// <summary>
    /// Writes an instance in the given state and makes it as cold as a dead node would have
    /// left it.
    /// </summary>
    /// <remarks>
    /// The state is reached through the journal's own writes — a commit for the states a
    /// running flow passes through, a completion for the states it ends in — so no test here
    /// asserts against a row the adapter would not produce. Only <c>updated_at</c> is moved
    /// by hand, because the alternative is waiting an hour.
    /// </remarks>
    private static async Task<Guid> AbandonAsync(
        PostgresTestSchema schema,
        FlowInstanceState state,
        int idleSeconds,
        string? tenantId = null)
    {
        var instance = Guid.NewGuid();

        var lease = await schema.Leases.AcquireAsync(
            instance, "dead-node", TimeSpan.FromMilliseconds(1), Cancellation);

        lease.IsSuccess.ShouldBeTrue(
            "a node takes the lease before it opens the instance row. The TTL is a " +
            "millisecond because the node this stands for is not coming back.");

        var started = await schema.Journal.StartAsync(
            new FlowInstanceStart
            {
                InstanceId = instance,
                FlowId = FlowId,
                FlowVersion = FlowVersion,
                TenantId = tenantId,
                Token = lease.Value.Token,
            },
            Cancellation);

        started.IsSuccess.ShouldBeTrue(
            started.IsFailure ? started.Error.ToString() : string.Empty);

        await MoveAsync(schema, instance, lease.Value.Token, state);

        if (idleSeconds > 0)
        {
            await schema.ExecuteAsync(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"""
                     UPDATE flow_instance
                        SET updated_at = now() - interval '{idleSeconds} seconds'
                      WHERE instance_id = '{instance}'
                     """),
                Cancellation);
        }

        return instance;
    }

    /// <summary>Moves a freshly opened instance to the state under test.</summary>
    private static async Task MoveAsync(
        PostgresTestSchema schema,
        Guid instance,
        FencingToken token,
        FlowInstanceState state)
    {
        if (state is FlowInstanceState.Pending)
        {
            return;
        }

        if (state is FlowInstanceState.Completed
            or FlowInstanceState.Failed
            or FlowInstanceState.TimedOut
            or FlowInstanceState.CompensationFailed)
        {
            var completed = await schema.Journal.CompleteAsync(
                instance, token, state, JournalPayload.Empty, Cancellation);

            completed.IsSuccess.ShouldBeTrue(
                completed.IsFailure ? completed.Error.ToString() : string.Empty);

            return;
        }

        var committed = await schema.Journal.CommitAsync(
            new StepCommit
            {
                Key = StepKey.First(instance, 0),
                Token = token,
                CapabilityId = "order.validate",
                CapabilityVersion = "1.0.0",
                Outcome = JournalOutcome.Success,
                State = state,
            },
            Cancellation);

        committed.IsSuccess.ShouldBeTrue(
            committed.IsFailure ? committed.Error.ToString() : string.Empty);
    }

    /// <summary>Takes a plan of the statement the adapter issues, with its own parameters.</summary>
    private static async Task<string> ExplainAsync(
        PostgresTestSchema schema,
        CancellationToken cancellationToken)
    {
        await using var connection = await schema.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = "EXPLAIN (COSTS OFF) " + PostgresRecoveryIndex.Statement(tenantScoped: false);
        command.Parameters.Add(new NpgsqlParameter("idle_before", NpgsqlDbType.TimestampTz)
        {
            Value = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30),
        });
        command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = 64 });

        var lines = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join(Environment.NewLine, lines);
    }
}
