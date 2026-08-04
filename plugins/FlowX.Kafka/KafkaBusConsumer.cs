using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Confluent.Kafka;

namespace FlowX.Kafka;

/// <summary>
/// Serves one Kafka consumer group per subscription, committing an offset only behind a
/// contiguous run of settled records.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Kafka has no per-message acknowledgement, and that is the whole of what is hard
/// here.</strong> <c>IBusConsumer</c> settles one delivery at a time; Kafka commits a
/// <em>watermark</em> per partition meaning "everything below this is done". Committing the
/// offset of a record acknowledged out of order would silently declare its unfinished
/// predecessors done, and losing them is a durability failure nothing would report. So this class
/// keeps a per-partition ledger and commits the highest offset with no gap below it — the rule
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0075-a-kafka-offset-is-committed-behind-a-contiguous-run-of-settled-records.md">ADR-0075</a>
/// records.
/// </para>
/// <para>
/// <strong>A record of another event type is settled, not skipped.</strong> One topic carries
/// every type this deployment emits and Kafka has no server-side filter, so a subscription reads
/// records it does not want. Leaving them unsettled would stop the watermark for ever behind the
/// first one; they are therefore marked done as soon as they are seen, which is honest — this
/// group has nothing to do with them.
/// </para>
/// <para>
/// <strong>It creates no topics.</strong> Partition count, replication factor and retention are a
/// topic's properties and a deployment's decision, for the reasons ADR-0074 states one transport
/// over; this package holds no <c>AdminClient</c>. A subscription to a topic that does not exist
/// reads nothing and says so.
/// </para>
/// <para>
/// <strong>Dead-lettering is a produce to a derived topic and then a settle, in that
/// order.</strong> Kafka has no dead-letter destination of its own, which is
/// <c>FlowX.Redis</c>'s situation rather than Service Bus's: a crash between the two redelivers
/// the original, which is at-least-once behaving as it always does, where the other order loses
/// the record.
/// </para>
/// </remarks>
public sealed class KafkaBusConsumer : IBusConsumer, IAsyncDisposable
{
    /// <summary>The broker family this consumer serves.</summary>
    public const string TransportName = "kafka";

    /// <summary>What a caller sees when a subscription could not be read.</summary>
    public const string ReceiveFailedCode = "kafka.receive_failed";

    private readonly KafkaOptions _options;
    private readonly ConcurrentDictionary<string, Group> _groups = new(StringComparer.Ordinal);
    private readonly Lazy<IProducer<string?, byte[]?>> _deadLetters;
    private bool _disposed;

    /// <summary>Creates a consumer.</summary>
    /// <param name="options">Where to read from. <see cref="KafkaOptions.BootstrapServers"/> is required.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public KafkaBusConsumer(KafkaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;

        _deadLetters = new Lazy<IProducer<string?, byte[]?>>(() =>
            new ProducerBuilder<string?, byte[]?>(new ProducerConfig
            {
                BootstrapServers = options.BootstrapServers,
                Acks = Acks.All,
                EnableIdempotence = true,
                MessageTimeoutMs = 10_000,
            })
            .SetLogHandler(static (_, _) => { })
            .Build());
    }

    /// <inheritdoc />
    public string Transport => TransportName;

    /// <inheritdoc />
    /// <remarks>
    /// <strong>Always <c>false</c> on success</strong>, which is the contract's "it was already
    /// there". A consumer group on Kafka is created by joining it and needs no declaration; the
    /// topic does need one and this package does not make it.
    /// </remarks>
    public ValueTask<Result<bool>> SubscribeAsync(
        BusSubscription subscription, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ObjectDisposedException.ThrowIf(_disposed, this);

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            _ = GroupFor(subscription);

            return ValueTask.FromResult(Result.Ok(false));
        }
        catch (KafkaException failure)
        {
            return ValueTask.FromResult(Result.Fail<bool>(Unreadable(subscription, failure)));
        }
    }

    /// <inheritdoc />
    public ValueTask<Result<IReadOnlyList<BusPartitionBatch>>> ReceiveAsync(
        BusSubscription subscription,
        int maxPartitions,
        int maxPerPartition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPartitions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPerPartition);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Group group;

        try
        {
            group = GroupFor(subscription);
        }
        catch (KafkaException failure)
        {
            return ValueTask.FromResult(
                Result.Fail<IReadOnlyList<BusPartitionBatch>>(Unreadable(subscription, failure)));
        }

        var order = new List<string?>();
        var buckets = new Dictionary<string, List<BusDelivery>>(StringComparer.Ordinal);
        var unkeyed = new List<BusDelivery>();
        var taken = 0;
        var ceiling = maxPartitions * maxPerPartition;

        while (taken < ceiling)
        {
            ConsumeResult<string?, byte[]?>? record;

            try
            {
                record = group.Consumer.Consume(_options.PollWait);
            }
            catch (ConsumeException failure)
            {
                return ValueTask.FromResult(
                    Result.Fail<IReadOnlyList<BusPartitionBatch>>(Unreadable(subscription, failure)));
            }

            if (record is null || record.IsPartitionEOF)
            {
                break;
            }

            taken++;

            group.Hold(record);

            if (!string.Equals(
                    KafkaHeaders.Text(record.Message.Headers, KafkaHeaders.Type),
                    subscription.Topic,
                    StringComparison.Ordinal))
            {
                // Another type on the shared topic. Settled at once: this group has nothing to do
                // with it, and leaving it open would stop the watermark behind it for ever.
                group.Settle(record.TopicPartitionOffset);

                continue;
            }

            var key = record.Message.Key;
            var bucket = key is null ? Unkeyed() : Bucket(key);

            if (bucket is null || bucket.Count >= maxPerPartition)
            {
                // Over a limit. Left held and unsettled, so the next pass offers it again and the
                // watermark does not move past it.
                continue;
            }

            bucket.Add(Read(record, subscription.Topic, group.DeliveriesOf(record.TopicPartitionOffset)));
        }

        IReadOnlyList<BusPartitionBatch> batches =
        [
            .. order.Select(key => new BusPartitionBatch(key, key is null ? unkeyed : buckets[key])),
        ];

        return ValueTask.FromResult(Result.Ok(batches));

        List<BusDelivery>? Bucket(string key)
        {
            if (buckets.TryGetValue(key, out var existing))
            {
                return existing;
            }

            if (order.Count >= maxPartitions)
            {
                return null;
            }

            order.Add(key);

            return buckets[key] = [];
        }

        List<BusDelivery>? Unkeyed()
        {
            if (order.Contains(null))
            {
                return unkeyed;
            }

            if (order.Count >= maxPartitions)
            {
                return null;
            }

            order.Add(null);

            return unkeyed;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <strong>Settles the record and then commits as far as the ledger allows.</strong> The
    /// commit is the highest offset in this partition with every offset below it settled, so an
    /// out-of-order acknowledgement moves nothing until its predecessors are done — which is
    /// exactly what makes an offset watermark safe to use as a per-message acknowledgement.
    /// </remarks>
    public ValueTask<Result<bool>> AcknowledgeAsync(
        BusSubscription subscription, BusDelivery delivery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(delivery);
        ObjectDisposedException.ThrowIf(_disposed, this);

        cancellationToken.ThrowIfCancellationRequested();

        Group group;

        try
        {
            group = GroupFor(subscription);
        }
        catch (KafkaException failure)
        {
            return ValueTask.FromResult(Result.Fail<bool>(Unreadable(subscription, failure)));
        }

        if (!KafkaTokens.TryParse(delivery.Token, out var position))
        {
            return ValueTask.FromResult(Result.Ok(false));
        }

        try
        {
            return ValueTask.FromResult(Result.Ok(group.SettleAndCommit(position)));
        }
        catch (KafkaException failure)
        {
            return ValueTask.FromResult(Result.Fail<bool>(Unreadable(subscription, failure)));
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result<bool>> DeadLetterAsync(
        BusSubscription subscription,
        BusDelivery delivery,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Group group;

        try
        {
            group = GroupFor(subscription);
        }
        catch (KafkaException failure)
        {
            return Result.Fail<bool>(Unreadable(subscription, failure));
        }

        if (!KafkaTokens.TryParse(delivery.Token, out var position) ||
            group.Held(position) is not { } record)
        {
            return false;
        }

        var copy = new Message<string?, byte[]?>
        {
            Key = record.Message.Key,
            Value = record.Message.Value,
            Headers = record.Message.Headers ?? [],
        };

        KafkaHeaders.Write(copy.Headers, KafkaHeaders.DeadLetterReason, reason);

        try
        {
            // Copy first, settle second. A crash between the two redelivers the original, which
            // at-least-once already permits; the other order loses the record (ADR-0038).
            await _deadLetters.Value
                .ProduceAsync(_options.DeadLetterTopic, copy, cancellationToken)
                .ConfigureAwait(false);

            return group.SettleAndCommit(position);
        }
        catch (Exception failure) when (failure is ProduceException<string?, byte[]?> or KafkaException)
        {
            return Result.Fail<bool>(Unreadable(subscription, failure));
        }
    }

    /// <summary>Closes every consumer this instance opened.</summary>
    /// <returns>A task that completes when they are closed.</returns>
    /// <remarks>
    /// <strong>Each consumer is closed rather than merely disposed</strong>, because closing is
    /// what leaves the group cleanly — the coordinator reassigns the partitions at once instead of
    /// waiting out a session timeout, which is the difference between a redeploy that pauses for a
    /// second and one that pauses for forty-five.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        foreach (var group in _groups.Values)
        {
            group.Close();
        }

        _groups.Clear();

        if (_deadLetters.IsValueCreated)
        {
            _deadLetters.Value.Flush(TimeSpan.FromSeconds(5));
            _deadLetters.Value.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Turns a consumed record into a delivery, readable or not.</summary>
    private static BusDelivery Read(
        ConsumeResult<string?, byte[]?> record, string topic, int deliveries)
    {
        var token = KafkaTokens.Of(record.TopicPartitionOffset);
        var key = record.Message.Key;

        if (!Guid.TryParse(KafkaHeaders.Text(record.Message.Headers, KafkaHeaders.EventId), out var eventId))
        {
            return BusDelivery.Unreadable(
                token,
                deliveries,
                key,
                $"The record carries no readable '{KafkaHeaders.EventId}' header, so it has no event id.");
        }

        return BusDelivery.Of(
            new BusMessage(
                eventId,
                topic,
                topic,
                KafkaHeaders.Text(record.Message.Headers, KafkaHeaders.SchemaVersion) ?? "1.0.0",
                key,
                record.Message.Value is { } value ? Encoding.UTF8.GetString(value) : null,
                KafkaHeaders.Text(record.Message.Headers, KafkaHeaders.TenantId)),
            token,
            deliveries);
    }

    private Group GroupFor(BusSubscription subscription) =>
        _groups.GetOrAdd(subscription.Group, (group, state) => Group.Join(group, state._options), this);

    private Error Unreadable(BusSubscription subscription, Exception failure) =>
        new Error(
            ReceiveFailedCode,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Could not read topic '{_options.Topic}' as group '{subscription.Group}'. This " +
                $"package creates no topics: a deployment does, with the partition count its " +
                $"ordering needs. {failure.Message}"),
            ErrorCategory.Unavailable)
            .With("topic", _options.Topic)
            .With("group", subscription.Group)
            .With("eventType", subscription.Topic);
}

/// <summary>The three numbers that name one delivery on Kafka.</summary>
/// <remarks>
/// A topic, a partition and an offset. Opaque above this package — <c>IBusConsumer</c> says a
/// token is "never derived from or compared with anything" — and a string rather than a struct
/// because that is what the contract carries.
/// </remarks>
internal static class KafkaTokens
{
    /// <summary>
    /// ASCII unit separator, which a topic name cannot contain.
    /// </summary>
    /// <remarks>
    /// A topic name is letters, digits, dot, underscore and hyphen, so any of those would be
    /// ambiguous against a topic that used it. A control character cannot appear in one at all.
    /// </remarks>
    private const char Separator = '\u001F';

    public static string Of(TopicPartitionOffset position) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{position.Topic}{Separator}{position.Partition.Value}{Separator}{position.Offset.Value}");

    public static bool TryParse(string token, out TopicPartitionOffset position)
    {
        position = default!;

        var parts = token.Split(Separator);

        if (parts.Length != 3 ||
            !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var partition) ||
            !long.TryParse(parts[2], CultureInfo.InvariantCulture, out var offset))
        {
            return false;
        }

        position = new TopicPartitionOffset(parts[0], new Partition(partition), new Offset(offset));

        return true;
    }
}
