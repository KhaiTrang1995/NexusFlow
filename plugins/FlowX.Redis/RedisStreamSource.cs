using System.Globalization;
using StackExchange.Redis;

namespace FlowX.Redis;

/// <summary>
/// Reads a Redis stream forwards from a position, as the windowing engine's source.
/// </summary>
/// <remarks>
/// <para>
/// <strong><c>XRANGE</c>, not <c>XREADGROUP</c>, and the difference is the whole design.</strong>
/// <see cref="RedisStreamBusConsumer"/> uses a consumer group because a bus delivery is held on
/// the consumer's behalf until it is acknowledged. A stream subscription owns a position instead
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0055-a-window-names-the-instance-it-starts.md">ADR-0055</a>),
/// and it must be able to re-read from that position after a crash to rebuild its open windows —
/// which a consumer group's pending-entries list cannot do, because it has already handed those
/// entries out. A range read from an id is exactly replayable, which is the guarantee
/// <see cref="IStreamSource"/> asks for.
/// </para>
/// <para>
/// <strong>The event time is a field on the record, and its absence is a refusal.</strong> A
/// stream id encodes the moment the producer <em>wrote</em> the entry, which is not when the
/// thing happened; using it would make every window a function of ingestion timing and every
/// replay produce a different answer. So a record must carry
/// <see cref="EventTimeField"/> — ISO-8601, round-trip — and one that does not is reported rather
/// than dated with this reader's clock.
/// </para>
/// <para>
/// <strong>Trimming is detected, not tolerated.</strong> Redis <c>XRANGE</c> from an id that has
/// been trimmed away returns the entries after it with no indication that anything is missing,
/// which is precisely the silent gap <see cref="StreamErrors.Trimmed"/> exists to prevent. So the
/// checkpointed id is verified to still be present before the range after it is served.
/// </para>
/// </remarks>
public sealed class RedisStreamSource : IStreamSource
{
    /// <summary>The field a record's event time is read from.</summary>
    /// <remarks>
    /// Named beside <see cref="RedisKeys.PayloadField"/>'s siblings rather than in
    /// <c>RedisKeys</c>, because those describe the streams <see cref="RedisStreamEventPublisher"/>
    /// writes and this describes a stream someone else produces. A source stream is an input to
    /// this platform, not an artefact of it.
    /// </remarks>
    public const string EventTimeField = "event-time";

    /// <summary>The field a record's body is read from.</summary>
    public const string PayloadField = "payload";

    /// <summary>The field a record's partition key is read from, when it carries one.</summary>
    public const string PartitionKeyField = "partition-key";

    /// <summary>The field a record's tenant is read from, when the deployment isolates.</summary>
    public const string TenantIdField = "tenant-id";

    private readonly IDatabase _database;

    /// <summary>Creates a stream source over one connection.</summary>
    /// <param name="connection">The multiplexer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    public RedisStreamSource(IConnectionMultiplexer connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _database = connection.GetDatabase();
    }

    /// <inheritdoc />
    public string Stream => "redis-stream";

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<StreamRecord>>> ReadAsync(
        StreamSubscription subscription,
        StreamPosition? after,
        int max,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);

        cancellationToken.ThrowIfCancellationRequested();

        var key = (RedisKey)subscription.Source;

        if (after is { } position)
        {
            // XRANGE from a trimmed id serves the entries after it and says nothing, so the gap
            // has to be looked for. One extra round trip per read, and it is the round trip that
            // makes "no gaps" a guarantee rather than a hope.
            var still = await _database.StreamRangeAsync(key, position.Value, position.Value, 1)
                .ConfigureAwait(false);

            if (still.Length == 0)
            {
                return StreamErrors.Trimmed(position);
            }
        }

        var entries = await _database
            .StreamRangeAsync(
                key,
                minId: after is { } from ? from.Value : StreamStart,
                maxId: null,
                count: max + (after is null ? 0 : 1))
            .ConfigureAwait(false);

        var records = new List<StreamRecord>(entries.Length);

        foreach (var entry in entries)
        {
            if (after is { } start && string.Equals(entry.Id, start.Value, StringComparison.Ordinal))
            {
                // XRANGE's lower bound is inclusive and the driver's overload takes no exclusive
                // form, so the position itself comes back and is skipped here. The `(` prefix
                // Redis 6.2 added is not exposed by this driver's typed API.
                continue;
            }

            if (Read(entry) is not { } record)
            {
                return new Error(
                    "stream.record_has_no_event_time",
                    $"Record '{entry.Id}' on '{subscription.Source}' carries no " +
                    $"'{EventTimeField}' field holding a round-trip ISO-8601 instant. A window " +
                    "is assigned from event time, so a record without one cannot be placed — and " +
                    "dating it with this reader's clock would make every window a function of " +
                    "when it happened to be read.",
                    ErrorCategory.Validation);
            }

            records.Add(record);

            if (records.Count == max)
            {
                break;
            }
        }

        return records;
    }

    /// <summary>
    /// <c>XRANGE</c>'s "from the oldest entry", spelled out rather than taken from
    /// <c>StackExchange.Redis.StreamPosition.Beginning</c>.
    /// </summary>
    /// <remarks>
    /// The driver has a <c>StreamPosition</c> too, and inside this namespace the name resolves to
    /// FlowX's — a member of the enclosing namespace beats a <c>using</c>. One character is
    /// clearer here than an alias that would have to be repeated in every file this package adds.
    /// </remarks>
    private static readonly RedisValue StreamStart = "-";

    private static StreamRecord? Read(StreamEntry entry)
    {
        if (Field(entry, EventTimeField) is not { } time ||
            !DateTimeOffset.TryParse(
                time,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var eventTime))
        {
            return null;
        }

        return new StreamRecord(
            new FlowX.StreamPosition(entry.Id!),
            eventTime,
            Field(entry, PartitionKeyField),
            Field(entry, PayloadField),
            Field(entry, TenantIdField));
    }

    private static string? Field(StreamEntry entry, string name)
    {
        foreach (var value in entry.Values)
        {
            if (value.Name == name)
            {
                return value.Value;
            }
        }

        return null;
    }
}
