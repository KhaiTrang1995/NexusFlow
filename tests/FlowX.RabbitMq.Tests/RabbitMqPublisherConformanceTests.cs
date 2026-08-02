using FlowX.Conformance;
using Xunit;

namespace FlowX.RabbitMq.Tests;

/// <summary>Runs the whole publisher suite against RabbitMQ.</summary>
/// <remarks>
/// <para>
/// <strong>This is the third derivation of <c>PublisherConformance</c> and the one that tests the
/// suite rather than the publisher.</strong> ADR-0018 deferred the suite because "writing it
/// against one test double would have been a suite that encodes its only implementation", and
/// <c>RedisStreamPublisherConformanceTests</c> discharged that with a second implementation. A
/// second implementation can still share an accident with the first. This one cannot: Redis
/// reaches ADR-0018 decision 3's per-key order by writing one totally-ordered stream per key, and
/// this reaches it by publishing serially down one confirming channel into an exchange that knows
/// nothing about keys. The suite is unmodified, and the file this sentence is in is the whole of
/// the evidence for that claim.
/// </para>
/// <para>
/// It skips when no broker is configured and <strong>fails</strong> when one was promised and did
/// not answer. That decision is <see cref="RabbitMqTestBroker"/>'s and is inherited rather than
/// re-implemented, for the reason <see cref="RabbitMqAvailabilityTests"/> states: a skip in the
/// second case reports an integration that never ran as a green job.
/// </para>
/// </remarks>
public sealed class RabbitMqPublisherConformanceTests : PublisherConformance, IAsyncLifetime
{
    private readonly List<RabbitMqBrokerUnderTest> _brokers = [];

    /// <inheritdoc />
    protected override async ValueTask<BrokerUnderTest> CreateBrokerAsync()
    {
        var broker = await RabbitMqBrokerUnderTest.CreateAsync(Cancellation);

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
