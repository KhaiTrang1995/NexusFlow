namespace FlowX;

/// <summary>
/// One declared stream subscription, as the source that has to serve it holds it.
/// </summary>
/// <param name="FlowId">The windowing flow's business identity, from <c>[Flow]</c>.</param>
/// <param name="FlowVersion">The exact version this node would run it at.</param>
/// <param name="Source">The stream, as the manifest published it as <c>topic</c>.</param>
/// <param name="Group">The reader group, so two subscriptions over one stream keep two checkpoints.</param>
/// <remarks>
/// <see cref="ChangeSubscription"/>'s four terms, and for its reasons: all four are in every
/// instance id a closed window derives
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0055-a-window-names-the-instance-it-starts.md">ADR-0055</a>),
/// and all four are the checkpoint's key.
/// </remarks>
public sealed record StreamSubscription(
    string FlowId,
    string FlowVersion,
    string Source,
    string Group);

/// <summary>
/// How far through a stream a subscription has read, in whatever terms the source keeps.
/// </summary>
/// <param name="Value">
/// The position, rendered by the source that produced it. Opaque above the plugin: never parsed,
/// compared or ordered by anything outside the implementation that made it.
/// </param>
/// <remarks>
/// <para>
/// <strong>Opaque for <see cref="ChangePosition"/>'s reason, and one more that is specific to a
/// window.</strong> The engine has to decide which position is safe to checkpoint, and it does so
/// by <em>arrival order</em> — the longest prefix of admitted records whose windows have all
/// closed — never by comparing two positions. A host that could compare them would eventually
/// take a minimum over open windows, and a minimum over an opaque ordering is a guess.
/// </para>
/// </remarks>
public readonly record struct StreamPosition(string Value);

/// <summary>One record the source is offering, and where it sits in the stream.</summary>
/// <param name="Position">
/// The position a subscription reaches by finishing with this record. Handed back verbatim; the
/// engine never constructs one.
/// </param>
/// <param name="EventTime">
/// When the thing this record describes <em>happened</em> — not when it was read, and not when it
/// was written. This is the only clock the engine has: windows are assigned from it and the
/// watermark is derived from it
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0056-the-watermark-is-observed-never-wall-clock.md">ADR-0056</a>).
/// A source that cannot supply an event time cannot be windowed by this engine, and must say so
/// rather than substituting <c>DateTimeOffset.UtcNow</c>: a wall-clock stand-in makes every
/// replay produce different windows, which is precisely what a checkpoint is supposed to prevent.
/// </param>
/// <param name="PartitionKey">The key the record was ordered by, or null.</param>
/// <param name="Payload">
/// The record body exactly as the source held it, or null. Deliberately a string and not a
/// deserialised object, for <see cref="BusMessage.Payload"/>'s reason: only generated code can
/// name a <c>JsonTypeInfo</c>, so a host that deserialised would reflect.
/// </param>
/// <param name="TenantId">Whose record this is, or null on a deployment that does not isolate.</param>
public sealed record StreamRecord(
    StreamPosition Position,
    DateTimeOffset EventTime,
    string? PartitionKey = null,
    string? Payload = null,
    string? TenantId = null);

/// <summary>The errors a stream source, a checkpoint store or the engine returns as values.</summary>
public static class StreamErrors
{
    /// <summary>The checkpointed position is no longer retained by the source.</summary>
    public const string TrimmedCode = "stream.trimmed";

    /// <summary>
    /// A window's records would exceed the memory this engine is allowed to hold for them.
    /// </summary>
    public const string WindowOverflowCode = "stream.window_overflow";

    /// <summary>The declared window shape is not one this engine implements.</summary>
    public const string UnsupportedWindowCode = "stream.unsupported_window";

    /// <summary>
    /// The position the checkpoint names is behind what the source can still serve, so every
    /// record between the two is gone.
    /// </summary>
    /// <remarks>
    /// <strong>Terminal, and loudly so.</strong> A stream engine that carried on from the oldest
    /// retained record would silently skip a gap and emit windows computed from part of their
    /// input — the failure mode that makes an aggregation quietly wrong for ever. The
    /// subscription stops instead, which an operator can see.
    /// </remarks>
    public static Error Trimmed(StreamPosition position) => new(
        TrimmedCode,
        $"The stream has been trimmed past the checkpointed position '{position.Value}'. Every " +
        "record between it and the oldest retained one is gone, so no window that spans the gap " +
        "can be computed from its whole input. The subscription is stopped rather than " +
        "restarted from the oldest retained record, which would emit partial windows silently.",
        ErrorCategory.Unavailable);

    /// <summary>The engine is holding as many records as it is allowed to.</summary>
    public static Error WindowOverflow(int resident, int limit) => new(
        WindowOverflowCode,
        $"{resident} records are resident across this subscription's open windows, which is over " +
        $"the limit of {limit}. This engine buffers a window's records until the watermark closes " +
        "it, so a window wider than the arrival rate can fill is bounded memory or it is none; " +
        "the bound is FlowXOptions.StreamMaxResidentRecords. Narrow the window, raise the limit " +
        "if the memory is genuinely available, or aggregate upstream.",
        ErrorCategory.Unavailable);

    /// <summary>The window specification names a shape this engine does not implement.</summary>
    public static Error UnsupportedWindow(string window) => new(
        UnsupportedWindowCode,
        $"Window '{window}' is not a shape this engine implements. Tumbling windows " +
        "(tumbling:<duration>) are the whole of what it does; sliding, session and global " +
        "windows are declared by [StreamTrigger] and refused here and by FLOWX1042.",
        ErrorCategory.Validation);
}

/// <summary>
/// The seam between a stream and the runtime — a log read forwards from a position the engine
/// supplies.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="IChangeFeed"/>'s shape with the cursor moved out.</strong> A change feed
/// holds its own cursor and takes no position; a stream source takes one, because the engine's
/// checkpoint is not "the last record read" but "the last record whose window has closed", and
/// only the engine knows which that is. So the source is stateless about progress and the
/// checkpoint lives in <see cref="IStreamCheckpointStore"/> — two seams rather than one, and the
/// split is the whole reason a restart can rebuild an open window
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0055-a-window-names-the-instance-it-starts.md">ADR-0055</a>).
/// </para>
/// <para>
/// <strong>What an implementation must guarantee</strong>, and the whole of what
/// <c>FlowStreamPump</c> relies on:
/// </para>
/// <list type="number">
/// <item><description>
/// <strong>No gaps, in arrival order.</strong> Reading from a position returns every record after
/// it, in the order the source committed them, with nothing skipped.
/// </description></item>
/// <item><description>
/// <strong>A position is replayable.</strong> Reading from position <em>p</em> twice returns the
/// same records, until retention removes them — at which point the read must fail with
/// <see cref="StreamErrors.Trimmed"/> rather than silently starting later.
/// </description></item>
/// <item><description>
/// <strong>An event time per record.</strong> Supplied by the producer or read off the record;
/// never the reader's clock.
/// </description></item>
/// </list>
/// </remarks>
public interface IStreamSource
{
    /// <summary>The source family this implementation serves, e.g. <c>redis-stream</c>.</summary>
    string Stream { get; }

    /// <summary>Takes up to <paramref name="max"/> records after a position.</summary>
    /// <param name="subscription">The subscription being read.</param>
    /// <param name="after">
    /// The last position this subscription finished with, or null to start at the beginning of
    /// what the source retains.
    /// </param>
    /// <param name="max">How many records this read will take.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The records, oldest first; an empty list when the subscription is caught up; or an
    /// <see cref="Error"/> — <see cref="StreamErrors.Trimmed"/> when the position is gone.
    /// </returns>
    ValueTask<Result<IReadOnlyList<StreamRecord>>> ReadAsync(
        StreamSubscription subscription,
        StreamPosition? after,
        int max,
        CancellationToken cancellationToken);
}
