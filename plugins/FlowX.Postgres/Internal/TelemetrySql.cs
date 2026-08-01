namespace FlowX.Postgres;

/// <summary>
/// The three questions <a href="../../../docs/12-Observability.md">12-Observability</a> §3 asks
/// of a store rather than of a code path.
/// </summary>
/// <remarks>
/// <para>
/// A gauge is a value read on collection, not written on an event, and the three gauges in §3's
/// table are all facts about rows: how many events are unpublished, how old the oldest one is,
/// and how many instances are not finished. None of them can be maintained by an in-process
/// counter, because none of them is about this process — an event staged by a node that has
/// since been replaced is still pending, and a counter in the new node starts at zero.
/// </para>
/// </remarks>
internal static class TelemetrySql
{
    /// <summary>
    /// <c>flowx_outbox_pending</c>: unpublished events by type.
    /// </summary>
    /// <remarks>
    /// §3 writes this one out in full — "<c>SELECT count(*) … WHERE published_at IS NULL GROUP
    /// BY type</c> over <c>outbox_event</c> is exactly this gauge" — and it is. The predicate
    /// is served by <c>outbox_event_pending_idx</c> from migration <c>0001</c>.
    /// </remarks>
    public const string PendingByType =
        """
        SELECT type, count(*)
          FROM outbox_event
         WHERE published_at IS NULL
         GROUP BY type
        """;

    /// <summary>
    /// <c>flowx_outbox_lag_seconds</c>: how long the oldest unpublished event of each type has
    /// been waiting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This needs no migration, and the claim that it did was wrong about which table
    /// holds the fact.</strong> §3 said "the age of a pending event is a fact the schema does
    /// not hold" and that "this gauge needs a column before it needs a meter", on the grounds
    /// that <c>outbox_event</c> carries exactly one timestamp — <c>published_at</c>, <c>NULL</c>
    /// for precisely these rows. That much is true of <c>outbox_event</c> alone, and it is not
    /// true of the schema.
    /// </para>
    /// <para>
    /// <strong>The staging timestamp is <c>flow_step.committed_at</c>, and the join to it is
    /// exact.</strong> <c>PostgresFlowJournal.CommitAsync</c> inserts the step row and stages
    /// the event in one transaction with one <c>sequence</c> value — <c>InsertStepAsync</c> and
    /// <c>StageOutboxAsync</c> are handed the same local — so <c>(instance_id, sequence)</c>
    /// identifies the step that staged the event, and <c>flow_step_sequence_idx</c> is
    /// <c>UNIQUE</c> on that pair. <c>committed_at</c> defaults to <c>now()</c>, evaluated
    /// inside that same transaction. So the age of a pending event is the age of the step
    /// commit that staged it, recorded since migration <c>0001</c>.
    /// </para>
    /// <para>
    /// <strong>ADR-0018 did not reject this, and reopening it is not required.</strong> What it
    /// rejected is "order by <c>now()</c> at staging time", because "a timestamp is not
    /// monotonic across nodes and ties are ordinary at commit granularity, so the order would be
    /// approximately right, which for an ordering guarantee is the same as wrong". Every word of
    /// that is about <em>ordering</em>, and none of it transfers to a duration: this gauge does
    /// not order anything, ties are irrelevant to it, and clock skew between nodes enters as a
    /// bounded error on a number whose SLO in §7 is five seconds with an alert at thirty. A
    /// quantity that is approximately right is what a lag gauge is; a sequence that is
    /// approximately ordered is not an order. <c>staged_seq</c> remains the ordering key and is
    /// not read here.
    /// </para>
    /// <para>
    /// <strong>The join cannot miss.</strong> Both tables cascade from <c>flow_instance</c>, and
    /// there is no path that stages an event without inserting the step row first — the insert
    /// precedes the staging call in the same transaction, and a duplicate-key failure on it
    /// returns before anything is staged. An <c>INNER JOIN</c> is therefore the right shape:
    /// were one ever to miss, a silently absent series is the correct report, because a lag this
    /// query cannot date is a lag it must not guess.
    /// </para>
    /// </remarks>
    public const string LagSecondsByType =
        """
        SELECT event.type,
               EXTRACT(EPOCH FROM (now() - min(step.committed_at)))
          FROM outbox_event AS event
          JOIN flow_step AS step
            ON step.instance_id = event.instance_id
           AND step.sequence    = event.sequence
         WHERE event.published_at IS NULL
         GROUP BY event.type
        """;

    /// <summary>
    /// <c>flowx_flow_active</c>: instances that have not reached a terminal state, by flow and
    /// state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Read from the journal rather than counted in the host, because
    /// <c>Suspended</c> is the state the metric exists for.</strong> §9 diagnoses stuck flows
    /// with <c>flowx_flow_active{state=Suspended|Running}</c> growing. A suspended instance is a
    /// row and not a thing in a process — it is parked precisely so that no process is holding
    /// it — so an in-process counter can only ever report the <c>Running</c> half, and only the
    /// half this node is running.
    /// </para>
    /// <para>
    /// <strong>§3 and §9 both said <c>state=Suspended</c> would be permanently zero because
    /// nothing sets it. That expired.</strong> <c>FlowEngine.InstanceStateFor</c> answers
    /// <c>FlowInstanceState.Suspended</c> for a flow that stopped at a suspension point, and
    /// <c>CloseInstanceAsync</c> writes it; WP-63 is the work package that did it, which is the
    /// same WP-63 those notes named as the reason it could not happen.
    /// </para>
    /// <para>
    /// <strong>The four non-terminal states, named rather than negated.</strong>
    /// <c>flow_instance_state_check</c> pins the closed set, and listing the live half here
    /// means a state added to that constraint arrives in this gauge as a deliberate edit rather
    /// than silently through a <c>NOT IN</c>. It is also the same set both shipped recovery
    /// indexes list, minus none.
    /// </para>
    /// <para>
    /// <strong>Every node reports the same numbers.</strong> This is a global count, so a
    /// deployment with three nodes publishes it three times and an exporter that sums across
    /// them triples it. That is inherent to a store-backed gauge — it is equally true of the
    /// <c>flowx_outbox_pending</c> query §3 writes out itself — and the answer is the ordinary
    /// one: aggregate with <c>max</c> across instances, not <c>sum</c>.
    /// </para>
    /// </remarks>
    public const string ActiveByFlowAndState =
        """
        SELECT flow_id, state, count(*)
          FROM flow_instance
         WHERE state IN ('Pending', 'Running', 'Suspended', 'Compensating')
         GROUP BY flow_id, state
        """;
}
