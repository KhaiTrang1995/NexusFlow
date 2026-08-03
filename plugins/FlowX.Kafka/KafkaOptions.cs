namespace FlowX.Kafka;

/// <summary>
/// The addresses this package publishes to and consumes from, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One record for both adapters</strong>, for <c>RabbitMqOptions</c>'s reason: the topic
/// the publisher writes to is the topic the consumer subscribes to, and a deployment that
/// configured them independently would have two ways to spell one name and no way to discover it
/// had used both.
/// </para>
/// <para>
/// <strong>Everything here is an address, and nothing is a policy.</strong> How many deliveries a
/// message gets before the host gives up is <c>FlowXOptions.BusMaxDeliveries</c>; how often the
/// consumer sweeps is <c>FlowXOptions.BusScanInterval</c>. Partition count, replication factor and
/// retention are a topic's, which is a deployment's — this package creates no topics, for
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0074-service-bus-topology-is-created-by-a-deployment-not-by-a-consumer.md">ADR-0074</a>'s
/// reasons stated one transport over.
/// </para>
/// </remarks>
public sealed record KafkaOptions
{
    /// <summary>The bootstrap servers, e.g. <c>localhost:9092</c>.</summary>
    public required string BootstrapServers { get; init; }

    /// <summary>The topic every published event goes to. Defaults to <c>flowx.events</c>.</summary>
    /// <remarks>
    /// <strong>One topic, and the event type is a header.</strong> A topic per event type would
    /// make every new contract a broker-side change and would spread one key's events across
    /// topics — losing the per-key order the record key is there to give. A subscription filters
    /// by type in this package rather than in the broker, because Kafka has no server-side
    /// filter and a consumer that read only its own topic would need one topic per type.
    /// </remarks>
    public string Topic { get; init; } = "flowx.events";

    /// <summary>
    /// The suffix appended to <see cref="Topic"/> for dead-lettered records. Defaults to
    /// <c>.dead</c>.
    /// </summary>
    /// <remarks>
    /// <strong>Derived, because Kafka has no dead-letter destination of its own.</strong> That is
    /// <c>FlowX.Redis</c>'s situation rather than Azure Service Bus's, and
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0073-a-dead-letter-destination-stays-derived-even-where-the-broker-has-one.md">ADR-0073</a>
    /// already decided that a destination is deployment configuration and is not published to the
    /// manifest — so <c>KafkaTriggerAttribute.DeadLetter</c> stays unread here too.
    /// </remarks>
    public string DeadLetterSuffix { get; init; } = ".dead";

    /// <summary>How long a poll waits for records before answering empty.</summary>
    /// <remarks>
    /// Short, because the host polls: <c>FlowBusScan</c> calls <c>ReceiveAsync</c> on its own
    /// interval, and a long wait here would be a second scheduler disagreeing with the first.
    /// </remarks>
    public TimeSpan PollWait { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The dead-letter topic for this deployment.</summary>
    public string DeadLetterTopic => Topic + DeadLetterSuffix;
}
