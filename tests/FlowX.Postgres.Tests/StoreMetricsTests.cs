using System.Diagnostics.Metrics;
using FlowX.Conformance.InMemory;
using FlowX.Observability;
using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// The three gauges of <a href="../../docs/12-Observability.md">12-Observability</a> §3 whose
/// value lives in the store, measured against a real PostgreSQL schema.
/// </summary>
/// <remarks>
/// <para>
/// <strong><c>flowx_outbox_lag_seconds</c> is the reason this file exists.</strong> §3 said the
/// gauge "needs a column before it needs a meter" and that "the age of a pending event is a fact
/// the schema does not hold". That was wrong about which table holds it:
/// <c>PostgresFlowJournal.CommitAsync</c> inserts the <c>flow_step</c> row and stages the event
/// under one <c>sequence</c> in one transaction, and <c>flow_step.committed_at</c> defaults to
/// <c>now()</c> inside it. So the staging instant has been recorded since migration <c>0001</c>,
/// one join away, and <see cref="APendingEventsAgeIsTheAgeOfTheStepThatStagedIt"/> is the
/// evidence rather than the argument.
/// </para>
/// <para>
/// These run against a real database on purpose. The claim being tested is about a join and a
/// default, and neither is checkable against a double — a fake that returned the age it was told
/// to would pass while proving nothing about the schema this repository actually ships.
/// </para>
/// </remarks>
public sealed class StoreMetricsTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// The finding: a pending event's age is computable today, with no migration.
    /// </summary>
    /// <remarks>
    /// The assertion is bounded on both sides. A lower bound alone would pass against a query
    /// that returned the age of the process; an upper bound alone would pass against zero. What
    /// is being pinned is that the number tracks the step commit — so the event is staged, a
    /// known interval is waited out, and the gauge is required to have moved by about that much.
    /// </remarks>
    [Fact]
    public async Task APendingEventsAgeIsTheAgeOfTheStepThatStagedIt()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);
        using var metrics = new PostgresStoreMetrics(schema.DataSource);
        using var recorder = new GaugeRecorder();

        await StageAsync(schema, ["order.placed"]);

        // Long enough that a gauge stuck at zero is distinguishable from one that is working,
        // and short enough not to slow the suite down.
        await Task.Delay(TimeSpan.FromMilliseconds(1200), Cancellation);

        await metrics.RefreshAsync(Cancellation);

        var lag = recorder.ReadDouble(TelemetryNames.OutboxLagSeconds);

        lag.ShouldHaveSingleItem();
        lag[0].Tags.ToArray().ShouldContain(
            new KeyValuePair<string, object?>(TelemetryNames.TypeLabel, "order.placed"));

        lag[0].Value.ShouldBeGreaterThan(
            1.0,
            "The event was staged over a second ago. A gauge reading zero here is one that " +
            "found no timestamp — which is what docs/12 §3 claimed the schema could not hold.");

        lag[0].Value.ShouldBeLessThan(
            60.0,
            "And it must be the age of this event rather than of the schema, the process or " +
            "the epoch.");
    }

    /// <summary>A published event contributes to neither outbox gauge.</summary>
    /// <remarks>
    /// The half that stops both gauges being a count of every event ever emitted. §9's runbook
    /// row reads a growing <c>flowx_outbox_pending</c> as "events not arriving", which is only
    /// true if a delivered event leaves the series.
    /// </remarks>
    [Fact]
    public async Task APublishedEventLeavesBothOutboxGauges()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);
        using var metrics = new PostgresStoreMetrics(schema.DataSource);
        using var recorder = new GaugeRecorder();

        await StageAsync(schema, ["order.placed"]);
        await metrics.RefreshAsync(Cancellation);

        recorder.Read(TelemetryNames.OutboxPending).ShouldHaveSingleItem();
        recorder.ReadDouble(TelemetryNames.OutboxLagSeconds).ShouldHaveSingleItem();

        var published = await schema.OutboxPublisher(new RecordingEventPublisher())
            .PublishPendingAsync(Cancellation);

        published.Published.ShouldBe(1);

        await metrics.RefreshAsync(Cancellation);

        recorder.Read(TelemetryNames.OutboxPending).ShouldBeEmpty(
            "published_at is set, so the row is not pending.");

        recorder.ReadDouble(TelemetryNames.OutboxLagSeconds).ShouldBeEmpty(
            "and a drained outbox has no lag rather than a lag of zero — an absent series and a " +
            "confident zero say different things about whether anything was measured.");
    }

    /// <summary>
    /// <c>flowx_outbox_pending</c> is the count §3 writes out, grouped by type.
    /// </summary>
    [Fact]
    public async Task PendingEventsAreCountedByType()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);
        using var metrics = new PostgresStoreMetrics(schema.DataSource);
        using var recorder = new GaugeRecorder();

        await StageAsync(schema, ["order.placed", "order.placed", "inventory.reserved"]);
        await metrics.RefreshAsync(Cancellation);

        var pending = recorder.Read(TelemetryNames.OutboxPending);

        pending.Count.ShouldBe(2, "two distinct types were staged.");

        Value(pending, "order.placed").ShouldBe(2);
        Value(pending, "inventory.reserved").ShouldBe(1);
    }

    /// <summary>
    /// <c>flowx_flow_active</c> reports a suspended instance, which §3 and §9 both said would be
    /// permanently zero.
    /// </summary>
    /// <remarks>
    /// Both notes gave the same reason — "<c>FlowInstanceState.Suspended</c> is a value nothing
    /// sets (WP-63)" — and both had expired by the time they were read:
    /// <c>FlowEngine.InstanceStateFor</c> answers <c>Suspended</c> for a flow that parked, and
    /// <c>CompleteAsync</c> writes it. This is the assertion that the state reaches a gauge.
    /// </remarks>
    [Fact]
    public async Task ASuspendedInstanceIsReportedAsActive()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);
        using var metrics = new PostgresStoreMetrics(schema.DataSource);
        using var recorder = new GaugeRecorder();

        var instance = Guid.NewGuid();

        await schema.Journal.StartAsync(
            new FlowInstanceStart
            {
                InstanceId = instance,
                FlowId = "order.place",
                FlowVersion = "1.0.0",
                Token = new FencingToken(1),
            },
            Cancellation);

        var parked = await schema.Journal.CompleteAsync(
            instance,
            new FencingToken(1),
            FlowInstanceState.Suspended,
            JournalPayload.Empty,
            new FlowWake(StepScope.Root, 0, DateTimeOffset.UtcNow.AddHours(1)),
            Cancellation);

        parked.IsSuccess.ShouldBeTrue(parked.IsFailure ? parked.Error.ToString() : string.Empty);

        await metrics.RefreshAsync(Cancellation);

        var active = recorder.Read(TelemetryNames.FlowActive);

        active.ShouldHaveSingleItem();
        active[0].Value.ShouldBe(1);
        active[0].Tags.ToArray().ShouldContain(
            new KeyValuePair<string, object?>(TelemetryNames.FlowLabel, "order.place"));
        active[0].Tags.ToArray().ShouldContain(
            new KeyValuePair<string, object?>(TelemetryNames.StateLabel, "Suspended"));
    }

    /// <summary>A completed instance is not active.</summary>
    [Fact]
    public async Task ACompletedInstanceIsNotReportedAsActive()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);
        using var metrics = new PostgresStoreMetrics(schema.DataSource);
        using var recorder = new GaugeRecorder();

        var instance = Guid.NewGuid();

        await schema.Journal.StartAsync(
            new FlowInstanceStart
            {
                InstanceId = instance,
                FlowId = "order.place",
                FlowVersion = "1.0.0",
                Token = new FencingToken(1),
            },
            Cancellation);

        await schema.Journal.CompleteAsync(
            instance,
            new FencingToken(1),
            FlowInstanceState.Completed,
            JournalPayload.Empty,
            wake: null,
            Cancellation);

        await metrics.RefreshAsync(Cancellation);

        recorder.Read(TelemetryNames.FlowActive).ShouldBeEmpty(
            "a terminal instance is not stuck, and counting it would make the gauge §9 reads " +
            "for stuck flows grow for ever on a healthy deployment.");
    }

    /// <summary>
    /// A disposed store stops contributing, and the instruments stay registered.
    /// </summary>
    /// <remarks>
    /// The property that makes the static registration safe: several schemas exist across one
    /// test run, and a snapshot nothing refreshes must not keep publishing the numbers it last
    /// saw.
    /// </remarks>
    [Fact]
    public async Task ADisposedStoreStopsContributing()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);
        using var recorder = new GaugeRecorder();

        var metrics = new PostgresStoreMetrics(schema.DataSource);

        await StageAsync(schema, ["order.placed"]);
        await metrics.RefreshAsync(Cancellation);

        recorder.Read(TelemetryNames.OutboxPending).ShouldHaveSingleItem();

        metrics.Dispose();

        recorder.Read(TelemetryNames.OutboxPending).ShouldBeEmpty();
    }

    private static long Value(IReadOnlyList<Measurement<long>> measurements, string type) =>
        measurements
            .Single(m => m.Tags.ToArray().Any(
                t => t.Key == TelemetryNames.TypeLabel && (string?)t.Value == type))
            .Value;

    /// <summary>
    /// Collects the FlowX observable gauges on demand.
    /// </summary>
    /// <remarks>
    /// <see cref="MeterListener.RecordObservableInstruments"/> is what makes an observable
    /// instrument testable without an exporter: it invokes every enabled callback and delivers
    /// the measurements synchronously, so a test can stage a row, refresh, and read what an
    /// exporter would have seen.
    /// </remarks>
    private sealed class GaugeRecorder : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<(string Name, long Value, KeyValuePair<string, object?>[] Tags)> _longs = [];
        private readonly List<(string Name, double Value, KeyValuePair<string, object?>[] Tags)> _doubles = [];

        public GaugeRecorder()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = static (instrument, listener) =>
                {
                    if (instrument.Meter.Name == FlowXTelemetry.SourceName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };

            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                _longs.Add((instrument.Name, value, tags.ToArray())));

            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                _doubles.Add((instrument.Name, value, tags.ToArray())));

            _listener.Start();
        }

        public IReadOnlyList<Measurement<long>> Read(string instrument)
        {
            _longs.Clear();
            _listener.RecordObservableInstruments();

            return [.. _longs.Where(m => m.Name == instrument).Select(m => new Measurement<long>(m.Value, m.Tags))];
        }

        public IReadOnlyList<Measurement<double>> ReadDouble(string instrument)
        {
            _doubles.Clear();
            _listener.RecordObservableInstruments();

            return [.. _doubles.Where(m => m.Name == instrument).Select(m => new Measurement<double>(m.Value, m.Tags))];
        }

        public void Dispose() => _listener.Dispose();
    }

    private static async Task StageAsync(PostgresTestSchema schema, string[] types)
    {
        var instance = Guid.NewGuid();

        await schema.Journal.StartAsync(
            new FlowInstanceStart
            {
                InstanceId = instance,
                FlowId = "order.place",
                FlowVersion = "1.0.0",
                Token = new FencingToken(1),
            },
            Cancellation);

        var committed = await schema.Journal.CommitAsync(
            new StepCommit
            {
                Key = StepKey.First(instance, 0),
                Token = new FencingToken(1),
                CapabilityId = "inventory.reserve",
                CapabilityVersion = "2.1.0",
                Outcome = JournalOutcome.Success,
                Outbox =
                [
                    .. types.Select(static type => new OutboxWrite
                    {
                        Type = type,
                        SchemaVersion = "1.0.0",
                    }),
                ],
            },
            Cancellation);

        committed.IsSuccess.ShouldBeTrue(
            committed.IsFailure ? committed.Error.ToString() : string.Empty);
    }
}
