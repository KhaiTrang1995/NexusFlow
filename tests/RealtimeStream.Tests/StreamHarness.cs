using System.Globalization;
using System.Text.Json;
using FlowX;
using FlowX.Conformance.InMemory;
using FlowX.Hosting;
using FlowX.Runtime;

namespace RealtimeStream.Tests;

/// <summary>
/// One node running <c>samples/realtime-stream</c>: its stores, its stream, and the sample's own
/// plan and dispatcher.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The plan and the dispatcher are the generated ones, deliberately.</strong>
/// <c>StreamScanTests</c> in <c>tests/FlowX.Hosting.Tests</c> drives a hand-built
/// <c>ExecutionPlan</c> with a recording dispatcher, because it is testing the engine and a real
/// flow would only add noise. These tests are the mirror image: the engine is taken as given and
/// what is under test is that <em>this sample</em> aggregates, routes and deduplicates. So
/// <see cref="AggregateTelemetryFlow.Plan"/> and its generated <c>Dispatcher</c> are what run,
/// and a change to the flow's <c>Define</c> method reaches these assertions.
/// </para>
/// <para>
/// <strong>The window is the declared one and so is the lateness.</strong> They are read off the
/// same strings the <c>[StreamTrigger]</c> carries rather than chosen here, so a test cannot
/// quietly pass against a window the sample does not declare. <see cref="DeclaredWindow"/> and
/// its siblings are the single place they are written down.
/// </para>
/// <para>
/// <strong>The stores are the conformance suite's</strong>, because
/// <c>InMemoryFlowJournal.StartAsync</c>'s refusal of a duplicate instance id is what every
/// deduplication assertion rests on, and a hand-rolled double would drift on exactly that.
/// </para>
/// </remarks>
internal sealed class StreamHarness
{
    /// <summary>The stream the sample's flow declares. Must match the <c>[StreamTrigger]</c>.</summary>
    public const string Source = "device.telemetry";

    /// <summary>The window the sample declares.</summary>
    public const string DeclaredWindow = "tumbling:1m";

    /// <summary>The lateness the sample declares, as the ISO-8601 duration the attribute takes.</summary>
    public const string DeclaredLateness = "PT10S";

    /// <summary>The checkpoint interval the sample declares.</summary>
    public const string DeclaredCheckpoint = "PT5S";

    /// <summary>The parallelism the sample declares.</summary>
    public const int DeclaredParallelism = 8;

    /// <summary>The window width, as a <see cref="TimeSpan"/>.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>A window boundary, so a staged event time lands where the test means it to.</summary>
    public static readonly DateTimeOffset Origin = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private FlowStreamScan? _scan;

    private StreamHarness(int channelCapacity, int maxResident, int readBudget, TimeSpan storeCost)
    {
        Options = new FlowXOptions
        {
            ApplicationName = "RealtimeStream.Tests",
            NodeName = "node",
            ShutdownDrainTimeout = TimeSpan.FromSeconds(30),
            StreamChannelCapacity = channelCapacity,
            StreamMaxResidentRecords = maxResident,
            StreamReadBudget = readBudget,
        };

        Store = new MeasuringAggregateStore(storeCost);
        Durability = new FlowDurability(Journal, Leases);
        Host = new FlowHost(new FlowEngine(SystemClock.Instance), Options, Durability);
    }

    public InMemoryFlowJournal Journal { get; } = new();

    public InMemoryLeaseStore Leases { get; } = new();

    public StagedStreamSource Stream { get; } = new();

    public RecordingCheckpointStore Checkpoints { get; private set; } = new();

    public InMemoryLateReadingLog Late { get; } = new();

    public MeasuringAggregateStore Store { get; }

    public FlowDurability Durability { get; }

    public FlowHost Host { get; }

    public FlowXOptions Options { get; }

    public FlowStreamCatalog Subscriptions { get; } = new();

    /// <summary>Builds a node over an empty stream.</summary>
    /// <param name="channelCapacity">
    /// How many records may sit between the reader and the windowing half. The bound
    /// <c>BoundedMemoryTests</c> is about.
    /// </param>
    /// <param name="maxResident">How many records may sit in open windows at once.</param>
    /// <param name="readBudget">How many records one pass may read.</param>
    /// <param name="storeCost">How long <see cref="PersistAggregate"/>'s store takes per window.</param>
    public static StreamHarness Create(
        int channelCapacity = 256,
        int maxResident = 20_000,
        int readBudget = 4096,
        TimeSpan storeCost = default)
    {
        var harness = new StreamHarness(channelCapacity, maxResident, readBudget, storeCost);

        harness.Stream.Measure(() => harness.Store.Windows);
        harness.Register(harness.Subscriptions);
        harness._scan = harness.ScanOver(harness.Subscriptions, harness.Checkpoints);

        return harness;
    }

    /// <summary>Runs one pass over the subscription.</summary>
    public ValueTask<StreamScanReport> PassAsync(CancellationToken ct) => _scan!.RunOnceAsync(ct);

    /// <summary>
    /// A second node over the same journal, leases, stream and checkpoint: no open windows, and
    /// whatever position was committed.
    /// </summary>
    /// <remarks>
    /// What a node death actually looks like to this engine. The open windows were never
    /// journaled — ADR-0055 says they need not be — so the new node rebuilds them by re-reading
    /// from the checkpoint, and every window whose flow had already committed derives the same
    /// instance id and meets the journal's primary key.
    /// </remarks>
    public StreamHarness Restart()
    {
        var next = new StreamHarness(
            Options.StreamChannelCapacity,
            Options.StreamMaxResidentRecords,
            Options.StreamReadBudget,
            TimeSpan.Zero)
        {
            Checkpoints = Checkpoints,
        };

        next.Register(next.Subscriptions);

        next._scan = new FlowStreamScan(
            new FlowHost(
                new FlowEngine(SystemClock.Instance), next.Options, new FlowDurability(Journal, Leases)),
            next.Subscriptions,
            Stream,
            Checkpoints,
            new TelemetrySideOutput(next.Late),
            new FlowDurability(Journal, Leases),
            next.Options);

        return next;
    }

    /// <summary>The subscription the sample's <c>[StreamTrigger]</c> declares.</summary>
    /// <remarks>
    /// Registered through <see cref="FlowStreamCatalog.Add"/> with the declared strings, which is
    /// exactly what the generated <c>AddFlowXStreamSubscriptions()</c> does — so a window shape
    /// the engine could not serve would throw here as it would at the sample's startup.
    /// </remarks>
    private void Register(FlowStreamCatalog catalogue) => catalogue.Add(
        new StreamSubscription(
            AggregateTelemetryFlow.Plan.Flow.Id,
            AggregateTelemetryFlow.Plan.Flow.Version,
            Source,
            string.Empty),
        DeclaredWindow,
        DeclaredLateness,
        DeclaredCheckpoint,
        DeclaredParallelism,
        AggregateTelemetryFlow.Plan,
        new AggregateTelemetryFlow.Dispatcher(
            new DetectAnomalies(), new FoldReadings(), new PersistAggregate(Store)));

    private FlowStreamScan ScanOver(FlowStreamCatalog catalogue, IStreamCheckpointStore checkpoints) =>
        new(
            Host,
            catalogue,
            Stream,
            checkpoints,
            new TelemetrySideOutput(Late),
            Durability,
            Options);

    /// <summary>Serialises a reading exactly as this sample's producer writes it.</summary>
    public static string Body(string deviceId, double celsius, double humidity = 0.4) =>
        JsonSerializer.Serialize(
            new TelemetryReading(deviceId, celsius, humidity),
            TelemetryJsonContext.Default.TelemetryReading);
}

/// <summary>
/// A stream a test stages records onto, which records the high-water mark of what it has handed
/// out and not got back.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The peak is sampled inside <see cref="ReadAsync"/>, which is the busiest moment
/// there is.</strong> That is the instant the engine is asking for more records, so it is exactly
/// where an engine that read ahead without consulting its channel would run away. Sampling from a
/// timer instead would measure whenever the timer happened to fire.
/// </para>
/// <para>
/// <strong>"Outstanding" is records handed to the engine that the flow has not finished
/// with.</strong> A record the engine holds is a record in this process's memory; a record still
/// on the stream is Redis's problem. So <c>HandedOut − Consumed</c> is the memory the design
/// claims to bound, and nothing else here is.
/// </para>
/// </remarks>
internal sealed class StagedStreamSource : IStreamSource
{
    private readonly List<StreamRecord> _records = [];
    private Func<int> _consumed = static () => 0;
    private int _handedOut;
    private int _peak;
    private int _reads;

    public string Stream => "staged";

    /// <summary>How many records the engine has been handed.</summary>
    public int HandedOut => Volatile.Read(ref _handedOut);

    /// <summary>The most records ever outstanding: handed out and not yet finished with.</summary>
    public int PeakOutstanding => Volatile.Read(ref _peak);

    /// <summary>How many reads the engine issued.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <summary>How many records are staged.</summary>
    public int Staged => _records.Count;

    /// <summary>The position of the last staged record.</summary>
    public StreamPosition LastPosition => _records[^1].Position;

    /// <summary>Tells the source how to ask the application what it has finished with.</summary>
    /// <param name="consumed">
    /// How many records the flow has aggregated. Multiplied by nothing: the store counts
    /// <em>records</em> so that the two numbers are in the same units.
    /// </param>
    public void Measure(Func<int> consumed) => _consumed = consumed;

    /// <summary>Stages one record.</summary>
    public StagedStreamSource Stage(DateTimeOffset eventTime, string? payload = null, string? tenantId = null)
    {
        _records.Add(new StreamRecord(
            new StreamPosition(_records.Count.ToString(CultureInfo.InvariantCulture)),
            eventTime,
            PartitionKey: null,
            Payload: payload ?? StreamHarness.Body("device-000", 21.5),
            TenantId: tenantId));

        return this;
    }

    /// <inheritdoc />
    public ValueTask<Result<IReadOnlyList<StreamRecord>>> ReadAsync(
        StreamSubscription subscription,
        StreamPosition? after,
        int max,
        CancellationToken cancellationToken)
    {
        var from = after is { } position
            ? int.Parse(position.Value, CultureInfo.InvariantCulture) + 1
            : 0;

        var take = _records.Skip(from).Take(max).ToList();

        Interlocked.Increment(ref _reads);

        var outstanding = Interlocked.Add(ref _handedOut, take.Count) - _consumed();

        if (outstanding > Volatile.Read(ref _peak))
        {
            Volatile.Write(ref _peak, outstanding);
        }

        return ValueTask.FromResult<Result<IReadOnlyList<StreamRecord>>>(take);
    }
}

/// <summary>
/// The sample's aggregate store, told how slow to be and counting what it was asked to keep.
/// </summary>
/// <remarks>
/// <strong>This is the "slow sink" the README's backpressure test asks for</strong>, supplied
/// through the seam the sample already has rather than through a
/// <c>WithSlowCapability&lt;PersistAggregate&gt;(delay)</c> hook the platform does not. The
/// capability under test is the real <see cref="PersistAggregate"/>; only what it writes to is
/// substituted, which is the narrower and more honest substitution.
/// </remarks>
internal sealed class MeasuringAggregateStore : IAggregateStore
{
    private readonly TimeSpan _cost;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AggregateToPersist> _aggregates =
        new(StringComparer.Ordinal);

    private int _windows;
    private int _writes;

    public MeasuringAggregateStore(TimeSpan cost) => _cost = cost;

    /// <summary>How many records this store has been handed, across every window.</summary>
    /// <remarks>
    /// Records rather than windows, so it is in the same units as
    /// <see cref="StagedStreamSource.HandedOut"/> and the difference between them is a record
    /// count rather than a ratio of two different things.
    /// </remarks>
    public int Windows => Volatile.Read(ref _windows);

    /// <summary>How many times a write was made, including the repeats an upsert absorbs.</summary>
    public int Writes => Volatile.Read(ref _writes);

    /// <summary>Every window this store holds, by key.</summary>
    public IReadOnlyDictionary<string, AggregateToPersist> Aggregates => _aggregates;

    /// <inheritdoc />
    public async ValueTask WriteAsync(
        string key, AggregateToPersist aggregate, CancellationToken cancellationToken)
    {
        if (_cost > TimeSpan.Zero)
        {
            await Task.Delay(_cost, cancellationToken).ConfigureAwait(false);
        }

        Interlocked.Increment(ref _writes);
        Interlocked.Add(ref _windows, aggregate.Stats.Readings + aggregate.Stats.Malformed);
        _aggregates[key] = aggregate;
    }
}

/// <summary>A checkpoint store that records what it was told, and can be told to forget.</summary>
internal sealed class RecordingCheckpointStore : IStreamCheckpointStore
{
    private readonly List<StreamPosition> _committed = [];
    private readonly Lock _gate = new();

    private StreamPosition? _position;

    /// <summary>Every position committed, in order.</summary>
    public IReadOnlyList<StreamPosition> Committed
    {
        get
        {
            lock (_gate)
            {
                return [.. _committed];
            }
        }
    }

    /// <summary>
    /// Whether to drop commits, so a test can arrange the crash a checkpoint never survived.
    /// </summary>
    public bool SuppressCommits { get; set; }

    /// <inheritdoc />
    public ValueTask<Result<StreamPosition?>> ReadAsync(
        StreamSubscription subscription, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult<Result<StreamPosition?>>(_position);
        }
    }

    /// <inheritdoc />
    public ValueTask<Result<bool>> CommitAsync(
        StreamSubscription subscription, StreamPosition position, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (SuppressCommits)
            {
                return ValueTask.FromResult<Result<bool>>(false);
            }

            _committed.Add(position);
            _position = position;

            return ValueTask.FromResult<Result<bool>>(true);
        }
    }
}
