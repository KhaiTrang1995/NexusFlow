using FlowX.Conformance;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace FlowX.Redis.Tests;

/// <summary>
/// What <see cref="PublisherConformance"/> cannot ask, because it is true of Redis rather than of
/// <see cref="IEventPublisher"/>.
/// </summary>
/// <remarks>
/// <para>
/// The suite holds this adapter to the contract and deliberately knows nothing about streams. Two
/// things are left over and both are named in
/// <see href="../../docs/adr/ADR-0018-outbox-publication-and-ordering.md">ADR-0018</see>'s list
/// of what a recording double could not prove: <strong>broker-side partitioning</strong> — that
/// per-key ordering is a property of the key space rather than of a loop in this process — and
/// <strong>what a real client does with a batch it half accepted</strong>.
/// </para>
/// <para>
/// The refusal is provoked with <c>WRONGTYPE</c>: a stream key occupied by a string, which Redis
/// refuses <c>XADD</c> against. It is a real server error travelling the real path, which a
/// hand-thrown exception would not be, and it is deterministic, which a torn-down connection
/// would not be.
/// </para>
/// </remarks>
public sealed class RedisStreamEventPublisherTests : IAsyncLifetime
{
    private readonly List<RedisStreamTestSpace> _spaces = [];

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Each partition key gets its own stream, and that is where the order lives.</summary>
    /// <remarks>
    /// ADR-0018 decision 3 offers per-key ordering and refuses a global one. A Redis stream is
    /// totally ordered, so a stream per key <em>is</em> that pair of statements — and a
    /// publisher that wrote everything to one key would satisfy the conformance suite while
    /// quietly offering the global order the record declines and serialising every key through
    /// one append point. Nothing portable can tell those two apart; this can.
    /// </remarks>
    [Fact]
    public async Task EachPartitionKeyGetsItsOwnStream()
    {
        var space = await CreateAsync();

        IReadOnlyList<OutboxRecord> batch =
        [
            Event("customer-1", "order.placed"),
            Event("customer-2", "order.placed"),
            Event("customer-1", "order.paid"),
            Event(null, "audit.noted"),
        ];

        var published = await space.Publisher.PublishAsync(batch, Cancellation);

        published.IsSuccess.ShouldBeTrue(published.IsFailure ? published.Error.ToString() : null);
        published.Value.ShouldBe(4);

        (await space.LengthAsync("customer-1")).ShouldBe(2, "two events on that key, one stream.");
        (await space.LengthAsync("customer-2")).ShouldBe(1, "and a different key is a different stream.");
        (await space.LengthAsync(null)).ShouldBe(1, "unkeyed events have a stream of their own.");

        space.StreamKey("customer-1").ToString().ShouldBe(
            space.Prefix + ":{customer-1}:events",
            "the cluster hash tag wraps the partition key, so one key's stream is one slot " +
            "and a consumer reading that key never spans two.");
    }

    /// <summary>
    /// A batch the broker half accepts is reported as the prefix that arrived.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The case ADR-0018 records as unproved in as many words: "what a real client does with a
    /// half-accepted batch". The second event's stream key is occupied by a string, so Redis
    /// refuses that one <c>XADD</c> and takes the others. The publisher stops there and reports
    /// <c>1</c>, which leaves the event that did arrive marked and the two behind it pending.
    /// </para>
    /// <para>
    /// <strong>It stops rather than skipping, and that is the contract rather than a
    /// limitation.</strong> Continuing past the refusal would publish the third event and report
    /// a count that includes the second, or report a set — and a set is not a statement a caller
    /// can act on, which is the whole reason decision 1 chose a prefix.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ABatchTheBrokerHalfAcceptsIsReportedAsThePrefixThatArrived()
    {
        var space = await CreateAsync();

        await space.OccupyAsync("customer-2");

        IReadOnlyList<OutboxRecord> batch =
        [
            Event("customer-1", "order.placed"),
            Event("customer-2", "order.placed"),
            Event("customer-1", "order.paid"),
        ];

        var published = await space.Publisher.PublishAsync(batch, Cancellation);

        published.IsSuccess.ShouldBeTrue(
            "a prefix that reached the broker is a success carrying a count, not a failure. " +
            "Reporting an Error here would leave the first event pending and redeliver it, " +
            "which is permitted but is not what the contract prefers.");

        published.Value.ShouldBe(1, "the first arrived; the second was refused and stopped the batch.");

        (await space.LengthAsync("customer-1")).ShouldBe(
            1,
            "and order.paid was not published past its refused sibling. Publishing it would " +
            "have put it on the wire in front of an event the caller is about to offer again.");
    }

    /// <summary>
    /// A refusal that takes nothing is an error value carrying the broker's own reason.
    /// </summary>
    /// <remarks>
    /// Retryable, because the row is still pending and the outbox will offer it again. That is
    /// the right category even for a refusal that will never succeed — a permanently refused
    /// event blocking its batch for ever is the trade-off ADR-0018 states and accepts, and there
    /// is no dead-letter path to divert it to.
    /// </remarks>
    [Fact]
    public async Task ARefusalThatTookNothingIsAnErrorCarryingTheBrokersReason()
    {
        var space = await CreateAsync();

        await space.OccupyAsync("customer-1");

        var published = await space.Publisher.PublishAsync(
            [Event("customer-1", "order.placed")], Cancellation);

        published.IsFailure.ShouldBeTrue("nothing reached the broker, so there is no prefix to report.");

        published.Error.Code.ShouldBe(RedisStreamEventPublisher.PublishFailedCode);
        published.Error.Category.ShouldBe(ErrorCategory.Unavailable);

        published.Error.Message.ShouldContain(
            "WRONGTYPE",
            Case.Sensitive,
            "the client's own message travels in the error, because an operator reading a log " +
            "needs the reason and a caller only needs the code.");
    }

    /// <summary>A configured bound keeps a stream from growing without limit.</summary>
    /// <remarks>
    /// Approximate by construction — <c>MAXLEN ~ n</c> — so the assertion is that the stream is
    /// bounded rather than that it holds exactly <c>n</c>. An exact bound makes <c>XADD</c>
    /// O(n) in the entries it removes and buys nothing for a queue whose purpose is to be
    /// drained.
    /// </remarks>
    [Fact]
    public async Task AConfiguredBoundKeepsTheStreamFromGrowingWithoutLimit()
    {
        var space = await CreateAsync(new RedisStreamOptions { MaxStreamLength = 2 });

        for (var i = 0; i < 40; i++)
        {
            var published = await space.Publisher.PublishAsync(
                [Event("customer-1", $"order.event-{i}")], Cancellation);

            published.IsSuccess.ShouldBeTrue(published.IsFailure ? published.Error.ToString() : null);
        }

        (await space.LengthAsync("customer-1")).ShouldBeLessThan(
            40,
            "the stream is trimmed. Left unbounded it holds every event ever published to that " +
            "key, in memory, for ever — which is why the option exists and why it is null by " +
            "default rather than set to a number nobody chose.");
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var space in _spaces)
        {
            await space.DisposeAsync();
        }

        _spaces.Clear();
    }

    private async ValueTask<RedisStreamTestSpace> CreateAsync(RedisStreamOptions? options = null)
    {
        var space = await RedisStreamTestSpace.CreateAsync(options, Cancellation);

        _spaces.Add(space);

        return space;
    }

    private static OutboxRecord Event(string? partitionKey, string type) => new()
    {
        EventId = Guid.NewGuid(),
        InstanceId = Guid.NewGuid(),
        Type = type,
        SchemaVersion = "1.0.0",
        PartitionKey = partitionKey,
        PayloadJson = $$"""{"type":"{{type}}"}""",
    };
}

/// <summary>One private stream prefix, its own connection, and the publisher over it.</summary>
/// <remarks>
/// The same arrangement as <c>RedisTestKeySpace</c>, for the same reason: a prefix nobody else
/// uses isolates a test exactly as well as a <c>FLUSHDB</c> and does not delete whatever else is
/// in the Redis a developer happens to have running.
/// </remarks>
internal sealed class RedisStreamTestSpace : IAsyncDisposable
{
    private readonly IConnectionMultiplexer _connection;
    private readonly RedisStreamOptions _options;
    private readonly IDatabase _database;

    private RedisStreamTestSpace(IConnectionMultiplexer connection, RedisStreamOptions options)
    {
        _connection = connection;
        _options = options;
        _database = connection.GetDatabase(options.Database);
        Publisher = new RedisStreamEventPublisher(connection, options);
    }

    /// <summary>The publisher under test.</summary>
    public RedisStreamEventPublisher Publisher { get; }

    /// <summary>The prefix this space occupies.</summary>
    public string Prefix => _options.KeyPrefix;

    /// <summary>Creates a space, or refuses to pretend it did.</summary>
    /// <param name="options">The options to use, with this space's prefix applied.</param>
    /// <param name="cancellationToken">Cancels the setup.</param>
    /// <returns>The prepared space.</returns>
    /// <exception cref="InvalidOperationException">
    /// A Redis server was promised by the environment and is not reachable.
    /// </exception>
    public static async ValueTask<RedisStreamTestSpace> CreateAsync(
        RedisStreamOptions? options,
        CancellationToken cancellationToken)
    {
        var connection = await RedisTestServer.ConnectAsync(cancellationToken);

        return new RedisStreamTestSpace(
            connection,
            (options ?? new RedisStreamOptions()) with
            {
                KeyPrefix = "flowx_t_" + Guid.NewGuid().ToString("n"),
            });
    }

    /// <summary>The stream key the package itself would use for a partition key.</summary>
    /// <param name="partitionKey">The key, or null for the unkeyed stream.</param>
    /// <returns>The key, built by the package rather than copied into this project.</returns>
    public RedisKey StreamKey(string? partitionKey) => RedisKeys.EventStream(Prefix, partitionKey);

    /// <summary>How many entries a key's stream holds.</summary>
    /// <param name="partitionKey">The key, or null for the unkeyed stream.</param>
    /// <returns>The entry count.</returns>
    public Task<long> LengthAsync(string? partitionKey) => _database.StreamLengthAsync(StreamKey(partitionKey));

    /// <summary>
    /// Puts a string where a key's stream would go, so Redis refuses to append to it.
    /// </summary>
    /// <param name="partitionKey">The key to block.</param>
    /// <returns>A task that completes when the key is occupied.</returns>
    /// <remarks>
    /// <c>WRONGTYPE</c> is a real server error on the real path — the command is written, sent
    /// and refused — which is what makes it worth more than a client the test broke on purpose.
    /// </remarks>
    public Task OccupyAsync(string? partitionKey) =>
        _database.StringSetAsync(StreamKey(partitionKey), "not a stream");

    /// <summary>Deletes everything this space wrote and closes the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var endpoint in _connection.GetEndPoints())
        {
            var server = _connection.GetServer(endpoint);

            if (server.IsReplica)
            {
                continue;
            }

            await foreach (var key in server.KeysAsync(_database.Database, Prefix + "*"))
            {
                await _database.KeyDeleteAsync(key);
            }
        }

        await _connection.CloseAsync();

        _connection.Dispose();
    }
}
