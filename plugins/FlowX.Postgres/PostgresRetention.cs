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
/// <strong>An event still owed to a consumer outranks every window.</strong> A purge cascades to
/// the instance's outbox rows, and until WP-56 that included rows nobody had published — which
/// <c>docs/adr/ADR-0016-postgres-journal-adapter.md</c> recorded as an accepted risk on the
/// grounds that nothing published them, so nothing was lost. That premise expired the moment
/// <see cref="PostgresOutboxPublisher"/> existed. Every purge below therefore
/// refuses an instance with an unconsumed event, whatever its state and however far past its
/// window it is: an event staged in the same transaction as the step that emitted it is a
/// promise to a consumer, and deleting it is a silent broken promise rather than retention.
/// </para>
/// <para>
/// <strong>Who the consumers are is a deployment fact, not a column.</strong> ADR-0018 read
/// "owed" off <c>published_at IS NULL</c>, which was the whole answer while the publisher was the
/// only consumer. <see cref="PostgresChangeFeed"/> made it wrong in both directions: a host with a
/// <c>[ChangeTrigger]</c> subscription and no broker never sets <c>published_at</c> at all, so
/// every instance it ever ran was held for ever; and a host with both would let the seven-day
/// published-row sweep delete rows a subscription had not read. So this class is told its
/// consumers — <see cref="RetentionConsumers"/> — and asks each of them the question it can
/// actually answer: the publisher's progress is <c>published_at</c>, and a change subscription's
/// is its row in <c>change_cursor</c>, which is the fact the feed already keeps rather than a
/// second one invented here.
/// </para>
/// <para>
/// <strong>The default is one publisher and no subscriptions</strong>, which is the behaviour
/// ADR-0018 shipped and the safe end of the trade: a deployment that has not said what consumes
/// its outbox holds unpublished rows rather than discarding them. Saying so costs one registration
/// (<c>AddFlowXPostgresRetentionConsumers</c>) and is the only way retention can be right, because
/// nothing in the database distinguishes "no publisher is wired" from "the publisher is down".
/// </para>
/// <para>
/// <strong>The refusal is counted, because a guard that only holds rows back is a leak with
/// good manners.</strong> A deployment whose declared consumers are not draining — no publisher
/// running, or a subscription whose cursor has stopped — accumulates instances that are never
/// purged. <see cref="RetentionSweep.HeldForPendingEvents"/> is how an operator sees that
/// happening instead of discovering it as unexplained growth.
/// </para>
/// </remarks>
public sealed class PostgresRetention
{
    /// <summary>
    /// Whether the outbox row aliased <c>e</c> is still owed to one of this deployment's
    /// consumers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One predicate, spliced into every statement below rather than written four
    /// times</strong>, because the windows are policies and this is a fact: an event is owed, or
    /// it is not. It is deliberately not scoped to a window — there is no age at which
    /// discarding an event no consumer has seen becomes correct.
    /// </para>
    /// <para>
    /// It moved to <see cref="OutboxSql.OwedToAConsumer"/> when <c>PostgresSubjectErasure</c>
    /// became its second caller, and the alias is kept so the statements below read as they did.
    /// Two copies of a predicate that were meant to agree are two predicates that will not.
    /// </para>
    /// </remarks>
    private const string Owed = OutboxSql.OwedToAConsumer;

    /// <summary>
    /// The guard WP-56 owed this class, read over every consumer rather than over the publisher
    /// alone: no instance is purged while one of its events is still owed to somebody.
    /// </summary>
    private const string NoPendingEvent =
        $"""
        NOT EXISTS (SELECT 1 FROM outbox_event e
                     WHERE e.instance_id = i.instance_id AND {Owed})
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
    /// Counts the instances a sweep would have removed and did not, because they still hold an
    /// event a declared consumer has not consumed.
    /// </summary>
    /// <remarks>
    /// The same two window expressions as the purges above, with the guard inverted. It is a
    /// third statement rather than a <c>RETURNING</c> clause on the first two because a
    /// <c>DELETE</c> can only report what it deleted — the rows this exists to count are
    /// exactly the ones the <c>DELETE</c> did not touch, and a purge that reports nothing
    /// about them is how a guard becomes invisible growth.
    /// </remarks>
    private const string CountHeldForPendingEvents =
        $"""
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
                        WHERE e.instance_id = i.instance_id AND {Owed})
        """;

    /// <summary>Removes outbox rows every consumer is past.</summary>
    /// <remarks>
    /// <strong>The window is the publisher's and the guard is everybody's.</strong> Seven days
    /// after publication a row is beyond the broker's redelivery, which is what the window
    /// encodes; a change subscription reading the same table has its own position and no window
    /// at all, so a published row it has not reached is deleted out from under it — ADR-0050
    /// recorded that as "a subscription that is down for longer than the published window loses
    /// the changes it never read, silently". The same predicate the instance purges carry ends
    /// it: the row goes when it is owed to nobody.
    /// </remarks>
    private const string PurgeOutbox =
        $"""
        DELETE FROM outbox_event e
         WHERE e.published_at IS NOT NULL
           AND e.published_at <= now() - (
                 SELECT p.retain_for FROM retention_policy p
                  WHERE p.flow_id = '*' AND p.state_class = 'OutboxPublished')
           AND NOT {Owed}
        """;

    /// <summary>Sets a window, for one flow or for the default.</summary>
    private const string SetPolicy =
        """
        INSERT INTO retention_policy (flow_id, state_class, retain_for)
        VALUES (@flow_id, @class, @retain_for)
        ON CONFLICT (flow_id, state_class) DO UPDATE SET retain_for = EXCLUDED.retain_for
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _publisher;
    private readonly Guid[] _cursors;
    private readonly string[] _sources;

    /// <summary>Creates a retention sweeper over a data source.</summary>
    /// <param name="dataSource">The data source; its connection string selects the schema.</param>
    /// <param name="consumers">
    /// What reads this deployment's outbox. Null is <see cref="RetentionConsumers.Default"/> — one
    /// publisher and no change subscriptions, which is what a host that has not said otherwise
    /// safely behaves as.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is null.</exception>
    /// <remarks>
    /// The cursor keys are derived once here rather than per sweep. They are a pure function of
    /// the subscription's four terms, which do not change while a process is running.
    /// </remarks>
    public PostgresRetention(NpgsqlDataSource dataSource, RetentionConsumers? consumers = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        var declared = consumers ?? RetentionConsumers.Default;

        _dataSource = dataSource;
        _publisher = declared.Publisher;
        _cursors = [.. declared.Subscriptions.Select(CursorIdFor)];
        _sources = [.. declared.Subscriptions.Select(static subscription => subscription.Source)];
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

    /// <summary>
    /// The id <see cref="PostgresChangeFeed"/> keys a subscription's cursor row on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The same derivation, written twice on purpose and pinned by a test.</strong> The
    /// feed's copy is private to it, as it should be — a store that exposed its key derivation
    /// would have made it a contract. Two copies that disagreed would make this class read a row
    /// that does not exist and purge an event a subscription is still owed, which is silent, so
    /// <c>RetentionTests.ACursorTheFeedCommittedIsTheCursorRetentionReads</c> drives the real
    /// feed and then asserts the effect here rather than comparing the two functions.
    /// </para>
    /// <para>
    /// SHA-256 over the four terms NUL-separated under the cursor scope, laid out as a UUID
    /// version 8, exactly as <c>0009</c>'s comment describes the column.
    /// </para>
    /// </remarks>
    internal static Guid CursorIdFor(ChangeSubscription subscription)
    {
        var material = string.Join(
            '\0',
            "flowx\0change\0cursor",
            subscription.FlowId,
            subscription.FlowVersion,
            subscription.Source,
            subscription.Group);

        Span<byte> digest = stackalloc byte[32];

        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(material), digest);

        Span<byte> bytes = stackalloc byte[16];

        digest[..16].CopyTo(bytes);

        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>Binds the consumer set every statement in this class asks about.</summary>
    private void Bind(NpgsqlCommand command)
    {
        command.Parameters.Add(Db.Bool("publisher", _publisher));
        command.Parameters.Add(Db.UuidArray("cursors", _cursors));
        command.Parameters.Add(Db.TextArray("sources", _sources));
    }

    private async ValueTask<int> ExecuteAsync(
        NpgsqlConnection connection,
        string statement,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = statement;
        Bind(command);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<int> CountAsync(
        NpgsqlConnection connection,
        string query,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = query;
        Bind(command);

        var count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return count is long value ? (int)value : 0;
    }
}

/// <summary>
/// What reads this deployment's outbox, which is the only input to what retention may delete.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Configuration rather than data, because the database cannot tell the difference
/// between a consumer that is absent and one that is behind.</strong> An unpublished row means
/// "the publisher has not sent it" where a publisher is wired and "nothing will ever send it"
/// where none is; a cursor row that has stopped moving means "the subscription is down" where the
/// flow is deployed and "nobody will ever read this" where it is not. Only the deployment knows
/// which, so it says — and the wrong answer here is a data loss in one direction and unbounded
/// growth in the other, which is why the default is the growth.
/// </para>
/// <para>
/// <strong>The subscriptions are the host's own <see cref="ChangeSubscription"/> values</strong>,
/// not a shape restated for this class. A cursor is keyed on the four terms they carry, so
/// anything less would be a second way to name a subscription and a chance to name it differently.
/// </para>
/// </remarks>
public sealed record RetentionConsumers
{
    /// <summary>One publisher, no change subscriptions: what ADR-0018 built and shipped.</summary>
    public static RetentionConsumers Default { get; } = new();

    /// <summary>
    /// Whether an <see cref="PostgresOutboxPublisher"/> drains this outbox to a broker.
    /// </summary>
    /// <remarks>
    /// True by default. A host that stages events and wires no publisher holds them rather than
    /// discarding them, which is the state ADR-0018 chose and the one an operator can see in
    /// <see cref="RetentionSweep.HeldForPendingEvents"/>.
    /// </remarks>
    public bool Publisher { get; init; } = true;

    /// <summary>Every change subscription this deployment serves over this schema.</summary>
    /// <remarks>
    /// <strong>Every one, not this node's.</strong> A cursor is a cluster-wide row and retention
    /// deletes cluster-wide rows, so a node declaring only what it happens to serve would purge
    /// events another node's subscription had not read. The set is the deployment's, which is why
    /// it is stated at registration rather than read from a catalogue.
    /// </remarks>
    public IReadOnlyList<ChangeSubscription> Subscriptions { get; init; } = [];
}

/// <summary>What one retention sweep removed, and what it refused to remove.</summary>
/// <param name="CompletedInstances">Completed instances past their window, with their steps.</param>
/// <param name="FailedInstances">Failed, timed-out and compensation-failed instances past theirs.</param>
/// <param name="PublishedEvents">
/// Outbox rows past the published window that every declared consumer is also past.
/// </param>
/// <param name="HeldForPendingEvents">
/// Instances past their window that were kept because they still hold an event a declared
/// consumer has not consumed. Persistently non-zero means a consumer is not draining — a
/// publisher that is not running or is stuck behind an event the broker keeps refusing, or a
/// change subscription whose cursor has stopped. The rows are safe; the pipeline is not, and this
/// is the number that says so before the disk does.
/// </param>
public readonly record struct RetentionSweep(
    int CompletedInstances,
    int FailedInstances,
    int PublishedEvents,
    int HeldForPendingEvents);
