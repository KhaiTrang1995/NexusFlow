namespace FlowX.Postgres;

/// <summary>
/// The two statements the outbox publisher issues, as constants.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="JournalSql"/> for the same reason
/// <c>PostgresOutboxPublisher</c> is separate from <c>PostgresFlowJournal</c>: staging an
/// event is part of a step commit and belongs to the journal's write surface, while
/// claiming, publishing and marking are a different transaction issued by a different
/// process on a different schedule. Gathering them here keeps "what the publisher touches"
/// answerable by reading one file.
/// </para>
/// <para>
/// Constants, and therefore not built from anything. The schema is selected by the
/// connection's <c>search_path</c>, so no statement here is assembled at run time.
/// </para>
/// </remarks>
internal static class OutboxSql
{
    /// <summary>
    /// Claims a batch of pending events for this transaction, in staging order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong><c>FOR UPDATE SKIP LOCKED</c> is what makes two publishers safe.</strong> A
    /// row this transaction claims is locked until it commits or rolls back, and a second
    /// publisher running the same statement steps over it rather than waiting behind it. Two
    /// publishers therefore claim disjoint sets and cannot both send the same event; the
    /// alternative — claim by <c>UPDATE … SET claimed_by</c> — needs a second write per event
    /// and a reaper for the publisher that died holding a claim.
    /// </para>
    /// <para>
    /// <strong>The <c>NOT EXISTS</c> is per-<c>partition_key</c> ordering, and it is the
    /// clause that survives a second publisher.</strong> Claiming rows by staging order
    /// alone is not enough: publisher A can hold key <c>K</c>'s older event while publisher B
    /// skips over it and claims K's newer one, and B would then reach the broker first. So a
    /// claimed row is dropped from this batch when its key has an older pending event that
    /// this claim did not take — held by another publisher, or simply beyond the batch limit.
    /// It stays pending and is claimed in a later pass, behind the sibling it must follow.
    /// </para>
    /// <para>
    /// <strong>A null <c>partition_key</c> is exempt, because it means "no order asked
    /// for".</strong> <c>OutboxWrite.PartitionKey</c> documents null as an unordered event,
    /// and holding one behind another null-keyed event would invent a guarantee nobody
    /// declared and serialise the whole unkeyed stream to do it.
    /// </para>
    /// <para>
    /// <strong>The join is what puts a tenant on the wire.</strong> <c>outbox_event</c> carries
    /// no <c>tenant_id</c> and 0008's comment says why; the emitting instance's row has one, and
    /// it is the same key the table's own policy decides visibility through. A publisher fanned
    /// out per schema already knows the tenant from the schema it claimed in, so this changes
    /// nothing there — what it adds is the answer at row isolation, where the schema says
    /// nothing and the consumer of the published message would otherwise have no tenant to start
    /// its flow in.
    /// </para>
    /// <para>
    /// <c>MATERIALIZED</c> is stated rather than inferred. <c>claimed</c> is referenced three
    /// times and PostgreSQL would materialise it anyway, but the locking is the point: the
    /// CTE must be evaluated exactly once, taking exactly one set of row locks, and saying so
    /// removes the question.
    /// </para>
    /// </remarks>
    public const string ClaimPending =
        """
        WITH claimed AS MATERIALIZED (
            SELECT event_id, instance_id, type, schema_version, partition_key, payload, staged_seq
              FROM outbox_event
             WHERE published_at IS NULL
             ORDER BY staged_seq
             LIMIT @batch
               FOR UPDATE SKIP LOCKED
        )
        SELECT c.event_id, c.instance_id, c.type, c.schema_version, c.partition_key, c.payload,
               i.tenant_id
          FROM claimed c
          JOIN flow_instance i ON i.instance_id = c.instance_id
         WHERE c.partition_key IS NULL
            OR NOT EXISTS (
                   SELECT 1
                     FROM outbox_event o
                    WHERE o.published_at IS NULL
                      AND o.partition_key = c.partition_key
                      AND o.staged_seq < c.staged_seq
                      AND NOT EXISTS (
                              SELECT 1 FROM claimed k WHERE k.event_id = o.event_id))
         ORDER BY c.staged_seq
        """;

    /// <summary>
    /// Marks the events the broker acknowledged.
    /// </summary>
    /// <remarks>
    /// Issued in the transaction that claimed them, so the rows are still locked and no
    /// second publisher can have touched them in between. A crash before the enclosing
    /// <c>COMMIT</c> loses this statement and leaves every row pending, which is the
    /// at-least-once half of <c>docs/11-Distributed-Runtime.md §4</c>: the event is delivered
    /// twice rather than lost once.
    /// </remarks>
    public const string MarkPublished =
        """
        UPDATE outbox_event
           SET published_at = now()
         WHERE event_id = ANY(@events)
        """;
}
