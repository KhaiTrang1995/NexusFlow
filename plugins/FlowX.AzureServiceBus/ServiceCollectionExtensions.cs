using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlowX.AzureServiceBus;

/// <summary>Registers this package's two Service Bus adapters, one call each.</summary>
/// <remarks>
/// <para>
/// <strong>Two calls rather than one, which is <c>FlowX.Redis</c>'s and <c>FlowX.RabbitMq</c>'s
/// convention and their reason.</strong> A deployment may publish to Service Bus and consume from
/// somewhere else — a migration between transports is exactly that arrangement, held for as long
/// as the old broker still has a backlog — and folding the two into one method would make a
/// consumer something a host acquires by asking for a publisher.
/// </para>
/// <para>
/// <strong>What they share is the connection, registered with <c>TryAdd</c>.</strong> A host
/// making both calls gets one <c>ServiceBusClient</c> rather than two, and whichever call came
/// first is the one whose connection string is used.
/// </para>
/// <para>
/// <strong>Everything registered here is <see cref="IAsyncDisposable"/>, so the provider must be
/// disposed asynchronously.</strong> Closing an AMQP link is a round trip to the namespace.
/// <c>IHost</c> already disposes its provider asynchronously; a caller building a
/// <c>ServiceProvider</c> by hand needs <c>await using</c>.
/// </para>
/// <para>
/// <strong>Neither call registers a loop, and neither creates an entity.</strong> What drives the
/// adapters is <c>PostgresOutboxPublisher</c>'s drain and <c>FlowX.Hosting</c>'s
/// <c>FlowBusService</c>, both of which a host already has; what creates the topic and its
/// subscriptions is a deployment (ADR-0074).
/// </para>
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IEventPublisher"/> over a Service Bus topic.</summary>
    /// <param name="services">The container being built.</param>
    /// <param name="connectionString">
    /// A Service Bus connection string, e.g. <c>Endpoint=sb://…;SharedAccessKeyName=…;SharedAccessKey=…</c>.
    /// </param>
    /// <param name="options">Which topic to send to. Defaults to <c>flowx-events</c>.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is null or blank.</exception>
    public static IServiceCollection AddFlowXAzureServiceBus(
        this IServiceCollection services,
        string connectionString,
        AzureServiceBusOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton(options ?? new AzureServiceBusOptions());
        services.TryAddSingleton(_ => new AzureServiceBusConnection(connectionString));
        services.AddSingleton<IEventPublisher>(provider => new AzureServiceBusEventPublisher(
            provider.GetRequiredService<AzureServiceBusConnection>(),
            provider.GetRequiredService<AzureServiceBusOptions>()));

        return services;
    }

    /// <summary>Registers <see cref="IBusConsumer"/> over Service Bus subscriptions.</summary>
    /// <param name="services">The container being built.</param>
    /// <param name="connectionString">A Service Bus connection string.</param>
    /// <param name="options">
    /// Which topic the subscriptions hang off. <strong>Must be the same options the publisher
    /// uses</strong> — the topic name is what makes one deployment's events findable and
    /// another's invisible. Registered with <c>TryAdd</c>, so the first of the two calls decides
    /// and the second cannot silently disagree with it.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is null or blank.</exception>
    public static IServiceCollection AddFlowXAzureServiceBusConsumer(
        this IServiceCollection services,
        string connectionString,
        AzureServiceBusOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton(options ?? new AzureServiceBusOptions());
        services.TryAddSingleton(_ => new AzureServiceBusConnection(connectionString));
        services.AddSingleton<IBusConsumer>(provider => new AzureServiceBusConsumer(
            provider.GetRequiredService<AzureServiceBusConnection>(),
            provider.GetRequiredService<AzureServiceBusOptions>()));

        return services;
    }
}
