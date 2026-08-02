namespace FlowX;

/// <summary>
/// What a closed window hands the flow it starts: the interval, and every record assigned to it.
/// </summary>
/// <param name="Source">The stream, exactly as declared and published.</param>
/// <param name="WindowStart">The window's inclusive lower bound, in event time.</param>
/// <param name="WindowEnd">The window's exclusive upper bound, in event time.</param>
/// <param name="Records">
/// The records the watermark closed this window over, in arrival order. Never empty: a window
/// with no records is not opened, so a flow is never started for an interval nothing happened in.
/// </param>
/// <remarks>
/// <para>
/// <strong>This type exists for <see cref="ScheduledFire"/>'s reason.</strong> A windowing flow
/// may not read a clock — <c>FLOWX1007</c> and <c>FLOWX1011</c> forbid it, and a
/// <c>Streaming</c> flow is journaled and replayed like a <c>Durable</c> one, so an ambient
/// <c>UtcNow</c> would make a resumed instance compute a different answer from the one it
/// committed. The interval it is aggregating is therefore input, journaled on
/// <c>flow_instance.input</c> and read back verbatim on a resume.
/// </para>
/// <para>
/// <strong>It is also the instance's identity, one derivation away.</strong>
/// <c>StreamIdentity.InstanceIdFor</c> derives the id from the flow, the subscription and this
/// interval, which is why the bounds are carried rather than only the records: a window rebuilt
/// after a node death derives the same id and is refused by the journal's primary key instead of
/// being aggregated twice
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0055-a-window-names-the-instance-it-starts.md">ADR-0055</a>).
/// </para>
/// <para>
/// <strong>A flow declaring <c>[StreamTrigger]</c> must take this as its input</strong>, and
/// <c>FLOWX1042</c> reports one that does not.
/// </para>
/// <para>
/// <strong>The records arrive with their bodies undeserialised</strong>, for
/// <see cref="BusMessage.Payload"/>'s reason. A flow that wants typed records deserialises them
/// in a capability, where a serialiser context is in scope and the failure is a <c>Result</c>.
/// </para>
/// </remarks>
public sealed record StreamWindowBatch(
    string Source,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    IReadOnlyList<StreamRecord> Records);

/// <summary>
/// Where a record too late for its window goes, so that lateness is never a silent drop.
/// </summary>
/// <remarks>
/// <para>
/// <strong>docs/09 §9 requires the side output by name</strong> — "records later than
/// <c>Lateness</c> are routed to a side output rather than dropped silently" — and this is the
/// whole of it. There is no second flow and no dead-letter stream: a sink the host registers is
/// the narrowest thing that discharges the promise, and it puts the decision about what to do
/// with a late record in the application that knows.
/// </para>
/// <para>
/// <strong>A host that registers a stream subscription and no sink is refused at startup.</strong>
/// The alternative is a default that discards, which is the behaviour the promise exists to
/// forbid, dressed as a convenience.
/// </para>
/// <para>
/// <strong>At-least-once, so it must be idempotent.</strong> A node that dies between routing a
/// late record and committing its checkpoint re-reads that record and routes it again. The
/// engine cannot deduplicate it: a late record belongs to a window that has already closed, so
/// there is no instance id for the journal to refuse.
/// </para>
/// </remarks>
public interface IStreamSideOutput
{
    /// <summary>Takes one record that arrived after its window closed.</summary>
    /// <param name="subscription">The subscription that read it.</param>
    /// <param name="record">The record.</param>
    /// <param name="watermark">The watermark at the moment it was rejected, for the operator.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The record, once it is somewhere durable, or an <see cref="Error"/>. A failure holds the
    /// checkpoint: a late record whose sink refused it has not been dealt with, and advancing
    /// past it would be the silent drop this seam exists to prevent.
    /// </returns>
    ValueTask<Result<StreamRecord>> OnLateAsync(
        StreamSubscription subscription,
        StreamRecord record,
        DateTimeOffset watermark,
        CancellationToken cancellationToken);
}

/// <summary>
/// Where a stream subscription's progress is kept: one position per subscription, durable.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Separate from <see cref="IStreamSource"/> because the safe position is the engine's
/// to decide.</strong> A source knows what it has served; only the engine knows which of those
/// records belong to a window that has closed and whose flow reached a recorded outcome. See
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0055-a-window-names-the-instance-it-starts.md">ADR-0055</a>
/// for what that position means and what a node death costs.
/// </para>
/// <para>
/// <strong>Committed after the flows have run, never before.</strong> The other order is
/// at-most-once and loses every window a crash lands in the middle of.
/// </para>
/// </remarks>
public interface IStreamCheckpointStore
{
    /// <summary>Where this subscription had got to, or null if it has never committed.</summary>
    /// <param name="subscription">The subscription.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The position, null for a subscription with no checkpoint, or an <see cref="Error"/>.</returns>
    ValueTask<Result<StreamPosition?>> ReadAsync(
        StreamSubscription subscription, CancellationToken cancellationToken);

    /// <summary>Records that every record up to and including a position has been dealt with.</summary>
    /// <param name="subscription">The subscription.</param>
    /// <param name="position">The position.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// <c>true</c> when the checkpoint moved, <c>false</c> when it was already there, or an
    /// <see cref="Error"/>. <c>false</c> is success: a node that committed and then died repeats
    /// the commit when it resumes.
    /// </returns>
    ValueTask<Result<bool>> CommitAsync(
        StreamSubscription subscription,
        StreamPosition position,
        CancellationToken cancellationToken);
}
