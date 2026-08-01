using FlowX.Conformance;
using StackExchange.Redis;

namespace FlowX.Redis.Tests;

/// <summary>
/// <see cref="PublisherConformance"/>'s harness over a real Redis, in its own key space.
/// </summary>
/// <remarks>
/// <para>
/// The suite needs three things and this supplies exactly those: the publisher, a way to read
/// one key's stream back in broker order, and a publisher pointed at a broker that is not there.
/// Nothing in <c>tests/FlowX.Conformance.Tests</c> was changed to accommodate Redis — the same
/// finding <c>RedisLeaseStoreConformanceTests</c> was commissioned to produce, one contract over.
/// </para>
/// <para>
/// <strong>The read goes through <c>XRANGE</c> rather than through anything this package
/// remembers.</strong> A harness that returned what it handed the publisher would assert that
/// the publisher was called; the suite's questions are all about what a consumer would find, so
/// the answers come off the server.
/// </para>
/// <para>
/// <strong>The unreachable publisher is a closed port, not a stub.</strong>
/// <c>AbortOnConnectFail = false</c> is what makes that possible: the multiplexer is returned
/// rather than throwing, and every command against it then fails the way a broker that went away
/// mid-deployment fails. A hand-written <see cref="IEventPublisher"/> that returned an
/// <see cref="Error"/> would assert that this test project can construct one.
/// </para>
/// </remarks>
internal sealed class RedisStreamBrokerUnderTest : BrokerUnderTest
{
    private readonly IConnectionMultiplexer _connection;
    private readonly List<IConnectionMultiplexer> _unreachable = [];
    private readonly RedisStreamOptions _options;
    private readonly IDatabase _database;

    private RedisStreamBrokerUnderTest(IConnectionMultiplexer connection, RedisStreamOptions options)
    {
        _connection = connection;
        _options = options;
        _database = connection.GetDatabase(options.Database);
        Publisher = new RedisStreamEventPublisher(connection, options);
    }

    /// <inheritdoc />
    public override IEventPublisher Publisher { get; }

    /// <summary>Creates a harness over a private key prefix, or refuses to pretend it did.</summary>
    /// <param name="cancellationToken">Cancels the setup.</param>
    /// <returns>The harness.</returns>
    /// <exception cref="InvalidOperationException">
    /// A Redis server was promised by the environment and is not reachable.
    /// </exception>
    public static async ValueTask<RedisStreamBrokerUnderTest> CreateAsync(CancellationToken cancellationToken)
    {
        var connection = await RedisTestServer.ConnectAsync(cancellationToken);

        return new RedisStreamBrokerUnderTest(
            connection,
            new RedisStreamOptions { KeyPrefix = "flowx_t_" + Guid.NewGuid().ToString("n") });
    }

    /// <inheritdoc />
    public override async ValueTask<IReadOnlyList<DeliveredEvent>> ReadAsync(
        string? partitionKey,
        CancellationToken cancellationToken)
    {
        var key = RedisKeys.EventStream(_options.KeyPrefix, partitionKey);

        var entries = await _database.StreamRangeAsync(key).WaitAsync(cancellationToken);

        return [.. entries.Select(Read)];
    }

    /// <inheritdoc />
    public override async ValueTask<IEventPublisher> UnreachableAsync(CancellationToken cancellationToken)
    {
        var configuration = new ConfigurationOptions
        {
            // Port 1 is reserved and nothing listens on it. AbortOnConnectFail is what makes
            // this a broker that is unreachable rather than a constructor that throws: the
            // multiplexer comes back, and the first command fails the way a broker that went
            // away between two passes of the drain fails.
            EndPoints = { { "127.0.0.1", 1 } },
            AbortOnConnectFail = false,
            ConnectTimeout = 250,
            ConnectRetry = 1,
            SyncTimeout = 250,
        };

        var dead = await ConnectionMultiplexer.ConnectAsync(configuration).WaitAsync(cancellationToken);

        _unreachable.Add(dead);

        return new RedisStreamEventPublisher(dead, _options);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        foreach (var dead in _unreachable)
        {
            dead.Dispose();
        }

        _unreachable.Clear();

        foreach (var endpoint in _connection.GetEndPoints())
        {
            var server = _connection.GetServer(endpoint);

            if (server.IsReplica)
            {
                continue;
            }

            await foreach (var key in server.KeysAsync(_database.Database, _options.KeyPrefix + "*"))
            {
                await _database.KeyDeleteAsync(key);
            }
        }

        await _connection.CloseAsync();

        _connection.Dispose();

        await base.DisposeAsync();
    }

    /// <summary>Reads one stream entry back into the shape the suite asserts on.</summary>
    /// <remarks>
    /// Field names come from <see cref="RedisKeys"/> rather than from string literals here: the
    /// entry layout is the package's published contract with a consumer, and a copy of it in a
    /// test project is a copy that can drift into agreeing with nothing.
    /// </remarks>
    private static DeliveredEvent Read(StreamEntry entry)
    {
        string? Field(string name) =>
            entry.Values.FirstOrDefault(value => value.Name == name) is { Value.IsNull: false } found
                ? (string?)found.Value
                : null;

        return new DeliveredEvent(
            Guid.Parse(Field(RedisKeys.EventIdField) ?? Guid.Empty.ToString()),
            Field(RedisKeys.TypeField) ?? string.Empty,
            Field(RedisKeys.SchemaVersionField) ?? string.Empty,
            Field(RedisKeys.PartitionKeyField),
            Field(RedisKeys.PayloadField));
    }
}
