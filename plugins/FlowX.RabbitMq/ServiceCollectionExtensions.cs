using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlowX.RabbitMq;

/// <summary>Registers this package's two RabbitMQ adapters, one call each.</summary>
/// <remarks>
/// <para>
/// <strong>Two calls rather than one, which is <c>FlowX.Redis</c>'s convention and its
/// reason.</strong> A deployment may publish to RabbitMQ and consume from somewhere else, or the
/// reverse — a migration between transports is exactly that arrangement, held for as long as the
/// old broker still has a backlog — and folding the two into one method would make a consumer
/// something a host acquires by asking for a publisher.
/// </para>
/// <para>
/// <strong>What they share is the connection, registered with <c>TryAdd</c>.</strong> A host
/// making both calls gets one AMQP connection rather than two, and whichever call came first is
/// the one whose URI is used. An AMQP connection is a TCP connection with a heartbeat, so one per
/// scope is the standard way to exhaust a broker's connection limit; the container owns exactly
/// one and disposes it with the provider.
/// </para>
/// <para>
/// <strong>Everything registered here is <see cref="IAsyncDisposable"/> and not
/// <see cref="IDisposable"/>, so the provider must be disposed asynchronously.</strong> Closing an
/// AMQP channel is a round trip to the broker, and a synchronous <c>Dispose</c> that blocked on it
/// would deadlock in exactly the place a host least wants one. <c>IHost</c> already disposes its
/// provider asynchronously; a caller building a <c>ServiceProvider</c> by hand needs
/// <c>await using</c>, and gets an <see cref="InvalidOperationException"/> naming the type if it
/// forgets.
/// </para>
/// <para>
/// <strong>Neither call registers a loop.</strong> An <see cref="IEventPublisher"/> is where a
/// staged event goes and an <see cref="IBusConsumer"/> is what a pass reads; what drives them is
/// <c>PostgresOutboxPublisher</c>'s drain and <c>FlowX.Hosting</c>'s <c>FlowBusService</c>, both
/// of which a host already has. Registering a background service here would put a hosting
/// dependency in a plugin, which is the line <c>FlowX.Redis</c> declines to cross and this
/// declines for the same reason.
/// </para>
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IEventPublisher"/> over a RabbitMQ topic exchange.</summary>
    /// <param name="services">The container being built.</param>
    /// <param name="uri">An AMQP URI, e.g. <c>amqp://guest:guest@localhost:5672/</c>.</param>
    /// <param name="options">
    /// Which exchange to publish to and what the queues are called. Defaults to the
    /// <c>flowx.events</c> exchange.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="uri"/> is null or blank.</exception>
    public static IServiceCollection AddFlowXRabbitMq(
        this IServiceCollection services,
        string uri,
        RabbitMqOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        services.TryAddSingleton(options ?? new RabbitMqOptions());
        services.TryAddSingleton(_ => new RabbitMqConnection(uri));
        services.AddSingleton<IEventPublisher>(provider => new RabbitMqEventPublisher(
            provider.GetRequiredService<RabbitMqConnection>(),
            provider.GetRequiredService<RabbitMqOptions>()));

        return services;
    }

    /// <summary>Registers <see cref="IBusConsumer"/> over RabbitMQ queues.</summary>
    /// <param name="services">The container being built.</param>
    /// <param name="uri">An AMQP URI, e.g. <c>amqp://guest:guest@localhost:5672/</c>.</param>
    /// <param name="options">
    /// Which exchange the queues are bound to and what they are called. <strong>Must be the same
    /// options the publisher uses</strong> — the exchange name is what makes one deployment's
    /// events findable and another's invisible, and a consumer bound to a different exchange
    /// reads nothing and says nothing. Registered with <c>TryAdd</c>, so the first of the two
    /// calls decides and the second cannot silently disagree with it.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="uri"/> is null or blank.</exception>
    public static IServiceCollection AddFlowXRabbitMqConsumer(
        this IServiceCollection services,
        string uri,
        RabbitMqOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        services.TryAddSingleton(options ?? new RabbitMqOptions());
        services.TryAddSingleton(_ => new RabbitMqConnection(uri));
        services.AddSingleton<IBusConsumer>(provider => new RabbitMqBusConsumer(
            provider.GetRequiredService<RabbitMqConnection>(),
            provider.GetRequiredService<RabbitMqOptions>()));

        return services;
    }
}
