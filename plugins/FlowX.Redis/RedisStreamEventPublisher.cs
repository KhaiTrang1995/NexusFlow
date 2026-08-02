using StackExchange.Redis;

namespace FlowX.Redis;

/// <summary>
/// Publishes staged outbox events to Redis Streams, one stream per <c>partition_key</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the broker plugin
/// <see href="../../docs/adr/ADR-0018-outbox-publication-and-ordering.md">ADR-0018</see> records
/// as missing.</strong> That record's negative consequence — "the only
/// <see cref="IEventPublisher"/> in the repository is a recording test double … acknowledgement
/// semantics, broker-side partitioning and what a real client does with a half-accepted batch
/// are all unproved" — is what this class answers, and <c>PublisherConformance</c> is what holds
/// it and the double to the same contract.
/// </para>
/// <para>
/// <strong>One stream per key is the ordering guarantee, not an optimisation.</strong> A Redis
/// stream is an append-only log with a total order, so <c>XADD</c> into the key's own stream
/// gives per-<c>partition_key</c> ordering directly and gives nothing across keys — which is
/// exactly the pair of statements ADR-0018 decision 3 makes. Two publishers appending to one
/// key's stream cannot reorder that key's events relative to each other either, because the
/// order they arrive in is decided by the single-threaded server rather than by either client;
/// what stops two publishers offering the same key's events out of staging order in the first
/// place is the claim query's hold-back, one layer up in <c>PostgresOutboxPublisher</c>.
/// <see cref="RedisKeys.EventStream"/> carries the argument in full.
/// </para>
/// <para>
/// <strong>The batch is published one event at a time, in order, and the count is where it
/// stopped.</strong> Pipelining the whole batch and awaiting the lot would be faster and would
/// break the contract twice over: Redis would apply the writes in an order this class did not
/// choose, and a partial failure would leave no prefix to report — only a set of successes,
/// which <see cref="IEventPublisher"/> explains is not a statement a caller can act on. The cost
/// is one round trip per event, paid inside the outbox's claim transaction; the batch size is
/// the knob for it.
/// </para>
/// <para>
/// <strong>A refusal is a value and a defect is an exception (ADR-0007).</strong> Every failure
/// this client can produce for a broker that is unreachable, overloaded or refusing — a
/// connection fault, a timeout, a server error — becomes an <see cref="Error"/> in the
/// <c>Unavailable</c> category, because the outbox is durable and the batch is offered again.
/// Anything else propagates: a publisher handed a null batch, or a multiplexer somebody disposed
/// underneath it, is broken rather than blocked, and marking nothing published is not the right
/// response to code that cannot work.
/// </para>
/// <para>
/// <strong>What this deliberately does not do.</strong> There is no consumer group, no
/// acknowledgement tracking and no dead-letter path. <see cref="IEventPublisher"/> is the seam
/// between the outbox and a broker "and nothing else"; a consumer's read position is the
/// consumer's, and ADR-0018 records DLQ as outside its package. An event a broker permanently
/// refuses is therefore retried for ever and holds up the events behind it, which is the same
/// trade-off the record states and not a property of this transport.
/// </para>
/// </remarks>
public sealed class RedisStreamEventPublisher : IEventPublisher
{
    /// <summary>What a caller sees when the broker could not be reached.</summary>
    /// <remarks>
    /// One code rather than one per client exception, because the caller's decision is the same
    /// for all of them — leave the rows pending and try the next pass — and a code is a contract
    /// a caller branches on rather than a rendering of a stack trace. The client's own message
    /// travels in <see cref="Error.Message"/>, where an operator can read it.
    /// </remarks>
    public const string PublishFailedCode = "redis.publish_failed";

    private readonly IDatabase _database;
    private readonly RedisStreamOptions _options;

    /// <summary>Creates a publisher over a connection.</summary>
    /// <param name="connection">The multiplexer. The caller owns it and its lifetime.</param>
    /// <param name="options">
    /// Where in the key space the streams live and how long they are kept. Defaults to the
    /// <c>flowx</c> prefix and no trimming.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    public RedisStreamEventPublisher(IConnectionMultiplexer connection, RedisStreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _options = options ?? new RedisStreamOptions();
        _database = connection.GetDatabase(_options.Database);
    }

    /// <inheritdoc />
    public async ValueTask<Result<int>> PublishAsync(
        IReadOnlyList<OutboxRecord> batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        for (var published = 0; published < batch.Count; published++)
        {
            try
            {
                await AppendAsync(batch[published], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is RedisException or RedisTimeoutException)
            {
                // The first event of the batch is reported as a failure and every later one as
                // a shorter prefix, which is the choice IEventPublisher states: a refusal that
                // took nothing is worth reporting with its reason, and one that took something
                // is worth reporting as a count, because that is what leaves the events which
                // did arrive marked. Both leave the rest pending.
                return published == 0 ? Failed(failure) : published;
            }
        }

        return batch.Count;
    }

    /// <summary>Appends one event to its key's stream.</summary>
    /// <remarks>
    /// <para>
    /// The cancellation token is applied to the wait rather than to the command, for the reason
    /// <see cref="RedisLeaseStore"/> gives: StackExchange.Redis has no per-command cancellation,
    /// because a multiplexed connection cannot withdraw a request already written to the socket.
    /// A cancelled publish may therefore still reach the broker — which is exactly the window
    /// at-least-once already covers, since the outbox's mark is rolled back with the claim and
    /// the event is offered again.
    /// </para>
    /// <para>
    /// <c>PartitionKey</c> is written as a field as well as being the key, so a consumer reading
    /// one stream does not have to parse the key it read it from, and <c>PublishedAt</c> is not
    /// written at all: it is the outbox's record of its own bookkeeping and is null on every
    /// event handed to a publisher.
    /// </para>
    /// <para>
    /// <strong>A null field is left out rather than written as null.</strong> Redis has no null
    /// in a stream entry — the client rejects one before it reaches the wire — and both nullable
    /// members of <see cref="OutboxRecord"/> are legitimately absent: an unkeyed event has no
    /// <c>PartitionKey</c>, and an event whose contract has no body has no <c>PayloadJson</c>.
    /// Substituting an empty string would be worse than omitting the field, because a consumer
    /// could no longer tell "this event was staged with no key" from "this event was staged with
    /// the empty key" — the same fold <see cref="RedisKeys.RootScope"/> exists to avoid one
    /// contract over.
    /// </para>
    /// </remarks>
    private async Task AppendAsync(OutboxRecord staged, CancellationToken cancellationToken)
    {
        List<NameValueEntry> entry =
        [
            new(RedisKeys.EventIdField, staged.EventId.ToString("d")),
            new(RedisKeys.InstanceIdField, staged.InstanceId.ToString("d")),
            new(RedisKeys.TypeField, staged.Type),
            new(RedisKeys.SchemaVersionField, staged.SchemaVersion),
        ];

        if (staged.PartitionKey is { } partitionKey)
        {
            entry.Add(new NameValueEntry(RedisKeys.PartitionKeyField, partitionKey));
        }

        if (staged.PayloadJson is { } payload)
        {
            entry.Add(new NameValueEntry(RedisKeys.PayloadField, payload));
        }

        // Omitted rather than written empty, for the reason above: a consumer must be able to
        // tell "this event came from a deployment that does not isolate" from "this event names
        // the empty tenant", because the first starts a flow and the second is a refusal.
        if (staged.TenantId is { Length: > 0 } tenant)
        {
            entry.Add(new NameValueEntry(RedisKeys.TenantIdField, tenant));
        }

        await _database
            .StreamAddAsync(
                RedisKeys.EventStream(_options.KeyPrefix, staged.PartitionKey),
                [.. entry],
                messageId: null,
                maxLength: _options.MaxStreamLength,
                useApproximateMaxLength: true)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Turns a client failure into the value the contract hands back.</summary>
    /// <remarks>
    /// <c>Unavailable</c> rather than <c>Internal</c>, because the category carries the retry
    /// decision and every failure caught here is one a later pass may well succeed at. Reporting
    /// a broker that is merely down as an internal fault would tell a caller to give up on
    /// something the outbox is holding for it.
    /// </remarks>
    private static Error Failed(Exception failure) => new(
        PublishFailedCode,
        $"The batch reached no event on the broker: {failure.Message}",
        ErrorCategory.Unavailable);
}
