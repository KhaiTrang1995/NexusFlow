using FlowX.Conformance;
using StackExchange.Redis;
using Xunit;

namespace FlowX.Redis.Tests;

/// <summary>Runs the whole rate-limiter suite against Redis.</summary>
/// <remarks>
/// <para>
/// <strong>The suite's central assertion is one this derivation makes real rather than
/// simulates.</strong> <c>TwoClientsOverOneServerShareOneBudget</c> is handed two multiplexers,
/// opened separately, sharing a TCP connection with each other in no sense whatsoever. That is
/// the relationship two pods have, and it is the only thing separating a rate limiter from a
/// counter in a field.
/// </para>
/// <para>
/// Nothing in <c>tests/FlowX.Conformance.Tests</c> was changed to make this pass, which is the
/// result that matters: had the suite needed an edit to accept Redis, the suite would have been
/// written against whichever store came first.
/// </para>
/// </remarks>
public sealed class RedisRateLimiterConformanceTests : RateLimiterConformance, IAsyncLifetime
{
    private readonly List<RateLimiterUnderTest> _harnesses = [];

    /// <inheritdoc />
    protected override async ValueTask<RateLimiterUnderTest> CreateAsync()
    {
        var harness = await RedisPolicyKeySpace.CreateAsync(Cancellation);

        _harnesses.Add(harness);

        return harness;
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var harness in _harnesses)
        {
            await harness.DisposeAsync();
        }

        _harnesses.Clear();
    }
}

/// <summary>Runs the whole idempotency suite against Redis.</summary>
public sealed class RedisIdempotencyConformanceTests : IdempotencyStoreConformance, IAsyncLifetime
{
    private readonly List<IdempotencyStoreUnderTest> _harnesses = [];

    /// <inheritdoc />
    protected override async ValueTask<IdempotencyStoreUnderTest> CreateAsync()
    {
        var harness = await RedisIdempotencyKeySpace.CreateAsync(Cancellation);

        _harnesses.Add(harness);

        return harness;
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var harness in _harnesses)
        {
            await harness.DisposeAsync();
        }

        _harnesses.Clear();
    }
}

/// <summary>
/// Two connections, one key prefix, and a third connection pointed nowhere.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two multiplexers rather than two stores over one multiplexer</strong>, and the
/// distinction is the whole of what the suite's shared-budget assertion tests. Two stores over
/// one connection would still be one client: they would share a socket, a command queue and any
/// caching the client does. Two multiplexers share nothing above TCP, which is what two pods
/// share.
/// </para>
/// <para>
/// The prefix isolation is <c>RedisTestKeySpace</c>'s, for its reason — a <c>FLUSHDB</c> per test
/// is the kind of test that gets a suite disinvited from a developer's machine.
/// </para>
/// </remarks>
internal sealed class RedisPolicyKeySpace : RateLimiterUnderTest
{
    private readonly RedisPolicyConnections _connections;

    private RedisPolicyKeySpace(RedisPolicyConnections connections)
    {
        _connections = connections;
        Limiter = new RedisRateLimiterStore(connections.First, connections.Options);
        SecondClient = new RedisRateLimiterStore(connections.Second, connections.Options);
    }

    /// <inheritdoc />
    public override IRateLimiterStore Limiter { get; }

    /// <inheritdoc />
    public override IRateLimiterStore SecondClient { get; }

    /// <summary>Opens two connections over a prefix nobody else uses.</summary>
    /// <param name="cancellationToken">Cancels the setup.</param>
    /// <returns>The prepared harness.</returns>
    public static async ValueTask<RedisPolicyKeySpace> CreateAsync(CancellationToken cancellationToken) =>
        new(await RedisPolicyConnections.OpenAsync(cancellationToken));

    /// <inheritdoc />
    public override async ValueTask<IRateLimiterStore> UnreachableAsync(CancellationToken cancellationToken) =>
        new RedisRateLimiterStore(
            await _connections.UnreachableAsync(cancellationToken),
            _connections.Options);

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await _connections.DisposeAsync();

        await base.DisposeAsync();
    }
}

/// <summary>The same harness for the idempotency store.</summary>
internal sealed class RedisIdempotencyKeySpace : IdempotencyStoreUnderTest
{
    private readonly RedisPolicyConnections _connections;

    private RedisIdempotencyKeySpace(RedisPolicyConnections connections)
    {
        _connections = connections;
        Store = new RedisIdempotencyStore(connections.First, connections.Options);
        SecondClient = new RedisIdempotencyStore(connections.Second, connections.Options);
    }

    /// <inheritdoc />
    public override IIdempotencyStore Store { get; }

    /// <inheritdoc />
    public override IIdempotencyStore SecondClient { get; }

    /// <summary>Opens two connections over a prefix nobody else uses.</summary>
    /// <param name="cancellationToken">Cancels the setup.</param>
    /// <returns>The prepared harness.</returns>
    public static async ValueTask<RedisIdempotencyKeySpace> CreateAsync(CancellationToken cancellationToken) =>
        new(await RedisPolicyConnections.OpenAsync(cancellationToken));

    /// <inheritdoc />
    public override async ValueTask<IIdempotencyStore> UnreachableAsync(CancellationToken cancellationToken) =>
        new RedisIdempotencyStore(
            await _connections.UnreachableAsync(cancellationToken),
            _connections.Options);

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await _connections.DisposeAsync();

        await base.DisposeAsync();
    }
}

/// <summary>The connections both policy harnesses need, opened and cleaned up once.</summary>
internal sealed class RedisPolicyConnections : IAsyncDisposable
{
    private readonly List<IConnectionMultiplexer> _opened = [];

    private RedisPolicyConnections(
        IConnectionMultiplexer first,
        IConnectionMultiplexer second,
        RedisPolicyOptions options)
    {
        First = first;
        Second = second;
        Options = options;

        _opened.Add(first);
        _opened.Add(second);
    }

    public IConnectionMultiplexer First { get; }

    public IConnectionMultiplexer Second { get; }

    public RedisPolicyOptions Options { get; }

    public static async ValueTask<RedisPolicyConnections> OpenAsync(CancellationToken cancellationToken)
    {
        var first = await RedisTestServer.ConnectAsync(cancellationToken);
        var second = await RedisTestServer.ConnectAsync(cancellationToken);

        return new RedisPolicyConnections(
            first,
            second,
            new RedisPolicyOptions { KeyPrefix = "flowx_t_" + Guid.NewGuid().ToString("n") });
    }

    /// <summary>
    /// A connection to a port nothing is listening on.
    /// </summary>
    /// <remarks>
    /// <c>AbortOnConnectFail = false</c>, so the multiplexer is constructed rather than throwing
    /// during setup — the suite is asserting what the <em>store</em> does when the server is
    /// absent, and a harness that could not be built would have proved nothing about that.
    /// </remarks>
    public async ValueTask<IConnectionMultiplexer> UnreachableAsync(CancellationToken cancellationToken)
    {
        var configuration = ConfigurationOptions.Parse("127.0.0.1:1");

        configuration.AbortOnConnectFail = false;
        configuration.ConnectTimeout = 200;
        configuration.SyncTimeout = 200;
        configuration.ConnectRetry = 0;

        var connection = await ConnectionMultiplexer.ConnectAsync(configuration).WaitAsync(cancellationToken);

        _opened.Add(connection);

        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        var database = First.GetDatabase(Options.Database);

        // GetServers() rather than a walk over GetEndPoints(): the endpoint list a multiplexer
        // reports includes the one it was configured with, which is not always the one it
        // resolved to, and GetServer refuses an endpoint it never connected. The lease harness
        // walks endpoints and gets away with it on a single-endpoint connection; two
        // multiplexers over one host do not.
        foreach (var server in First.GetServers())
        {
            if (server.IsReplica || !server.IsConnected)
            {
                continue;
            }

            await foreach (var key in server.KeysAsync(database.Database, Options.KeyPrefix + "*"))
            {
                await database.KeyDeleteAsync(key);
            }
        }

        foreach (var connection in _opened)
        {
            await connection.CloseAsync();

            connection.Dispose();
        }

        _opened.Clear();
    }
}
