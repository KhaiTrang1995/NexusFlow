using Npgsql;

namespace FlowX.Postgres;

/// <summary>
/// The one query a recovery scan needs, on PostgreSQL: which instances look like a node
/// died holding them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is its own class rather than a second interface on
/// <see cref="PostgresFlowJournal"/>.</strong> The two arguments point in opposite
/// directions and the tie-break is what a reader should take from this paragraph, so both
/// are written down.
/// </para>
/// <para>
/// The argument for folding it in is that the query reads <c>flow_instance</c> — the
/// journal's own table, written by the journal's own statements, with the same columns and
/// the same stored spellings. Two classes over one table is a seam where a column can be
/// added on one side and missed on the other.
/// </para>
/// <para>
/// The argument against, which wins here, is three things at once.
/// <see cref="IRecoveryIndex"/> was split out of <see cref="IFlowJournal"/> on purpose —
/// its own remarks say a scan "is not part of executing an instance" — and putting it back
/// onto the type every durable write passes through would give that type a member no write
/// uses and <c>JournalConformance</c> says nothing about. Nothing is gained in wiring
/// either: the container matches on the service type, so a journal that also implemented
/// this would still need its own registration line, which is exactly what
/// <c>FlowXServiceCollectionExtensions</c> warns about. And it is what makes "this node does
/// not sweep" expressible as one registration a deployment declines rather than as a
/// different journal it has to substitute.
/// </para>
/// <para>
/// <strong>The seam already exists and this is not the first stitch in it.</strong>
/// <see cref="PostgresRetention"/> reads and deletes <c>flow_instance</c> from outside the
/// journal for the same reason — a sweep on a timer is not the executing instance's
/// contract. This is the third class over those tables, which is why the answer is the one
/// the adapter already gave rather than a new shape.
/// </para>
/// <para>
/// <strong>There is nothing here for a caller to be refused.</strong> The journal returns a
/// stale token or a duplicate key as a value because those are answers; a query that finds
/// nothing has already answered, with an empty list. So this returns a success or it
/// propagates — a closed connection or a missing table is a store the caller cannot reach,
/// which is a different problem from having been told no (ADR-0007).
/// </para>
/// </remarks>
public sealed class PostgresRecoveryIndex : IRecoveryIndex
{
    /// <summary>
    /// The states an instance can be abandoned in, as the <c>IN</c> list both the query and
    /// <c>0003_index_abandoned_instances.sql</c> use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This text has to match the migration's index predicate character for
    /// character in meaning, and the plan test is what holds it there.</strong> A partial
    /// index is only usable when the planner can prove the query's predicate implies the
    /// index's; widen one side and PostgreSQL silently falls back to a sequential scan and a
    /// top-N sort over every instance ever written. <c>TheQueryIsServedByAnIndexAndNotBySortingTheTable</c>
    /// reads the plan and fails when that happens, which is the only way this pairing is
    /// checked rather than believed.
    /// </para>
    /// <para>
    /// <strong>Three states, not the four that are non-terminal.</strong> <c>Suspended</c> is
    /// unfinished and is deliberately not here: it means "waiting for a signal, a timer or a
    /// child flow", so no node holds it and none died holding it. A sweep that took it over
    /// would resume a flow that is parked by design, fence out whatever eventually delivers
    /// the signal, and — because a parked instance is stale by definition — do it again on
    /// every sweep for as long as the instance waits. The reference implementation the
    /// contract is read against draws the same three.
    /// </para>
    /// </remarks>
    private const string Unfinished = "state IN ('Pending', 'Running', 'Compensating')";

    /// <summary>
    /// What a candidate carries, in reader order.
    /// </summary>
    /// <remarks>
    /// Six columns, and neither JSON column among them. <see cref="AbandonedInstance"/> is
    /// deliberately not <see cref="FlowInstanceRecord"/> for this reason: <c>input</c> and
    /// <c>state_bag</c> are what a resume reads once, after the lease is won, and a sweep
    /// that fetched them would pay for two documents per candidate on every pass to decide
    /// something neither of them answers.
    /// </remarks>
    private const string CandidateColumns =
        "instance_id, flow_id, flow_version, tenant_id, state, updated_at";

    /// <summary>
    /// The candidates, stalest first, bounded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong><c>ORDER BY updated_at</c> is the contract's anti-starvation property, not a
    /// presentation choice.</strong> Ordered by id, an instance pinned to a flow version no
    /// deployed node carries would sit at the head of every page for ever and the page behind
    /// it would never be reached. Ordered by staleness it is still returned, and so is
    /// everything behind it — and every instance that <em>is</em> resumed has its row touched,
    /// which moves it to the back. The page advances.
    /// </para>
    /// <para>
    /// <strong>No tie-break column.</strong> Adding <c>, instance_id</c> would read as
    /// tidiness and would cost the index scan its ordering — the planner would have to sort
    /// what it had just read in order. Rows sharing a millisecond are returned in whatever
    /// order the index holds them, which is harmless: the sweep starts at a random offset
    /// inside the page anyway, precisely so that identical nodes handed an identical page do
    /// not contend for its first row.
    /// </para>
    /// </remarks>
    private const string ListAbandoned =
        $"""
         SELECT {CandidateColumns}
           FROM flow_instance
          WHERE {Unfinished}
            AND updated_at < @idle_before
          ORDER BY updated_at
          LIMIT @limit
         """;

    /// <summary>
    /// The same query, restricted to one tenant.
    /// </summary>
    /// <remarks>
    /// A second statement rather than <c>(@tenant IS NULL OR tenant_id = @tenant)</c> on the
    /// first. That predicate cannot be planned away when the parameter is null, so the
    /// untenanted sweep — the only one the runtime issues today — would carry a filter it can
    /// never fail, evaluated once per row of every page.
    /// <para>
    /// The tenant is a filter over the same index rather than an index of its own. Ordering
    /// by staleness within a tenant would need <c>(tenant_id, updated_at)</c>, and a second
    /// partial index on this table is a write on every step boundary of every flow — charged
    /// to every deployment, to serve a parameter nothing in the runtime sets yet. A
    /// deployment that shards its nodes by tenant is the one that should add it.
    /// </para>
    /// </remarks>
    private const string ListAbandonedForTenant =
        $"""
         SELECT {CandidateColumns}
           FROM flow_instance
          WHERE {Unfinished}
            AND tenant_id = @tenant
            AND updated_at < @idle_before
          ORDER BY updated_at
          LIMIT @limit
         """;

    /// <summary>
    /// The same page, with no tenant allowed to occupy more than
    /// <see cref="AbandonedInstanceQuery.PerTenantLimit"/> of it.
    /// </summary>
    /// <remarks>
    /// <see cref="PostgresTimerIndex"/>'s fair statement, over staleness instead of due time and
    /// for the same reason: <c>ORDER BY … LIMIT n</c> returns the oldest <em>n</em> rows in the
    /// table, so a tenant with more than <em>n</em> abandoned instances owns every page and the
    /// sweep never learns another tenant is waiting. Issued only when a non-zero
    /// <see cref="AbandonedInstanceQuery.PerTenantLimit"/> asks for it, because it reads every
    /// stale row rather than stopping at <em>n</em>.
    /// </remarks>
    private const string ListAbandonedFairly =
        $"""
         SELECT {CandidateColumns}
           FROM (SELECT {CandidateColumns},
                        ROW_NUMBER() OVER (PARTITION BY tenant_id ORDER BY updated_at) AS tenant_rank
                   FROM flow_instance
                  WHERE {Unfinished}
                    AND updated_at < @idle_before) ranked
          WHERE tenant_rank <= @per_tenant
          ORDER BY updated_at
          LIMIT @limit
         """;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates a recovery index over a data source.</summary>
    /// <param name="dataSource">
    /// The data source. Its connection string selects the schema, and the adapter does not
    /// own it — a data source is pooled and shared, and disposing one from here would close
    /// connections the journal is still using.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is null.</exception>
    public PostgresRecoveryIndex(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <summary>
    /// The statement a sweep issues, so that a plan can be taken of the query that actually
    /// runs rather than of a transcription of it.
    /// </summary>
    /// <param name="tenantScoped">
    /// Whether the sweep is restricted to one tenant, as
    /// <see cref="AbandonedInstanceQuery.TenantId"/> asks.
    /// </param>
    /// <returns>The SQL, ready to be prefixed with <c>EXPLAIN</c>.</returns>
    /// <remarks>
    /// Public for two readers, and the reasoning is the one that already made
    /// <see cref="PostgresMigrator.ReadScript"/> public. The first is an operator: a sweep
    /// that has become slow is diagnosed by explaining the statement, and recovering it from
    /// a decompiler is not a diagnosis. The second is
    /// <c>TheQueryIsServedByAnIndexAndNotBySortingTheTable</c>, which asserts that the plan
    /// for this is an ordered index scan — an assertion against a copied string would keep
    /// passing after the statement it was copied from had drifted away from the index, which
    /// is precisely the regression worth catching.
    /// </remarks>
    public static string Statement(bool tenantScoped) =>
        tenantScoped ? ListAbandonedForTenant : ListAbandoned;

    /// <summary>The statement a fair sweep issues. See <see cref="Statement"/>.</summary>
    /// <returns>The SQL, ready to be prefixed with <c>EXPLAIN</c>.</returns>
    public static string FairStatement() => ListAbandonedFairly;

    /// <inheritdoc />
    /// <remarks>
    /// Answers "looks abandoned", and cannot answer more than that: leases live in
    /// <c>flow_lease</c>, which this does not read, and could live in another store
    /// altogether. <see cref="ILeaseStore.AcquireAsync"/> is the arbiter, and a candidate
    /// this returns that another node then wins is a skip rather than a wasted query — it is
    /// the ordinary outcome when a fleet sweeps.
    /// </remarks>
    public async ValueTask<Result<IReadOnlyList<AbandonedInstance>>> ListAbandonedAsync(
        AbandonedInstanceQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Limit <= 0)
        {
            // "At most none" is a question with an answer, and it is this one. Passing it
            // through would make PostgreSQL raise on a negative LIMIT, turning a caller's
            // arithmetic slip into an unreachable-store exception — the one thing an
            // exception from here is supposed to mean.
            return Array.Empty<AbandonedInstance>();
        }

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var closing = connection.ConfigureAwait(false);

        using var command = connection.CreateCommand();

        // A scan scoped to one tenant is already capped at that tenant's share; asking for the
        // window function as well would be the same question twice.
        var fair = query.TenantId is null && query.PerTenantLimit > 0;

        command.CommandText = query.TenantId is not null
            ? ListAbandonedForTenant
            : fair ? ListAbandonedFairly : ListAbandoned;

        command.Parameters.Add(Db.Timestamp("idle_before", query.IdleBefore));
        command.Parameters.Add(Db.Int("limit", query.Limit));

        if (query.TenantId is not null)
        {
            command.Parameters.Add(Db.Text("tenant", query.TenantId));
        }

        if (fair)
        {
            command.Parameters.Add(Db.Int("per_tenant", query.PerTenantLimit));
        }

        var candidates = new List<AbandonedInstance>();

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(ReadCandidate(reader));
        }

        return candidates;
    }

    /// <summary>Reads the candidate on the current row.</summary>
    /// <remarks>
    /// Ordinals matched to <see cref="CandidateColumns"/>, for the reason
    /// <c>JournalRows</c> gives: reading by name costs a lookup per column per row, and the
    /// pairing is only safe because both halves are in this one file and neither is
    /// assembled at run time.
    /// </remarks>
    private static AbandonedInstance ReadCandidate(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetString(2),
        Db.NullableString(reader, 3),
        StoredEnums.ToInstanceState(reader.GetString(4)),
        Db.ReadTimestamp(reader, 5));
}
