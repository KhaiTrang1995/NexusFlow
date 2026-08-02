using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace FlowX.RabbitMq.Tests;

/// <summary>What a host actually resolves after calling <c>AddFlowXRabbitMq</c>.</summary>
/// <remarks>
/// Asserted by building a provider and resolving, rather than by reading the
/// <c>ServiceDescriptor</c> list, because the descriptor list says what was written down and a
/// host cares what comes out. The same reasoning as <c>FlowX.Redis.Tests</c>'s registration test,
/// and the reason both projects reference the container implementation rather than only its
/// abstractions.
/// </remarks>
public sealed class ServiceRegistrationTests
{
    private const string Uri = "amqp://guest:guest@127.0.0.1:1/";

    /// <summary>The publisher resolves, and it is this package's.</summary>
    /// <remarks>
    /// <strong>It resolves with no broker running, and that is the property being asserted.</strong>
    /// <c>ConnectionFactory.CreateConnectionAsync</c> throws when the broker is down, so a
    /// registration that connected eagerly would turn "RabbitMQ restarted while we were
    /// deploying" into a host that will not start — instead of a batch the outbox offers again on
    /// the next pass, which is what ADR-0007 asks for. The URI here points at a reserved port and
    /// nothing listens on it.
    /// </remarks>
    [Fact]
    public async Task ThePublisherResolvesWithoutABroker()
    {
        await using var provider = new ServiceCollection().AddFlowXRabbitMq(Uri).BuildServiceProvider();

        provider.GetRequiredService<IEventPublisher>().ShouldBeOfType<RabbitMqEventPublisher>();
    }

    /// <summary>The consumer resolves, and it is this package's.</summary>
    [Fact]
    public async Task TheConsumerResolvesWithoutABroker()
    {
        await using var provider = new ServiceCollection().AddFlowXRabbitMqConsumer(Uri).BuildServiceProvider();

        provider.GetRequiredService<IBusConsumer>().ShouldBeOfType<RabbitMqBusConsumer>();
    }

    /// <summary>
    /// Publishing and consuming are two registrations, and neither drags the other in.
    /// </summary>
    /// <remarks>
    /// <strong>The assertion is about what is <em>not</em> registered.</strong> A deployment
    /// migrating between transports publishes to the new broker while the old one still has a
    /// backlog to consume, and a call that registered both halves would make that arrangement
    /// unexpressible. It is also the failure shape WP-53 shipped: a package that registers half a
    /// stack leaves a host believing it has a whole one.
    /// </remarks>
    [Fact]
    public async Task PublishingDoesNotRegisterAConsumerAndConsumingDoesNotRegisterAPublisher()
    {
        await using var publishing = new ServiceCollection().AddFlowXRabbitMq(Uri).BuildServiceProvider();

        publishing.GetService<IBusConsumer>().ShouldBeNull(
            "a host that publishes to RabbitMQ has not thereby said it consumes from it.");

        await using var consuming = new ServiceCollection().AddFlowXRabbitMqConsumer(Uri).BuildServiceProvider();

        consuming.GetService<IEventPublisher>().ShouldBeNull(
            "and the reverse. Two calls, because they are two decisions.");
    }

    /// <summary>Both calls together share one connection.</summary>
    /// <remarks>
    /// An AMQP connection is a TCP connection with a heartbeat, so one per registration is the
    /// standard way to double a broker's connection count for nothing. <c>TryAdd</c> is what makes
    /// the second call reuse the first's, and this is the assertion that it does rather than a
    /// comment saying it should.
    /// </remarks>
    [Fact]
    public async Task BothCallsShareOneConnection()
    {
        await using var provider = new ServiceCollection()
            .AddFlowXRabbitMq(Uri)
            .AddFlowXRabbitMqConsumer(Uri)
            .BuildServiceProvider();

        provider.GetServices<RabbitMqConnection>().ShouldHaveSingleItem();
    }

    /// <summary>The options a caller passed are the options both adapters get.</summary>
    /// <remarks>
    /// The exchange name is what makes one deployment's events findable and another's invisible,
    /// so a registration that quietly used the default would leave a consumer bound to an
    /// exchange nothing publishes to — reading nothing, and saying nothing about it.
    /// </remarks>
    [Fact]
    public async Task TheOptionsGivenAreTheOptionsResolved()
    {
        var options = new RabbitMqOptions { Exchange = "crm.events", QueuePrefix = "crm" };

        await using var provider = new ServiceCollection()
            .AddFlowXRabbitMq(Uri, options)
            .AddFlowXRabbitMqConsumer(Uri)
            .BuildServiceProvider();

        provider.GetRequiredService<RabbitMqOptions>().ShouldBeSameAs(
            options,
            "the second call registers its own with TryAdd, so the first call's survive and the " +
            "two adapters cannot disagree about which exchange they are talking about.");
    }
}
