using FlowX.Conformance;
using StackExchange.Redis;
using Xunit;

namespace FlowX.Redis.Tests;

/// <summary>Runs the whole lease suite against Redis.</summary>
/// <remarks>
/// <para>
/// <strong>This class is the package's deliverable as much as the adapter is.</strong>
/// <c>LeaseStoreConformance</c> is inherited from another assembly, unmodified, exactly as
/// <c>PostgresLeaseStoreConformanceTests</c> inherits it and exactly as
/// <c>docs/17-Plugin-System.md §5</c> describes a third party claiming conformance. Until this
/// project existed the suite had been derived once, which shows that deriving works and shows
/// nothing about whether the assertions are store-independent — a suite with one
/// implementation is a suite shaped like that implementation, and nobody can tell. ADR-0006's
/// claim is that exactly-one-writer is a property of two primitives rather than of any
/// particular database, and this is the first evidence either way.
/// </para>
/// <para>
/// Nothing in <c>tests/FlowX.Conformance.Tests</c> was changed to make this pass. That is the
/// result the package was commissioned to produce: had the suite needed an edit to accept
/// Redis, the suite would have been written against PostgreSQL and WP-51 would have failed.
/// </para>
/// <para>
/// The expiry assertions wait out a real TTL against Redis's own clock, which is the only
/// clock this store consults — every decision it makes is taken inside a Lua script against
/// <c>TIME</c> on the server. A fake clock would have made them cheaper and would have stopped
/// them being about expiry.
/// </para>
/// </remarks>
public sealed class RedisLeaseStoreConformanceTests : LeaseStoreConformance, IAsyncLifetime
{
    private readonly List<RedisTestKeySpace> _keySpaces = [];

    /// <inheritdoc />
    protected override async ValueTask<ILeaseStore> CreateStoreAsync()
    {
        var keySpace = await RedisTestKeySpace.CreateAsync(Cancellation);

        _keySpaces.Add(keySpace);

        return keySpace.Leases;
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var keySpace in _keySpaces)
        {
            await keySpace.DisposeAsync();
        }

        _keySpaces.Clear();
    }
}

/// <summary>
/// Runs the whole result-cache suite against Redis.
/// </summary>
/// <remarks>
/// <para>
/// The first of the two implementations <c>ResultCacheConformance</c> is held to, and the suite
/// is inherited from another assembly unmodified — the arrangement
/// <c>docs/17-Plugin-System.md §5</c> describes for a third party claiming conformance, and the
/// one <c>RedisLeaseStoreConformanceTests</c> already demonstrates for <c>ILeaseStore</c>.
/// </para>
/// <para>
/// <strong>The expiry assertions are answered by Redis's own key TTL</strong>, which is the one
/// place this adapter differs sharply from <c>RedisLeaseStore</c>: that store deliberately
/// keeps its expiry as a value in a hash, because Redis deleting a lease key would reset a
/// fencing-token counter that must never restart (ADR-0019). A cache entry has nothing to lose
/// when Redis deletes it, which is why this one is a plain <c>SET … EX</c> and why its key
/// space may live under an eviction policy the lease store's may not.
/// </para>
/// <para>
/// Isolated by key prefix rather than by <c>FLUSHDB</c>, for <c>RedisTestKeySpace</c>'s reason:
/// a prefix nobody else uses satisfies "a fresh, empty store" exactly as well and does not
/// delete whatever else is in the Redis a developer happens to have running.
/// </para>
/// </remarks>
public sealed class RedisResultCacheConformanceTests : ResultCacheConformance, IAsyncLifetime
{
    private readonly List<IConnectionMultiplexer> _connections = [];

    /// <inheritdoc />
    protected override async ValueTask<IResultCache> CreateCacheAsync()
    {
        var connection = await RedisTestServer.ConnectAsync(Cancellation);

        _connections.Add(connection);

        return new RedisResultCache(
            connection,
            new RedisCacheOptions { KeyPrefix = "flowx_t_" + Guid.NewGuid().ToString("n") });
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            // Every key this class wrote carries a TTL of at most five minutes, so there is
            // nothing to sweep that Redis will not sweep itself — the one place a cache is
            // easier to clean up after than a lease store, whose keys deliberately have none.
            await connection.CloseAsync();

            connection.Dispose();
        }

        _connections.Clear();
    }
}
