using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using FlowX;

namespace RealtimeStream;

/// <summary>Source-generated serialisation for the contracts that cross a boundary.</summary>
/// <remarks>
/// <para>
/// <strong>Every contract a journaled flow writes into its state bag is in here, and
/// <c>FLOWX1006</c> is what says so.</strong> A <c>Streaming</c> flow journals each step's
/// result exactly as a <c>Durable</c> one does — that is what makes the instance id a rebuilt
/// window derives refuse the second run — and a value reaches the journal only through
/// <c>JournalPayload.Of&lt;T&gt;</c>, which requires the generated <c>JsonTypeInfo&lt;T&gt;</c>.
/// There is no overload that reflects, which is what keeps the write path trim- and
/// NativeAOT-safe. The list is not maintained by guesswork: the compiler names the contract it
/// cannot find.
/// </para>
/// <para>
/// <strong><see cref="StreamWindowBatch"/> is in here because it is the flow's input</strong>,
/// and a stream flow's input is journaled on <c>flow_instance.input</c> so that a resumed
/// instance aggregates the interval it committed to. <see cref="StreamRecord"/> and
/// <see cref="StreamPosition"/> follow it because they are what a batch is made of.
/// </para>
/// <para>
/// <strong><see cref="TelemetryReading"/> is in here for a different reason from all of
/// them.</strong> It is never journaled: it is the shape of a record's body on the wire, and
/// <see cref="FoldReadings"/> names this context explicitly to read one. It is in the same
/// context rather than a second because a producer and a consumer of one stream disagreeing
/// about a naming policy is a bug that shows up as every field being null.
/// </para>
/// <para>
/// The camelCase policy matches what a producer conventionally writes. Without it the wire
/// names are the C# ones and a body carrying <c>"deviceId"</c> deserialises to a
/// <see cref="TelemetryReading"/> with a null device — a missing member deserialises to
/// <c>default</c>, silently.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TelemetryReading))]
[JsonSerializable(typeof(StreamWindowBatch))]
[JsonSerializable(typeof(StreamRecord))]
[JsonSerializable(typeof(StreamPosition))]
[JsonSerializable(typeof(DeviceStats))]
[JsonSerializable(typeof(AnomalyVerdict))]
[JsonSerializable(typeof(AggregateToPersist))]
[JsonSerializable(typeof(PersistedAggregate))]
[JsonSerializable(typeof(AggregateComputed))]
[JsonSerializable(typeof(LateReading))]
public sealed partial class TelemetryJsonContext : JsonSerializerContext;

/// <summary>Where a window's aggregate is kept.</summary>
/// <remarks>
/// <para>
/// The seam a real deployment puts Timescale, ClickHouse or InfluxDB behind, and the seam
/// <c>BoundedMemoryTests</c> puts a deliberately slow implementation behind. It is an interface
/// for the second reason as much as the first: "the sink is slower than the stream" is a
/// property of the sink, and the only honest way to test what the engine does about it is to
/// supply one that is.
/// </para>
/// <para>
/// <strong><see cref="WriteAsync"/> is an upsert, and the interface says so rather than leaving
/// it to each implementation.</strong> The checkpoint is committed after a window's flow has
/// run, so a crash in between makes the engine rebuild that window and run it again; the journal
/// refuses the duplicate instance, but a node that died before the journal recorded anything
/// reaches this method twice with the same key. An implementation that appended would turn the
/// platform's at-least-once into a double-counted aggregate.
/// </para>
/// </remarks>
public interface IAggregateStore
{
    /// <summary>Writes one window's aggregate, replacing any aggregate already under the key.</summary>
    /// <param name="key">What to file it under, from <see cref="PersistAggregate.KeyFor"/>.</param>
    /// <param name="aggregate">The window's statistics and its verdict.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the aggregate is durable.</returns>
    ValueTask WriteAsync(string key, AggregateToPersist aggregate, CancellationToken cancellationToken);
}

/// <summary>Where a record too late for its window goes.</summary>
/// <remarks>
/// Separate from <see cref="IStreamSideOutput"/> so that the sample's sink is a plain object a
/// test can read, and the adapter that satisfies the platform's seam is three lines. A real
/// deployment writes these to a topic, a table or an alert.
/// </remarks>
public interface ILateReadingLog
{
    /// <summary>Takes one late record.</summary>
    /// <param name="reading">The record, and how far behind the watermark it was.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the record is somewhere durable.</returns>
    ValueTask WriteAsync(LateReading reading, CancellationToken cancellationToken);
}

/// <summary>
/// The platform's side-output seam, over this application's <see cref="ILateReadingLog"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Registering this is not optional, and the host will not read a stream without
/// it.</strong> docs/09 §9 promises that a record later than the declared lateness is "routed to
/// a side output rather than dropped silently"; a host with no <see cref="IStreamSideOutput"/>
/// has nowhere to route one, so <c>AddFlowX</c> builds no stream pass at all rather than
/// defaulting to a sink that discards. A default that discarded would be the silent drop the
/// promise exists to forbid, with a type name on it.
/// </para>
/// <para>
/// <strong>A failure here holds the checkpoint, on purpose.</strong> Returning an
/// <see cref="Error"/> stops the pass and leaves the checkpoint behind the late record, so the
/// next pass offers it again. That is the whole point of the seam: a late record whose sink
/// refused it has not been dealt with, and advancing past it would be the silent drop by another
/// route.
/// </para>
/// </remarks>
public sealed class TelemetrySideOutput : IStreamSideOutput
{
    private readonly ILateReadingLog _log;

    /// <summary>Creates the side output.</summary>
    /// <param name="log">Where a late reading is kept.</param>
    /// <exception cref="ArgumentNullException"><paramref name="log"/> is null.</exception>
    public TelemetrySideOutput(ILateReadingLog log)
    {
        ArgumentNullException.ThrowIfNull(log);

        _log = log;
    }

    /// <inheritdoc />
    public async ValueTask<Result<StreamRecord>> OnLateAsync(
        StreamSubscription subscription,
        StreamRecord record,
        DateTimeOffset watermark,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(record);

        await _log
            .WriteAsync(
                new LateReading(
                    subscription.Source,
                    record.Position.Value,
                    record.EventTime,
                    watermark,
                    record.Payload),
                cancellationToken)
            .ConfigureAwait(false);

        return record;
    }
}

/// <summary>The aggregate store this sample runs on: a dictionary, keyed as the store demands.</summary>
/// <remarks>
/// In memory, so <c>dotnet run</c> needs no time-series database on top of the PostgreSQL and
/// Redis it already needs. The capability does not know or care — which is the point of the
/// seam, and the reason the sample's story survives someone putting Timescale behind it.
/// </remarks>
internal sealed class InMemoryAggregateStore : IAggregateStore
{
    private readonly ConcurrentDictionary<string, AggregateToPersist> _aggregates = new(StringComparer.Ordinal);

    /// <summary>Every window this store holds, by key.</summary>
    public IReadOnlyDictionary<string, AggregateToPersist> Aggregates => _aggregates;

    /// <summary>How many times a write was made, including the repeats an upsert absorbs.</summary>
    /// <remarks>
    /// Counted separately from <see cref="Aggregates"/> so a test can tell "written twice, filed
    /// once" — which is what at-least-once delivery into an idempotent sink looks like — from
    /// "written once".
    /// </remarks>
    public int Writes => Volatile.Read(ref _writes);

    private int _writes;

    /// <inheritdoc />
    public ValueTask WriteAsync(
        string key, AggregateToPersist aggregate, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _writes);
        _aggregates[key] = aggregate;

        return ValueTask.CompletedTask;
    }
}

/// <summary>The late-reading log this sample runs on.</summary>
internal sealed class InMemoryLateReadingLog : ILateReadingLog
{
    private readonly ConcurrentQueue<LateReading> _readings = new();

    /// <summary>Every late record this log has taken, in the order it took them.</summary>
    public IReadOnlyCollection<LateReading> Readings => _readings;

    /// <inheritdoc />
    public ValueTask WriteAsync(LateReading reading, CancellationToken cancellationToken)
    {
        _readings.Enqueue(reading);

        return ValueTask.CompletedTask;
    }
}
