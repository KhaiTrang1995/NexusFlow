using FlowX.Conformance.InMemory;
using FlowX.Hosting;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Hosting.Tests;

/// <summary>
/// A deliberately slow consumer, and the bound that stops the source outrunning it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>06 §10 is a backpressure diagram, and this is the assertion it was missing.</strong>
/// The section says the engine "never grows an unbounded queue" and "pauses consumption at the
/// source" — claims that are true of any engine right up until the consumer is slower than the
/// producer, which is the only case worth testing. So the flow here takes milliseconds per window
/// while the source has thousands of records ready, and what is measured is how many records the
/// source was <em>asked for</em> beyond what the flows had consumed.
/// </para>
/// <para>
/// <strong>The measure is resident records, not throughput.</strong> A record the engine has been
/// handed and has not finished with is a record in this process's memory; a record the source
/// still holds is Redis's problem. So <c>HandedOut − Consumed</c> is exactly the memory the
/// design claims to bound, and it is sampled by the source itself on every read — the busiest
/// moment there is.
/// </para>
/// <para>
/// <strong><see cref="TheBoundIsWhatBoundsMemoryAndNotTheArithmetic"/> is the test that can
/// fail.</strong> An assertion that some number stays under a limit is worth nothing unless
/// raising the limit makes it climb: it would pass just as well against an engine that read the
/// whole stream and against one that read nothing. That test runs the identical workload twice,
/// at two capacities, and asserts the peak follows the capacity. Delete the channel from
/// <c>FlowStreamScan</c> and the first assertion of both tests fails.
/// </para>
/// </remarks>
public sealed class StreamBackpressureTests
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private const string Source = "device.telemetry";

    /// <summary>How many records the source holds. Far more than any bound under test.</summary>
    private const int Staged = 4000;

    /// <summary>
    /// A slow consumer does not make the engine hold the stream: the backlog stays at the source.
    /// </summary>
    [Fact]
    public async Task ASlowConsumerBoundsResidentMemory()
    {
        var fixture = Fixture.Create(channelCapacity: 8, stepCost: TimeSpan.FromMilliseconds(2));

        await fixture.PassAsync();

        fixture.Dispatcher.RecordsConsumed.ShouldBeGreaterThan(
            0, "a pass that consumed nothing would bound memory by doing no work.");

        fixture.Source.PeakResident.ShouldBeLessThanOrEqualTo(
            64,
            $"the channel holds 8, one window holds 10, and a window's records are the flow's " +
            $"until it returns — {Staged} records were available and the engine never held more " +
            "than a few dozen, because it issues no read at all when the channel is full.");
    }

    /// <summary>
    /// The peak follows the declared capacity, which is what says the bound is doing the work.
    /// </summary>
    /// <remarks>
    /// <strong>Written so that it fails if the bound stops being enforced.</strong> The two runs
    /// differ in exactly one value. If <c>FlowStreamScan</c> read ahead regardless — an unbounded
    /// buffer, or a channel whose fullness it did not consult before reading — both peaks would
    /// be the whole stream and the first assertion would fail; if it never read ahead at all,
    /// both would be tiny and the second would fail. Only an engine that actually reads up to
    /// its capacity and then stops passes both.
    /// </remarks>
    [Fact]
    public async Task TheBoundIsWhatBoundsMemoryAndNotTheArithmetic()
    {
        var tight = Fixture.Create(channelCapacity: 8, stepCost: TimeSpan.FromMilliseconds(2));
        var loose = Fixture.Create(channelCapacity: 2048, stepCost: TimeSpan.FromMilliseconds(2));

        await tight.PassAsync();
        await loose.PassAsync();

        tight.Source.PeakResident.ShouldBeLessThanOrEqualTo(64);

        loose.Source.PeakResident.ShouldBeGreaterThan(
            512,
            "the same workload against a channel 256 times larger holds hundreds of records — so " +
            "the first assertion is a statement about the bound rather than about how fast the " +
            "flows happen to run.");
    }

    /// <summary>The read budget bounds how long one node holds a subscription lease.</summary>
    /// <remarks>
    /// The other reason not to read a whole stream in one pass: a node that never yields is a
    /// node another cannot take a permanently-behind subscription away from.
    /// </remarks>
    [Fact]
    public async Task OnePassReadsNoMoreThanItsBudget()
    {
        var fixture = Fixture.Create(channelCapacity: 64, stepCost: TimeSpan.Zero, readBudget: 100);

        var report = await fixture.PassAsync();

        report.Read.ShouldBeLessThanOrEqualTo(100);
        fixture.Source.HandedOut.ShouldBeLessThanOrEqualTo(100);
    }

    /// <summary>One node's stores and a stream with more on it than any pass will take.</summary>
    private sealed class Fixture
    {
        private FlowStreamScan? _resident;

        private Fixture(int channelCapacity, int readBudget)
        {
            Options = new FlowXOptions
            {
                ApplicationName = "Tests",
                NodeName = "node",
                ShutdownDrainTimeout = TimeSpan.FromSeconds(30),
                StreamChannelCapacity = channelCapacity,
                StreamMaxResidentRecords = 100_000,
                StreamReadBudget = readBudget,
            };

            Durability = new FlowDurability(Journal, Leases);
            Host = new FlowHost(new FlowEngine(SystemClock.Instance), Options, Durability);
        }

        public InMemoryFlowJournal Journal { get; } = new();

        public InMemoryLeaseStore Leases { get; } = new();

        public MeasuringStreamSource Source { get; } = new();

        public RecordingCheckpointStore Checkpoints { get; } = new();

        public RecordingSideOutput SideOutput { get; } = new();

        public FlowDurability Durability { get; }

        public FlowHost Host { get; }

        public FlowXOptions Options { get; }

        public FlowStreamCatalog Subscriptions { get; } = new();

        public RecordingStreamDispatcher Dispatcher { get; } = new();

        public static Fixture Create(int channelCapacity, TimeSpan stepCost, int readBudget = 10_000)
        {
            var fixture = new Fixture(channelCapacity, readBudget);

            fixture.Dispatcher.StepCost = stepCost;
            fixture.Source.Measure(() => fixture.Dispatcher.RecordsConsumed);

            for (var index = 0; index < Staged; index++)
            {
                fixture.Source.Stage(Origin.AddSeconds(index));
            }

            fixture.Subscriptions.Add(
                new StreamSubscription("telemetry.aggregate", "1.0.0", StreamBackpressureTests.Source, string.Empty),
                "tumbling:10s",
                "PT0S",
                "PT0S",
                1,
                StreamScanTests.Plan(ExecutionProfile.Streaming),
                fixture.Dispatcher);

            fixture._resident = new FlowStreamScan(
                fixture.Host,
                fixture.Subscriptions,
                fixture.Source,
                fixture.Checkpoints,
                fixture.SideOutput,
                fixture.Durability,
                fixture.Options);

            return fixture;
        }

        public ValueTask<StreamScanReport> PassAsync() =>
            _resident!.RunOnceAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A stream that records the high-water mark of what it has handed out and not got back.
    /// </summary>
    /// <remarks>
    /// Sampled on every read, which is the moment the engine is asking for more — so the peak is
    /// measured exactly where an unbounded engine would run away. It is a <see cref="Volatile"/>
    /// read of a counter the consumer writes, which is enough: the assertion is about an order of
    /// magnitude, not about a single record.
    /// </remarks>
    private sealed class MeasuringStreamSource : IStreamSource
    {
        private readonly List<StreamRecord> _records = [];
        private Func<int> _consumed = static () => 0;
        private int _handedOut;
        private int _peak;

        public string Stream => "measuring";

        /// <summary>How many records the engine has been handed.</summary>
        public int HandedOut => Volatile.Read(ref _handedOut);

        /// <summary>The most records ever outstanding: handed out and not yet consumed.</summary>
        public int PeakResident => Volatile.Read(ref _peak);

        /// <summary>Tells the source how to ask the consumer what it has finished with.</summary>
        public void Measure(Func<int> consumed) => _consumed = consumed;

        public void Stage(DateTimeOffset eventTime) => _records.Add(new StreamRecord(
            new StreamPosition(_records.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            eventTime));

        public ValueTask<Result<IReadOnlyList<StreamRecord>>> ReadAsync(
            StreamSubscription subscription,
            StreamPosition? after,
            int max,
            CancellationToken cancellationToken)
        {
            var from = after is { } position
                ? int.Parse(position.Value, System.Globalization.CultureInfo.InvariantCulture) + 1
                : 0;

            var take = _records.Skip(from).Take(max).ToList();
            var outstanding = Interlocked.Add(ref _handedOut, take.Count) - _consumed();

            if (outstanding > Volatile.Read(ref _peak))
            {
                Volatile.Write(ref _peak, outstanding);
            }

            return ValueTask.FromResult<Result<IReadOnlyList<StreamRecord>>>(take);
        }
    }
}
