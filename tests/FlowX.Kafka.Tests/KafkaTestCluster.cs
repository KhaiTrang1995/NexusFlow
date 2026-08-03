using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Xunit;

namespace FlowX.Kafka.Tests;

/// <summary>
/// Decides, once, whether there is a Kafka cluster to test against — and records why when there
/// is not.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The three-way discipline is <c>RabbitMqTestBroker</c>'s and
/// <c>ServiceBusTestNamespace</c>'s, deliberately mirrored rather than reinvented.</strong> No
/// cluster configured: every test skips, carrying this class's reason. A cluster configured but
/// unreachable: every test <strong>fails</strong>, because that is the case which looks like
/// success in CI.
/// </para>
/// <para>
/// <strong>Topics are created here and by nothing in the plugin.</strong> That is the plugin's
/// decision under test rather than a fixture convenience: <c>KafkaBusConsumer</c> holds no
/// <c>AdminClient</c>, so a suite that let it create its own topics would exercise a code path
/// which does not exist. A test fixture is a deployment for this purpose, and it uses the
/// administrative client a deployment would.
/// </para>
/// </remarks>
internal static class KafkaTestCluster
{
    /// <summary>The variable that supplies bootstrap servers.</summary>
    public const string ConnectionVariable = "FLOWX_KAFKA_BOOTSTRAP";

    private static readonly Lazy<Probe> Result = new(Run, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The configured bootstrap servers, or null when none were supplied.</summary>
    public static string? BootstrapServers =>
        Environment.GetEnvironmentVariable(ConnectionVariable) is { Length: > 0 } value
            ? value
            : null;

    /// <summary>Whether the environment promised a cluster, whether or not one answered.</summary>
    public static bool IsPromised => BootstrapServers is not null;

    /// <summary>Whether a cluster answered.</summary>
    public static bool IsAvailable => Result.Value.Available;

    /// <summary>Why, in a sentence a reader can act on. Never empty.</summary>
    public static string Reason => Result.Value.Reason;

    /// <summary>
    /// Options over a topology this fixture owns, with the topics created.
    /// </summary>
    /// <param name="partitions">How many partitions the events topic gets.</param>
    /// <param name="cancellationToken">Cancels the setup.</param>
    /// <returns>The options, naming topics nothing else is using.</returns>
    /// <exception cref="InvalidOperationException">
    /// A cluster was promised by the environment and is not reachable.
    /// </exception>
    public static async ValueTask<KafkaOptions> IsolatedAsync(
        int partitions, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            if (IsPromised)
            {
                throw new InvalidOperationException(
                    $"{ConnectionVariable} is set, so this run promised a Kafka cluster, and none " +
                    $"answered. This is a failure rather than a skip on purpose: a skip here would " +
                    $"report the whole publisher conformance suite as green against a broker that " +
                    $"was never reached. {Reason}");
            }

            Assert.Skip(Reason);
        }

        var options = new KafkaOptions
        {
            BootstrapServers = BootstrapServers!,
            Topic = "flowx.t." + Guid.NewGuid().ToString("N")[..12],
            PollWait = TimeSpan.FromMilliseconds(750),
        };

        using var admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = options.BootstrapServers }).Build();

        await admin.CreateTopicsAsync(
        [
            new TopicSpecification
            {
                Name = options.Topic,
                NumPartitions = partitions,
                ReplicationFactor = 1,
            },
            new TopicSpecification
            {
                Name = options.DeadLetterTopic,
                NumPartitions = 1,
                ReplicationFactor = 1,
            },
        ]).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        return options;
    }

    /// <summary>Removes the topics a fixture created.</summary>
    /// <param name="options">The fixture's topology.</param>
    public static async ValueTask DropAsync(KafkaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            using var admin = new AdminClientBuilder(
                new AdminClientConfig { BootstrapServers = options.BootstrapServers }).Build();

            await admin.DeleteTopicsAsync([options.Topic, options.DeadLetterTopic]).ConfigureAwait(false);
        }
        catch (DeleteTopicsException)
        {
            // A topic that is already gone is the outcome this method wanted.
        }
    }

    private static Probe Run()
    {
        if (BootstrapServers is not { } bootstrap)
        {
            return new Probe(
                false,
                $"Set {ConnectionVariable} to a Kafka bootstrap server list — for example " +
                "\"localhost:9092\" — to run this suite against a real cluster.");
        }

        try
        {
            using var admin = new AdminClientBuilder(
                new AdminClientConfig { BootstrapServers = bootstrap }).Build();

            var metadata = admin.GetMetadata(TimeSpan.FromSeconds(10));

            return metadata.Brokers.Count > 0
                ? new Probe(true, "A Kafka cluster answered.")
                : new Probe(false, "The cluster answered with no brokers.");
        }
        catch (Exception failure)
        {
            return new Probe(false, failure.Message);
        }
    }

    private sealed record Probe(bool Available, string Reason);
}
