using FlowX;

namespace RealtimeStream;

/// <summary>One device's reading, as a producer wrote it onto <c>device.telemetry</c>.</summary>
/// <param name="DeviceId">Which device reported. Also the stream's partition key.</param>
/// <param name="Celsius">The temperature it measured.</param>
/// <param name="Humidity">Relative humidity, as a fraction between zero and one.</param>
/// <remarks>
/// <para>
/// <strong>This type is never the flow's input, and that is the shape of the whole
/// sample.</strong> A stream-triggered flow takes a <see cref="StreamWindowBatch"/> — an
/// interval and the records the watermark closed it over — and each record's
/// <see cref="StreamRecord.Payload"/> arrives as the string the source held. So this contract is
/// what <see cref="FoldReadings"/> deserialises <em>inside a capability</em>, where a
/// source-generated <c>JsonSerializerContext</c> is in scope and a malformed body is a
/// <c>Result</c> rather than an exception thrown in the engine's step loop.
/// </para>
/// <para>
/// There is no timestamp on it. The event time is on <see cref="StreamRecord.EventTime"/>,
/// which is where the engine reads it from and the only clock it has; a second copy in the body
/// would be a second answer to "when did this happen" and the two would eventually disagree.
/// </para>
/// </remarks>
public sealed record TelemetryReading(string DeviceId, double Celsius, double Humidity);

/// <summary>
/// What one closed window aggregated to: the interval, and the statistics over its records.
/// </summary>
/// <param name="Source">The stream the window was cut from.</param>
/// <param name="WindowStart">Inclusive lower bound, in event time.</param>
/// <param name="WindowEnd">Exclusive upper bound, in event time.</param>
/// <param name="Devices">How many distinct devices reported inside the interval.</param>
/// <param name="Readings">How many records were folded.</param>
/// <param name="Malformed">
/// How many records carried a body this application could not read. Counted rather than thrown:
/// one unreadable record in a window of nine hundred is a producer bug, and refusing the window
/// would lose the eight hundred and ninety-nine that were fine.
/// </param>
/// <param name="MinCelsius">The lowest temperature in the interval.</param>
/// <param name="MaxCelsius">The highest.</param>
/// <param name="MeanCelsius">The arithmetic mean over <paramref name="Readings"/>.</param>
/// <remarks>
/// <strong>The interval is carried, not recomputed.</strong> It comes off the
/// <see cref="StreamWindowBatch"/> the engine handed the flow, which is journaled on the
/// instance's input — so a resumed instance aggregates the same interval it committed to rather
/// than one derived from when it happened to resume. A <c>Streaming</c> flow may not read a
/// clock at all (<c>FLOWX1007</c>, <c>FLOWX1011</c>), and this is the field that makes it not
/// need one.
/// </remarks>
public sealed record DeviceStats(
    string Source,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    int Devices,
    int Readings,
    int Malformed,
    double MinCelsius,
    double MaxCelsius,
    double MeanCelsius);

/// <summary>Whether a window's statistics are worth waking somebody for.</summary>
/// <param name="IsAnomalous">Whether any rule fired.</param>
/// <param name="Reason">Which rule, or null when none did.</param>
public sealed record AnomalyVerdict(bool IsAnomalous, string? Reason);

/// <summary>
/// A window's statistics together with its verdict, which is what the store is asked to keep.
/// </summary>
/// <remarks>
/// The one step whose input is built from more than one prior result, which is what
/// <c>.Step&lt;TCapability, TStepIn&gt;(map)</c> exists for: nothing in the flow produces this,
/// because it is the join of the fold and the detection.
/// </remarks>
public sealed record AggregateToPersist(DeviceStats Stats, bool IsAnomalous, string? Reason);

/// <summary>A window's aggregate, once the store holds it.</summary>
/// <param name="Key">
/// What the store filed it under. Derived from the source and the interval, so a window written
/// twice overwrites rather than duplicating — which is what makes the at-least-once checkpoint
/// safe at the far end as well as at the near one.
/// </param>
public sealed record PersistedAggregate(string Key);

/// <summary>Published once a window's aggregate is durable.</summary>
/// <remarks>
/// Staged into the outbox by the same transaction that commits the step, because a
/// <c>Streaming</c> flow is journaled exactly as a <c>Durable</c> one is and so has a
/// transaction to stage into. That is the condition <c>FLOWX1024</c> checks.
/// </remarks>
public sealed record AggregateComputed(
    string Source,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    int Readings,
    double MeanCelsius,
    bool IsAnomalous);

/// <summary>
/// One record that arrived after its window had already closed, as the side output keeps it.
/// </summary>
/// <param name="Source">The stream it was read from.</param>
/// <param name="Position">Where the source served it from.</param>
/// <param name="EventTime">When the thing it describes happened.</param>
/// <param name="Watermark">Where the watermark was at the moment it was refused.</param>
/// <param name="Payload">The body, exactly as the source held it.</param>
/// <remarks>
/// <para>
/// <strong><see cref="Watermark"/> is the field that makes this useful.</strong> "This record
/// was late" is not actionable; "this record was 4 minutes 12 seconds behind the watermark when
/// it arrived, and the declared lateness is 10 seconds" tells an operator whether to widen the
/// lateness or to go and find the producer whose clock is wrong.
/// </para>
/// <para>
/// <strong>At-least-once, so whatever consumes these must tolerate a repeat.</strong> A node
/// that dies between routing a late record and committing its checkpoint re-reads that record
/// and routes it again, and the engine cannot deduplicate it: a late record belongs to a window
/// that has already closed, so there is no instance id for the journal to refuse.
/// </para>
/// </remarks>
public sealed record LateReading(
    string Source,
    string Position,
    DateTimeOffset EventTime,
    DateTimeOffset Watermark,
    string? Payload)
{
    /// <summary>How far behind the watermark this record was when it arrived.</summary>
    public TimeSpan Lateness => Watermark - EventTime;
}
