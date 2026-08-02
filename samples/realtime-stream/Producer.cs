using System.Globalization;
using System.Text.Json;
using FlowX;
using FlowX.Redis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace RealtimeStream;

/// <summary>How the sample's own producer behaves. Nothing in the platform reads this.</summary>
/// <remarks>
/// A sample that needed a separate producer process to show anything would be a sample nobody
/// ran. This is the generator, and it is deliberately the only part of the application that
/// knows the stream is Redis besides the one line in <c>Program.cs</c> that registers the source.
/// </remarks>
public sealed class TelemetryProducerOptions
{
    /// <summary>The stream to write to. Must match the flow's <c>[StreamTrigger]</c> source.</summary>
    public string Source { get; set; } = "device.telemetry";

    /// <summary>How many devices report.</summary>
    public int Devices { get; set; } = 12;

    /// <summary>How long of a gap in event time each batch advances by.</summary>
    /// <remarks>
    /// Event time, not wall-clock. The window is a minute wide, so a sample that produced at
    /// real speed would close its first window sixty seconds after it started and its second
    /// sixty seconds after that. Advancing event time faster than the clock is what lets
    /// <c>dotnet run</c> show a window closing within a few seconds — and it changes nothing
    /// about the engine, which has never read a wall clock
    /// (<a href="../../docs/adr/ADR-0056-the-watermark-is-observed-never-wall-clock.md">ADR-0056</a>).
    /// </remarks>
    public TimeSpan EventTimeStep { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait between batches, in real time.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How many entries to keep on the stream. Trimming is approximate, and generous on purpose.
    /// </summary>
    /// <remarks>
    /// A stream trimmed past the checkpointed position stops the subscription rather than
    /// resuming with a gap — <c>StreamErrors.Trimmed</c>, and it is terminal — so a demonstration
    /// that trimmed aggressively would look like a bug in the engine. The number is far above
    /// anything this producer can outrun at the interval above.
    /// </remarks>
    public int Retain { get; set; } = 100_000;

    /// <summary>
    /// Whether to emit one deliberately ancient record, so the side output has something in it.
    /// </summary>
    /// <remarks>
    /// The README's third "thing to try", done for you once at startup: a record five minutes
    /// behind the watermark belongs to a window that closed long ago, so it is routed to
    /// <see cref="ILateReadingLog"/> rather than silently dropped.
    /// </remarks>
    public bool EmitOneLateReading { get; set; } = true;
}

/// <summary>
/// Writes telemetry onto the stream the flow subscribes to, so <c>dotnet run</c> has something
/// to aggregate.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the only hosted service in the project, and it is not part of the
/// platform's story.</strong> Nothing drives the flow from here: the records go into Redis and
/// <c>FlowStreamScan</c> — registered by <c>AddFlowX</c>, driven by <c>FlowStreamService</c> —
/// finds them on its next pass. Deleting this class and pointing the sample at a real producer
/// would change nothing else in the application.
/// </para>
/// <para>
/// <strong>Every record carries an event time, and the field is not optional.</strong>
/// <c>RedisStreamSource</c> refuses a record with no <c>event-time</c> field rather than dating
/// it with the reader's clock, because a wall-clock stand-in makes every replay produce
/// different windows.
/// </para>
/// </remarks>
public sealed class TelemetryProducer : BackgroundService
{
    private readonly IConnectionMultiplexer _connection;
    private readonly TelemetryProducerOptions _options;
    private readonly ILogger<TelemetryProducer> _logger;

    /// <summary>Creates the producer.</summary>
    /// <param name="connection">The Redis multiplexer.</param>
    /// <param name="options">What to produce, and how fast.</param>
    /// <param name="logger">Where progress is reported.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public TelemetryProducer(
        IConnectionMultiplexer connection,
        TelemetryProducerOptions options,
        ILogger<TelemetryProducer> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var database = _connection.GetDatabase();
        var key = (RedisKey)_options.Source;

        // Where in event time to carry on from.
        //
        // NOT simply "now". This producer advances event time faster than the clock, so a second
        // `dotnet run` against a stream the first one left behind would start emitting records
        // dated minutes before the watermark those records already established — and the engine
        // would correctly route every one of them to the side output. The sample would look
        // broken, and what it would actually be demonstrating is that the reader has no memory.
        //
        // So the producer resumes from the stream, which is what a real one does. It is also the
        // only honest reading of "event time": the last thing that happened is the last thing
        // that happened, whoever wrote it.
        var eventTime = await ResumeFromAsync(database, key).ConfigureAwait(false);
        var batches = 0L;

        Log.Producing(
            _logger, _options.Devices, _options.Source, _options.EventTimeStep, _options.Interval);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                for (var device = 0; device < _options.Devices; device++)
                {
                    await WriteAsync(database, key, DeviceId(device), eventTime, Celsius(device, batches))
                        .ConfigureAwait(false);
                }

                if (_options.EmitOneLateReading && batches == LateReadingAfterBatches)
                {
                    // Five minutes behind the watermark, which is far outside the declared
                    // PT10S lateness, so its window has closed and cannot be reopened.
                    await WriteAsync(
                            database,
                            key,
                            DeviceId(0),
                            eventTime - TimeSpan.FromMinutes(5),
                            Celsius(0, batches))
                        .ConfigureAwait(false);

                    Log.WroteLateReading(_logger);
                }

                eventTime += _options.EventTimeStep;
                batches++;

                await Task.Delay(_options.Interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown. Whatever is on the stream stays on it, and the subscription resumes from
            // its checkpoint when a node picks it up again.
        }
    }

    /// <summary>Which batch the deliberately late record is written after.</summary>
    /// <remarks>
    /// Late enough that the watermark has moved well past the window it belongs to, so the
    /// record is genuinely refused rather than merely out of order — an out-of-order record
    /// inside the declared lateness joins its window and updates it, which is the other half of
    /// what <c>Lateness</c> buys and is not what this is demonstrating.
    /// </remarks>
    private const long LateReadingAfterBatches = 40;

    private async Task WriteAsync(
        IDatabase database, RedisKey key, string deviceId, DateTimeOffset eventTime, double celsius)
    {
        var payload = JsonSerializer.Serialize(
            new TelemetryReading(deviceId, celsius, Humidity(deviceId)),
            TelemetryJsonContext.Default.TelemetryReading);

        await database.StreamAddAsync(
                key,
                [
                    new NameValueEntry(
                        RedisStreamSource.EventTimeField,
                        eventTime.ToString("O", CultureInfo.InvariantCulture)),
                    new NameValueEntry(RedisStreamSource.PayloadField, payload),
                    new NameValueEntry(RedisStreamSource.PartitionKeyField, deviceId),
                ],
                maxLength: _options.Retain,
                useApproximateMaxLength: true)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The event time to carry on from: after whatever is already on the stream, or a window
    /// boundary near now if the stream is empty.
    /// </summary>
    /// <remarks>
    /// Aligned to a window boundary in the empty case so the first closed window is a whole one
    /// rather than whatever fraction of a minute the process happened to start in.
    /// </remarks>
    private async Task<DateTimeOffset> ResumeFromAsync(IDatabase database, RedisKey key)
    {
        var last = await database
            .StreamRangeAsync(key, minId: "-", maxId: "+", count: 1, messageOrder: Order.Descending)
            .ConfigureAwait(false);

        if (last.Length == 1 &&
            last[0].Values.FirstOrDefault(value => value.Name == RedisStreamSource.EventTimeField)
                is { Value.IsNullOrEmpty: false } field &&
            DateTimeOffset.TryParse(
                field.Value.ToString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var latest))
        {
            return latest + _options.EventTimeStep;
        }

        return Align(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    /// <summary>The window boundary at or before an instant.</summary>
    private static DateTimeOffset Align(DateTimeOffset instant, TimeSpan width) =>
        DateTimeOffset.UnixEpoch.AddTicks(
            (instant.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks)
            / width.Ticks * width.Ticks);

    private static string DeviceId(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"device-{index:D3}");

    /// <summary>
    /// A temperature that drifts, and periodically drifts far enough to trip the detector.
    /// </summary>
    /// <remarks>
    /// Deterministic rather than random, so two runs of the sample produce the same anomalies
    /// and "the detector fired" is reproducible rather than a coin toss.
    /// </remarks>
    private static double Celsius(int device, long batch) =>
        20d + (device % 5) + (Math.Sin(batch / 8d) * 6d) + (batch % 97 == 96 ? 55d : 0d);

    private static double Humidity(string deviceId) =>
        0.3 + (Math.Abs(deviceId.GetHashCode(StringComparison.Ordinal)) % 40 / 100d);
}

/// <summary>Prints each window's aggregate as it is filed, and each late record as it arrives.</summary>
/// <remarks>
/// A sample whose only output was the absence of errors would be a sample nobody could tell was
/// working. This reads the two in-memory stores the application registered and reports what has
/// appeared in them since it last looked.
/// </remarks>
public sealed class AggregateReporter : BackgroundService
{
    private readonly IAggregateStore _store;
    private readonly ILateReadingLog _late;
    private readonly ILogger<AggregateReporter> _logger;

    /// <summary>Creates the reporter.</summary>
    /// <param name="store">Where windows are filed.</param>
    /// <param name="late">Where late records are filed.</param>
    /// <param name="logger">Where the report goes.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public AggregateReporter(IAggregateStore store, ILateReadingLog late, ILogger<AggregateReporter> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(late);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _late = late;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_store is not InMemoryAggregateStore store || _late is not InMemoryLateReadingLog late)
        {
            // A deployment that put a real store behind the seam reads it with its own tools.
            return;
        }

        // Keyed rather than counted: the store is a ConcurrentDictionary and enumerating one is
        // in no particular order, so "everything past the first N I saw" would report some
        // windows twice and others never.
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var reportedLate = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);

                foreach (var entry in store.Aggregates
                    .Where(entry => !reported.Contains(entry.Key))
                    .OrderBy(entry => entry.Value.Stats.WindowStart)
                    .ToList())
                {
                    reported.Add(entry.Key);

                    Log.Window(
                        _logger,
                        entry.Value.Stats.WindowStart,
                        entry.Value.Stats.WindowEnd,
                        entry.Value.Stats.Readings,
                        entry.Value.Stats.Devices,
                        entry.Value.Stats.MeanCelsius,
                        entry.Value.Stats.MinCelsius,
                        entry.Value.Stats.MaxCelsius);

                    if (entry.Value.IsAnomalous)
                    {
                        Log.Anomaly(_logger, entry.Value.Reason);
                    }
                }

                foreach (var reading in late.Readings
                    .Where(reading => !reportedLate.Contains(reading.Position))
                    .ToList())
                {
                    reportedLate.Add(reading.Position);

                    Log.LateReading(
                        _logger,
                        reading.Position,
                        reading.Source,
                        reading.EventTime,
                        reading.Lateness);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }
}

/// <summary>
/// This sample's log messages, source-generated rather than interpolated at the call site.
/// </summary>
/// <remarks>
/// <c>CA1848</c> and <c>CA1873</c> are errors in this repository, and the fix they ask for is
/// the right one rather than a formality: <c>[LoggerMessage]</c> emits a strongly-typed delegate
/// with the template parsed at compile time, so a disabled level costs nothing and no boxing
/// happens on the path a window closes.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Producing telemetry for {Devices} devices onto '{Source}', advancing event " +
                  "time by {Step} every {Interval}.")]
    public static partial void Producing(
        ILogger logger, int devices, string source, TimeSpan step, TimeSpan interval);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Wrote one reading five minutes behind the watermark. Its window has already " +
                  "closed, so the engine routes it to the side output rather than dropping it.")]
    public static partial void WroteLateReading(ILogger logger);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Window {Start:HH:mm:ss}-{End:HH:mm:ss}: {Readings} readings from {Devices} " +
                  "devices, mean {Mean:F1} C, range {Min:F1}-{Max:F1} C.")]
    public static partial void Window(
        ILogger logger,
        DateTimeOffset start,
        DateTimeOffset end,
        int readings,
        int devices,
        double mean,
        double min,
        double max);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "  anomaly: {Reason}")]
    public static partial void Anomaly(ILogger logger, string? reason);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Warning,
        Message = "Late record {Position} on '{Source}': event time {EventTime:HH:mm:ss} is " +
                  "{Lateness} behind the watermark. Routed to the side output, not dropped.")]
    public static partial void LateReading(
        ILogger logger, string position, string source, DateTimeOffset eventTime, TimeSpan lateness);
}
