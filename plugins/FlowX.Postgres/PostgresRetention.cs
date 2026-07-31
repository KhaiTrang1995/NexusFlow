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
/// </remarks>
public sealed class PostgresRetention
{
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
        """
        DELETE FROM flow_instance i
         WHERE i.state = 'Completed'
           AND i.updated_at <= now() - COALESCE(
                 (SELECT p.retain_for FROM retention_policy p
                   WHERE p.flow_id = i.flow_id AND p.state_class = 'Completed'),
                 (SELECT p.retain_for FROM retention_policy p
                   WHERE p.flow_id = '*' AND p.state_class = 'Completed'))
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
        """
        DELETE FROM flow_instance i
         WHERE i.state IN ('Failed', 'TimedOut', 'CompensationFailed')
           AND i.updated_at <= now() - COALESCE(
                 (SELECT p.retain_for FROM retention_policy p
                   WHERE p.flow_id = i.flow_id AND p.state_class = 'Failed'),
                 (SELECT p.retain_for FROM retention_policy p
                   WHERE p.flow_id = '*' AND p.state_class = 'Failed'))
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

        var completed = await ExecuteAsync(connection, PurgeCompleted, cancellationToken)
            .ConfigureAwait(false);

        var failed = await ExecuteAsync(connection, PurgeFailed, cancellationToken)
            .ConfigureAwait(false);

        var published = await ExecuteAsync(connection, PurgeOutbox, cancellationToken)
            .ConfigureAwait(false);

        return new RetentionSweep(completed, failed, published);
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
}

/// <summary>What one retention sweep removed.</summary>
/// <param name="CompletedInstances">Completed instances past their window, with their steps.</param>
/// <param name="FailedInstances">Failed, timed-out and compensation-failed instances past theirs.</param>
/// <param name="PublishedEvents">Outbox rows a publisher had already sent.</param>
public readonly record struct RetentionSweep(
    int CompletedInstances,
    int FailedInstances,
    int PublishedEvents);
