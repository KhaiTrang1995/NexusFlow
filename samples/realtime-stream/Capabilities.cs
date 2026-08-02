using System.Globalization;
using System.Text.Json;
using FlowX;

namespace RealtimeStream;

/// <summary>Errors this application can produce.</summary>
/// <remarks>
/// Declared in one place so the codes are greppable and so two capabilities cannot invent two
/// spellings of the same condition.
/// </remarks>
public static class TelemetryErrors
{
    /// <summary>Every record in the window was unreadable.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The one case where a malformed body does refuse the window.</strong>
    /// <see cref="FoldReadings"/> counts unreadable records and aggregates the rest, because one
    /// bad body in nine hundred is a producer bug and losing the other eight hundred and
    /// ninety-nine would be a worse answer than a slightly wrong one. A window in which
    /// <em>nothing</em> was readable has no statistics to report at all, and emitting
    /// <c>AggregateComputed</c> with a mean of zero would put a number into a dashboard that no
    /// device ever measured.
    /// </para>
    /// <para>
    /// <c>Validation</c>, so it is a business outcome rather than an exception (ADR-0007). The
    /// window's flow fails, the checkpoint still advances past it — a window that failed has
    /// happened, and re-reading it would fail identically — and the operator sees a flow with a
    /// recorded failure naming the stream and the interval.
    /// </para>
    /// </remarks>
    public static Error NoReadableRecords(string source, DateTimeOffset windowStart, int records) =>
        new Error(
            "telemetry.no_readable_records",
            $"All {records} records in the window starting {windowStart:O} on '{source}' carried " +
            "a body this application could not read as a TelemetryReading. A window with no " +
            "readable record has no statistics, and publishing one computed from nothing would " +
            "put a number nobody measured into whatever reads the aggregate.",
            ErrorCategory.Validation)
            .With("source", source)
            .With("records", records);
}

/// <summary>
/// Deserialises a closed window's records and folds them into one set of statistics.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is where the sample's aggregation lives, and the placement is the
/// point.</strong> <c>IFlowBuilder</c> has no <c>.Aggregate(...)</c>; a flow expresses order,
/// condition and recovery, and "what the mean of a minute of temperatures is" is none of those.
/// So the fold is a capability like any other — constructed with no arguments, called with a
/// batch, testable by <c>new FoldReadings()</c> and one method call, and published in the
/// manifest under its own id and version.
/// </para>
/// <para>
/// <strong>The payloads are deserialised here because here is where a serialiser context is in
/// scope.</strong> <see cref="StreamRecord.Payload"/> is a string rather than a deserialised
/// object for <see cref="BusMessage.Payload"/>'s reason: only generated code can name a
/// <c>JsonTypeInfo</c>, so an engine that deserialised would reflect and constraint C2 forbids
/// it. <see cref="TelemetryJsonContext"/> is that generated code, and this capability is the
/// first place in the path that can name it.
/// </para>
/// <para>
/// <strong>Reading the whole window is not a memory risk, and it is worth saying why.</strong>
/// The window's records are already resident — the engine held them until the watermark closed
/// it, under <c>FlowXOptions.StreamMaxResidentRecords</c>, and handed them over. This method
/// allocates one <see cref="TelemetryReading"/> per record and no second copy of the batch: the
/// fold is a single pass with running totals, not a materialised projection.
/// </para>
/// <para>
/// <strong>Idempotent, and genuinely so.</strong> It reads its input and touches nothing else,
/// so a rebuilt window folded a second time produces a bit-identical answer — which is what
/// makes a replayed window's recorded output comparable to the original's.
/// </para>
/// </remarks>
[Capability("telemetry.fold", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true)]
public sealed class FoldReadings : ICapability<StreamWindowBatch, DeviceStats>
{
    /// <inheritdoc />
    public ValueTask<Result<DeviceStats>> ExecuteAsync(
        StreamWindowBatch input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var devices = new HashSet<string>(StringComparer.Ordinal);
        var readings = 0;
        var malformed = 0;
        var total = 0d;
        var min = double.MaxValue;
        var max = double.MinValue;

        foreach (var record in input.Records)
        {
            if (Read(record.Payload) is not { } reading)
            {
                malformed++;

                continue;
            }

            devices.Add(reading.DeviceId);
            readings++;
            total += reading.Celsius;
            min = Math.Min(min, reading.Celsius);
            max = Math.Max(max, reading.Celsius);
        }

        if (readings == 0)
        {
            return ValueTask.FromResult<Result<DeviceStats>>(
                TelemetryErrors.NoReadableRecords(
                    input.Source, input.WindowStart, input.Records.Count));
        }

        return ValueTask.FromResult<Result<DeviceStats>>(new DeviceStats(
            input.Source,
            input.WindowStart,
            input.WindowEnd,
            devices.Count,
            readings,
            malformed,
            min,
            max,
            total / readings));
    }

    /// <summary>
    /// One record's body, or null when it is not a reading this application understands.
    /// </summary>
    /// <remarks>
    /// A <c>try</c> around a deserialisation rather than a <c>Utf8JsonReader</c> walk, because
    /// the only question asked of a malformed body is whether it was one. The exception is
    /// caught here and turned into a count rather than allowed to leave the capability: a
    /// producer that writes one bad record should not stop a subscription, and an exception out
    /// of a step is not the shape a business outcome takes (ADR-0007).
    /// </remarks>
    private static TelemetryReading? Read(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(payload, TelemetryJsonContext.Default.TelemetryReading);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Decides whether a window's statistics are worth waking somebody for.</summary>
/// <remarks>
/// <para>
/// Two rules, both computed from the fold's output and nothing else. A rule that called out to a
/// thresholds service would make this step a network call on every window and would make the
/// verdict depend on when the window happened to run — which, for a flow that is replayed from a
/// journal, means a resumed instance could reach a different verdict from the one it committed.
/// </para>
/// <para>
/// The thresholds are constants rather than configuration for the same reason the window is on
/// the trigger: they are what the output <em>means</em>. A deployment that could retune them
/// would be changing what "anomalous" says, not how fast the sample runs.
/// </para>
/// </remarks>
[Capability("telemetry.detect", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true)]
public sealed class DetectAnomalies : ICapability<DeviceStats, AnomalyVerdict>
{
    /// <summary>Above this mean, the fleet is overheating.</summary>
    public const double MeanCelsiusCeiling = 60d;

    /// <summary>A spread wider than this inside one minute is a sensor disagreeing with itself.</summary>
    public const double SpreadCelsiusCeiling = 40d;

    /// <inheritdoc />
    public ValueTask<Result<AnomalyVerdict>> ExecuteAsync(
        DeviceStats input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.MeanCelsius > MeanCelsiusCeiling)
        {
            return Verdict(
                $"The mean of {input.MeanCelsius.ToString("F1", CultureInfo.InvariantCulture)} °C " +
                $"over {input.Readings} readings is above {MeanCelsiusCeiling} °C.");
        }

        var spread = input.MaxCelsius - input.MinCelsius;

        if (spread > SpreadCelsiusCeiling)
        {
            return Verdict(
                $"The spread of {spread.ToString("F1", CultureInfo.InvariantCulture)} °C inside " +
                $"one window is above {SpreadCelsiusCeiling} °C.");
        }

        return ValueTask.FromResult<Result<AnomalyVerdict>>(new AnomalyVerdict(false, null));
    }

    private static ValueTask<Result<AnomalyVerdict>> Verdict(string reason) =>
        ValueTask.FromResult<Result<AnomalyVerdict>>(new AnomalyVerdict(true, reason));
}

/// <summary>Writes one window's aggregate to the store, and is the step that can be slow.</summary>
/// <remarks>
/// <para>
/// <strong>The sink the backpressure story is told against.</strong> A time-series database
/// under load answers a bulk write in tens of milliseconds; the stream does not slow down to
/// match. What the engine does about that is refuse to read ahead — it computes the room left in
/// its bounded channel and issues no read at all when there is none, so the backlog stays in
/// Redis rather than in this process's heap. <c>BoundedMemoryTests</c> substitutes an
/// <see cref="IAggregateStore"/> that sleeps and asserts that the peak record count resident in
/// the process stays inside the declared channel capacity.
/// </para>
/// <para>
/// <strong>Idempotent because the key is derived, and the key is derived because the checkpoint
/// is committed late.</strong> A node that dies after this write and before the checkpoint moves
/// re-reads the window, rebuilds it, and — if the journal has no instance for it — writes the
/// aggregate again. <see cref="IAggregateStore.WriteAsync"/> is an upsert keyed on the source
/// and the interval, so the second write replaces the first rather than adding a row. That is
/// what makes at-least-once delivery add up to exactly-once <em>state</em>, and it is the half
/// of the guarantee the platform cannot supply for you.
/// </para>
/// <para>
/// The declaration is also what makes <see cref="Policies.BulkWrite"/>'s retry legal:
/// <c>FLOWX1014</c> is an error when a retry is attached to a capability that declares
/// <c>Idempotent = false</c>.
/// </para>
/// </remarks>
[Capability("telemetry.persist", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true)]
public sealed class PersistAggregate : ICapability<AggregateToPersist, PersistedAggregate>
{
    private readonly IAggregateStore _store;

    /// <summary>Creates the capability.</summary>
    /// <param name="store">Where a window's aggregate is kept.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public PersistAggregate(IAggregateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public async ValueTask<Result<PersistedAggregate>> ExecuteAsync(
        AggregateToPersist input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var key = KeyFor(input.Stats);

        await _store.WriteAsync(key, input, ct).ConfigureAwait(false);

        return new PersistedAggregate(key);
    }

    /// <summary>
    /// What a window's aggregate is filed under: the stream and the interval, and nothing else.
    /// </summary>
    /// <param name="stats">The window's statistics.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stats"/> is null.</exception>
    /// <remarks>
    /// Derived from exactly the terms a rebuilt window derives its instance id from, so the two
    /// deduplicate together. A key carrying a node name, a timestamp or a counter would make the
    /// second write of a rebuilt window a second row, which is the silent double-count the whole
    /// identity derivation exists to prevent.
    /// </remarks>
    public static string KeyFor(DeviceStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{stats.Source}|{stats.WindowStart:O}|{stats.WindowEnd:O}");
    }
}
