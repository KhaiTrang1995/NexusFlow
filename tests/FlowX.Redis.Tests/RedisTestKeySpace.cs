using StackExchange.Redis;

namespace FlowX.Redis.Tests;

/// <summary>One private key prefix, its own connection, and the lease store over it.</summary>
/// <remarks>
/// <para>
/// <strong>A prefix per test, not a <c>FLUSHDB</c> per test.</strong> The conformance suite
/// requires "a fresh, empty store … no state may survive between them". Flushing the database
/// would satisfy that and would also delete whatever else is in the Redis a developer happens
/// to have running, which is the kind of test that gets a suite disinvited from a machine. A
/// prefix nobody else uses satisfies it exactly as well, costs nothing, and lets several of
/// these exist at once — which the conformance suite does, because it creates one per test
/// case and disposes them together at the end of the class.
/// </para>
/// <para>
/// This is the shape <c>PostgresTestSchema</c> has, with the isolation mechanism swapped for
/// the one Redis offers. The three-way skip decision is <see cref="RedisTestServer"/>'s and is
/// inherited rather than re-implemented.
/// </para>
/// </remarks>
internal sealed class RedisTestKeySpace : IAsyncDisposable
{
    private readonly IConnectionMultiplexer _connection;

    private RedisTestKeySpace(IConnectionMultiplexer connection, RedisLeaseOptions options)
    {
        _connection = connection;
        Options = options;
        Database = connection.GetDatabase(options.Database);
        Leases = new RedisLeaseStore(connection, options);
    }

    /// <summary>The prefix this key space occupies.</summary>
    public RedisLeaseOptions Options { get; }

    /// <summary>The raw database, for assertions the adapter's own API cannot make.</summary>
    public IDatabase Database { get; }

    /// <summary>The lease store under test.</summary>
    public RedisLeaseStore Leases { get; }

    /// <summary>Creates a key space, or refuses to pretend it did.</summary>
    /// <param name="cancellationToken">Cancels the setup.</param>
    /// <returns>The prepared key space.</returns>
    /// <exception cref="InvalidOperationException">
    /// A Redis server was promised by the environment and is not reachable.
    /// </exception>
    public static async ValueTask<RedisTestKeySpace> CreateAsync(CancellationToken cancellationToken)
    {
        var connection = await RedisTestServer.ConnectAsync(cancellationToken);

        return new RedisTestKeySpace(
            connection,
            new RedisLeaseOptions { KeyPrefix = "flowx_t_" + Guid.NewGuid().ToString("n") });
    }

    /// <summary>The key the adapter itself would use for an instance.</summary>
    /// <param name="instanceId">The instance.</param>
    /// <returns>The key, built by the package rather than copied into this project.</returns>
    public RedisKey LeaseKey(Guid instanceId) => RedisKeys.Lease(Options.KeyPrefix, instanceId);

    /// <summary>
    /// How long Redis will keep the lease key, or null when it will keep it indefinitely.
    /// </summary>
    /// <remarks>
    /// The assertion the whole design turns on is made through this: the key that holds the
    /// fencing-token counter must carry no expiry, because Redis deleting it is Redis
    /// resetting the counter.
    /// </remarks>
    /// <param name="instanceId">The instance.</param>
    /// <returns>The remaining time to live, or null for none.</returns>
    public Task<TimeSpan?> TimeToLiveAsync(Guid instanceId) =>
        Database.KeyTimeToLiveAsync(LeaseKey(instanceId));

    /// <summary>Whether the lease key is still in Redis at all.</summary>
    /// <param name="instanceId">The instance.</param>
    /// <returns>Whether the key exists.</returns>
    public Task<bool> KeyExistsAsync(Guid instanceId) => Database.KeyExistsAsync(LeaseKey(instanceId));

    /// <summary>Deletes everything this key space wrote and closes the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var endpoint in _connection.GetEndPoints())
        {
            var server = _connection.GetServer(endpoint);

            if (server.IsReplica)
            {
                continue;
            }

            await foreach (var key in server.KeysAsync(Database.Database, Options.KeyPrefix + "*"))
            {
                await Database.KeyDeleteAsync(key);
            }
        }

        await _connection.CloseAsync();

        _connection.Dispose();
    }
}
