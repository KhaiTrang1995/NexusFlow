using Npgsql;
using NpgsqlTypes;

namespace FlowX.Postgres;

/// <summary>
/// Applies the retention windows in <c>retention_policy</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The policy is data.</strong> <c>docs/11-Distributed-Runtime.md §2</c> states
/// retention as a table in a document — thirty days for a completed instance, a hundred and
/// eighty for a failed one, until completion for a suspended one, seven days for a published
/// outbox row — and a table in a document deletes nothing. The same numbers are seeded into
/// <c>retention_policy</c> by the initial migration, where an operator can change one per
/// flow without a deployment and this class can read them.
/// </para>
/// <para>
/// <strong>A null window means "not on a timer", not "immediately".</strong> A
/// <c>Suspended</c> instance is kept until it completes or its deadline passes, which is a
/// question about the instance rather than about the clock. The arithmetic gives that for
/// free: <c>now() - NULL</c> is null and every comparison against it is false, so a null
/// window matches no row.
/// </para>
/// <para>
/// <strong>An archived parent leaves a legible orphan.</strong> ADR-0015 requires it: a
/// <c>Detached</c> sub-flow can outlive its parent, so a purge can remove a parent while a
/// child is still running. <c>flow_instance.parent_instance_id</c> therefore carries no
/// foreign key, and the child keeps reporting the id of the parent it had.
/// <c>RetentionTests.PurgingAParentLeavesItsRunningChildLegible</c> is where that is
/// checked rather than assumed.
/// </para>
/// <para>
/// <strong>A pending event outranks every window.</strong> A purge cascades to the instance's
/// outbox rows, and until WP-56 that included rows nobody had published — which
/// <c>docs/adr/ADR-0016-postgres-journal-adapter.md</c> recorded as an accepted risk on the
/// grounds that nothing published them, so nothing was lost. That premise expired the moment
/// <see cref="PostgresOutboxPublisher"/> existed. Every purge below therefore
/// refuses an instance with an unpublished event, whatever its state and however far past its
/// window it is: an event staged in the same transaction as the step that emitted it is a
/// promise to a consumer, and deleting it is a silent broken promise rather than retention.
/// </para>
/// <para>
/// <strong>The refusal is counted, because a guard that only holds rows back is a leak with
/// good manners.</strong> A deployment with no publisher wired — the supported configuration
/// this adapter shipped in until now — stages events nothing will ever publish, and under
/// this guard those instances are never purged. <see cref="RetentionSweep.HeldForPendingEvents"/>
/// is how an operator sees that happening instead of discovering it as unexplained growth.
/// </para>
/// </remarks>
public sealed class PostgresRetention
{
    /// <summary>
    /// The guard WP-56 owed this class: no instance is purged while it still has an event
    /// nobody has published.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One predicate, spliced into both purges rather than written twice, because the two
    /// windows are two policies and this is one rule. A pending event is not "old data past
    /// its window" whichever window the instance is under — it is an undelivered promise, and
    /// the cascade that would remove it is silent.
    /// </para>
    /// <para>
    /// It is deliberately not scoped to a window. The published half of the outbox has its
    /// own seven-day sweep below; this is about the unpublished half, and there is no age at
    /// which discarding an unsent event becomes correct. An instance held here is held until
    /// a publisher drains it, which is a load-bearing consequence rather than a side effect —
    /// see the count in <see cref="RetentionSweep.HeldForPendingEvents"/>.
    /// </para>
    /// <para>
    /// Answered against <c>outbox_event_pending_instance_idx</c> from migration <c>0004</c>,
    /// which indexes the pending rows alone. Without it every candidate instance would probe
    /// every event it ever emitted, and an instance is a purge candidate precisely because it
    /// is old and has emitted all of them.
    /// </para>
    /// </remarks>
    private const string NoPendingEvent =
        """
        NOT EXISTS (SELECT 1 FROM outbox_event e
                     WHERE e.instance_id = i.instance_id AND e.published_at IS NULL)
        """;

    /// <summary>
    /// Removes completed instances whose window has passed. Steps and outbox rows follow
    /// by cascade.
    /// </summary>
    /// <remarks>
    /// The window is looked up per flow with a fallback to the <c>'*'</c> default, so an
    /// operator can lengthen retention for one flow without touching the rest. A null
    /// window makes the subtraction null and the comparison false, which is how "not on a
    /// timer" is expressed without a second code path.
    /// </remarks>
    private const string PurgeCompleted =
        $"""
        DELETE FROM flow_instance i
         WHERE i.state = 'Completed'
           AND i.updated_at <= now() - COALESCE(
                 (SELECT p.retain_for FROM retention_policy p
                   WHERE p.flow_id = i.flow_id AND p.state_class = 'Completed'),
                 (SELECT p.retain_for FROM retention_policy p
                   WHERE p.flow_id = '*' AND p.state_class = 'Completed'))
           AND {NoPendingEvent}
        """;

    /// <summary>
    /// Removes instances that ended badly, once their longer window has passed.
    /// </summary>
    /// <remarks>
    /// <c>TimedOut</c> is in this set and the document's retention table does not mention
    /// it. A flow that ran out of deadline failed; keeping it for the completed window
    /// would discard the evidence for an incident sooner than the incident is investigated.
    /// </remarks>
    private const string PurgeFailed =
        $"""
        DELETE FROM flow_instance i
         WHERE i.state IN ('Failed', 'TimedOut', 'CompensationFailed')
           AND i.updated_at <= now() - COALESCE(
                 (SELECT p.retain_for FROM retention_policy p
                   WHERE p.flow_id = i.flow_id AND p.state_class = 'Failed'),
                 (SELECT p.retain_for FROM retention_policy p
                   WHERE p.flow_id = '*' AND p.state_class = 'Failed'))
           AND {NoPendingEvent}
        """;

    /// <summary>
    /// Counts the instances a sweep would have removed and did not, because they still have
    /// an unpublished event.
    /// </summary>
    /// <remarks>
    /// The same two window expressions as the purges above, with the guard inverted. It is a
    /// third statement rather than a <c>RETURNING</c> clause on the first two because a
    /// <c>DELETE</c> can only report what it deleted — the rows this exists to count are
    /// exactly the ones the <c>DELETE</c> did not touch, and a purge that reports nothing
    /// about them is how a guard becomes invisible growth.
    /// </remarks>
    private const string CountHeldForPendingEvents =
        """
        SELECT count(*) FROM flow_instance i
         WHERE ((i.state = 'Completed'
                 AND i.updated_at <= now() - COALESCE(
                       (SELECT p.retain_for FROM retention_policy p
                         WHERE p.flow_id = i.flow_id AND p.state_class = 'Completed'),
                       (SELECT p.retain_for FROM retention_policy p
                         WHERE p.flow_id = '*' AND p.state_class = 'Completed')))
             OR (i.state IN ('Failed', 'TimedOut', 'CompensationFailed')
                 AND i.updated_at <= now() - COALESCE(
                       (SELECT p.retain_for FROM retention_policy p
                         WHERE p.flow_id = i.flow_id AND p.state_class = 'Failed'),
                       (SELECT p.retain_for FROM retention_policy p
                         WHERE p.flow_id = '*' AND p.state_class = 'Failed'))))
           AND EXISTS (SELECT 1 FROM outbox_event e
                        WHERE e.instance_id = i.instance_id AND e.published_at IS NULL)
        """;

    /// <summary>Removes outbox rows a publisher has already sent.</summary>
    private const string PurgeOutbox =
        """
        DELETE FROM outbox_event e
         WHERE e.published_at IS NOT NULL
           AND e.published_at <= now() - (
                 SELECT p.retain_for FROM retention_policy p
                  WHERE p.flow_id = '*' AND p.state_class = 'OutboxPublished')
        """;

    /// <summary>Sets a window, for one flow or for the default.</summary>
    private const string SetPolicy =
        """
        INSERT INTO retention_policy (flow_id, state_class, retain_for)
        VALUES (@flow_id, @class, @retain_for)
        ON CONFLICT (flow_id, state_class) DO UPDATE SET retain_for = EXCLUDED.retain_for
        """;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates a retention sweeper over a data source.</summary>
    /// <param name="dataSource">The data source; its connection string selects the schema.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is null.</exception>
    public PostgresRetention(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <summary>
    /// Deletes everything past its window, and reports what it removed.
    /// </summary>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    /// <returns>How many rows of each kind were removed.</returns>
    public async ValueTask<RetentionSweep> PurgeAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var closing = connection.ConfigureAwait(false);

        // Counted before anything is deleted. Afterwards the answer would be the same rows —
        // the guard is what stopped them going — but reading it first says plainly that this
        // number is about the sweep that was declined, not about what survived it.
        var held = await CountAsync(connection, CountHeldForPendingEvents, cancellationToken)
            .ConfigureAwait(false);

        var completed = await ExecuteAsync(connection, PurgeCompleted, cancellationToken)
            .ConfigureAwait(false);

        var failed = await ExecuteAsync(connection, PurgeFailed, cancellationToken)
            .ConfigureAwait(false);

        var published = await ExecuteAsync(connection, PurgeOutbox, cancellationToken)
            .ConfigureAwait(false);

        return new RetentionSweep(completed, failed, published, held);
    }

    /// <summary>
    /// Sets a retention window.
    /// </summary>
    /// <param name="flowId">The flow it applies to, or <c>"*"</c> for the default.</param>
    /// <param name="stateClass">
    /// <c>Completed</c>, <c>Failed</c>, <c>Suspended</c> or <c>OutboxPublished</c>.
    /// </param>
    /// <param name="retainFor">How long to keep it, or null to keep it off a timer.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async ValueTask SetPolicyAsync(
        string flowId,
        string stateClass,
        TimeSpan? retainFor,
        CancellationToken cancellationToken = default)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var closing = connection.ConfigureAwait(false);

        using var command = connection.CreateCommand();

        command.CommandText = SetPolicy;
        command.Parameters.Add(Db.Text("flow_id", flowId));
        command.Parameters.Add(Db.Text("class", stateClass));
        command.Parameters.Add(new NpgsqlParameter("retain_for", NpgsqlDbType.Interval)
        {
            Value = retainFor.HasValue ? retainFor.Value : DBNull.Value,
        });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int> ExecuteAsync(
        NpgsqlConnection connection,
        string statement,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = statement;

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int> CountAsync(
        NpgsqlConnection connection,
        string query,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = query;

        var count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return count is long value ? (int)value : 0;
    }
}

/// <summary>What one retention sweep removed, and what it refused to remove.</summary>
/// <param name="CompletedInstances">Completed instances past their window, with their steps.</param>
/// <param name="FailedInstances">Failed, timed-out and compensation-failed instances past theirs.</param>
/// <param name="PublishedEvents">Outbox rows a publisher had already sent.</param>
/// <param name="HeldForPendingEvents">
/// Instances past their window that were kept because they still hold an unpublished event.
/// Persistently non-zero means events are being staged and never published — a deployment
/// with no <see cref="IEventPublisher"/> wired, a publisher that is not running, or one that
/// is stuck behind an event the broker keeps refusing. The rows are safe; the pipeline is
/// not, and this is the number that says so before the disk does.
/// </param>
public readonly record struct RetentionSweep(
    int CompletedInstances,
    int FailedInstances,
    int PublishedEvents,
    int HeldForPendingEvents);
