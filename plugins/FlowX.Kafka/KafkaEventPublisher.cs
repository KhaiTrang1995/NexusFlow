using System.Text;
using Confluent.Kafka;

namespace FlowX.Kafka;

/// <summary>
/// Publishes staged outbox events to one topic, with the partition key as the record key.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The fourth broker <c>PublisherConformance</c> holds to the contract, and the first
/// whose ordering is the broker's own.</strong> Redis reaches ADR-0018's per-key order by writing
/// one stream per key, RabbitMQ by publishing serially down one confirmed channel, Service Bus by
/// AMQP 1.0 settlement down one sender. Kafka needs none of those: records sharing a key hash to
/// one partition and a partition is a total order. The suite is unmodified for the fourth time.
/// </para>
/// <para>
/// <strong><c>Acks = All</c> and there is no setting to lower it.</strong>
/// <c>PostgresOutboxPublisher</c> marks a row published when this method returns a count
/// including it, so an acknowledgement from a leader that had not yet replicated would mark as
/// delivered an event a leader election can still discard. <c>EnableIdempotence</c> is on for the
/// same reason from the other side: a producer retry that duplicated a record would put one key's
/// event into a partition twice.
/// </para>
/// <para>
/// <strong>What acceptance means, stated because it is narrower than it looks.</strong> The
/// partition took the record; it does not follow that any consumer group will read it. A topic
/// nobody subscribes to accepts events and the outbox marks them published — the same property a
/// Redis stream nobody reads has, and where <c>docs/11-Distributed-Runtime.md §5</c> already puts
/// the boundary.
/// </para>
/// <para>
/// <strong>One record at a time, awaited, and the count is where it stopped.</strong> The client
/// batches underneath — that is what <c>linger.ms</c> is — but each <c>ProduceAsync</c> is awaited
/// to its acknowledgement before the next is sent, because <c>IEventPublisher</c>'s prefix
/// contract needs a partial failure to leave the events that did arrive marked, and a fire-and-
/// forget batch reports one outcome for all of them.
/// </para>
/// </remarks>
public sealed class KafkaEventPublisher : IEventPublisher, IAsyncDisposable
{
    /// <summary>What a caller sees when the cluster could not be reached.</summary>
    /// <remarks>
    /// One code rather than one per librdkafka error: the caller's decision is the same for all of
    /// them — leave the rows pending and try the next pass. The client's own message travels in
    /// <see cref="Error.Message"/>.
    /// </remarks>
    public const string PublishFailedCode = "kafka.publish_failed";

    private readonly IProducer<string?, byte[]?> _producer;
    private readonly KafkaOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>Creates a publisher over a producer this instance owns.</summary>
    /// <param name="options">Where to publish. <see cref="KafkaOptions.BootstrapServers"/> is required.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public KafkaEventPublisher(KafkaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;

        _producer = new ProducerBuilder<string?, byte[]?>(new ProducerConfig
        {
            BootstrapServers = options.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,

            // Bounded, so an unreachable cluster fails a pass rather than blocking the outbox
            // drain for as long as librdkafka's default five minutes.
            MessageTimeoutMs = 10_000,
        })
        .SetLogHandler(static (_, _) => { })
        .Build();
    }

    /// <inheritdoc />
    public async ValueTask<Result<int>> PublishAsync(
        IReadOnlyList<OutboxRecord> batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (batch.Count == 0)
        {
            return 0;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            for (var published = 0; published < batch.Count; published++)
            {
                try
                {
                    await _producer
                        .ProduceAsync(_options.Topic, Record(batch[published]), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception failure) when (failure is ProduceException<string?, byte[]?> or KafkaException)
                {
                    // The first event of the batch is reported as a failure and every later one
                    // as a shorter prefix, which is the choice IEventPublisher states: a refusal
                    // that took nothing is worth reporting with its reason, and one that took
                    // something is worth reporting as a count.
                    return published == 0 ? Result.Fail<int>(Failed(failure)) : published;
                }
            }

            return batch.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Flushes and closes the producer this publisher owns.</summary>
    /// <returns>A task that completes when it is closed.</returns>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        // Bounded: a producer holding records for a cluster that is gone must not stop a host
        // from shutting down. Anything still queued was never acknowledged, so the outbox has
        // it pending and the next process publishes it.
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
        _gate.Dispose();

        return ValueTask.CompletedTask;
    }

    /// <summary>Turns a staged row into a record.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The key is the partition key and nothing else.</strong> Kafka hashes it to choose a
    /// partition, so every event of one key lands in one partition and a partition is read in
    /// order — which is ADR-0018's promise expressed as a broker feature rather than as a
    /// discipline this package has to keep. An unkeyed event gets a null key and is spread across
    /// partitions, which is exactly right: it is ordered against nothing.
    /// </para>
    /// <para>
    /// The event id and the type are headers because Kafka has no field for either.
    /// <c>PublishedAt</c> is not written at all — it is the outbox's record of its own bookkeeping
    /// and is null on every event handed to a publisher.
    /// </para>
    /// </remarks>
    private static Message<string?, byte[]?> Record(OutboxRecord staged)
    {
        var headers = new Headers();

        KafkaHeaders.Write(headers, KafkaHeaders.EventId, staged.EventId.ToString("d"));
        KafkaHeaders.Write(headers, KafkaHeaders.Type, staged.Type);
        KafkaHeaders.Write(headers, KafkaHeaders.InstanceId, staged.InstanceId.ToString("d"));
        KafkaHeaders.Write(headers, KafkaHeaders.SchemaVersion, staged.SchemaVersion);
        KafkaHeaders.Write(headers, KafkaHeaders.TenantId, staged.TenantId);

        return new Message<string?, byte[]?>
        {
            Key = staged.PartitionKey,
            Value = staged.PayloadJson is { } payload ? Encoding.UTF8.GetBytes(payload) : null,
            Headers = headers,
        };
    }

    private static Error Failed(Exception failure) =>
        new Error(
            PublishFailedCode,
            "The Kafka cluster did not accept the event: " + failure.Message,
            ErrorCategory.Unavailable);
}
