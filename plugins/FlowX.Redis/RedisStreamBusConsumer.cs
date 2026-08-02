using System.Globalization;
using StackExchange.Redis;

namespace FlowX.Redis;

// StackExchange.Redis and FlowX both have a StreamPosition, and both are in scope here. The
// driver's is the one this file means — the `0-0` / `$` sentinels an XREADGROUP starts from —
// while FlowX's is the opaque cursor a stream subscription checkpoints (see RedisStreamSource).
// Aliased rather than renaming either: the driver's name is not ours to change, and FlowX's
// matches ChangePosition, which is the shape it is a sibling of. Inside the namespace rather
// than above it, because a compilation-unit alias loses to a member of the enclosing namespace
// and FlowX.StreamPosition is one.
using StreamPosition = StackExchange.Redis.StreamPosition;

/// <summary>
/// Consumes the streams <see cref="RedisStreamEventPublisher"/> writes, one consumer group per
/// subscription, one partition per stream.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the other half of the seam
/// <see href="../../docs/adr/ADR-0018-outbox-publication-and-ordering.md">ADR-0018</see>
/// declared.</strong> That record's negative consequence said publication was proved and nothing
/// consumed; <see cref="RedisStreamEventPublisher"/>'s own remarks said, in as many words,
/// <em>"there is no consumer group, no acknowledgement tracking and no dead-letter path"</em>.
/// This class is all three, and it is deliberately the <em>only</em> place in the repository that
/// knows Redis Streams has them.
/// </para>
/// <para>
/// <strong>The stream is the partition, and that is the whole of the ordering design.</strong>
/// The publisher writes one stream per <c>partition_key</c>
/// (<see cref="RedisKeys.EventStream"/>), so reading one stream front to back reproduces the
/// publisher's order for that key exactly — no sequence number to compare and no reordering
/// buffer. Partitions are handed to the caller as separate
/// <see cref="BusPartitionBatch"/>es precisely so it can run them concurrently and their
/// contents serially, which is
/// <see href="../../docs/adr/ADR-0037-the-consumer-offers-per-key-order.md">ADR-0037</see>.
/// </para>
/// <para>
/// <strong>Redis has no topic, so the streams are discovered by <c>SCAN</c> and the topic is a
/// filter.</strong> There is no "list the partitions of a topic" operation, because there is no
/// topic — an event's type travels in the <see cref="RedisKeys.TypeField"/> field. So each pass
/// scans the key prefix for streams and reads each one under a group named for
/// <em>both</em> the subscription's group and its topic. An entry of another type reaching this
/// subscription's group is acknowledged unread, which is correct because that group belongs to
/// this subscription alone and nothing else will ever be offered it. The cost of the scan is
/// stated in ADR-0037's negative consequences rather than hidden here.
/// </para>
/// <para>
/// <strong>A refusal is a value and a defect is an exception (ADR-0007)</strong>, exactly as in
/// <see cref="RedisStreamEventPublisher"/>: a broker that is unreachable, overloaded or refusing
/// becomes an <see cref="Error"/> in the <c>Unavailable</c> category, because the messages stay
/// pending and the next pass tries again. Anything else propagates.
/// </para>
/// </remarks>
public sealed class RedisStreamBusConsumer : IBusConsumer
{
    /// <summary>The broker family this consumer serves.</summary>
    /// <remarks>
    /// Compared against a subscription's declared transport at registration, so a
    /// <c>[KafkaTrigger]</c> wired to this is a startup failure rather than a subscription
    /// consumed from a broker the manifest does not name.
    /// </remarks>
    public const string TransportName = "redis-streams";

    /// <summary>What a caller sees when the broker could not be reached.</summary>
    /// <remarks>
    /// One code rather than one per client exception, for
    /// <see cref="RedisStreamEventPublisher.PublishFailedCode"/>'s reason: the caller's decision
    /// is the same for all of them — leave the messages pending and try the next pass.
    /// </remarks>
    public const string ConsumeFailedCode = "redis.consume_failed";

    /// <summary>The field a dead-lettered entry gains, saying why it was diverted.</summary>
    public const string DeadLetterReasonField = "dead-letter-reason";

    /// <summary>The field a dead-lettered entry gains, saying when.</summary>
    public const string DeadLetterAtField = "dead-letter-at";

    /// <summary>The suffix that turns a stream key into its dead-letter stream's key.</summary>
    /// <remarks>
    /// Derived from the source rather than declared per subscription, which is why
    /// <c>KafkaTriggerAttribute.DeadLetter</c> stays unread
    /// (<see href="../../docs/adr/ADR-0038-a-poison-message-is-dead-lettered.md">ADR-0038</see>).
    /// A destination is deployment configuration, and the manifest deliberately publishes none.
    /// </remarks>
    public const string DeadLetterSuffix = ":dead";

    private readonly IConnectionMultiplexer _connection;
    private readonly IDatabase _database;
    private readonly RedisStreamOptions _options;
    private readonly string _consumerName;

    /// <summary>Creates a consumer over a connection.</summary>
    /// <param name="connection">The multiplexer. The caller owns it and its lifetime.</param>
    /// <param name="options">
    /// Where in the key space the streams live. Must be the same options the publisher writing
    /// them uses, because the key prefix is what makes one deployment's events findable and
    /// another's invisible.
    /// </param>
    /// <param name="consumerName">
    /// This node's name within the consumer group. Defaults to the machine name and the process
    /// id, which is enough for the one thing Redis uses it for: attributing a pending entry to a
    /// consumer so a later <c>XAUTOCLAIM</c> can take it away from a dead one.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    public RedisStreamBusConsumer(
        IConnectionMultiplexer connection,
        RedisStreamOptions? options = null,
        string? consumerName = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _connection = connection;
        _options = options ?? new RedisStreamOptions();
        _database = connection.GetDatabase(_options.Database);
        _consumerName = consumerName ?? DefaultConsumerName();
    }

    /// <inheritdoc />
    public string Transport => TransportName;

    /// <summary>
    /// The consumer group one subscription reads under.
    /// </summary>
    /// <param name="subscription">The subscription.</param>
    /// <returns>The group name.</returns>
    /// <remarks>
    /// <strong>The topic is in the name as well as the declared group, and it has to be.</strong>
    /// A Redis stream carries every type of event one partition key produced, and a consumer group
    /// distributes those entries among its members. Two subscriptions on different topics sharing
    /// one group would therefore steal each other's entries — each acknowledging the other's
    /// messages unread, silently. Naming the group for the pair makes each subscription's
    /// pending-entries list its own.
    /// </remarks>
    public static string GroupNameFor(BusSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        return subscription.Group + ":" + subscription.Topic;
    }

    /// <inheritdoc />
    public async ValueTask<Result<bool>> SubscribeAsync(
        BusSubscription subscription, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        var group = GroupNameFor(subscription);

        try
        {
            var created = false;

            foreach (var stream in await StreamsAsync(cancellationToken).ConfigureAwait(false))
            {
                created |= await EnsureGroupAsync(stream, group, cancellationToken).ConfigureAwait(false);
            }

            return created;
        }
        catch (Exception failure) when (failure is RedisException or RedisTimeoutException)
        {
            return Result.Fail<bool>(Failed("The subscription could not be created", failure));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>Reclaimed entries first, then new ones.</strong> <c>XAUTOCLAIM</c> takes over the
    /// entries a dead consumer was holding past the idle window and hands them back with their
    /// delivery count raised — which is what makes at-least-once true across a node that died
    /// mid-flow, and what feeds ADR-0038's delivery limit. Reading new entries first would let a
    /// busy stream starve the very messages that need attention most.
    /// </para>
    /// <para>
    /// <strong>An entry of another type is acknowledged and not returned.</strong> This group
    /// belongs to one subscription, so an entry it does not want will never be wanted; leaving it
    /// pending would block the partition for ever with a message that is not poison, merely
    /// somebody else's.
    /// </para>
    /// </remarks>
    public async ValueTask<Result<IReadOnlyList<BusPartitionBatch>>> ReceiveAsync(
        BusSubscription subscription,
        int maxPartitions,
        int maxPerPartition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        var group = GroupNameFor(subscription);
        var batches = new List<BusPartitionBatch>();

        try
        {
            foreach (var stream in await StreamsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (batches.Count >= maxPartitions)
                {
                    break;
                }

                var deliveries = await ReadAsync(
                        stream, subscription, group, maxPerPartition, cancellationToken)
                    .ConfigureAwait(false);

                if (deliveries.Count > 0)
                {
                    batches.Add(new BusPartitionBatch(PartitionKeyOf(stream), deliveries));
                }
            }

            return Result.Ok<IReadOnlyList<BusPartitionBatch>>(batches);
        }
        catch (Exception failure) when (failure is RedisException or RedisTimeoutException)
        {
            return Result.Fail<IReadOnlyList<BusPartitionBatch>>(
                Failed("No message could be read", failure));
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result<bool>> AcknowledgeAsync(
        BusSubscription subscription, BusDelivery delivery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(delivery);

        try
        {
            var acknowledged = await _database
                .StreamAcknowledgeAsync(
                    StreamKeyFor(delivery.PartitionKey),
                    GroupNameFor(subscription),
                    delivery.Token)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            return acknowledged > 0;
        }
        catch (Exception failure) when (failure is RedisException or RedisTimeoutException)
        {
            return Result.Fail<bool>(Failed("The message could not be acknowledged", failure));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <strong>Copy, then acknowledge, and the order is the whole of the guarantee.</strong> A
    /// crash between the two leaves the original pending and redelivers it, which at-least-once
    /// already permits and which produces at worst a duplicate in the dead-letter stream. The
    /// other order loses the message.
    /// </remarks>
    public async ValueTask<Result<bool>> DeadLetterAsync(
        BusSubscription subscription,
        BusDelivery delivery,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var source = StreamKeyFor(delivery.PartitionKey);
        var dead = (RedisKey)(source.ToString() + DeadLetterSuffix);

        try
        {
            var original = await _database
                .StreamRangeAsync(source, delivery.Token, delivery.Token, 1)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            var fields = original.Length > 0
                ? original[0].Values.ToList()
                : [];

            fields.Add(new NameValueEntry(DeadLetterReasonField, reason));
            fields.Add(new NameValueEntry(
                DeadLetterAtField,
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));

            await _database
                .StreamAddAsync(
                    dead,
                    [.. fields],
                    messageId: null,
                    maxLength: _options.MaxStreamLength,
                    useApproximateMaxLength: true)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            var acknowledged = await _database
                .StreamAcknowledgeAsync(source, GroupNameFor(subscription), delivery.Token)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            return acknowledged > 0;
        }
        catch (Exception failure) when (failure is RedisException or RedisTimeoutException)
        {
            return Result.Fail<bool>(Failed("The message could not be dead-lettered", failure));
        }
    }

    /// <summary>Every event stream under the configured prefix, dead-letter streams excluded.</summary>
    /// <remarks>
    /// <para>
    /// <strong>A <c>SCAN</c> per pass, and it is the price of Redis having no topic.</strong>
    /// There is no server-side mapping from a topic to its partitions, so the partitions are the
    /// keys and the keys have to be enumerated. <c>KeysAsync</c> is <c>SCAN</c> rather than
    /// <c>KEYS</c>, so it does not block the server, but it is O(keyspace) in the prefix and is
    /// recorded as such in ADR-0037.
    /// </para>
    /// <para>
    /// Replicas are skipped, because a replica would return the same keys and produce a second
    /// batch of the same partitions. Dead-letter streams are excluded by suffix, because a group
    /// reading its own dead letters would reprocess exactly what it gave up on.
    /// </para>
    /// </remarks>
    private async ValueTask<IReadOnlyList<RedisKey>> StreamsAsync(CancellationToken cancellationToken)
    {
        var pattern = _options.KeyPrefix + "*events";
        var keys = new List<RedisKey>();

        foreach (var endpoint in _connection.GetEndPoints())
        {
            var server = _connection.GetServer(endpoint);

            if (server.IsReplica || !server.IsConnected)
            {
                continue;
            }

            await foreach (var key in server
                .KeysAsync(_database.Database, pattern)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                keys.Add(key);
            }
        }

        // Ordinal, so two nodes enumerate the partitions in the same order and the partition
        // leases above this are contended predictably rather than by scan-cursor accident.
        keys.Sort(static (left, right) =>
            string.CompareOrdinal(left.ToString(), right.ToString()));

        return keys;
    }

    /// <summary>Creates the group at the start of the stream if it is not already there.</summary>
    /// <remarks>
    /// <para>
    /// <strong><c>StreamPosition.Beginning</c>, and the first draft of this had
    /// <c>NewMessages</c>.</strong> That is the intuitive choice — "a subscription created today
    /// has no claim on events published before it existed" — and against this publisher it loses
    /// almost everything. The reason is <see cref="RedisKeys.EventStream"/>: there is one stream
    /// per <c>partition_key</c>, and <c>FlowEmitter</c> writes <c>PartitionKey =
    /// ctx.FlowInstanceId</c>, so <em>nearly every partition is created by the very event that is
    /// meant to start a flow</em>. A group created at <c>$</c> on a stream discovered after that
    /// <c>XADD</c> begins after the only entry in it. Five integration tests failed with
    /// <c>Received = 0</c>, which is precisely the "consumer that never fires" silent failure
    /// this suite exists to catch, and a unit test against a double could not have found it.
    /// </para>
    /// <para>
    /// <strong>What <c>Beginning</c> costs, stated rather than discovered.</strong> A subscription
    /// added to a Redis that already holds history reads that history. For a log that is the
    /// ordinary meaning of a new consumer group, and it is bounded by
    /// <see cref="RedisStreamOptions.MaxStreamLength"/> where a deployment sets one — but it is a
    /// real replay, and a new subscription on a busy key space should expect it.
    /// </para>
    /// <para>
    /// <c>BUSYGROUP</c> is the ordinary answer on every pass after the first and is success, not
    /// an error: this call is idempotent by contract.
    /// </para>
    /// </remarks>
    private async ValueTask<bool> EnsureGroupAsync(
        RedisKey stream, string group, CancellationToken cancellationToken)
    {
        try
        {
            return await _database
                .StreamCreateConsumerGroupAsync(stream, group, StreamPosition.Beginning)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RedisServerException busy) when (busy.Message.Contains("BUSYGROUP", StringComparison.Ordinal))
        {
            return false;
        }
    }

    /// <summary>One stream's pending and new entries, oldest first.</summary>
    private async ValueTask<IReadOnlyList<BusDelivery>> ReadAsync(
        RedisKey stream,
        BusSubscription subscription,
        string group,
        int max,
        CancellationToken cancellationToken)
    {
        if (!await EnsureGroupExistsAsync(stream, group, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var deliveries = new List<BusDelivery>();
        var partitionKey = PartitionKeyOf(stream);

        // Reclaimed first. A consumer that died holding an entry is why this is at-least-once,
        // and its delivery count is what eventually dead-letters a message nothing can process.
        var reclaimed = await _database
            .StreamAutoClaimAsync(stream, group, _consumerName, (long)ReclaimAfter.TotalMilliseconds, "0-0", max)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        await CollectAsync(reclaimed.ClaimedEntries, deliveries, stream, subscription, group, partitionKey, cancellationToken)
            .ConfigureAwait(false);

        if (deliveries.Count >= max)
        {
            return deliveries;
        }

        var fresh = await _database
            .StreamReadGroupAsync(
                stream, group, _consumerName, StreamPosition.NewMessages, max - deliveries.Count)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        await CollectAsync(fresh, deliveries, stream, subscription, group, partitionKey, cancellationToken)
            .ConfigureAwait(false);

        return deliveries;
    }

    /// <summary>
    /// Turns entries into deliveries, acknowledging the ones this subscription did not ask for.
    /// </summary>
    private async ValueTask CollectAsync(
        StreamEntry[] entries,
        List<BusDelivery> deliveries,
        RedisKey stream,
        BusSubscription subscription,
        string group,
        string? partitionKey,
        CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
        {
            if (entry.IsNull)
            {
                continue;
            }

            var token = entry.Id.ToString()!;
            var type = Field(entry, RedisKeys.TypeField);

            if (type is not null &&
                !string.Equals(type, subscription.Topic, StringComparison.Ordinal))
            {
                // Somebody else's event, in a group that is ours alone. It will never be wanted
                // here, so leaving it pending would block this partition on a message that is not
                // poison — merely addressed elsewhere.
                await _database
                    .StreamAcknowledgeAsync(stream, group, entry.Id)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);

                continue;
            }

            deliveries.Add(Read(entry, token, partitionKey, subscription.Topic,
                await DeliveryCountAsync(stream, group, entry.Id, cancellationToken).ConfigureAwait(false)));
        }
    }

    /// <summary>How many times the broker has handed this entry out, including now.</summary>
    /// <remarks>
    /// Read from the pending-entries list, which is where Redis keeps it. One <c>XPENDING</c> per
    /// entry is a real cost and is the honest one: the count decides whether a message is
    /// dead-lettered, and inferring it from anything else would be inventing the number that
    /// ADR-0038's bound rests on. An entry that has already left the pending list — acknowledged
    /// underneath us — reports one, which errs towards processing rather than diverting.
    /// </remarks>
    private async ValueTask<int> DeliveryCountAsync(
        RedisKey stream, string group, RedisValue id, CancellationToken cancellationToken)
    {
        var pending = await _database
            .StreamPendingMessagesAsync(stream, group, 1, RedisValue.Null, id, id)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        return pending.Length > 0 ? pending[0].DeliveryCount : 1;
    }

    /// <summary>Whether the group exists, creating it if the stream has gained entries since.</summary>
    private async ValueTask<bool> EnsureGroupExistsAsync(
        RedisKey stream, string group, CancellationToken cancellationToken)
    {
        try
        {
            var groups = await _database
                .StreamGroupInfoAsync(stream)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            if (groups.Any(existing => string.Equals(existing.Name, group, StringComparison.Ordinal)))
            {
                return true;
            }
        }
        catch (RedisServerException missing) when (missing.Message.Contains("NOGROUP", StringComparison.Ordinal))
        {
            // The stream exists and has no groups at all.
        }

        return await EnsureGroupAsync(stream, group, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one stream entry into a delivery, or into the reason it is not one.
    /// </summary>
    /// <remarks>
    /// <strong>An entry that cannot be read is returned rather than skipped.</strong> Skipping it
    /// would leave it in the pending list for ever with nothing willing to take it, which is the
    /// head-of-line block ADR-0038 exists to end. It comes back as
    /// <see cref="BusDelivery.Unreadable"/> and the caller dead-letters it on its first delivery.
    /// </remarks>
    private static BusDelivery Read(
        StreamEntry entry, string token, string? partitionKey, string topic, int deliveries)
    {
        var rawId = Field(entry, RedisKeys.EventIdField);

        if (rawId is null)
        {
            return BusDelivery.Unreadable(
                token, deliveries, partitionKey,
                $"it carries no '{RedisKeys.EventIdField}' field, so no instance id can be derived " +
                "from it and a redelivery could not be recognised");
        }

        if (!Guid.TryParse(rawId, out var eventId))
        {
            return BusDelivery.Unreadable(
                token, deliveries, partitionKey,
                $"its '{RedisKeys.EventIdField}' field is '{rawId}', which is not a GUID");
        }

        if (Field(entry, RedisKeys.TypeField) is not { } type)
        {
            return BusDelivery.Unreadable(
                token, deliveries, partitionKey,
                $"it carries no '{RedisKeys.TypeField}' field, so nothing says what it is");
        }

        return BusDelivery.Of(
            new BusMessage(
                eventId,
                topic,
                type,
                Field(entry, RedisKeys.SchemaVersionField) ?? string.Empty,

                // The entry's own field rather than the key it came from: an event staged with no
                // key is in the unkeyed stream and legitimately has none, and reading the key back
                // would invent one.
                Field(entry, RedisKeys.PartitionKeyField),
                Field(entry, RedisKeys.PayloadField),

                // The tenant the publisher wrote, which is what the host starts the flow in on
                // a deployment that isolates. Absent is null and not a refusal here: whether an
                // untenanted message may start a flow is admission's decision, and a consumer
                // that refused it would be deciding isolation in a plugin.
                Field(entry, RedisKeys.TenantIdField)),
            token,
            deliveries);
    }

    /// <summary>The stream one partition key's events live in.</summary>
    private RedisKey StreamKeyFor(string? partitionKey) =>
        RedisKeys.EventStream(_options.KeyPrefix, partitionKey);

    /// <summary>
    /// The partition key a stream key names, or null for the stream of unordered events.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="RedisKeys.EventStream"/>, and it relies on the property that
    /// method's remarks establish: a keyed stream always carries braces immediately after the
    /// prefix and the unkeyed one never does, so the two shapes cannot collide.
    /// </remarks>
    private string? PartitionKeyOf(RedisKey stream)
    {
        var text = stream.ToString();
        var opening = _options.KeyPrefix.Length + 1;

        if (text.Length <= opening || text[opening] != '{')
        {
            return null;
        }

        var closing = text.LastIndexOf('}');

        return closing > opening ? text[(opening + 1)..closing] : null;
    }

    private static string? Field(StreamEntry entry, string name) =>
        entry.Values.FirstOrDefault(value => value.Name == name) is { Value.IsNull: false } found
            ? (string?)found.Value
            : null;

    /// <summary>
    /// How long an entry may sit with a consumer before another may take it over.
    /// </summary>
    /// <remarks>
    /// Not configurable, deliberately, and generous. This is the broker's visibility timeout and
    /// the thing that decides how long a flow may run before a second node is offered its message
    /// — and a value too small turns a slow flow into a redelivery storm, which the derived
    /// instance id then converts into a great many refusals rather than a great many runs. Five
    /// minutes is longer than any flow whose deadline the default <c>FlowXOptions</c> would allow.
    /// </remarks>
    private static readonly TimeSpan ReclaimAfter = TimeSpan.FromMinutes(5);

    /// <summary>This node's name inside the consumer group.</summary>
    private static string DefaultConsumerName() =>
        Environment.MachineName + ":" +
        Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Turns a client failure into the value the contract hands back.</summary>
    private static Error Failed(string what, Exception failure) => new(
        ConsumeFailedCode,
        $"{what}: {failure.Message}",
        ErrorCategory.Unavailable);
}
