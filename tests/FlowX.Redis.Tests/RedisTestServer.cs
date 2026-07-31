using StackExchange.Redis;
using Xunit;

namespace FlowX.Redis.Tests;

/// <summary>
/// Decides, once, whether there is a Redis server to test against — and records why when
/// there is not.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The three-way discipline is <c>PostgresTestDatabase</c>'s, deliberately mirrored
/// rather than reinvented.</strong> That class carries the reasoning in full; what follows is
/// the short form and the two places Redis differs.
/// </para>
/// <para>
/// <strong>No server configured: every test skips, and each skip carries this class's
/// reason.</strong> A run with no Redis reads as a run with no Redis rather than as a green
/// suite.
/// </para>
/// <para>
/// <strong>A server configured but unreachable: every test fails.</strong> That is the case
/// worth being loud about, because it is the one that looks like success in CI — somebody
/// wired a service container, the variable is set, the container did not come up, and a skip
/// would report the whole lease suite as green against a store that was never reached.
/// <see cref="Unreachable"/> is thrown rather than skipped for exactly that reason.
/// </para>
/// <para>
/// <strong>The decision itself is asserted.</strong> <see cref="RedisAvailabilityTests"/> runs
/// whether or not a server exists and checks that this class explained itself, so the skip
/// path cannot quietly become empty.
/// </para>
/// <para>
/// <strong>Redis differs from PostgreSQL in one way that matters to this harness.</strong>
/// There is no schema, so tests cannot be isolated by creating one. They are isolated by key
/// instead: every store gets a prefix nobody else uses, which the conformance suite's "a
/// fresh, empty store, called once per test" requirement is satisfied by exactly as well and
/// far more cheaply. <see cref="FlushAsync"/> exists for the tests that need to prove a key
/// is absent rather than merely unread.
/// </para>
/// </remarks>
internal static class RedisTestServer
{
    /// <summary>The variable that supplies a connection string.</summary>
    public const string ConnectionVariable = "FLOWX_REDIS_CONNECTION";

    private static readonly Lazy<Probe> Result = new(Run, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The configured connection string, or null when none was supplied.</summary>
    public static string? ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionVariable) is { Length: > 0 } value
            ? value
            : null;

    /// <summary>
    /// Whether the environment promised a server, whether or not one answered.
    /// </summary>
    /// <remarks>
    /// The distinction this whole class turns on. "Nobody asked for a Redis" is a reason to
    /// skip; "somebody asked for a Redis and there isn't one" is a defect in the run.
    /// </remarks>
    public static bool IsPromised => ConnectionString is not null;

    /// <summary>Whether a server answered.</summary>
    public static bool IsAvailable => Result.Value.Available;

    /// <summary>Why, in a sentence a reader can act on. Never empty.</summary>
    public static string Reason => Result.Value.Reason;

    /// <summary>The exception a test throws when a promised server is not there.</summary>
    /// <returns>The exception to throw.</returns>
    public static InvalidOperationException Unreachable() => new(
        $"{ConnectionVariable} is set, so this run promised a Redis server, and none " +
        $"answered. This is a failure rather than a skip on purpose: a skip here would " +
        $"report the whole lease conformance suite as green against a store that was never " +
        $"reached. {Reason}");

    /// <summary>
    /// A connection to the configured server, or the skip or failure that stands in for it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the connect.</param>
    /// <returns>The multiplexer. The caller owns it.</returns>
    /// <exception cref="InvalidOperationException">
    /// A server was promised by the environment and is not reachable.
    /// </exception>
    public static async ValueTask<IConnectionMultiplexer> ConnectAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            if (IsPromised)
            {
                throw Unreachable();
            }

            Assert.Skip(Reason);
        }

        var configuration = ConfigurationOptions.Parse(ConnectionString!);

        configuration.AllowAdmin = true;

        return await ConnectionMultiplexer.ConnectAsync(configuration).WaitAsync(cancellationToken);
    }

    private static Probe Run()
    {
        var connectionString = ConnectionString;

        if (connectionString is null)
        {
            return new Probe(
                false,
                $"No Redis server is configured, so every test that needs one is skipped and " +
                $"nothing about this adapter has been verified. Set {ConnectionVariable} to a " +
                $"StackExchange.Redis configuration string to run them — for example " +
                $"'localhost:6379'. Any database will do; the store namespaces itself by key " +
                "prefix and each test uses its own.");
        }

        try
        {
            var configuration = ConfigurationOptions.Parse(connectionString);

            configuration.AllowAdmin = true;
            configuration.AbortOnConnectFail = true;

            using var connection = ConnectionMultiplexer.Connect(configuration);

            var endpoint = connection.GetEndPoints().FirstOrDefault();

            if (endpoint is null)
            {
                return new Probe(
                    false,
                    $"{ConnectionVariable} names no endpoint, so there is nothing to connect to.");
            }

            var version = connection.GetServer(endpoint).Version;

            return new Probe(true, $"Connected to Redis {version}.");
        }
        catch (RedisConnectionException failure)
        {
            return new Probe(
                false,
                $"{ConnectionVariable} is set but the server did not answer: {failure.Message}");
        }
        catch (RedisTimeoutException failure)
        {
            return new Probe(
                false,
                $"{ConnectionVariable} is set but the server did not answer in time: {failure.Message}");
        }
        catch (ArgumentException failure)
        {
            return new Probe(
                false,
                $"{ConnectionVariable} is not a usable configuration string: {failure.Message}");
        }
    }

    private readonly record struct Probe(bool Available, string Reason);
}
