using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace FlowX.Redis;

/// <summary>Registers this package's two Redis adapters, one call each.</summary>
/// <remarks>
/// <para>
/// <strong>One service type per call, and deliberately only one.</strong>
/// <see cref="AddFlowXRedis"/> registers <see cref="ILeaseStore"/> and nothing else — no
/// <see cref="IFlowJournal"/>, no <see cref="IRecoveryIndex"/>, and no
/// <see cref="IEventPublisher"/>. A host wiring it alone runs no durable flow; it wires a
/// journal too, and the arrangement <see cref="ILeaseStore"/>'s own remarks describe — leases
/// in Redis, the journal in PostgreSQL, sharing no transaction — is two
/// <c>Add…</c> calls rather than one.
/// </para>
/// <para>
/// <strong><see cref="AddFlowXRedisStreams"/> is a third such call, for the same reason.</strong>
/// A deployment may take its leases from Redis and its broker from somewhere else, or the
/// reverse, and folding the two into one method would make a lease store something a host
/// acquires by asking for a publisher. What they do share is the multiplexer: both calls
/// register it with <c>TryAdd</c>, so a host that makes both gets one connection rather than
/// two, and whichever call came first is the one whose configuration string is used.
/// </para>
/// <para>
/// <strong>The multiplexer is a singleton because it must be.</strong>
/// StackExchange.Redis multiplexes every command in a process over one connection, and
/// creating one per scope is the standard way to exhaust a Redis server's connection limit.
/// The container therefore owns exactly one and disposes it with the provider.
/// </para>
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers <see cref="ILeaseStore"/> over one Redis connection.</summary>
    /// <param name="services">The container being built.</param>
    /// <param name="configuration">A StackExchange.Redis configuration string.</param>
    /// <param name="options">Where in the key space the leases live.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="configuration"/> is null or blank.</exception>
    public static IServiceCollection AddFlowXRedis(
        this IServiceCollection services,
        string configuration,
        RedisLeaseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);

        var settings = options ?? new RedisLeaseOptions();

        services.AddSingleton(settings);
        services.TryAddSingleton<IConnectionMultiplexer>(
            _ => ConnectionMultiplexer.Connect(configuration));
        services.AddSingleton<ILeaseStore>(provider => new RedisLeaseStore(
            provider.GetRequiredService<IConnectionMultiplexer>(),
            provider.GetRequiredService<RedisLeaseOptions>()));

        return services;
    }

    /// <summary>Registers <see cref="IEventPublisher"/> over Redis Streams.</summary>
    /// <param name="services">The container being built.</param>
    /// <param name="configuration">A StackExchange.Redis configuration string.</param>
    /// <param name="options">Where in the key space the streams live, and how long they are kept.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="configuration"/> is null or blank.</exception>
    /// <remarks>
    /// <para>
    /// <strong>This registers the seam, not the drain.</strong> An <see cref="IEventPublisher"/>
    /// is where a staged event goes; what reads the outbox and offers it one is the journal
    /// adapter's business — <c>PostgresOutboxPublisher</c> takes one of these in its constructor
    /// and a host runs its loop. Registering a background service here would put a hosting
    /// dependency in a plugin and would give a host a drain over whichever journal happened to be
    /// registered, which is a decision this package has no standing to take.
    /// </para>
    /// <para>
    /// The multiplexer is a singleton because it must be: StackExchange.Redis multiplexes every
    /// command in a process over one connection, and creating one per scope is the standard way
    /// to exhaust a Redis server's connection limit.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddFlowXRedisStreams(
        this IServiceCollection services,
        string configuration,
        RedisStreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);

        var settings = options ?? new RedisStreamOptions();

        services.AddSingleton(settings);
        services.TryAddSingleton<IConnectionMultiplexer>(
            _ => ConnectionMultiplexer.Connect(configuration));
        services.AddSingleton<IEventPublisher>(provider => new RedisStreamEventPublisher(
            provider.GetRequiredService<IConnectionMultiplexer>(),
            provider.GetRequiredService<RedisStreamOptions>()));

        return services;
    }

    /// <summary>Registers <see cref="IBusConsumer"/> over Redis Streams.</summary>
    /// <param name="services">The container being built.</param>
    /// <param name="configuration">A StackExchange.Redis configuration string.</param>
    /// <param name="options">
    /// Where in the key space the streams live. <strong>Must be the same options the publisher
    /// writing them uses</strong> — the key prefix is what makes one deployment's events findable
    /// and another's invisible, and a consumer pointed at a different prefix reads nothing and
    /// says nothing.
    /// </param>
    /// <param name="consumerName">This node's name inside the consumer group.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="configuration"/> is null or blank.</exception>
    /// <remarks>
    /// <para>
    /// <strong>A fourth call, for the reason there are three.</strong> A deployment may take its
    /// leases from Redis and its broker from somewhere else, publish to Redis and consume from
    /// somewhere else, or any other combination. What they share is the multiplexer, registered
    /// with <c>TryAdd</c> so a host making several calls gets one connection.
    /// </para>
    /// <para>
    /// <strong>This registers the consumer, not the loop.</strong> What drives it is
    /// <c>FlowX.Hosting</c>'s <c>FlowBusService</c>, which <c>AddFlowX</c> already registers and
    /// which does nothing until both an <see cref="IBusConsumer"/> and a subscription exist.
    /// Registering a background service here would put a hosting dependency in a plugin — the
    /// same line <see cref="AddFlowXRedisStreams"/> declines to cross.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddFlowXRedisStreamConsumer(
        this IServiceCollection services,
        string configuration,
        RedisStreamOptions? options = null,
        string? consumerName = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);

        var settings = options ?? new RedisStreamOptions();

        services.TryAddSingleton(settings);
        services.TryAddSingleton<IConnectionMultiplexer>(
            _ => ConnectionMultiplexer.Connect(configuration));
        services.AddSingleton<IBusConsumer>(provider => new RedisStreamBusConsumer(
            provider.GetRequiredService<IConnectionMultiplexer>(),
            provider.GetRequiredService<RedisStreamOptions>(),
            consumerName));

        return services;
    }
}
