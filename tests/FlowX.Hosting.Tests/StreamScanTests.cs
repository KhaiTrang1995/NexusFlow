using FlowX.Conformance.InMemory;
using FlowX.Hosting;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Hosting.Tests;

/// <summary>
/// What a closed window starts, what stops one window starting two flows, what happens to a
/// record that arrives too late, and where the checkpoint is allowed to be.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ChangeScanTests"/>'s framing, and the same reason for asserting on the journal's
/// instance count rather than on "something ran": a stream is at-least-once because the
/// checkpoint is committed after the windows' flows, so what has to be proved is that the
/// <em>second</em> reading of a window does not aggregate it again.
/// </para>
/// <para>
/// The stores are the conformance suite's reference implementations, because
/// <c>StartAsync</c>'s refusal of a duplicate id is what the whole design rests on. The source
/// is a double here and a real Redis in <c>tests/FlowX.Redis.Tests</c>; the checkpoint store is a
/// double here and a real PostgreSQL in <c>tests/FlowX.Postgres.Tests</c>.
/// </para>
/// </remarks>
public sealed class StreamScanTests
{
    private static readonly CapabilityDescriptor Aggregate =
        CapabilityDescriptor.Create("telemetry.aggregate", "1.0.0", isIdempotent: true);

    private static readonly DateTimeOffset Origin = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private const string Source = "device.telemetry";

    /// <summary>A pass over a stream with nothing on it starts nothing.</summary>
    [Fact]
    public async Task APassOverAnEmptyStreamStartsNothing()
    {
        var fixture = Fixture.Create();

        var report = await fixture.PassAsync();

        report.Read.ShouldBe(0);
        report.Windows.ShouldBe(0);
        fixture.Journal.Instances.ShouldBeEmpty();
    }

    /// <summary>Records that do not close a window start nothing, and are not lost.</summary>
    /// <remarks>
    /// The consequence of ADR-0056 that a user has to know: an open window is invisible until the
    /// data closes it, and the checkpoint stays behind its oldest record so a restart rebuilds it.
    /// </remarks>
    [Fact]
    public async Task AnOpenWindowStartsNothingAndHoldsTheCheckpoint()
    {
        var fixture = Fixture.Create();

        fixture.Source.Stage(Origin, Origin.AddSeconds(5));

        var report = await fixture.PassAsync();

        report.Read.ShouldBe(2);
        report.Windows.ShouldBe(0);
        fixture.Journal.Instances.ShouldBeEmpty();
        fixture.Checkpoints.Committed.ShouldBeEmpty(
            "nothing has been dealt with, so there is no settled prefix to commit.");
    }

    /// <summary>A closed window starts one flow, and hands it the interval and its records.</summary>
    [Fact]
    public async Task AClosedWindowStartsTheFlow()
    {
        var fixture = Fixture.Create();

        fixture.Source.Stage(Origin, Origin.AddSeconds(5), Origin.AddSeconds(11));

        var report = await fixture.PassAsync();

        report.Windows.ShouldBe(1);
        report.Started.ShouldBe(1);

        fixture.Journal.Instances.ShouldHaveSingleItem().FlowId.ShouldBe("telemetry.aggregate");

        var batch = fixture.Dispatcher.Inputs[0].ShouldBeOfType<StreamWindowBatch>();

        batch.Source.ShouldBe(Source);
        batch.WindowStart.ShouldBe(Origin);
        batch.WindowEnd.ShouldBe(Origin.AddSeconds(10));
        batch.Records.Count.ShouldBe(2, "the record that closed the window is in the next one.");

        fixture.Checkpoints.Committed.ShouldHaveSingleItem().Value.ShouldBe(
            "1",
            "the prefix stops at the last record of the closed window; record 2 is still open.");
    }

    /// <summary>
    /// A window read a second time, because the checkpoint never committed, is deduplicated
    /// rather than aggregated twice.
    /// </summary>
    /// <remarks>
    /// <strong>This is the whole of what makes window state not worth journaling</strong>
    /// (ADR-0055). The second pass rebuilds the identical window, derives the identical id, and
    /// meets <c>flow_instance</c>'s primary key.
    /// </remarks>
    [Fact]
    public async Task AWindowRebuiltAfterACrashIsDeduplicated()
    {
        var fixture = Fixture.Create();

        fixture.Source.Stage(Origin, Origin.AddSeconds(5), Origin.AddSeconds(11));
        fixture.Checkpoints.SuppressCommits = true;

        (await fixture.PassAsync()).Started.ShouldBe(1);

        fixture.Journal.Instances.Count.ShouldBe(1);

        // A new node: the windows it holds are gone, and the checkpoint it reads is the one that
        // never moved.
        var restarted = fixture.Restart();

        var report = await restarted.PassAsync();

        report.Windows.ShouldBe(1, "the window is rebuilt from the same records.");
        report.Deduplicated.ShouldBe(1);
        report.Started.ShouldBe(0);

        fixture.Journal.Instances.Count.ShouldBe(
            1, "and the journal holds one instance, not two.");
    }

    /// <summary>A record later than the declared lateness reaches the side output.</summary>
    [Fact]
    public async Task ARecordPastItsWindowIsRoutedAndNotDropped()
    {
        var fixture = Fixture.Create();

        fixture.Source.Stage(Origin, Origin.AddSeconds(11), Origin.AddSeconds(1));

        var report = await fixture.PassAsync();

        report.Late.ShouldBe(1);
        fixture.SideOutput.Routed.ShouldHaveSingleItem().Position.Value.ShouldBe("2");
    }

    /// <summary>A side output that refuses holds the checkpoint rather than losing the record.</summary>
    [Fact]
    public async Task ASideOutputThatRefusesHoldsTheCheckpoint()
    {
        var fixture = Fixture.Create();

        fixture.SideOutput.Refuse = true;
        fixture.Source.Stage(Origin, Origin.AddSeconds(11), Origin.AddSeconds(1));

        var report = await fixture.PassAsync();

        report.Error.ShouldNotBeNull();

        fixture.Checkpoints.Stored!.Value.Value.ShouldBe(
            "0",
            "the checkpoint is where the closed window's prefix ended and no further: the late " +
            "record is at position 2 and was never taken, so a resuming node re-reads it rather " +
            "than skipping the one thing a side output exists to keep.");
    }

    /// <summary>
    /// A window whose records name two tenants is refused rather than aggregated into one
    /// instance.
    /// </summary>
    [Fact]
    public async Task AWindowThatMixesTenantsIsRefused()
    {
        var fixture = Fixture.Create();

        fixture.Source.Stage(Origin, "tenant-a");
        fixture.Source.Stage(Origin.AddSeconds(1), "tenant-b");
        fixture.Source.Stage(Origin.AddSeconds(11), "tenant-a");

        var report = await fixture.PassAsync();

        report.Error!.Code.ShouldBe("stream.mixed_tenants");
        fixture.Journal.Instances.ShouldBeEmpty();
    }

    /// <summary>A subscription whose source has been trimmed past its checkpoint stops.</summary>
    /// <remarks>
    /// The one case this engine refuses to guess at. Resuming from the oldest retained record
    /// would emit windows computed from part of their input, silently and for ever.
    /// </remarks>
    [Fact]
    public async Task ATrimmedSourceStopsTheSubscription()
    {
        var fixture = Fixture.Create();

        fixture.Checkpoints.Stored = new StreamPosition("gone");
        fixture.Source.Trimmed = true;

        var report = await fixture.PassAsync();

        report.Error!.Code.ShouldBe(StreamErrors.TrimmedCode);

        (await fixture.PassAsync()).Error!.Code.ShouldBe(
            StreamErrors.TrimmedCode, "and it stays stopped rather than trying again for ever.");
    }

    /// <summary>A window wider than the memory bound stops the subscription, and never evicts.</summary>
    [Fact]
    public async Task AWindowOverTheResidentBoundStopsTheSubscription()
    {
        var fixture = Fixture.Create(maxResident: 2);

        fixture.Source.Stage(Origin, Origin.AddSeconds(1), Origin.AddSeconds(2), Origin.AddSeconds(3));

        var report = await fixture.PassAsync();

        report.Error!.Code.ShouldBe(StreamErrors.WindowOverflowCode);
        fixture.Journal.Instances.ShouldBeEmpty();
    }

    /// <summary>A host with no journal reads no stream.</summary>
    [Fact]
    public void AHostWithNoJournalIsDisabled()
    {
        var options = new FlowXOptions { ApplicationName = "Tests", NodeName = "node" };
        var host = new FlowHost(new FlowEngine(SystemClock.Instance), options);
        var catalogue = new FlowStreamCatalog();

        catalogue.Add(
            new StreamSubscription("telemetry.aggregate", "1.0.0", Source, string.Empty),
            "tumbling:10s", "PT0S", "PT0S", 1, Plan(ExecutionProfile.Streaming), new RecordingStreamDispatcher());

        new FlowStreamScan(
                host,
                catalogue,
                new RecordingStreamSource(),
                new RecordingCheckpointStore(),
                new RecordingSideOutput(),
                new FlowDurability(new InMemoryFlowJournal(), new InMemoryLeaseStore()),
                options)
            .IsEnabled
            .ShouldBeFalse("the host was built with no durability, so a rebuilt window would run twice.");
    }

    /// <summary>Every profile but <c>Streaming</c> is refused at registration.</summary>
    [Theory]
    [InlineData(ExecutionProfile.Ephemeral)]
    [InlineData(ExecutionProfile.Durable)]
    public void AFlowThatIsNotStreamingIsRefused(ExecutionProfile profile)
    {
        Should.Throw<ArgumentException>(() => new FlowStreamCatalog().Add(
                new StreamSubscription("telemetry.aggregate", "1.0.0", Source, string.Empty),
                "tumbling:10s", "PT0S", "PT0S", 1, Plan(profile), new RecordingStreamDispatcher()))
            .Message.ShouldContain("Streaming");
    }

    /// <summary>A window shape the engine does not implement is refused at registration too.</summary>
    /// <remarks>
    /// FLOWX1042 reports it at build time; this is the same refusal for a registration written by
    /// hand or generated by an older build, which never passed the analyzer.
    /// </remarks>
    [Fact]
    public void AWindowShapeTheEngineDoesNotImplementIsRefused()
    {
        Should.Throw<ArgumentException>(() => new FlowStreamCatalog().Add(
                new StreamSubscription("telemetry.aggregate", "1.0.0", Source, string.Empty),
                "session:5m", "PT0S", "PT0S", 1, Plan(ExecutionProfile.Streaming), new RecordingStreamDispatcher()))
            .Message.ShouldContain("session:5m");
    }

    /// <summary>A <c>Streaming</c> flow's steps are journaled, exactly as a <c>Durable</c> one's are.</summary>
    /// <remarks>
    /// The runtime change P7 made, asserted where it is observable: without it the derived
    /// instance id would be inert and every rebuilt window would aggregate a second time.
    /// </remarks>
    [Fact]
    public async Task AStreamingFlowJournalsItsSteps()
    {
        var fixture = Fixture.Create();

        fixture.Source.Stage(Origin, Origin.AddSeconds(11));

        await fixture.PassAsync();

        var instance = fixture.Journal.Instances.ShouldHaveSingleItem();

        var frontier = await fixture.Journal.ReadResumeFrontierAsync(
            instance.InstanceId, TestContext.Current.CancellationToken);

        frontier.Value.Committed.ShouldNotBeEmpty(
            "one row per step boundary, which is what makes the derived instance id refuse a " +
            "rebuilt window rather than merely name it.");
    }

    internal static ExecutionPlan Plan(ExecutionProfile profile) => ExecutionPlan.Create(
        FlowDescriptor.Create("telemetry.aggregate", "1.0.0", profile, TimeSpan.FromMinutes(5)),
        StepGraph.Create([StepNode.ForCapability(0, Aggregate)]));

    /// <summary>One node's stores, catalogue and source.</summary>
    private sealed class Fixture
    {
        private FlowStreamScan? _resident;

        private Fixture(int maxResident)
        {
            Options = new FlowXOptions
            {
                ApplicationName = "Tests",
                NodeName = "node",
                ShutdownDrainTimeout = TimeSpan.FromSeconds(5),
                StreamChannelCapacity = 16,
                StreamMaxResidentRecords = maxResident,
            };

            Durability = new FlowDurability(Journal, Leases);
            Host = new FlowHost(new FlowEngine(SystemClock.Instance), Options, Durability);
        }

        public InMemoryFlowJournal Journal { get; } = new();

        public InMemoryLeaseStore Leases { get; } = new();

        public RecordingStreamSource Source { get; } = new();

        public RecordingCheckpointStore Checkpoints { get; } = new();

        public RecordingSideOutput SideOutput { get; } = new();

        public FlowDurability Durability { get; }

        public FlowHost Host { get; }

        public FlowXOptions Options { get; }

        public FlowStreamCatalog Subscriptions { get; } = new();

        public RecordingStreamDispatcher Dispatcher { get; } = new();

        public static Fixture Create(int maxResident = 1000)
        {
            var fixture = new Fixture(maxResident);

            fixture.Subscriptions.Add(
                new StreamSubscription("telemetry.aggregate", "1.0.0", StreamScanTests.Source, string.Empty),
                "tumbling:10s",
                "PT0S",
                "PT0S",
                1,
                Plan(ExecutionProfile.Streaming),
                fixture.Dispatcher);

            fixture._resident = fixture.ScanOver(fixture.Subscriptions);

            return fixture;
        }

        /// <summary>
        /// A second node over the same stores and the same source: no open windows, and whatever
        /// checkpoint was committed.
        /// </summary>
        public Fixture Restart()
        {
            var next = new Fixture(1000)
            {
                _resident = null,
            };

            next.Subscriptions.Add(
                new StreamSubscription("telemetry.aggregate", "1.0.0", StreamScanTests.Source, string.Empty),
                "tumbling:10s", "PT0S", "PT0S", 1, Plan(ExecutionProfile.Streaming), next.Dispatcher);

            var host = new FlowHost(
                new FlowEngine(SystemClock.Instance), next.Options, new FlowDurability(Journal, Leases));

            next._resident = new FlowStreamScan(
                host, next.Subscriptions, Source, Checkpoints, next.SideOutput,
                new FlowDurability(Journal, Leases), next.Options);

            return next;
        }

        public ValueTask<StreamScanReport> PassAsync() =>
            _resident!.RunOnceAsync(TestContext.Current.CancellationToken);

        private FlowStreamScan ScanOver(FlowStreamCatalog catalogue) => new(
            Host, catalogue, Source, Checkpoints, SideOutput, Durability, Options);
    }
}

/// <summary>
/// A stream over a list, read from a position, that can be told it has been trimmed.
/// </summary>
/// <remarks>
/// The position is the record's ordinal rendered as a string, because the contract says a
/// position is opaque above the plugin — a double whose position had structure would let a defect
/// in the engine pass here and fail against Redis.
/// </remarks>
internal sealed class RecordingStreamSource : IStreamSource
{
    private readonly List<StreamRecord> _records = [];

    /// <summary>Whether the next read fails because the checkpointed position is gone.</summary>
    public bool Trimmed { get; set; }

    /// <summary>How many records the engine has been handed, over the life of this source.</summary>
    public int HandedOut { get; private set; }

    public string Stream => "recording";

    /// <summary>Stages records at the given event times, in arrival order.</summary>
    public void Stage(params DateTimeOffset[] eventTimes)
    {
        foreach (var eventTime in eventTimes)
        {
            Stage(eventTime, tenantId: null);
        }
    }

    /// <summary>Stages one record belonging to a tenant.</summary>
    public void Stage(DateTimeOffset eventTime, string? tenantId)
    {
        _records.Add(new StreamRecord(
            new StreamPosition(_records.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            eventTime,
            TenantId: tenantId));
    }

    public ValueTask<Result<IReadOnlyList<StreamRecord>>> ReadAsync(
        StreamSubscription subscription,
        StreamPosition? after,
        int max,
        CancellationToken cancellationToken)
    {
        if (Trimmed)
        {
            return ValueTask.FromResult<Result<IReadOnlyList<StreamRecord>>>(
                StreamErrors.Trimmed(after ?? new StreamPosition("0")));
        }

        var from = after is { } position
            ? int.Parse(position.Value, System.Globalization.CultureInfo.InvariantCulture) + 1
            : 0;

        var take = _records.Skip(from).Take(max).ToList();

        HandedOut += take.Count;

        return ValueTask.FromResult<Result<IReadOnlyList<StreamRecord>>>(take);
    }
}

/// <summary>A checkpoint in a field, that can be told to lose every commit.</summary>
internal sealed class RecordingCheckpointStore : IStreamCheckpointStore
{
    private readonly List<StreamPosition> _committed = [];

    /// <summary>Stops the checkpoint moving, which is what a crash before the commit leaves.</summary>
    public bool SuppressCommits { get; set; }

    /// <summary>The position a resuming node reads.</summary>
    public StreamPosition? Stored { get; set; }

    /// <summary>Every position committed, in order.</summary>
    public IReadOnlyList<StreamPosition> Committed => _committed;

    public ValueTask<Result<StreamPosition?>> ReadAsync(
        StreamSubscription subscription, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Ok(Stored));

    public ValueTask<Result<bool>> CommitAsync(
        StreamSubscription subscription, StreamPosition position, CancellationToken cancellationToken)
    {
        if (SuppressCommits)
        {
            return ValueTask.FromResult(Result.Ok(false));
        }

        _committed.Add(position);
        Stored = position;

        return ValueTask.FromResult(Result.Ok(true));
    }
}

/// <summary>A side output that records what it took, and can refuse.</summary>
internal sealed class RecordingSideOutput : IStreamSideOutput
{
    private readonly List<StreamRecord> _routed = [];

    /// <summary>Whether the sink refuses, which must hold the checkpoint.</summary>
    public bool Refuse { get; set; }

    /// <summary>Every late record taken, in order.</summary>
    public IReadOnlyList<StreamRecord> Routed => _routed;

    public ValueTask<Result<StreamRecord>> OnLateAsync(
        StreamSubscription subscription,
        StreamRecord record,
        DateTimeOffset watermark,
        CancellationToken cancellationToken)
    {
        if (Refuse)
        {
            return ValueTask.FromResult(Result.Fail<StreamRecord>(
                new Error("sink.unavailable", "No.", ErrorCategory.Unavailable)));
        }

        _routed.Add(record);

        return ValueTask.FromResult(Result.Ok(record));
    }
}

/// <summary>Records the window batch each flow was started with, and can be made slow.</summary>
internal sealed class RecordingStreamDispatcher : IStepDispatcher
{
    private readonly Lock _gate = new();
    private readonly List<object?> _inputs = [];

    /// <summary>How long each step takes, which is how a slow consumer is expressed.</summary>
    public TimeSpan StepCost { get; set; }

    /// <summary>How many records the flows have consumed, which is the drain side of the bound.</summary>
    public int RecordsConsumed { get; private set; }

    public IReadOnlyList<object?> Inputs
    {
        get
        {
            lock (_gate)
            {
                return [.. _inputs];
            }
        }
    }

    public JournalPayload DescribeInput(object? input)
    {
        lock (_gate)
        {
            _inputs.Add(input);

            if (input is StreamWindowBatch batch)
            {
                RecordsConsumed += batch.Records.Count;
            }
        }

        return JournalPayload.Empty;
    }

    public async ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
    {
        if (StepCost > TimeSpan.Zero)
        {
            await Task.Delay(StepCost, ct).ConfigureAwait(false);
        }

        return StepOutcome.Success;
    }

    public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(StepOutcome.Success);

    public bool Evaluate(int stepIndex, FlowContext ctx) => true;

    public int Select(int stepIndex, FlowContext ctx) =>
        throw new NotSupportedException("This double runs plans with no switch step.");

    public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
        throw new NotSupportedException("This dispatcher has no iteration to begin.");

    public FlowContext EnterIteration(
        int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
        throw new NotSupportedException("This dispatcher has no iteration to enter.");
}
