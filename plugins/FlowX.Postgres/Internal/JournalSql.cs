namespace FlowX.Postgres;

/// <summary>
/// Every statement the journal issues, as constants.
/// </summary>
/// <remarks>
/// <para>
/// Constants, and therefore not built from anything. The schema is selected by the
/// connection's <c>search_path</c> rather than spliced into the text, so no statement here
/// is assembled at run time and none of them can carry a value into a position where it
/// would be read as SQL.
/// </para>
/// <para>
/// Gathered in one file because they are the schema's read and write surface, and a
/// column added to a table has to be added to a known set of statements rather than
/// hunted for.
/// </para>
/// </remarks>
internal static class JournalSql
{
    /// <summary>PostgreSQL's <c>unique_violation</c>, the append-only refusal.</summary>
    public const string UniqueViolation = "23505";

    /// <summary>
    /// PostgreSQL's <c>insufficient_privilege</c>, which is what a row-level security
    /// <c>WITH CHECK</c> raises when a write would produce a row the writer could not read.
    /// </summary>
    /// <remarks>
    /// Migration <c>0006</c>'s policies are the only thing in this schema that raises it, and
    /// they raise it for exactly one mistake: opening an instance under a tenant the
    /// connection is not scoped to. It is caught and returned as
    /// <see cref="TenantErrors.CrossTenantDenied"/> rather than propagating, because a refusal
    /// is a value and only a broken store is an exception (ADR-0007) — and this is the store
    /// working correctly and saying no.
    /// </remarks>
    public const string InsufficientPrivilege = "42501";

    /// <summary>The <c>flow_instance</c> columns a read returns, in reader order.</summary>
    private const string InstanceColumns =
        """
        instance_id, flow_id, flow_version, tenant_id, state, fence, resume_from_step,
        input, state_bag, correlation_id, trace_id, deadline_at,
        parent_instance_id, parent_scope, parent_step_id, created_at, updated_at,
        wake_at, wake_step_id, wake_scope
        """;

    /// <summary>Opens the instance row. The primary key is what refuses a second start.</summary>
    public const string StartInstance =
        """
        INSERT INTO flow_instance (
            instance_id, flow_id, flow_version, tenant_id, state, fence,
            input, subject_digest, correlation_id, trace_id, deadline_at,
            parent_instance_id, parent_scope, parent_step_id)
        VALUES (
            @instance, @flow_id, @flow_version, @tenant_id, @state, @fence,
            @input, @subject_digest, @correlation_id, @trace_id, @deadline_at,
            @parent_instance, @parent_scope, @parent_step)
        """;

    /// <summary>Reads one instance row.</summary>
    public const string ReadInstance =
        $"SELECT {InstanceColumns} FROM flow_instance WHERE instance_id = @instance";

    /// <summary>
    /// Raises the fence, and reports enough to say why it did not.
    /// </summary>
    /// <remarks>
    /// One round trip that distinguishes all three outcomes. Reading first and updating
    /// second would leave a window in which another node's acquisition lands between the
    /// two, and this call is the one that closes exactly that kind of window.
    /// </remarks>
    public const string RaiseFence =
        """
        WITH current AS (
            SELECT fence FROM flow_instance WHERE instance_id = @instance
        ), raised AS (
            UPDATE flow_instance
               SET fence = @token, updated_at = now(), version = version + 1
             WHERE instance_id = @instance AND fence <= @token
            RETURNING fence
        )
        SELECT (SELECT fence FROM current) AS present,
               (SELECT fence FROM raised)  AS raised,
               EXISTS (SELECT 1 FROM current) AS found
        """;

    /// <summary>
    /// Takes the instance's row lock and reads what every write has to check against.
    /// </summary>
    /// <remarks>
    /// <c>FOR UPDATE</c> is what makes the fence check, the terminal check and the
    /// allocation of <c>next_sequence</c> one decision rather than three racing ones. A
    /// second writer on the same instance waits here, which is the correct place for it to
    /// wait: it is about to be told its token is stale.
    /// </remarks>
    public const string LockInstance =
        "SELECT state, fence, next_sequence FROM flow_instance WHERE instance_id = @instance FOR UPDATE";

    /// <summary>Appends one step row.</summary>
    public const string InsertStep =
        """
        INSERT INTO flow_step (
            instance_id, scope, step_id, attempt, sequence,
            capability_id, capability_version, outcome,
            result, nondeterministic, duration_ms)
        VALUES (
            @instance, @scope, @step, @attempt, @sequence,
            @capability_id, @capability_version, @outcome,
            @result, @nondeterministic, @duration_ms)
        RETURNING committed_at
        """;

    /// <summary>Stages one outbox row in the transaction that recorded the step.</summary>
    public const string InsertOutbox =
        """
        INSERT INTO outbox_event (
            event_id, instance_id, sequence, ordinal, type, schema_version, partition_key, payload)
        VALUES (@event, @instance, @sequence, @ordinal, @type, @schema_version, @partition_key, @payload)
        """;

    /// <summary>
    /// Advances the instance alongside the step it just committed.
    /// </summary>
    /// <remarks>
    /// Every optional column is <c>COALESCE</c>d against its present value, so a commit
    /// that carries no state bag leaves the last snapshot standing rather than erasing it,
    /// and a commit that carries no hint leaves the last hint standing — which is what
    /// <c>TheResumeHintIsNotTheResumeCursor</c> observes when it writes a hint on one step
    /// and expects to still read it after the next.
    /// </remarks>
    public const string AdvanceInstance =
        """
        UPDATE flow_instance
           SET next_sequence       = next_sequence + 1,
               state               = COALESCE(@state::text, state),
               state_bag           = COALESCE(@state_bag::json, state_bag),
               state_bag_sequence  = CASE WHEN @state_bag::json IS NULL
                                          THEN state_bag_sequence ELSE @sequence END,
               resume_from_step    = COALESCE(@resume_hint::int, resume_from_step),
               updated_at          = now(),
               version             = version + 1
         WHERE instance_id = @instance
        """;

    /// <summary>
    /// Moves the instance to the state it comes to rest in, fenced like every other write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three wake columns are assigned unconditionally rather than <c>COALESCE</c>d like
    /// the state bag, and the difference is the point. A state bag that is not supplied means
    /// "unchanged" — the last snapshot still describes the instance. A wake that is not
    /// supplied means <em>nothing is due to wake this instance</em>, which is the truth for
    /// every state but <c>Suspended</c> and has to overwrite whatever the instance was waiting
    /// on before. A completed instance keeping the instant it was parked at would be woken for
    /// ever by the timer sweep.
    /// </para>
    /// </remarks>
    public const string CompleteInstance =
        """
        UPDATE flow_instance
           SET state        = @state,
               state_bag    = COALESCE(@state_bag::json, state_bag),
               wake_at      = @wake_at,
               wake_step_id = @wake_step,
               wake_scope   = @wake_scope,
               updated_at   = now(),
               version      = version + 1
         WHERE instance_id = @instance
        """;

    /// <summary>Reads the instance and its committed steps for a resume.</summary>
    public const string ReadFrontierInstance = ReadInstance;

    /// <summary>Every committed step, in commit order.</summary>
    public const string ReadFrontierSteps =
        """
        SELECT scope, step_id, attempt, sequence, capability_id, capability_version,
               outcome, result, nondeterministic, duration_ms, committed_at
          FROM flow_step
         WHERE instance_id = @instance
         ORDER BY sequence
        """;

    /// <summary>Every staged event, in the order the steps that staged them committed.</summary>
    public const string ReadOutbox =
        """
        SELECT event_id, instance_id, type, schema_version, partition_key, payload, published_at
          FROM outbox_event
         WHERE instance_id = @instance
         ORDER BY sequence, ordinal
        """;
}
