using Npgsql;

namespace FlowX.Postgres;

/// <summary>
/// The one query a timer sweep needs, on PostgreSQL: which parked instances are due to be
/// woken.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The fourth class over these tables, and it is here for the reason the third
/// was.</strong> <see cref="PostgresRetention"/> and <see cref="PostgresRecoveryIndex"/> both
/// read <c>flow_instance</c> from outside <see cref="PostgresFlowJournal"/>, because a sweep
/// on a timer is not part of executing an instance and does not belong on the type every
/// durable write passes through. This is a sweep on a timer. Folding it into the recovery
/// index instead would be worse than folding it into the journal: the two ask opposite
/// questions of disjoint sets of rows, and one class answering both would invite one query
/// answering both — which no partial index can serve.
/// </para>
/// <para>
/// <strong>There is nothing here for a caller to be refused.</strong> A query that finds
/// nothing has already answered, with an empty list. So this returns a success or it
/// propagates: a closed connection or a missing column is a store the caller cannot reach,
/// which is a different problem from having been told no (ADR-0007).
/// </para>
/// </remarks>
public sealed class PostgresTimerIndex : ITimerIndex
{
    /// <summary>
    /// The state a due instance is in, as the predicate both the query and
    /// <c>0005_suspended_wake.sql</c> use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This has to match the migration's index predicate in meaning, and the plan test
    /// is what holds it there.</strong> A partial index is only usable when the planner can
    /// prove the query's predicate implies the index's; widen one side and PostgreSQL falls
    /// back to a sequential scan and a top-N sort over every instance ever written.
    /// <c>TheDueQueryIsServedByAnIndexAndNotBySortingTheTable</c> reads the plan and fails when
    /// that happens, which is the only way this pairing is checked rather than believed.
    /// </para>
    /// <para>
    /// <strong>One state, and it is the one <see cref="PostgresRecoveryIndex"/> excludes.</strong>
    /// A parked instance was not abandoned and must not be swept up as though it were; an
    /// abandoned one has no wake instant and would not be found here. The two sweeps partition
    /// the unfinished rows between them rather than overlapping.
    /// </para>
    /// </remarks>
    private const string Suspended = "state = 'Suspended'";

    /// <summary>
    /// What a candidate carries, in reader order.
    /// </summary>
    /// <remarks>
    /// Five columns, and neither JSON column among them, for the reason
    /// <see cref="PostgresRecoveryIndex"/> reads six: <c>input</c> and <c>state_bag</c> are
    /// what a resume reads once, after the lease is won. No <c>state</c> either — every row
    /// this query returns is <c>Suspended</c>, so selecting it would be a constant fetched per
    /// row.
    /// </remarks>
    private const string CandidateColumns =
        "instance_id, flow_id, flow_version, tenant_id, wake_at";

    /// <summary>The candidates, longest overdue first, bounded.</summary>
    /// <remarks>
    /// <para>
    /// <strong><c>ORDER BY wake_at</c> is the anti-starvation property, not a presentation
    /// choice</strong> — the same one <see cref="PostgresRecoveryIndex"/> gets from ordering by
    /// staleness. An instance pinned to a flow version no deployed node carries would otherwise
    /// sit at the head of every page for ever and the page behind it would never be reached.
    /// </para>
    /// <para>
    /// <strong><c>&lt;=</c>, not <c>&lt;</c>.</strong> A wait is over at the instant it is due,
    /// and the engine compares the same way — a strict inequality here would leave an instance
    /// whose instant is exactly now unfetched until the next sweep, and would disagree with the
    /// loop about a boundary a suite can hit exactly with a fake clock.
    /// </para>
    /// </remarks>
    private const string ListDue =
        $"""
         SELECT {CandidateColumns}
           FROM flow_instance
          WHERE {Suspended}
            AND wake_at <= @due_before
          ORDER BY wake_at
          LIMIT @limit
         """;

    /// <summary>
    /// The same query, restricted to one tenant.
    /// </summary>
    /// <remarks>
    /// A second statement rather than <c>(@tenant IS NULL OR tenant_id = @tenant)</c> on the
    /// first, for the reason <see cref="PostgresRecoveryIndex"/> keeps two: that predicate
    /// cannot be planned away when the parameter is null, so the untenanted sweep — the only
    /// one the runtime issues today — would carry a filter it can never fail, evaluated once
    /// per row of every page.
    /// </remarks>
    private const string ListDueForTenant =
        $"""
         SELECT {CandidateColumns}
           FROM flow_instance
          WHERE {Suspended}
            AND tenant_id = @tenant
            AND wake_at <= @due_before
          ORDER BY wake_at
          LIMIT @limit
         """;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates a timer index over a data source.</summary>
    /// <param name="dataSource">
    /// The data source. Its connection string selects the schema, and the adapter does not own
    /// it — a data source is pooled and shared, and disposing one from here would close
    /// connections the journal is still using.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is null.</exception>
    public PostgresTimerIndex(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <summary>
    /// The statement a sweep issues, so that a plan can be taken of the query that actually
    /// runs rather than of a transcription of it.
    /// </summary>
    /// <param name="tenantScoped">
    /// Whether the sweep is restricted to one tenant, as <see cref="DueInstanceQuery.TenantId"/>
    /// asks.
    /// </param>
    /// <returns>The SQL, ready to be prefixed with <c>EXPLAIN</c>.</returns>
    /// <remarks>
    /// Public for the two readers <see cref="PostgresRecoveryIndex.Statement"/> is public for:
    /// an operator diagnosing a slow sweep, and the plan assertion that keeps this statement
    /// and <c>flow_instance_due_idx</c> in step.
    /// </remarks>
    public static string Statement(bool tenantScoped) => tenantScoped ? ListDueForTenant : ListDue;

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<DueInstance>>> ListDueAsync(
        DueInstanceQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Limit <= 0)
        {
            // "At most none" is a question with an answer, and it is this one. Passing it
            // through would make PostgreSQL raise on a negative LIMIT, turning a caller's
            // arithmetic slip into an unreachable-store exception.
            return Array.Empty<DueInstance>();
        }

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var closing = connection.ConfigureAwait(false);

        using var command = connection.CreateCommand();

        command.CommandText = query.TenantId is null ? ListDue : ListDueForTenant;
        command.Parameters.Add(Db.Timestamp("due_before", query.DueBefore));
        command.Parameters.Add(Db.Int("limit", query.Limit));

        if (query.TenantId is not null)
        {
            command.Parameters.Add(Db.Text("tenant", query.TenantId));
        }

        var candidates = new List<DueInstance>();

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
    /// Ordinals matched to <see cref="CandidateColumns"/>, for the reason <c>JournalRows</c>
    /// gives: reading by name costs a lookup per column per row, and the pairing is only safe
    /// because both halves are in this one file.
    /// </remarks>
    private static DueInstance ReadCandidate(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetString(2),
        Db.NullableString(reader, 3),
        Db.ReadTimestamp(reader, 4));
}
