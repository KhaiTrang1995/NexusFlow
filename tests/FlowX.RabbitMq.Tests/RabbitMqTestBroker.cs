using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Xunit;

namespace FlowX.RabbitMq.Tests;

/// <summary>
/// Decides, once, whether there is a RabbitMQ broker to test against — and records why when
/// there is not.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The three-way discipline is <c>PostgresTestDatabase</c>'s and <c>RedisTestServer</c>'s,
/// deliberately mirrored rather than reinvented.</strong> No broker configured: every test skips,
/// and each skip carries this class's reason, so a run with no broker reads as a run with no
/// broker. A broker configured but unreachable: every test <strong>fails</strong>, because that is
/// the case that looks like success in CI — somebody wired a service container, the variable is
/// set, the container did not come up, and a skip would report the whole publisher conformance
/// suite as green against a broker that was never reached.
/// </para>
/// <para>
/// <strong>docs/26-CRM-Sample.md §9 states the refusal this class implements</strong>: "this
/// plugin ships only with a suite that runs against a real broker. A conformance suite that skips
/// its own subject is a failing gate, not a passing one."
/// </para>
/// <para>
/// <strong>RabbitMQ differs from Redis in one way that matters to this harness.</strong> There is
/// no key space to prefix, so tests are isolated by topology: every fixture gets an exchange, a
/// dead-letter exchange and a queue prefix nobody else uses, and deletes all three afterwards.
/// That satisfies the conformance suite's "a fresh, empty broker, called once per test" exactly
/// as well as a fresh vhost would and needs no management plugin to do it.
/// </para>
/// </remarks>
internal static class RabbitMqTestBroker
{
    /// <summary>The variable that supplies an AMQP URI.</summary>
    public const string ConnectionVariable = "FLOWX_RABBITMQ_CONNECTION";

    private static readonly Lazy<Probe> Result = new(Run, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The configured AMQP URI, or null when none was supplied.</summary>
    public static string? ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionVariable) is { Length: > 0 } value
            ? value
            : null;

    /// <summary>
    /// Whether the environment promised a broker, whether or not one answered.
    /// </summary>
    /// <remarks>
    /// The distinction this whole class turns on. "Nobody asked for a RabbitMQ" is a reason to
    /// skip; "somebody asked for a RabbitMQ and there isn't one" is a defect in the run.
    /// </remarks>
    public static bool IsPromised => ConnectionString is not null;

    /// <summary>Whether a broker answered.</summary>
    public static bool IsAvailable => Result.Value.Available;

    /// <summary>Why, in a sentence a reader can act on. Never empty.</summary>
    public static string Reason => Result.Value.Reason;

    /// <summary>The exception a test throws when a promised broker is not there.</summary>
    /// <returns>The exception to throw.</returns>
    public static InvalidOperationException Unreachable() => new(
        $"{ConnectionVariable} is set, so this run promised a RabbitMQ broker, and none " +
        $"answered. This is a failure rather than a skip on purpose: a skip here would report " +
        $"the whole publisher conformance suite as green against a broker that was never " +
        $"reached. {Reason}");

    /// <summary>
    /// A connection to the configured broker, or the skip or failure that stands in for it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the connect.</param>
    /// <returns>The connection holder. The caller owns it.</returns>
    /// <exception cref="InvalidOperationException">
    /// A broker was promised by the environment and is not reachable.
    /// </exception>
    public static async ValueTask<RabbitMqConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            if (IsPromised)
            {
                throw Unreachable();
            }

            Assert.Skip(Reason);
        }

        var connection = new RabbitMqConnection(ConnectionString!, "flowx-tests");

        // Opened here rather than lazily, so a broker that went away between the probe and this
        // test fails in the harness with the broker's own message rather than inside an assertion.
        await connection.OpenAsync(cancellationToken);

        return connection;
    }

    /// <summary>A topology nobody else in this run is using.</summary>
    /// <param name="deliveryLimit">
    /// How many deliveries the broker itself allows before dead-lettering. Left at the package
    /// default unless a test is about that number.
    /// </param>
    /// <returns>The options.</returns>
    public static RabbitMqOptions IsolatedTopology(int? deliveryLimit = null)
    {
        var suffix = Guid.NewGuid().ToString("n");

        var options = new RabbitMqOptions
        {
            Exchange = "flowx.t." + suffix,
            DeadLetterExchange = "flowx.t." + suffix + ".dead",
            QueuePrefix = "flowx.t." + suffix,
        };

        return deliveryLimit is { } limit ? options with { DeliveryLimit = limit } : options;
    }

    /// <summary>Deletes everything a fixture declared, so the next run starts empty.</summary>
    /// <param name="options">The topology to remove.</param>
    /// <param name="queues">The queues declared under it.</param>
    /// <param name="cancellationToken">Cancels the teardown.</param>
    /// <returns>A task that completes when it is gone.</returns>
    /// <remarks>
    /// A durable queue outlives the process that declared it, so a suite that did not clean up
    /// would leave one queue per test on the broker for ever — and the next run's
    /// <c>SubscribeAsync</c> would find a queue it did not create, which is a fact one of these
    /// tests asserts on.
    /// </remarks>
    public static async ValueTask DropAsync(
        RabbitMqOptions options, IEnumerable<string> queues, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            return;
        }

        await using var connection = new RabbitMqConnection(ConnectionString!, "flowx-tests");

        try
        {
            var open = await connection.OpenAsync(cancellationToken);
            await using var channel = await open.CreateChannelAsync(cancellationToken: cancellationToken);

            foreach (var queue in queues)
            {
                await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false, cancellationToken: cancellationToken);
            }

            await channel.ExchangeDeleteAsync(options.Exchange, ifUnused: false, cancellationToken: cancellationToken);
            await channel.ExchangeDeleteAsync(options.DeadLetterExchange, ifUnused: false, cancellationToken: cancellationToken);
        }
        catch (RabbitMQClientException)
        {
            // Teardown is best effort: a broker that went away mid-run has already failed the
            // test that mattered, and failing again here would hide it.
        }
    }

    private static Probe Run()
    {
        var uri = ConnectionString;

        if (uri is null)
        {
            return new Probe(
                false,
                $"No RabbitMQ broker is configured, so every test that needs one is skipped and " +
                $"nothing about this adapter has been verified. Set {ConnectionVariable} to an " +
                $"AMQP URI to run them — for example 'amqp://guest:guest@localhost:5672/'. Any " +
                "vhost will do; each test declares its own exchange and queues and deletes them.");
        }

        try
        {
            var factory = new ConnectionFactory
            {
                Uri = new Uri(uri),
                AutomaticRecoveryEnabled = false,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
            };

            using var connection = factory.CreateConnectionAsync().GetAwaiter().GetResult();

            var version = connection.ServerProperties?.TryGetValue("version", out var raw) == true && raw is byte[] bytes
                ? System.Text.Encoding.UTF8.GetString(bytes)
                : "an unstated version";

            return new Probe(true, $"Connected to RabbitMQ {version}.");
        }
        catch (BrokerUnreachableException failure)
        {
            return new Probe(
                false,
                $"{ConnectionVariable} is set but the broker did not answer: {failure.Message}");
        }
        catch (Exception failure) when (failure is UriFormatException or ArgumentException)
        {
            return new Probe(
                false,
                $"{ConnectionVariable} is not a usable AMQP URI: {failure.Message}");
        }
    }

    private readonly record struct Probe(bool Available, string Reason);
}
