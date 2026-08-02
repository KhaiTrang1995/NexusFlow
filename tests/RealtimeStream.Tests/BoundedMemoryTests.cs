using Shouldly;
using Xunit;

namespace RealtimeStream.Tests;

/// <summary>
/// A store slower than the stream, and the bound that stops this sample's memory growing to
/// match.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the sample's headline property, asserted as correctness and not as
/// throughput.</strong> <c>samples/realtime-stream/README.md</c> asked for a test that produced a
/// million records, ran for thirty seconds and asserted on <c>PeakBytes</c> and a Kafka pause
/// count. None of those three is available: there is no Kafka in this repository, resident bytes
/// are a property of the GC rather than of the engine, and a thirty-second test that measures a
/// rate is a benchmark — which is B13, and B13 is deferred. What survives the translation is the
/// claim underneath, and it is a claim about <em>correctness</em>: the number of records this
/// process holds is bounded by a number the deployment declared, and it stays bounded no matter
/// how far behind the sink falls.
/// </para>
/// <para>
/// <strong>The measure is records, not bytes.</strong> A record the engine has been handed and
/// the flow has not finished with is a record in this process's heap; a record still on the
/// stream is Redis's problem. So <c>HandedOut − Consumed</c> is exactly the quantity the design
/// bounds, it is an integer rather than an estimate, and it is sampled by the source itself on
/// every read — the moment the engine is asking for more, which is where an unbounded engine
/// would run away. Asserting on <c>GC.GetTotalMemory</c> instead would produce a test that fails
/// when someone changes an unrelated allocation and passes when the bound is deleted.
/// </para>
/// <para>
/// <strong><see cref="ThePeakFollowsTheDeclaredCapacity"/> is what makes the first assertion
/// mean something.</strong> "Some number stayed under some limit" is worth nothing unless raising
/// the limit makes it climb — it would pass equally against an engine that read the whole stream
/// and one that read nothing. That test runs the identical workload at two capacities and
/// asserts the peak follows the capacity.
/// </para>
/// </remarks>
public sealed class BoundedMemoryTests
{
    /// <summary>
    /// How many records the stream holds. Far more than any bound under test, and far more than
    /// the slow store can absorb inside one pass.
    /// </summary>
    private const int Staged = 600;

    /// <summary>
    /// How long the store takes per window. Slow enough that the reader would outrun it by
    /// hundreds of records if nothing stopped it.
    /// </summary>
    private static readonly TimeSpan StoreCost = TimeSpan.FromMilliseconds(2);

    /// <summary>
    /// A store slower than the stream does not make this sample hold the stream: the backlog
    /// stays at the source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The records are spaced one window apart in event time, so each one closes the window
    /// before it and at most a couple sit in open windows at any moment. That is deliberate: it
    /// leaves the bounded channel as the only thing holding records, so the number this test
    /// measures is the number the assertion is about rather than a sum of two unrelated bounds.
    /// </para>
    /// <para>
    /// The window, the lateness and the parallelism are the ones the sample declares. A test
    /// that chose its own would be asserting about an arrangement nobody deploys.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASlowStoreBoundsTheRecordsThisProcessHolds()
    {
        var harness = StreamHarness.Create(channelCapacity: 16, storeCost: StoreCost);

        StageOneRecordPerWindow(harness, Staged);

        await harness.PassAsync(TestContext.Current.CancellationToken);

        harness.Store.Writes.ShouldBeGreaterThan(
            0, "a pass that aggregated nothing would bound memory by doing no work.");

        harness.Stream.PeakOutstanding.ShouldBeLessThanOrEqualTo(
            Ceiling(channelCapacity: 16),
            "the channel holds 16, a couple of records sit in windows the watermark has not " +
            $"closed, and one window's records belong to the flow until it returns. {Staged} " +
            "records were available and this process never held more than a few dozen, because " +
            "FlowStreamScan issues no read at all when the channel is full.");

        harness.Stream.Reads.ShouldBeGreaterThan(
            Staged / 4,
            "and it took them a few at a time rather than in one gulp. A pass that read " +
            $"{Staged} records in a handful of calls would have held them all, whatever the " +
            "channel was declared to be.");
    }

    /// <summary>
    /// The peak follows the declared capacity, which is what says the bound is doing the work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Written so that it fails if the bound stops being enforced.</strong> The two runs
    /// differ in exactly one value. If <c>FlowStreamScan</c> read ahead regardless of how full
    /// its channel was, both peaks would be far larger and the first assertion would fail; if it
    /// never read ahead at all, both would be tiny and the second would fail. Only an engine that
    /// really does read up to its capacity and then stops satisfies both.
    /// </para>
    /// <para>
    /// This is also the test that was used to check the suite can fail at all. Replacing
    /// <c>FlowStreamScan.ReadIntoAsync</c>'s
    /// <c>var room = _options.StreamChannelCapacity - channel.Reader.Count;</c> with
    /// <c>var room = _options.StreamChannelCapacity;</c> — a reader that never consults the
    /// channel — makes the first assertion here and the last assertion above both fail, because
    /// the engine then materialises a whole further batch on top of a full channel.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ThePeakFollowsTheDeclaredCapacity()
    {
        var tight = StreamHarness.Create(channelCapacity: 16, storeCost: StoreCost);
        var loose = StreamHarness.Create(channelCapacity: 512, storeCost: StoreCost);

        StageOneRecordPerWindow(tight, Staged);
        StageOneRecordPerWindow(loose, Staged);

        await tight.PassAsync(TestContext.Current.CancellationToken);
        await loose.PassAsync(TestContext.Current.CancellationToken);

        tight.Stream.PeakOutstanding.ShouldBeLessThanOrEqualTo(Ceiling(channelCapacity: 16));

        loose.Stream.PeakOutstanding.ShouldBeGreaterThan(
            256,
            "the same workload against a channel 32 times larger holds hundreds of records — so " +
            "the assertion above is a statement about the declared bound rather than about how " +
            "fast the flows happen to run.");

        loose.Stream.PeakOutstanding.ShouldBeLessThanOrEqualTo(
            Ceiling(channelCapacity: 512),
            "and the larger channel is bounded by its own capacity too, by the same rule. The " +
            "engine reads up to what it declared and then stops; it does not read whatever is " +
            "there.");
    }

    /// <summary>
    /// A window wider than the memory bound stops the subscription rather than evicting records.
    /// </summary>
    /// <remarks>
    /// The other half of the memory story, and the one that is a refusal rather than a pause. The
    /// channel bounds what is in flight; <c>StreamMaxResidentRecords</c> bounds what is sitting
    /// in open windows, and a window that would exceed it is refused with
    /// <c>stream.window_overflow</c>. Evicting instead would emit an aggregate computed from part
    /// of its input — a number that is quietly wrong, which is worse than a subscription that
    /// visibly stopped.
    /// </remarks>
    [Fact]
    public async Task AWindowOverTheResidentBoundStopsRatherThanDroppingRecords()
    {
        var harness = StreamHarness.Create(maxResident: 4);

        // All inside one window, so they are all resident at once and none of them closes it.
        for (var index = 0; index < 32; index++)
        {
            harness.Stream.Stage(
                StreamHarness.Origin.AddSeconds(index),
                StreamHarness.Body("device-000", 20 + index));
        }

        var report = await harness.PassAsync(TestContext.Current.CancellationToken);

        report.Error.ShouldNotBeNull();
        report.Error.Code.ShouldBe("stream.window_overflow");

        harness.Store.Aggregates.ShouldBeEmpty(
            "nothing was aggregated from a partial window, which is the point of refusing.");
    }

    /// <summary>
    /// The most records this process may hold, given a channel capacity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The channel's capacity, plus a small allowance for the records that are legitimately
    /// somewhere else at the same instant: the one or two sitting in windows the watermark has
    /// not closed yet, and the window whose records belong to the running flow until it returns.
    /// With one record per window that allowance is tiny, which is why the workload is staged
    /// that way.
    /// </para>
    /// <para>
    /// Stated as a function rather than a literal so that the two capacities under test are
    /// judged by one rule. A literal per test is how a bound quietly becomes "whatever the last
    /// run happened to produce".
    /// </para>
    /// </remarks>
    private static int Ceiling(int channelCapacity) => channelCapacity + WindowAllowance;

    /// <summary>Records legitimately outside the channel while a pass is running.</summary>
    private const int WindowAllowance = 8;

    /// <summary>
    /// Stages records one window apart, so each closes the one before it and open windows stay
    /// shallow.
    /// </summary>
    private static void StageOneRecordPerWindow(StreamHarness harness, int count)
    {
        for (var index = 0; index < count; index++)
        {
            harness.Stream.Stage(
                StreamHarness.Origin + (StreamHarness.Window * index),
                StreamHarness.Body("device-000", 20 + (index % 7)));
        }
    }
}
