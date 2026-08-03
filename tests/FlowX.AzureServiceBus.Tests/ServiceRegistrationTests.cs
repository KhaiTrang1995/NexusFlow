using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace FlowX.AzureServiceBus.Tests;

/// <summary>What a host actually resolves after calling <c>AddFlowXAzureServiceBus</c>.</summary>
/// <remarks>
/// Asserted by building a provider and resolving, rather than by reading the
/// <c>ServiceDescriptor</c> list, because the descriptor list says what was written down and a
/// host cares what comes out.
/// </remarks>
public sealed class ServiceRegistrationTests
{
    /// <summary>A namespace nothing listens on. Port 1 is reserved.</summary>
    private const string ConnectionString =
        "Endpoint=sb://127.0.0.1:1;SharedAccessKeyName=RootManageSharedAccessKey;" +
        "SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    /// <summary>The publisher resolves, and it is this package's.</summary>
    /// <remarks>
    /// <strong>It resolves with no namespace reachable, and that is the property.</strong> A
    /// registration that connected eagerly would turn "the namespace was throttling while we were
    /// deploying" into a host that will not start — instead of a batch the outbox offers again on
    /// the next pass, which is what ADR-0007 asks for.
    /// </remarks>
    [Fact]
    public async Task ThePublisherResolvesWithoutANamespace()
    {
        await using var provider = new ServiceCollection()
            .AddFlowXAzureServiceBus(ConnectionString)
            .BuildServiceProvider();

        provider.GetRequiredService<IEventPublisher>()
            .ShouldBeOfType<AzureServiceBusEventPublisher>();
    }

    /// <summary>The consumer resolves, and it is this package's.</summary>
    [Fact]
    public async Task TheConsumerResolvesWithoutANamespace()
    {
        await using var provider = new ServiceCollection()
            .AddFlowXAzureServiceBusConsumer(ConnectionString)
            .BuildServiceProvider();

        provider.GetRequiredService<IBusConsumer>().ShouldBeOfType<AzureServiceBusConsumer>();
    }

    /// <summary>
    /// Publishing and consuming are two registrations, and neither drags the other in.
    /// </summary>
    /// <remarks>
    /// <strong>The assertion is about what is <em>not</em> registered.</strong> A deployment
    /// migrating between transports publishes to the new broker while the old one still has a
    /// backlog to consume, and a call that registered both halves would make that arrangement
    /// unexpressible.
    /// </remarks>
    [Fact]
    public async Task PublishingDoesNotRegisterAConsumerAndConsumingDoesNotRegisterAPublisher()
    {
        await using var publishing = new ServiceCollection()
            .AddFlowXAzureServiceBus(ConnectionString)
            .BuildServiceProvider();

        publishing.GetService<IBusConsumer>().ShouldBeNull(
            "a host that asked for a publisher did not ask to consume.");

        await using var consuming = new ServiceCollection()
            .AddFlowXAzureServiceBusConsumer(ConnectionString)
            .BuildServiceProvider();

        consuming.GetService<IEventPublisher>().ShouldBeNull(
            "and a host that asked to consume did not ask for a publisher.");
    }

    /// <summary>Both calls share one client, so a host holds one connection and not two.</summary>
    /// <remarks>
    /// An AMQP connection with a heartbeat is what a namespace counts against its limit, and one
    /// per adapter is the standard way to spend that limit twice over.
    /// </remarks>
    [Fact]
    public async Task BothCallsShareOneConnection()
    {
        await using var provider = new ServiceCollection()
            .AddFlowXAzureServiceBus(ConnectionString)
            .AddFlowXAzureServiceBusConsumer(ConnectionString)
            .BuildServiceProvider();

        var first = provider.GetRequiredService<AzureServiceBusConnection>();
        var second = provider.GetRequiredService<AzureServiceBusConnection>();

        second.ShouldBeSameAs(first);
    }

    /// <summary>The options a host supplies are the options both adapters get.</summary>
    /// <remarks>
    /// Registered with <c>TryAdd</c>, so the first call decides. The failure this prevents is a
    /// deployment whose publisher writes to one topic and whose consumer reads another — which
    /// reads nothing, says nothing, and looks like a broker that is not delivering.
    /// </remarks>
    [Fact]
    public async Task TheFirstCallsOptionsAreTheOnesBothAdaptersUse()
    {
        var options = new AzureServiceBusOptions { Topic = "somebody-elses-topic" };

        await using var provider = new ServiceCollection()
            .AddFlowXAzureServiceBus(ConnectionString, options)
            .AddFlowXAzureServiceBusConsumer(ConnectionString, new AzureServiceBusOptions())
            .BuildServiceProvider();

        provider.GetRequiredService<AzureServiceBusOptions>().Topic.ShouldBe("somebody-elses-topic");
    }

    /// <summary>A subscription's name is the group and the event type, and it is injective.</summary>
    [Fact]
    public void ASubscriptionIsNamedForItsGroupAndItsEventType()
    {
        var options = new AzureServiceBusOptions();

        options.SubscriptionFor("scoring", "lead.created").ShouldBe("scoring--lead.created");

        options.SubscriptionFor("scoring.lead", "created")
            .ShouldNotBe(
                options.SubscriptionFor("scoring", "lead.created"),
                "a separator an event type can contain would collapse two subscriptions into one.");
    }
}
