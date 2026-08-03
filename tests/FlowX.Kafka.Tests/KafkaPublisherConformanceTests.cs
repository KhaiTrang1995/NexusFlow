using FlowX.Conformance;
using Xunit;

namespace FlowX.Kafka.Tests;

/// <summary>Runs the whole publisher suite against Apache Kafka.</summary>
/// <remarks>
/// <para>
/// <strong>The fifth derivation of <c>PublisherConformance</c>, and the one where the broker
/// supplies the property.</strong> ADR-0018 deferred the suite because "writing it against one
/// test double would have been a suite that encodes its only implementation". Redis discharged
/// that with a second implementation, RabbitMQ with a second transport, Service Bus with a third
/// settlement model — and this with a broker that gives per-key order for free, because a key
/// hashes to a partition and a partition is a total order. Four mechanisms with nothing in common
/// and one unmodified suite.
/// </para>
/// <para>
/// It skips when no cluster is configured and <strong>fails</strong> when one was promised and did
/// not answer. That decision is <see cref="KafkaTestCluster"/>'s and is inherited rather than
/// re-implemented.
/// </para>
/// </remarks>
public sealed class KafkaPublisherConformanceTests : PublisherConformance, IAsyncLifetime
{
    private readonly List<KafkaBrokerUnderTest> _brokers = [];

    /// <inheritdoc />
    protected override async ValueTask<BrokerUnderTest> CreateBrokerAsync()
    {
        var broker = await KafkaBrokerUnderTest.CreateAsync(Cancellation);

        _brokers.Add(broker);

        return broker;
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var broker in _brokers)
        {
            await broker.DisposeAsync();
        }

        _brokers.Clear();
    }
}
