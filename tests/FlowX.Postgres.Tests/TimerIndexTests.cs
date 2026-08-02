using Npgsql;
using NpgsqlTypes;
using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// The timer sweep's query against a real table: what it returns, in what order, and by which
/// plan.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every row these tests examine is written by the adapter.</strong> A parked instance
/// is reached through <c>CompleteAsync</c> carrying a <see cref="FlowWake"/>, which is the
/// only way one is ever produced — so a test here cannot pass against a row shape the journal
/// does not write, and the three <c>wake_</c> columns are exercised end to end rather than
/// asserted about.
/// </para>
/// <para>
/// <strong>The parts that need a real database to mean anything.</strong> That the wake
/// columns round-trip through the journal's own write and read; that the query is served by
/// <c>flow_instance_due_idx</c> rather than by reading every instance ever written and sorting
/// it; and that a completed instance's wake is cleared, which is the difference between a
/// timer that fires once and one that fires for ever.
/// </para>
/// <para>
/// The skip behaviour is <see cref="PostgresTestDatabase"/>'s and is inherited: no connection
/// string configured is a skip carrying a reason, a connection string with no server behind it
/// is a failure.
/// </para>
/// </remarks>
public sealed class TimerIndexTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A parked instance's wait round-trips through the journal's own write.</summary>
    /// <remarks>
    /// All three columns or none: <c>flow_instance_wake_check</c> refuses anything else, which
    /// is what lets the read side test one and trust the other two.
    /// </remarks>
    [Fact]
    public async Task TheWaitAnInstanceIsParkedAtRoundTrips()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var due = Due(30);
        var instance = await ParkAsync(schema, new FlowWake(StepScope.Root.Element(3), 7, due));

        var read = await schema.Journal.ReadInstanceAsync(instance, Cancellation);

        read.Value.Wake!.Value.StepId.ShouldBe(7);
        read.Value.Wake!.Value.Scope.Text.ShouldBe("3", "the iteration is part of the wait's identity");

        // Microseconds, because that is PostgreSQL's timestamptz resolution and DateTimeOffset
        // carries a hundred nanoseconds. The wait is the value, not its last two digits.
        read.Value.Wake!.Value.At.ShouldBe(due, TimeSpan.FromMilliseconds(1));
    }

    /// <summary>An instance that is due is returned; one that is not is not.</summary>
    /// <remarks>
    /// The whole of the sweep's filter. A sweep interval of ten seconds against a table of
    /// week-long waits is only affordable because the not-yet-due rows are excluded at the
    /// store rather than fetched, leased and put back.
    /// </remarks>
    [Fact]
    public async Task OnlyTheInstancesWhoseWaitHasComeDueAreReturned()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var overdue = await ParkAsync(schema, new FlowWake(StepScope.Root, 1, Due(-60)));
        _ = await ParkAsync(schema, new FlowWake(StepScope.Root, 1, Due(600)));

        var due = await ListAsync(schema);

        due.Select(candidate => candidate.InstanceId).ShouldBe([overdue]);
    }

    /// <summary>The candidates come back longest overdue first.</summary>
    /// <remarks>
    /// The anti-starvation property, not a presentation choice: an instance pinned to a flow
    /// version no deployed node carries would otherwise hold the head of every page for ever
    /// and the page behind it would never be reached.
    /// </remarks>
    [Fact]
    public async Task TheCandidatesComeBackLongestOverdueFirst()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var recent = await ParkAsync(schema, new FlowWake(StepScope.Root, 1, Due(-10)));
        var oldest = await ParkAsync(schema, new FlowWake(StepScope.Root, 1, Due(-600)));

        var due = await ListAsync(schema);

        due.Select(candidate => candidate.InstanceId).ShouldBe([oldest, recent]);
    }

    /// <summary>An instance that finished carries no wait, so no sweep finds it again.</summary>
    /// <remarks>
    /// <strong>The assignment in <c>CompleteInstance</c> is unconditional, and this is why.</strong>
    /// The state bag is <c>COALESCE</c>d because an absent one means "unchanged"; a wake is
    /// assigned because an absent one means "nothing is due to wake this instance". A
    /// completed instance keeping the instant it was parked at would be returned by every
    /// sweep for the rest of its retention window.
    /// </remarks>
    [Fact]
    public async Task AnInstanceThatFinishedCarriesNoWait()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var instance = await ParkAsync(schema, new FlowWake(StepScope.Root, 1, Due(-60)));

        (await ListAsync(schema)).ShouldHaveSingleItem();

        await schema.Journal.CompleteAsync(
            instance,
            new FencingToken(1),
            FlowInstanceState.Completed,
            JournalPayload.Empty,
            wake: null,
            Cancellation);

        (await ListAsync(schema)).ShouldBeEmpty("the wait went with the state that ended it");

        (await schema.Journal.ReadInstanceAsync(instance, Cancellation)).Value.Wake.ShouldBeNull();
    }

    /// <summary>
    /// The query is answered by an ordered index scan, not by reading the table and sorting it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the test <c>0005_suspended_wake.sql</c> exists for.</strong> A partial
    /// index is only usable when the planner can prove the query's predicate implies the
    /// index's, and the ordering is only free when the ordering column leads the index. Both
    /// halves are silent when they break: the query keeps returning the right rows, by reading
    /// every instance ever written and top-N sorting it, on every node, on every sweep.
    /// </para>
    /// <para>
    /// The statement explained is the one the adapter issues, taken from
    /// <see cref="PostgresTimerIndex.Statement"/> rather than transcribed here — a copy would
    /// go on passing after the original had drifted, which is the regression this catches.
    /// </para>
    /// <para>
    /// Enough rows for the planner to have a choice worth making. Against a handful a
    /// sequential scan is genuinely cheaper and choosing it would be correct, so a plan
    /// assertion on a small table asserts nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheDueQueryIsServedByAnIndexAndNotBySortingTheTable()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        await schema.ExecuteAsync(
            """
            INSERT INTO flow_instance (
                instance_id, flow_id, flow_version, state, fence, wake_at, wake_step_id, wake_scope)
            SELECT gen_random_uuid(), 'order.place', '1.2.0',
                   (ARRAY['Pending', 'Running', 'Suspended', 'Compensating', 'Completed', 'Failed'])[1 + (i % 6)],
                   1,
                   now() + (i || ' seconds')::interval,
                   1,
                   ''
              FROM generate_series(1, 20000) i;
            ANALYZE flow_instance;
            """,
            Cancellation);

        var plan = await ExplainAsync(schema, Cancellation);

        plan.ShouldContain(
            "flow_instance_due_idx",
            Case.Sensitive,
            $"the sweep must be one indexed read. Plan was:{Environment.NewLine}{plan}");

        plan.ShouldNotContain(
            "Seq Scan",
            Case.Sensitive,
            "a sequential scan means the partial index's predicate no longer covers the " +
            $"query's. Plan was:{Environment.NewLine}{plan}");

        plan.ShouldNotContain(
            "Sort",
            Case.Sensitive,
            "a sort means the ordering column no longer leads the index, so every candidate " +
            "is read before the page is cut. That is the cost the ordering was supposed to " +
            $"be free of. Plan was:{Environment.NewLine}{plan}");
    }

    /// <summary>
    /// A per-tenant cap puts a quiet tenant into a page its own wake instant could not reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The window function is the only untestable-by-inspection part of this
    /// adapter.</strong> <c>ORDER BY wake_at LIMIT n</c> returns the <em>n</em> most overdue rows
    /// in the table, so the tenant with the longest backlog owns every page and the sweep never
    /// learns another tenant is waiting — which is the starvation
    /// <c>FlowX.Hosting.Tests.TenantFairnessTests</c> demonstrates end to end. This asserts the
    /// half of the repair that lives in SQL, against a real PostgreSQL rather than against the
    /// reference index.
    /// </para>
    /// <para>
    /// The first list is the arrangement and not a spare assertion: without it, a fair page
    /// containing the quiet tenant would be evidence of nothing, because the unfair one might
    /// have contained it too.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task APerTenantCapPutsAQuietTenantIntoThePage()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        for (var i = 0; i < 4; i++)
        {
            await ParkAsync(schema, new FlowWake(StepScope.Root, 1, Due(-600 + i)), "acme");
        }

        var quiet = await ParkAsync(schema, new FlowWake(StepScope.Root, 1, Due(-10)), "globex");

        var unfair = await ListAsync(schema, limit: 3);

        unfair.ShouldAllBe(candidate => candidate.TenantId == "acme",
            "three slots and the three most overdue rows all belong to one tenant");

        var fair = await ListAsync(schema, limit: 3, perTenantLimit: 2);

        fair.ShouldContain(candidate => candidate.InstanceId == quiet);
        fair.Count(candidate => candidate.TenantId == "acme").ShouldBe(2, "the cap");
    }

    /// <summary>Starts an instance and parks it at a wait, through the journal's own writes.</summary>
    private static async Task<Guid> ParkAsync(
        PostgresTestSchema schema,
        FlowWake wake,
        string? tenantId = null)
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
            wake,
            Cancellation);

        return instance;
    }

    /// <summary>The sweep's own query, as a sweep issues it.</summary>
    private static async Task<IReadOnlyList<DueInstance>> ListAsync(
        PostgresTestSchema schema,
        int limit = 64,
        int perTenantLimit = 0)
    {
        var listed = await new PostgresTimerIndex(schema.DataSource).ListDueAsync(
            new DueInstanceQuery
            {
                DueBefore = DateTimeOffset.UtcNow,
                Limit = limit,
                PerTenantLimit = perTenantLimit,
            },
            Cancellation);

        return listed.Value;
    }

    /// <summary>An instant relative to now, so a test never has to wait for one.</summary>
    private static DateTimeOffset Due(int seconds) =>
        DateTimeOffset.UtcNow.AddSeconds(seconds);

    private static async Task<string> ExplainAsync(
        PostgresTestSchema schema,
        CancellationToken cancellationToken)
    {
        await using var connection = await schema.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = "EXPLAIN (COSTS OFF) " + PostgresTimerIndex.Statement(tenantScoped: false);
        command.Parameters.Add(new NpgsqlParameter("due_before", NpgsqlDbType.TimestampTz)
        {
            Value = DateTimeOffset.UtcNow,
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
