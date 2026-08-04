using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlowX.Kafka;

/// <summary>Registers this package's two Kafka adapters, one call each.</summary>
/// <remarks>
/// <para>
/// <strong>Two calls rather than one</strong>, which is every other broker plugin's convention
/// here and its reason: a deployment may publish to Kafka and consume from somewhere else, which
/// is exactly what a migration between transports looks like while the old broker still has a
/// backlog.
/// </para>
/// <para>
/// <strong>What they share is the options record, registered with <c>TryAdd</c>.</strong> They do
/// not share a client: a Kafka producer and a Kafka consumer are separate connections with
/// separate configuration, unlike an AMQP connection that multiplexes both.
/// </para>
/// <para>
/// <strong>Everything registered here is <see cref="IAsyncDisposable"/>, so the provider must be
/// disposed asynchronously</strong> — a consumer leaves its group on close, and a producer flushes
/// what it is holding.
/// </para>
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IEventPublisher"/> over a Kafka topic.</summary>
    /// <param name="services">The container being built.</param>
    /// <param name="bootstrapServers">The cluster, e.g. <c>localhost:9092</c>.</param>
    /// <param name="options">Which topic to publish to. Defaults to <c>flowx.events</c>.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="bootstrapServers"/> is null or blank.</exception>
    public static IServiceCollection AddFlowXKafka(
        this IServiceCollection services,
        string bootstrapServers,
        KafkaOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(bootstrapServers);

        services.TryAddSingleton(options ?? new KafkaOptions { BootstrapServers = bootstrapServers });
        services.AddSingleton<IEventPublisher>(provider =>
            new KafkaEventPublisher(provider.GetRequiredService<KafkaOptions>()));

        return services;
    }

    /// <summary>Registers <see cref="IBusConsumer"/> over Kafka consumer groups.</summary>
    /// <param name="services">The container being built.</param>
    /// <param name="bootstrapServers">The cluster, e.g. <c>localhost:9092</c>.</param>
    /// <param name="options">
    /// Which topic the groups read. <strong>Must be the same options the publisher uses</strong> —
    /// a consumer subscribed to a different topic reads nothing and says nothing. Registered with
    /// <c>TryAdd</c>, so the first of the two calls decides.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="bootstrapServers"/> is null or blank.</exception>
    public static IServiceCollection AddFlowXKafkaConsumer(
        this IServiceCollection services,
        string bootstrapServers,
        KafkaOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(bootstrapServers);

        services.TryAddSingleton(options ?? new KafkaOptions { BootstrapServers = bootstrapServers });
        services.AddSingleton<IBusConsumer>(provider =>
            new KafkaBusConsumer(provider.GetRequiredService<KafkaOptions>()));

        return services;
    }
}
