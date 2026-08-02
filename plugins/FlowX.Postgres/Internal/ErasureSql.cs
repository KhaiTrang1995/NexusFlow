namespace FlowX.Postgres;

/// <summary>
/// The four statements an erasure is: one that finds, and three that clear.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing here deletes a row.</strong> An instance row also records that a flow ran, at
/// what version, with what outcome and when — the system's own operational history rather than
/// personal data about the subject — and a durable execution that vanished mid-recovery would be
/// indistinguishable from one that never happened. So every column that could carry a value the
/// subject supplied is set to null and the skeleton stays.
/// </para>
/// <para>
/// <strong>The handle is cleared last and deliberately.</strong> Once <c>subject_digest</c> is
/// null the row is unreachable by this predicate for ever, so clearing it before the payloads
/// would leave a crash between the two statements holding rows that are neither erased nor
/// findable. Inside one transaction the ordering is belt and braces; it is stated because the
/// next person to add a fifth statement has to know which end to add it at.
/// </para>
/// </remarks>
internal static class ErasureSql
{
    /// <summary>
    /// Every instance carrying this subject's handle, with what stands in the way of each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tenant predicate is stated rather than left to row-level security. The policy does
    /// apply — an erasure runs on a scoped connection like every other statement — but a
    /// destructive statement whose blast radius is decided entirely by a session setting is one
    /// mistake away from clearing another tenant's rows, and <c>IS NOT DISTINCT FROM</c> is the
    /// comparison that also means what it says for a single-tenant deployment's null.
    /// </para>
    /// <para>
    /// <c>owed</c> is counted rather than joined on, because a partial answer is the wrong
    /// shape: the caller has to be told which instances are being withheld and why, and a query
    /// that filtered them out would report a smaller <c>Matched</c> and read as success.
    /// </para>
    /// <para>
    /// "Owed" is <c>PostgresRetention</c>'s question, asked by the same predicate rather than by
    /// a copy of it — see <see cref="OutboxSql.OwedToAConsumer"/>. A deployment that stages
    /// events and reads them nowhere owes nothing and is not held; one that declares a publisher
    /// which is not running accumulates withheld instances, and the receipt names each of them
    /// rather than reporting a shorter list.
    /// </para>
    /// </remarks>
    public const string FindSubject =
        $"""
        SELECT i.instance_id,
               i.state,
               (SELECT count(*) FROM outbox_event e
                 WHERE e.instance_id = i.instance_id AND {OutboxSql.OwedToAConsumer}) AS owed
          FROM flow_instance i
         WHERE i.subject_digest = @digest
           AND i.tenant_id IS NOT DISTINCT FROM @tenant
         ORDER BY i.created_at
        """;

    /// <summary>Clears every recorded step result and captured nondeterministic read.</summary>
    /// <remarks>
    /// <c>nondeterministic</c> goes with <c>result</c>. It is the journal's record of what a step
    /// read that it could not have computed — a clock reading, a minted identifier, an external
    /// lookup — and a lookup keyed by the subject is as much about the subject as the result it
    /// produced.
    /// </remarks>
    public const string ClearSteps =
        """
        UPDATE flow_step
           SET result = NULL, nondeterministic = NULL
         WHERE instance_id = ANY(@instances)
           AND (result IS NOT NULL OR nondeterministic IS NOT NULL)
        """;

    /// <summary>Clears the body of every staged event these instances emitted.</summary>
    /// <remarks>
    /// Only reachable for instances the caller has already established owe nothing to any
    /// declared consumer: emptying a body a publisher has not drained would put a message on a
    /// broker that does not carry what its type says it carries.
    /// </remarks>
    public const string ClearEvents =
        """
        UPDATE outbox_event
           SET payload = NULL
         WHERE instance_id = ANY(@instances)
           AND payload IS NOT NULL
        """;

    /// <summary>Clears the input, the state bag and the handle itself.</summary>
    /// <remarks>
    /// <c>version</c> is advanced because this is a write like any other and an optimistic
    /// reader holding the previous value must see that the row moved. <c>updated_at</c> is what
    /// an operator reads to answer "when was this erased", which is the one fact about the
    /// erasure the row itself is allowed to keep.
    /// </remarks>
    public const string ClearInstances =
        """
        UPDATE flow_instance
           SET input = NULL,
               state_bag = NULL,
               subject_digest = NULL,
               updated_at = now(),
               version = version + 1
         WHERE instance_id = ANY(@instances)
        """;
}
