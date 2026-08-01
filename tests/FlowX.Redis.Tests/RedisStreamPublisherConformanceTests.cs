using FlowX.Conformance;
using Xunit;

namespace FlowX.Redis.Tests;

/// <summary>Runs the whole publisher suite against Redis Streams.</summary>
/// <remarks>
/// <para>
/// <strong>This class is what discharges ADR-0018's revisit condition.</strong> That record
/// deferred <c>PublisherConformance</c> with a reason — "writing it against one test double
/// would have been a suite that encodes its only implementation" — and named the condition for
/// writing it: "a broker plugin exists and <c>PublisherConformance</c> can hold two
/// implementations to this contract". <c>RecordingPublisherConformanceTests</c> is the double;
/// this is the client with a socket, and the two derive the same suite unmodified.
/// </para>
/// <para>
/// It skips when no Redis is configured and <strong>fails</strong> when one was promised and did
/// not answer. That decision is <see cref="RedisTestServer"/>'s and is inherited rather than
/// re-implemented, for the reason <c>RedisAvailabilityTests</c> states: a skip in the second case
/// reports an integration that never ran as a green job.
/// </para>
/// </remarks>
public sealed class RedisStreamPublisherConformanceTests : PublisherConformance, IAsyncLifetime
{
    private readonly List<RedisStreamBrokerUnderTest> _brokers = [];

    /// <inheritdoc />
    protected override async ValueTask<BrokerUnderTest> CreateBrokerAsync()
    {
        var broker = await RedisStreamBrokerUnderTest.CreateAsync(Cancellation);

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
