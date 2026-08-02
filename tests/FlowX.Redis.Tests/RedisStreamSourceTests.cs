using System.Globalization;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace FlowX.Redis.Tests;

/// <summary>
/// Reading a Redis stream forwards from a position: the three guarantees
/// <see cref="IStreamSource"/> asks an implementation for, against a real server.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Replayability is the one a double cannot prove and the one everything rests on.</strong>
/// A node that dies rebuilds its open windows by re-reading from the checkpoint
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0055-a-window-names-the-instance-it-starts.md">ADR-0055</a>),
/// which is only true if a range read from an id returns the same records every time. A consumer
/// group cannot do that — it has already handed those entries out — which is why this reads with
/// <c>XRANGE</c> and <see cref="RedisStreamBusConsumer"/> does not.
/// </para>
/// <para>
/// <strong>And trimming is the case a source must refuse rather than survive.</strong> Redis
/// serves the entries after a trimmed id with no indication that anything is missing, so the gap
/// has to be looked for. The test trims the stream under a held position, which is the real
/// failure, on the real server.
/// </para>
/// </remarks>
public sealed class RedisStreamSourceTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Origin = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>An empty stream is caught up, not an error.</summary>
    [Fact]
    public async Task AnEmptyStreamOffersNothing()
    {
        await using var space = await StreamSpace.CreateAsync(Cancellation);

        var read = await space.Source.ReadAsync(space.Subscription, null, 10, Cancellation);

        read.IsSuccess.ShouldBeTrue();
        read.Value.ShouldBeEmpty();
    }

    /// <summary>Records come back oldest first, with the event time the producer wrote.</summary>
    [Fact]
    public async Task RecordsAreOfferedInOrderWithTheirEventTime()
    {
        await using var space = await StreamSpace.CreateAsync(Cancellation);

        await space.AddAsync(Origin, "{\"n\":1}");
        await space.AddAsync(Origin.AddSeconds(30), "{\"n\":2}");

        var read = await space.Source.ReadAsync(space.Subscription, null, 10, Cancellation);

        read.Value.Count.ShouldBe(2);
        read.Value[0].EventTime.ShouldBe(Origin);
        read.Value[0].Payload.ShouldBe("{\"n\":1}");
        read.Value[1].EventTime.ShouldBe(Origin.AddSeconds(30));
    }

    /// <summary>A read from a position excludes that position and returns everything after it.</summary>
    [Fact]
    public async Task AReadFromAPositionIsExclusive()
    {
        await using var space = await StreamSpace.CreateAsync(Cancellation);

        await space.AddAsync(Origin, "1");
        await space.AddAsync(Origin.AddSeconds(1), "2");
        await space.AddAsync(Origin.AddSeconds(2), "3");

        var first = await space.Source.ReadAsync(space.Subscription, null, 1, Cancellation);

        var rest = await space.Source.ReadAsync(
            space.Subscription, first.Value[0].Position, 10, Cancellation);

        rest.Value.Count.ShouldBe(2);
        rest.Value[0].Payload.ShouldBe("2");
    }

    /// <summary>
    /// Reading from a position twice returns the same records — which is what lets a restart
    /// rebuild an open window.
    /// </summary>
    [Fact]
    public async Task APositionIsReplayable()
    {
        await using var space = await StreamSpace.CreateAsync(Cancellation);

        await space.AddAsync(Origin, "1");
        await space.AddAsync(Origin.AddSeconds(1), "2");
        await space.AddAsync(Origin.AddSeconds(2), "3");

        var opening = await space.Source.ReadAsync(space.Subscription, null, 1, Cancellation);
        var checkpoint = opening.Value[0].Position;

        var once = await space.Source.ReadAsync(space.Subscription, checkpoint, 10, Cancellation);
        var again = await space.Source.ReadAsync(space.Subscription, checkpoint, 10, Cancellation);

        again.Value.Select(static r => r.Position).ShouldBe(once.Value.Select(static r => r.Position));
    }

    /// <summary>A stream trimmed past the held position is refused, not resumed from the gap.</summary>
    [Fact]
    public async Task ATrimmedPositionIsRefused()
    {
        await using var space = await StreamSpace.CreateAsync(Cancellation);

        await space.AddAsync(Origin, "1");
        await space.AddAsync(Origin.AddSeconds(1), "2");
        await space.AddAsync(Origin.AddSeconds(2), "3");

        var opening = await space.Source.ReadAsync(space.Subscription, null, 1, Cancellation);
        var checkpoint = opening.Value[0].Position;

        await space.Database.StreamTrimAsync(space.Key, maxLength: 1, useApproximateMaxLength: false);

        var read = await space.Source.ReadAsync(space.Subscription, checkpoint, 10, Cancellation);

        read.IsFailure.ShouldBeTrue(
            "the entries between the checkpoint and the oldest retained one are gone, so a " +
            "window spanning the gap could only be computed from part of its input.");

        read.Error.Code.ShouldBe(StreamErrors.TrimmedCode);
    }

    /// <summary>A record with no event time is reported rather than dated with the reader's clock.</summary>
    /// <remarks>
    /// The refusal ADR-0056 turns on. A stream id says when the entry was written, which is not
    /// when the thing happened; substituting either that or <c>UtcNow</c> would make every window
    /// a function of ingestion timing and every replay produce a different answer.
    /// </remarks>
    [Fact]
    public async Task ARecordWithNoEventTimeIsRefused()
    {
        await using var space = await StreamSpace.CreateAsync(Cancellation);

        await space.Database.StreamAddAsync(
            space.Key, [new NameValueEntry(RedisStreamSource.PayloadField, "1")]);

        var read = await space.Source.ReadAsync(space.Subscription, null, 10, Cancellation);

        read.IsFailure.ShouldBeTrue();
        read.Error.Code.ShouldBe("stream.record_has_no_event_time");
    }

    /// <summary>One stream key nobody else uses, and the source over it.</summary>
    private sealed class StreamSpace : IAsyncDisposable
    {
        private readonly IConnectionMultiplexer _connection;

        private StreamSpace(IConnectionMultiplexer connection, string key)
        {
            _connection = connection;
            Database = connection.GetDatabase();
            Key = key;
            Source = new RedisStreamSource(connection);
            Subscription = new StreamSubscription("telemetry.aggregate", "1.0.0", key, string.Empty);
        }

        public IDatabase Database { get; }

        public RedisKey Key { get; }

        public RedisStreamSource Source { get; }

        public StreamSubscription Subscription { get; }

        public static async ValueTask<StreamSpace> CreateAsync(CancellationToken cancellationToken)
        {
            var connection = await RedisTestServer.ConnectAsync(cancellationToken);

            return new StreamSpace(connection, "flowx_t_" + Guid.NewGuid().ToString("n"));
        }

        public Task<RedisValue> AddAsync(DateTimeOffset eventTime, string payload) =>
            Database.StreamAddAsync(
                Key,
                [
                    new NameValueEntry(
                        RedisStreamSource.EventTimeField,
                        eventTime.ToString("O", CultureInfo.InvariantCulture)),
                    new NameValueEntry(RedisStreamSource.PayloadField, payload),
                ]);

        public async ValueTask DisposeAsync()
        {
            await Database.KeyDeleteAsync(Key);
            await _connection.DisposeAsync();
        }
    }
}
