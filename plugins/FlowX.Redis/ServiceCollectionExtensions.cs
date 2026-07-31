using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace FlowX.Redis;

/// <summary>Registers the Redis lease store.</summary>
/// <remarks>
/// <para>
/// <strong>One service type, and deliberately only one.</strong> This package implements
/// <see cref="ILeaseStore"/> and nothing else — no <see cref="IFlowJournal"/>, no
/// <see cref="IRecoveryIndex"/>. A host wiring this alone runs no durable flow; it wires a
/// journal too, and the arrangement <see cref="ILeaseStore"/>'s own remarks describe — leases
/// in Redis, the journal in PostgreSQL, sharing no transaction — is two
/// <c>Add…</c> calls rather than one.
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
        services.AddSingleton<IConnectionMultiplexer>(
            _ => ConnectionMultiplexer.Connect(configuration));
        services.AddSingleton<ILeaseStore>(provider => new RedisLeaseStore(
            provider.GetRequiredService<IConnectionMultiplexer>(),
            provider.GetRequiredService<RedisLeaseOptions>()));

        return services;
    }
}
