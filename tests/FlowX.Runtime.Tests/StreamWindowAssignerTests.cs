using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// What a record is assigned to, what closes a window, what makes a record late, and which
/// position is safe to checkpoint.
/// </summary>
/// <remarks>
/// <strong>Every claim ADR-0055 makes about not journaling window state rests on this class being
/// a pure function of event times.</strong> So these tests never read a clock, never sleep, and
/// drive the state machine one record at a time — and the rebuild test replays the same records
/// into a second instance and asserts it reaches the same state, which is exactly what a node
/// death asks a restart to do.
/// </remarks>
public sealed class StreamWindowAssignerTests
{
    private static readonly DateTimeOffset Origin =
        new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Windows are aligned to the epoch, not to the first record seen.</summary>
    /// <remarks>
    /// The property that lets a rebuilt window derive the id the original did. If the origin were
    /// the first record, two nodes reading from two checkpoints would cut the same stream into
    /// different intervals and every window would be a new instance.
    /// </remarks>
    [Fact]
    public void WindowsAreAlignedToTheEpoch()
    {
        var spec = Spec(TimeSpan.FromMinutes(1));

        spec.WindowStartFor(new DateTimeOffset(2026, 8, 2, 12, 0, 37, TimeSpan.Zero))
            .ShouldBe(new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));

        spec.WindowStartFor(new DateTimeOffset(2026, 8, 2, 12, 1, 0, TimeSpan.Zero))
            .ShouldBe(new DateTimeOffset(2026, 8, 2, 12, 1, 0, TimeSpan.Zero));

        spec.WindowStartFor(new DateTimeOffset(2026, 8, 2, 13, 0, 37, TimeSpan.FromHours(1)))
            .ShouldBe(
                new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero),
                "an offset is normalised before alignment, so two nodes in two zones agree.");
    }

    /// <summary>A window opens on its first record and stays open until the watermark passes it.</summary>
    [Fact]
    public void AWindowStaysOpenUntilTheWatermarkReachesItsEnd()
    {
        var assigner = new StreamWindowAssigner(Spec(TimeSpan.FromMinutes(1)), maxResident: 100);

        assigner.Admit(Record("1", Origin)).ShouldBe(StreamAdmission.Windowed);
        assigner.Admit(Record("2", Origin.AddSeconds(30))).ShouldBe(StreamAdmission.Windowed);

        assigner.TakeClosed().ShouldBeEmpty("nothing has arrived past the window's end.");
        assigner.OpenWindows.ShouldBe(1);

        assigner.Admit(Record("3", Origin.AddSeconds(61))).ShouldBe(StreamAdmission.Windowed);

        var closed = assigner.TakeClosed().ShouldHaveSingleItem();

        closed.Start.ShouldBe(Origin);
        closed.End.ShouldBe(Origin.AddMinutes(1));
        closed.Records.Count.ShouldBe(2, "the record that closed it belongs to the next window.");
        assigner.Resident.ShouldBe(1);
    }

    /// <summary>Lateness holds a window open past its end, by exactly the declared amount.</summary>
    [Fact]
    public void LatenessHoldsAWindowOpen()
    {
        var assigner = new StreamWindowAssigner(
            Spec(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10)), maxResident: 100);

        assigner.Admit(Record("1", Origin));
        assigner.Admit(Record("2", Origin.AddSeconds(65)));

        assigner.TakeClosed().ShouldBeEmpty("the watermark is 65s − 10s = 55s, short of the end.");

        assigner.Admit(Record("3", Origin.AddSeconds(30)))
            .ShouldBe(StreamAdmission.Windowed, "an out-of-order record still fits its window.");

        assigner.Admit(Record("4", Origin.AddSeconds(75)));

        assigner.TakeClosed().ShouldHaveSingleItem().Records.Count.ShouldBe(2);
    }

    /// <summary>A record whose window has closed is late, and is never silently placed.</summary>
    [Fact]
    public void ARecordPastItsWindowIsLate()
    {
        var assigner = new StreamWindowAssigner(Spec(TimeSpan.FromMinutes(1)), maxResident: 100);

        assigner.Admit(Record("1", Origin));
        assigner.Admit(Record("2", Origin.AddSeconds(61)));
        assigner.TakeClosed();

        assigner.Admit(Record("3", Origin.AddSeconds(30))).ShouldBe(StreamAdmission.Late);
        assigner.Resident.ShouldBe(1, "a late record joins no window.");
    }

    /// <summary>The watermark never moves backwards.</summary>
    [Fact]
    public void TheWatermarkIsMonotonic()
    {
        var assigner = new StreamWindowAssigner(Spec(TimeSpan.FromMinutes(1)), maxResident: 100);

        assigner.Admit(Record("1", Origin.AddMinutes(10)));

        var high = assigner.Watermark;

        assigner.Admit(Record("2", Origin));

        assigner.Watermark.ShouldBe(high);
    }

    /// <summary>
    /// The checkpoint is the last position of the longest settled prefix, and an open window
    /// holds it exactly where its oldest record is.
    /// </summary>
    /// <remarks>
    /// <strong>The property the whole no-window-state decision rests on.</strong> If the
    /// checkpoint could move past a record belonging to an open window, a restart would rebuild
    /// that window without it and emit an aggregate computed from part of its input.
    /// </remarks>
    [Fact]
    public void TheCheckpointNeverMovesPastAnOpenWindowsOldestRecord()
    {
        var assigner = new StreamWindowAssigner(Spec(TimeSpan.FromMinutes(1)), maxResident: 100);

        assigner.Admit(Record("1", Origin));
        assigner.Admit(Record("2", Origin.AddSeconds(61)));
        assigner.Admit(Record("3", Origin.AddSeconds(30)));

        var closed = assigner.TakeClosed().ShouldHaveSingleItem();

        assigner.TakeCheckpoint().ShouldBeNull("nothing is settled yet.");

        assigner.Settle(closed.Start);

        assigner.TakeCheckpoint().ShouldBe(
            new StreamPosition("1"),
            "record 2 is in the open window, so the prefix stops at record 1 — which is where a " +
            "restart must resume from to rebuild it.");
    }

    /// <summary>A late record settles the prefix once the side output has taken it.</summary>
    [Fact]
    public void ALateRecordSettlesWhenTheSideOutputTakesIt()
    {
        var assigner = new StreamWindowAssigner(Spec(TimeSpan.FromSeconds(30)), maxResident: 100);

        assigner.Admit(Record("1", Origin));
        assigner.Admit(Record("2", Origin.AddSeconds(31)));

        assigner.Settle(assigner.TakeClosed().ShouldHaveSingleItem().Start);
        assigner.Admit(Record("3", Origin.AddSeconds(1))).ShouldBe(StreamAdmission.Late);

        assigner.TakeCheckpoint().ShouldBe(new StreamPosition("1"));

        assigner.SettleLate(new StreamPosition("3"));

        assigner.TakeCheckpoint().ShouldBeNull(
            "record 2 is still in an open window and sits before record 3 in arrival order.");
    }

    /// <summary>The resident bound refuses rather than evicting.</summary>
    /// <remarks>
    /// The half of the memory bound the channel does not cover. An engine that evicted here would
    /// emit a window computed from part of its input, which is wrong in a way nothing downstream
    /// could detect.
    /// </remarks>
    [Fact]
    public void TheResidentBoundRefuses()
    {
        var assigner = new StreamWindowAssigner(Spec(TimeSpan.FromHours(1)), maxResident: 2);

        assigner.Admit(Record("1", Origin));
        assigner.Admit(Record("2", Origin.AddSeconds(1)));

        assigner.Admit(Record("3", Origin.AddSeconds(2))).ShouldBe(StreamAdmission.Overflowed);
        assigner.Resident.ShouldBe(2, "the refused record is not held.");
    }

    /// <summary>
    /// Replaying the same records from the same position reaches the same state — which is what a
    /// node death asks a restart to do.
    /// </summary>
    [Fact]
    public void ReplayingFromTheCheckpointRebuildsTheSameWindows()
    {
        var records = new[]
        {
            Record("1", Origin),
            Record("2", Origin.AddSeconds(30)),
            Record("3", Origin.AddSeconds(61)),
            Record("4", Origin.AddSeconds(70)),
        };

        var died = new StreamWindowAssigner(Spec(TimeSpan.FromMinutes(1)), maxResident: 100);

        foreach (var record in records)
        {
            died.Admit(record);
        }

        died.TakeClosed();

        var restarted = new StreamWindowAssigner(Spec(TimeSpan.FromMinutes(1)), maxResident: 100);

        foreach (var record in records)
        {
            restarted.Admit(record);
        }

        restarted.TakeClosed().ShouldHaveSingleItem().Start.ShouldBe(Origin);
        restarted.Watermark.ShouldBe(died.Watermark);
        restarted.Resident.ShouldBe(died.Resident);
    }

    /// <summary>The window a rebuild produces derives the id the original did.</summary>
    /// <remarks>
    /// The other half of ADR-0055: rebuilding identically is worth nothing unless the rebuilt
    /// window is refused by the journal, and it is refused because the id is the same.
    /// </remarks>
    [Fact]
    public void AWindowDerivesTheSameInstanceIdOnEveryNode()
    {
        var first = StreamIdentity.InstanceIdFor(
            "telemetry.aggregate", "1.0.0", "device.telemetry", string.Empty, Origin, Origin.AddMinutes(1));

        var second = StreamIdentity.InstanceIdFor(
            "telemetry.aggregate", "1.0.0", "device.telemetry", string.Empty,
            Origin.ToOffset(TimeSpan.FromHours(5)),
            Origin.AddMinutes(1).ToOffset(TimeSpan.FromHours(5)));

        second.ShouldBe(first, "the bounds are normalised to UTC before they are hashed.");

        StreamIdentity
            .InstanceIdFor(
                "telemetry.aggregate", "1.0.0", "device.telemetry", string.Empty,
                Origin.AddMinutes(1), Origin.AddMinutes(2))
            .ShouldNotBe(first);
    }

    /// <summary>Every window shape but tumbling is refused, with the id that reports it.</summary>
    [Theory]
    [InlineData("sliding:1m:30s")]
    [InlineData("session:5m")]
    [InlineData("global")]
    [InlineData("tumbling:")]
    [InlineData("tumbling:0s")]
    [InlineData("tumbling:1week")]
    public void AWindowShapeTheEngineDoesNotImplementIsRefused(string window)
    {
        var read = StreamWindowSpec.Read(window, "PT0S", "PT5S", 1);

        read.IsFailure.ShouldBeTrue();
        read.Error.Code.ShouldBe(StreamErrors.UnsupportedWindowCode);
    }

    /// <summary>The short duration forms docs/09 §9 prints are read.</summary>
    [Theory]
    [InlineData("tumbling:500ms", 0.5)]
    [InlineData("tumbling:30s", 30)]
    [InlineData("tumbling:1m", 60)]
    [InlineData("tumbling:1h", 3600)]
    public void ATumblingWindowsWidthIsRead(string window, double seconds)
    {
        StreamWindowSpec.Read(window, "PT0S", "PT5S", 1)
            .Value.Size.ShouldBe(TimeSpan.FromSeconds(seconds));
    }

    private static StreamWindowSpec Spec(TimeSpan size, TimeSpan? lateness = null) =>
        new(size, lateness ?? TimeSpan.Zero, TimeSpan.FromSeconds(5), 1);

    private static StreamRecord Record(string position, DateTimeOffset eventTime) =>
        new(new StreamPosition(position), eventTime);
}
