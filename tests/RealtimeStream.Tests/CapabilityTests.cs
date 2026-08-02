using FlowX;
using FlowX.Testing;
using Shouldly;
using Xunit;

namespace RealtimeStream.Tests;

/// <summary>
/// The three capabilities on their own, constructed and called with no engine anywhere.
/// </summary>
/// <remarks>
/// A capability that can only be tested through a flow is a capability that has a transport or a
/// runtime inside it. These are here to show that this sample's do not: each is
/// <c>new</c>-ed, handed an input, and asked for a <c>Result</c>.
/// </remarks>
public sealed class CapabilityTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>A context with usable defaults. None of these capabilities reads anything off it.</summary>
    private static readonly CapabilityContext Context = new TestCapabilityContext("test-key");

    private static StreamWindowBatch Batch(params StreamRecord[] records) => new(
        StreamHarness.Source,
        StreamHarness.Origin,
        StreamHarness.Origin + StreamHarness.Window,
        records);

    private static StreamRecord Record(int position, string? payload) => new(
        new StreamPosition(position.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        StreamHarness.Origin.AddSeconds(position),
        PartitionKey: null,
        Payload: payload);

    /// <summary>The fold reads every record's body and reports statistics over them.</summary>
    [Fact]
    public async Task TheFoldComputesStatisticsOverTheWindowsRecords()
    {
        var result = await new FoldReadings().ExecuteAsync(
            Batch(
                Record(0, StreamHarness.Body("device-a", 10)),
                Record(1, StreamHarness.Body("device-b", 30)),
                Record(2, StreamHarness.Body("device-a", 20))),
            Context,
            None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Readings.ShouldBe(3);
        result.Value.Devices.ShouldBe(2);
        result.Value.MinCelsius.ShouldBe(10);
        result.Value.MaxCelsius.ShouldBe(30);
        result.Value.MeanCelsius.ShouldBe(20);
        result.Value.WindowStart.ShouldBe(StreamHarness.Origin);
    }

    /// <summary>
    /// A record this application cannot read is counted, and the rest of the window is still
    /// aggregated.
    /// </summary>
    /// <remarks>
    /// The decision worth pinning, because both directions are defensible and only one is
    /// implemented. Refusing the window on one malformed body would let a single misconfigured
    /// producer delete an entire minute of a fleet's telemetry; aggregating the rest and counting
    /// the failure keeps the answer and reports the fault. <see cref="DeviceStats.Malformed"/> is
    /// where the fault is reported, which is why it is on the contract rather than logged.
    /// </remarks>
    [Fact]
    public async Task AMalformedRecordIsCountedAndTheRestOfTheWindowStillAggregates()
    {
        var result = await new FoldReadings().ExecuteAsync(
            Batch(
                Record(0, StreamHarness.Body("device-a", 10)),
                Record(1, "{ this is not json"),
                Record(2, null),
                Record(3, StreamHarness.Body("device-b", 30))),
            Context,
            None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Readings.ShouldBe(2);
        result.Value.Malformed.ShouldBe(2);
        result.Value.MeanCelsius.ShouldBe(20, "the mean is over the readable records only.");
    }

    /// <summary>A window in which nothing was readable is a failure, not a mean of zero.</summary>
    /// <remarks>
    /// The one case where a malformed body does refuse. Publishing statistics computed from no
    /// records would put a number into a dashboard that no device measured, which is worse than
    /// a window that visibly failed.
    /// </remarks>
    [Fact]
    public async Task AWindowWithNothingReadableFailsRatherThanPublishingZero()
    {
        var result = await new FoldReadings().ExecuteAsync(
            Batch(Record(0, "not json"), Record(1, "also not json")),
            Context,
            None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("telemetry.no_readable_records");
        result.Error.Category.ShouldBe(ErrorCategory.Validation);
    }

    /// <summary>The detector fires on a mean above its ceiling, and says which rule it was.</summary>
    [Fact]
    public async Task TheDetectorFiresOnAMeanAboveItsCeiling()
    {
        var stats = new DeviceStats(
            StreamHarness.Source,
            StreamHarness.Origin,
            StreamHarness.Origin + StreamHarness.Window,
            Devices: 1,
            Readings: 10,
            Malformed: 0,
            MinCelsius: 70,
            MaxCelsius: 80,
            MeanCelsius: DetectAnomalies.MeanCelsiusCeiling + 1);

        var result = await new DetectAnomalies().ExecuteAsync(stats, Context, None);

        result.Value.IsAnomalous.ShouldBeTrue();
        result.Value.Reason.ShouldNotBeNullOrEmpty(
            "a verdict with no reason is an alert nobody can act on.");
    }

    /// <summary>An ordinary window is not an anomaly.</summary>
    [Fact]
    public async Task TheDetectorIsSilentOnAnOrdinaryWindow()
    {
        var stats = new DeviceStats(
            StreamHarness.Source,
            StreamHarness.Origin,
            StreamHarness.Origin + StreamHarness.Window,
            Devices: 4,
            Readings: 40,
            Malformed: 0,
            MinCelsius: 18,
            MaxCelsius: 24,
            MeanCelsius: 21);

        var result = await new DetectAnomalies().ExecuteAsync(stats, Context, None);

        result.Value.IsAnomalous.ShouldBeFalse();
        result.Value.Reason.ShouldBeNull();
    }

    /// <summary>
    /// The store key is a function of the stream and the interval, and of nothing else.
    /// </summary>
    /// <remarks>
    /// <strong>This is the half of exactly-once the platform cannot supply.</strong> The engine
    /// commits the checkpoint after a window's flow has run, so a crash in between makes it
    /// rebuild and re-run that window; the journal refuses the duplicate instance, but a node
    /// that died before the journal recorded anything reaches the store twice. A key derived from
    /// the interval makes the second write an overwrite. A key carrying a node name, a counter or
    /// a timestamp would make it a second row, and the aggregate would be double-counted with
    /// nothing anywhere reporting it.
    /// </remarks>
    [Fact]
    public void TheStoreKeyIsDerivedFromTheWindowAndNotMinted()
    {
        var stats = new DeviceStats(
            StreamHarness.Source,
            StreamHarness.Origin,
            StreamHarness.Origin + StreamHarness.Window,
            Devices: 1,
            Readings: 1,
            Malformed: 0,
            MinCelsius: 20,
            MaxCelsius: 20,
            MeanCelsius: 20);

        // The same interval aggregated to a different answer — which is what a rebuilt window
        // would look like if the fold were not deterministic — must still be the same key.
        var recomputed = stats with { Readings = 2, MeanCelsius = 21 };

        PersistAggregate.KeyFor(recomputed).ShouldBe(PersistAggregate.KeyFor(stats));

        PersistAggregate.KeyFor(stats with { WindowStart = StreamHarness.Origin.AddMinutes(1) })
            .ShouldNotBe(
                PersistAggregate.KeyFor(stats), "a different interval is a different aggregate.");
    }
}
