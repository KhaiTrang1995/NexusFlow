using FlowX.Conformance;
using Xunit;

namespace FlowX.AzureServiceBus.Tests;

/// <summary>Runs the whole publisher suite against Azure Service Bus.</summary>
/// <remarks>
/// <para>
/// <strong>The fourth derivation of <c>PublisherConformance</c>, and the third transport.</strong>
/// ADR-0018 deferred the suite because "writing it against one test double would have been a suite
/// that encodes its only implementation". Redis discharged that with a second implementation;
/// RabbitMQ with a second <em>transport</em>, reaching per-key order by a mechanism a Redis stream
/// has nothing in common with. This is the third mechanism — AMQP 1.0 settles a transfer as part
/// of the protocol, so there are no publisher confirms to switch on and nothing corresponding to a
/// channel to keep serial. The suite is unmodified, and this file is the whole of the evidence.
/// </para>
/// <para>
/// It skips when no namespace is configured and <strong>fails</strong> when one was promised and
/// did not answer. That decision is <see cref="ServiceBusTestNamespace"/>'s and is inherited
/// rather than re-implemented.
/// </para>
/// </remarks>
public sealed class ServiceBusPublisherConformanceTests : PublisherConformance, IAsyncLifetime
{
    private readonly List<ServiceBusBrokerUnderTest> _brokers = [];

    /// <inheritdoc />
    protected override async ValueTask<BrokerUnderTest> CreateBrokerAsync()
    {
        var broker = await ServiceBusBrokerUnderTest.CreateAsync(Cancellation);

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
