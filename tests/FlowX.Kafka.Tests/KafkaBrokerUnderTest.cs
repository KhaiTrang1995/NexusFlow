using System.Text;
using Confluent.Kafka;
using FlowX.Conformance;

namespace FlowX.Kafka.Tests;

/// <summary>
/// <see cref="PublisherConformance"/>'s harness over a real Kafka cluster, on topics this fixture
/// created.
/// </summary>
/// <remarks>
/// <para>
/// The suite needs three things and this supplies exactly those: the publisher, a way to read one
/// key's events back in broker order, and a publisher pointed at a cluster that is not there.
/// <strong>Nothing in <c>tests/FlowX.Conformance.Tests</c> was changed to accommodate
/// Kafka</strong> — the fourth implementation, and the one whose ordering mechanism is the
/// broker's own rather than a discipline the publisher keeps.
/// </para>
/// <para>
/// <strong>The read is a raw consumer over every partition, not the plugin's.</strong> The suite
/// asks what reached the broker; answering it through <c>KafkaBusConsumer</c> would make a
/// publisher test pass or fail on the consumer's ledger. So this drains the topic with a plain
/// client reading from the beginning, and the order it reports is the order the partitions hold.
/// </para>
/// <para>
/// <strong>Three partitions, so per-key order is a claim and not an accident.</strong> On a
/// single-partition topic every event is ordered against every other and the suite could not tell
/// a publisher that keyed correctly from one that ignored keys entirely.
/// </para>
/// </remarks>
internal sealed class KafkaBrokerUnderTest : BrokerUnderTest
{
    private readonly KafkaOptions _options;
    private readonly KafkaEventPublisher _publisher;
    private readonly List<KafkaEventPublisher> _unreachable = [];

    private KafkaBrokerUnderTest(KafkaOptions options)
    {
        _options = options;
        _publisher = new KafkaEventPublisher(options);
    }

    /// <inheritdoc />
    public override IEventPublisher Publisher => _publisher;

    /// <summary>Creates a harness over a private topic, or refuses to pretend it did.</summary>
    /// <param name="cancellationToken">Cancels the setup.</param>
    /// <returns>The harness.</returns>
    public static async ValueTask<KafkaBrokerUnderTest> CreateAsync(CancellationToken cancellationToken)
    {
        var options = await KafkaTestCluster.IsolatedAsync(partitions: 3, cancellationToken);

        return new KafkaBrokerUnderTest(options);
    }

    /// <inheritdoc />
    public override ValueTask<IReadOnlyList<DeliveredEvent>> ReadAsync(
        string? partitionKey,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DeliveredEvent> found =
        [
            .. Drain(cancellationToken).Where(delivered =>
                string.Equals(delivered.PartitionKey, partitionKey, StringComparison.Ordinal)),
        ];

        return ValueTask.FromResult(found);
    }

    /// <inheritdoc />
    public override ValueTask<IEventPublisher> UnreachableAsync(CancellationToken cancellationToken)
    {
        // Port 1 is reserved and nothing listens on it. The producer connects lazily, so this
        // constructor succeeds and the first publish is what meets the closed port — the shape a
        // cluster that went away mid-deployment has.
        var dead = new KafkaEventPublisher(_options with { BootstrapServers = "127.0.0.1:1" });

        _unreachable.Add(dead);

        return ValueTask.FromResult<IEventPublisher>(dead);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        foreach (var dead in _unreachable)
        {
            await dead.DisposeAsync();
        }

        _unreachable.Clear();

        await _publisher.DisposeAsync();
        await KafkaTestCluster.DropAsync(_options);
        await base.DisposeAsync();
    }

    /// <summary>Everything on the topic, from the beginning, in partition order.</summary>
    /// <remarks>
    /// A fresh group id per call and no commits at all: the harness is asking the same question
    /// more than once in one test, and a group that remembered where it got to would answer the
    /// second call with nothing.
    /// </remarks>
    private List<DeliveredEvent> Drain(CancellationToken cancellationToken)
    {
        using var consumer = new ConsumerBuilder<string?, byte[]?>(new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = "harness-" + Guid.NewGuid().ToString("N")[..8],
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnablePartitionEof = true,
        })
        .SetLogHandler(static (_, _) => { })
        .Build();

        consumer.Subscribe(_options.Topic);

        var drained = new List<DeliveredEvent>();
        var finished = new HashSet<int>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (DateTimeOffset.UtcNow < deadline && finished.Count < 3)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var record = consumer.Consume(TimeSpan.FromMilliseconds(500));

            if (record is null)
            {
                continue;
            }

            if (record.IsPartitionEOF)
            {
                finished.Add(record.Partition.Value);

                continue;
            }

            drained.Add(new DeliveredEvent(
                Guid.TryParse(KafkaHeaders.Text(record.Message.Headers, KafkaHeaders.EventId), out var id)
                    ? id
                    : Guid.Empty,
                KafkaHeaders.Text(record.Message.Headers, KafkaHeaders.Type) ?? string.Empty,
                KafkaHeaders.Text(record.Message.Headers, KafkaHeaders.SchemaVersion) ?? string.Empty,
                record.Message.Key,
                record.Message.Value is { } value ? Encoding.UTF8.GetString(value) : null));
        }

        consumer.Close();

        return drained;
    }
}
