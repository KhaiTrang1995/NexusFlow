using Shouldly;
using Xunit;

namespace RealtimeStream.Tests;

/// <summary>
/// What a closed window aggregates to, what happens to a record that arrives too late, and what
/// stops a rebuilt window being counted twice.
/// </summary>
/// <remarks>
/// The three promises <c>samples/realtime-stream/README.md</c> makes about correctness, each
/// asserted against the sample's own flow rather than against a stand-in. The engine's own
/// version of these lives in <c>tests/FlowX.Hosting.Tests/StreamScanTests.cs</c>; what is added
/// here is that <em>this application's</em> capabilities produce the right answer when the engine
/// hands them a window.
/// </remarks>
public sealed class WindowingTests
{
    /// <summary>A closed window aggregates its records and nothing else.</summary>
    /// <remarks>
    /// The window's upper bound is exclusive, so the record that closes it is in the next window
    /// and not in this one. That is the assertion most likely to catch a change to the assigner:
    /// an inclusive bound would put four readings in the aggregate instead of three and nothing
    /// else in this file would notice.
    /// </remarks>
    [Fact]
    public async Task AClosedWindowAggregatesItsOwnRecordsOnly()
    {
        var harness = StreamHarness.Create();

        harness.Stream
            .Stage(StreamHarness.Origin, StreamHarness.Body("device-a", 10))
            .Stage(StreamHarness.Origin.AddSeconds(20), StreamHarness.Body("device-b", 20))
            .Stage(StreamHarness.Origin.AddSeconds(40), StreamHarness.Body("device-a", 30))

            // Past the window's end plus the declared PT10S of lateness, so it closes the first
            // window and opens the second.
            .Stage(StreamHarness.Origin.AddSeconds(75), StreamHarness.Body("device-c", 40));

        var report = await harness.PassAsync(TestContext.Current.CancellationToken);

        report.Windows.ShouldBe(1);
        report.Started.ShouldBe(1);

        var aggregate = harness.Store.Aggregates.Values.ShouldHaveSingleItem();

        aggregate.Stats.WindowStart.ShouldBe(StreamHarness.Origin);
        aggregate.Stats.WindowEnd.ShouldBe(StreamHarness.Origin + StreamHarness.Window);
        aggregate.Stats.Readings.ShouldBe(3, "the record that closed the window is in the next one.");
        aggregate.Stats.Devices.ShouldBe(2, "device-a reported twice.");
        aggregate.Stats.MinCelsius.ShouldBe(10);
        aggregate.Stats.MaxCelsius.ShouldBe(30);
        aggregate.Stats.MeanCelsius.ShouldBe(20);
        aggregate.Stats.Malformed.ShouldBe(0);
    }

    /// <summary>An open window aggregates nothing, and holds the checkpoint where it started.</summary>
    /// <remarks>
    /// The consequence of ADR-0056 a user has to know: an open window is invisible until data
    /// closes it, and no amount of waiting changes that because the watermark is observed rather
    /// than taken from a clock. The checkpoint staying behind the window's oldest record is what
    /// makes the restart in
    /// <see cref="AWindowRebuiltAfterANodeDeathIsNotAggregatedTwice"/> able to rebuild it.
    /// </remarks>
    [Fact]
    public async Task AnOpenWindowAggregatesNothingAndHoldsTheCheckpoint()
    {
        var harness = StreamHarness.Create();

        harness.Stream
            .Stage(StreamHarness.Origin, StreamHarness.Body("device-a", 10))
            .Stage(StreamHarness.Origin.AddSeconds(20), StreamHarness.Body("device-b", 20));

        var report = await harness.PassAsync(TestContext.Current.CancellationToken);

        report.Read.ShouldBe(2);
        report.Windows.ShouldBe(0);

        harness.Store.Aggregates.ShouldBeEmpty();
        harness.Checkpoints.Committed.ShouldBeEmpty(
            "nothing has been dealt with, so there is no settled prefix to commit.");
    }

    /// <summary>
    /// A record later than the declared lateness is routed to the side output, never dropped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The promise docs/09 §9 makes by name, and the reason
    /// <see cref="TelemetrySideOutput"/> is a required registration rather than an optional one.
    /// The assertion is on the sample's own <see cref="LateReading"/> log, because "the engine
    /// called something" is not the promise — "this application has the record and can act on
    /// it" is.
    /// </para>
    /// <para>
    /// The watermark it carries is asserted too. A side output that recorded only "this was late"
    /// would leave an operator unable to tell a producer with a broken clock from a lateness
    /// setting that is too tight.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARecordPastTheLatenessIsRoutedToTheSideOutputAndNotDropped()
    {
        var harness = StreamHarness.Create();

        harness.Stream
            .Stage(StreamHarness.Origin, StreamHarness.Body("device-a", 10))

            // Moves the watermark five minutes on, closing every window behind it.
            .Stage(StreamHarness.Origin.AddMinutes(5), StreamHarness.Body("device-b", 20))

            // Belongs to the first window, which is long closed and cannot be reopened.
            .Stage(StreamHarness.Origin.AddSeconds(30), StreamHarness.Body("device-c", 99))

            .Stage(StreamHarness.Origin.AddMinutes(7), StreamHarness.Body("device-d", 21));

        var report = await harness.PassAsync(TestContext.Current.CancellationToken);

        report.Late.ShouldBe(1);

        var late = harness.Late.Readings.ShouldHaveSingleItem();

        late.Source.ShouldBe(StreamHarness.Source);
        late.EventTime.ShouldBe(StreamHarness.Origin.AddSeconds(30));
        late.Lateness.ShouldBeGreaterThan(
            TimeSpan.Zero, "a record the engine refused is behind the watermark by construction.");
        late.Payload.ShouldNotBeNull();

        harness.Store.Aggregates.Values
            .ShouldAllBe(aggregate => aggregate.Stats.MaxCelsius < 99,
                "the late record's reading is in no window's aggregate — it was routed, and " +
                "routing it into an already-published aggregate is exactly what lateness means " +
                "it cannot do.");
    }

    /// <summary>
    /// An out-of-order record still inside the lateness joins its window rather than the side
    /// output.
    /// </summary>
    /// <remarks>
    /// The other half of what <c>Lateness = "PT10S"</c> buys, and the half a test asserting only
    /// the side output would let regress to "everything out of order is late". A stream whose
    /// records arrive slightly reordered is the ordinary case; a lateness allowance that did not
    /// actually hold windows open for it would be a setting that cost memory and bought nothing.
    /// </remarks>
    [Fact]
    public async Task AnOutOfOrderRecordInsideTheLatenessStillJoinsItsWindow()
    {
        var harness = StreamHarness.Create();

        harness.Stream
            .Stage(StreamHarness.Origin.AddSeconds(50), StreamHarness.Body("device-a", 10))

            // Behind the one before it, but the watermark trails by PT10S so the window it
            // belongs to is still open.
            .Stage(StreamHarness.Origin.AddSeconds(45), StreamHarness.Body("device-b", 30))

            .Stage(StreamHarness.Origin.AddSeconds(75), StreamHarness.Body("device-c", 20));

        var report = await harness.PassAsync(TestContext.Current.CancellationToken);

        report.Late.ShouldBe(0, "it was out of order, not late.");

        harness.Late.Readings.ShouldBeEmpty();

        harness.Store.Aggregates.Values
            .ShouldHaveSingleItem()
            .Stats.Readings.ShouldBe(2, "both records are in the window they belong to.");
    }

    /// <summary>
    /// A window rebuilt after a node death derives the same instance and is not aggregated twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is what makes window state not worth journaling</strong>, and it is the
    /// property the whole checkpoint design rests on. The first node aggregates the window and
    /// dies before its checkpoint moves; the second node re-reads from the position that never
    /// advanced, rebuilds an identical window because assignment is a pure function of event
    /// time, derives an identical instance id, and meets the journal's primary key.
    /// </para>
    /// <para>
    /// The store's write count is asserted alongside the journal's instance count because they
    /// answer different questions. One instance says the platform deduplicated; one aggregate
    /// under one key says this application would have been correct even if it had not — which is
    /// the half the platform cannot supply, and the reason
    /// <see cref="PersistAggregate.KeyFor"/> derives the key from the interval.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AWindowRebuiltAfterANodeDeathIsNotAggregatedTwice()
    {
        var harness = StreamHarness.Create();

        harness.Stream
            .Stage(StreamHarness.Origin, StreamHarness.Body("device-a", 10))
            .Stage(StreamHarness.Origin.AddSeconds(30), StreamHarness.Body("device-b", 20))
            .Stage(StreamHarness.Origin.AddSeconds(75), StreamHarness.Body("device-c", 30));

        harness.Checkpoints.SuppressCommits = true;

        (await harness.PassAsync(TestContext.Current.CancellationToken)).Started.ShouldBe(1);

        harness.Journal.Instances.Count.ShouldBe(1);
        harness.Store.Aggregates.Count.ShouldBe(1);

        // A new node: no open windows, and the checkpoint that never moved.
        var restarted = harness.Restart();

        var report = await restarted.PassAsync(TestContext.Current.CancellationToken);

        report.Windows.ShouldBe(1, "the window is rebuilt from the same records.");
        report.Deduplicated.ShouldBe(1);
        report.Started.ShouldBe(0);

        harness.Journal.Instances.Count.ShouldBe(
            1, "and the journal holds one instance, not two.");

        harness.Store.Aggregates.Count.ShouldBe(
            1, "one interval, one aggregate — the key is derived from the window, not minted.");
    }

    /// <summary>A window whose flow ran lets the checkpoint move past it, and no further.</summary>
    /// <remarks>
    /// The progress rule, asserted where it is observable: the checkpoint reaches the last record
    /// of the closed window and stops at the first record of the one still open. A checkpoint
    /// that ran ahead of the open window would make a restart unable to rebuild it, which is the
    /// one failure the whole arrangement exists to prevent.
    /// </remarks>
    [Fact]
    public async Task TheCheckpointStopsAtTheOldestUnsettledRecord()
    {
        var harness = StreamHarness.Create();

        harness.Stream
            .Stage(StreamHarness.Origin, StreamHarness.Body("device-a", 10))
            .Stage(StreamHarness.Origin.AddSeconds(30), StreamHarness.Body("device-b", 20))
            .Stage(StreamHarness.Origin.AddSeconds(75), StreamHarness.Body("device-c", 30));

        await harness.PassAsync(TestContext.Current.CancellationToken);

        harness.Checkpoints.Committed.ShouldNotBeEmpty();

        harness.Checkpoints.Committed[^1].Value.ShouldBe(
            "1",
            "records 0 and 1 were the closed window; record 2 opened the next one and is not " +
            "dealt with, so the prefix stops before it.");
    }
}
