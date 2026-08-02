namespace FlowX.Postgres;

/// <summary>
/// The two statements the change feed issues, as constants.
/// </summary>
/// <remarks>
/// Separate from <see cref="OutboxSql"/> for the reason that one is separate from
/// <c>JournalSql</c>: draining the outbox to a broker and reading it forward from a cursor are
/// two different transactions, issued by two different processes, on two different schedules —
/// and they must not touch each other's columns. Gathering these here keeps "what a change
/// subscription touches" answerable by reading one file: <c>outbox_event</c> read-only, and
/// <c>change_cursor</c>.
/// </remarks>
internal static class ChangeFeedSql
{
    /// <summary>
    /// Reads one subscription's next batch: one type, below the visibility barrier, after the
    /// cursor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The barrier is the correctness of the whole feed</strong>
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0048-a-change-feed-advances-a-cursor.md">ADR-0048</a>).
    /// <c>pg_snapshot_xmin(pg_current_snapshot())</c> is the oldest transaction still in flight,
    /// so a row whose <c>staged_xid</c> is below it was staged by a transaction that has
    /// finished — and no transaction that could still insert a row with an equal or lower stamp
    /// exists. Advancing a cursor past such a row therefore cannot skip a row that has not been
    /// written yet, which is the hazard <c>0004</c>'s own comment describes for
    /// <c>staged_seq</c>.
    /// </para>
    /// <para>
    /// <strong>The cursor is the pair, compared as a row.</strong> <c>staged_seq</c> alone is not
    /// safe — a transaction takes its id at its first write, which is normally a step row long
    /// before its outbox insert, so a row inserted earlier can carry a higher stamp — and
    /// <c>staged_xid</c> alone cannot separate two events one transaction staged. The pair
    /// orders both, and PostgreSQL's row comparison expresses it in one predicate the index on
    /// <c>(type, staged_xid, staged_seq)</c> can answer.
    /// </para>
    /// <para>
    /// <strong><c>published_at</c> is not read, and that is the point.</strong> That column is
    /// <see cref="PostgresOutboxPublisher"/>'s progress; a feed that filtered on it would consume
    /// the same rows the broker path consumes and the two would take events from each other
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0050-a-change-trigger-observes-the-outbox.md">ADR-0047</a>).
    /// </para>
    /// <para>
    /// <strong>No locking clause.</strong> A reader takes nothing: one node at a time reads a
    /// subscription because the host holds a lease over it, and a second reader that slipped
    /// through would be refused by the journal's primary key rather than by a row lock.
    /// </para>
    /// </remarks>
    public const string ReadFrom =
        """
        SELECT event_id, type, schema_version, partition_key, payload::text,
               staged_xid::text, staged_seq
          FROM outbox_event
         WHERE type = @type
           AND staged_xid < pg_snapshot_xmin(pg_current_snapshot())
           AND (staged_xid, staged_seq) > (@position_xid::xid8, @position_seq)
         ORDER BY staged_xid, staged_seq
         LIMIT @batch
        """;

    /// <summary>Reads where a subscription's cursor is, or nothing when it has never read.</summary>
    /// <remarks>
    /// A missing row is the beginning of the feed rather than an error: a subscription deployed
    /// today has read nothing, and starting it from <c>('0', 0)</c> means it observes every
    /// change the outbox still holds. That is a deliberate consequence — see
    /// <see cref="PostgresChangeFeed"/>.
    /// </remarks>
    public const string ReadCursor =
        """
        SELECT position_xid::text, position_seq
          FROM change_cursor
         WHERE subscription_id = @subscription
        """;

    /// <summary>Moves a subscription's cursor forward, and never backward.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The <c>WHERE</c> on the update is what makes the operation monotonic</strong>,
    /// which <c>IChangeFeed.CommitAsync</c> requires of every implementation. A node that was
    /// fenced out mid-batch, or a pass that raced a faster node, must not move a cursor
    /// backwards and re-run everything between: that would be a correctness bug the journal's
    /// primary key would hide as a wall of deduplications.
    /// </para>
    /// <para>
    /// Written as an upsert so the first commit needs no separate insert path, and so two nodes
    /// committing the same subscription for the first time cannot both insert.
    /// </para>
    /// </remarks>
    public const string CommitCursor =
        """
        INSERT INTO change_cursor (subscription_id, position_xid, position_seq)
        VALUES (@subscription, @position_xid::xid8, @position_seq)
        ON CONFLICT (subscription_id) DO UPDATE
           SET position_xid = EXCLUDED.position_xid,
               position_seq = EXCLUDED.position_seq,
               updated_at   = now()
         WHERE (change_cursor.position_xid, change_cursor.position_seq)
             < (EXCLUDED.position_xid, EXCLUDED.position_seq)
        """;
}
