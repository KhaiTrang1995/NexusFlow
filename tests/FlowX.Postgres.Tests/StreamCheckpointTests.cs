using Shouldly;
using Xunit;

namespace FlowX.Postgres.Tests;

/// <summary>
/// A stream subscription's checkpoint, against the real server.
/// </summary>
/// <remarks>
/// <strong>What is worth asserting here is what the table does <em>not</em> hold.</strong> The
/// interesting property of this store is that it is one opaque string per subscription and no
/// window state at all — so the tests are about a position surviving a restart, two subscriptions
/// not sharing a row, and a repeated commit being reported as the no-op it is. Everything a
/// window means is asserted in <c>FlowX.Runtime.Tests</c> and <c>FlowX.Hosting.Tests</c>, where
/// it belongs, because none of it touches a database
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0055-a-window-names-the-instance-it-starts.md">ADR-0055</a>).
/// </remarks>
public sealed class StreamCheckpointTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static readonly StreamSubscription Subscription =
        new("telemetry.aggregate", "1.0.0", "device.telemetry", string.Empty);

    /// <summary>A subscription that has never committed reads no position.</summary>
    /// <remarks>
    /// Null rather than a sentinel, because the engine's answer to "no checkpoint" is to read the
    /// stream from the beginning of what the source retains — and a store that invented a
    /// starting position would be deciding that for it.
    /// </remarks>
    [Fact]
    public async Task ASubscriptionWithNoCheckpointReadsNothing()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var store = new PostgresStreamCheckpointStore(schema.DataSource);

        (await store.ReadAsync(Subscription, Cancellation)).Value.ShouldBeNull();
    }

    /// <summary>A committed position is what the next node reads.</summary>
    [Fact]
    public async Task ACommittedPositionIsReadBackVerbatim()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var store = new PostgresStreamCheckpointStore(schema.DataSource);

        (await store.CommitAsync(Subscription, new StreamPosition("1754130000000-7"), Cancellation))
            .Value.ShouldBeTrue();

        (await store.ReadAsync(Subscription, Cancellation)).Value!.Value.Value.ShouldBe(
            "1754130000000-7",
            "the position is the source's rendering and this store never parses it.");
    }

    /// <summary>Committing the same position twice reports that nothing moved.</summary>
    /// <remarks>
    /// Not an error: a node that committed and then died repeats the commit when it resumes, and
    /// that is the ordinary path rather than a fault.
    /// </remarks>
    [Fact]
    public async Task ARepeatedCommitMovesNothing()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var store = new PostgresStreamCheckpointStore(schema.DataSource);

        await store.CommitAsync(Subscription, new StreamPosition("7-0"), Cancellation);

        (await store.CommitAsync(Subscription, new StreamPosition("7-0"), Cancellation))
            .Value.ShouldBeFalse();

        (await store.CommitAsync(Subscription, new StreamPosition("8-0"), Cancellation))
            .Value.ShouldBeTrue();
    }

    /// <summary>Two subscriptions over one stream keep two checkpoints.</summary>
    /// <remarks>
    /// The key is derived from all four terms, so a second flow reading the same source — or the
    /// same flow at a new version — does not inherit a position it never reached.
    /// </remarks>
    [Fact]
    public async Task TwoSubscriptionsOverOneStreamDoNotShareACheckpoint()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Cancellation);

        var store = new PostgresStreamCheckpointStore(schema.DataSource);
        var other = Subscription with { FlowId = "telemetry.alert" };
        var next = Subscription with { FlowVersion = "1.1.0" };

        await store.CommitAsync(Subscription, new StreamPosition("5-0"), Cancellation);

        (await store.ReadAsync(other, Cancellation)).Value.ShouldBeNull();
        (await store.ReadAsync(next, Cancellation)).Value.ShouldBeNull();

        (await store.ReadAsync(Subscription, Cancellation)).Value!.Value.Value.ShouldBe("5-0");
    }
}
