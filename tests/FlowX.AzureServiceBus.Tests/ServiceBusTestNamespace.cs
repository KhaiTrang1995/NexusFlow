using Azure.Messaging.ServiceBus;
using Xunit;

namespace FlowX.AzureServiceBus.Tests;

/// <summary>
/// Decides, once, whether there is a Service Bus namespace to test against — and records why when
/// there is not.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The three-way discipline is <c>RabbitMqTestBroker</c>'s, deliberately mirrored rather
/// than reinvented.</strong> No namespace configured: every test skips, and each skip carries this
/// class's reason. A namespace configured but unreachable: every test <strong>fails</strong>,
/// because that is the case which looks like success in CI — somebody wired a service container,
/// the variable is set, the container did not come up, and a skip would report the whole publisher
/// conformance suite as green against a broker that was never reached.
/// </para>
/// <para>
/// <strong>docs/26-CRM-Sample.md §9 states the refusal this class implements</strong>: "this
/// plugin ships only with a suite that runs against a real broker. A conformance suite that skips
/// its own subject is a failing gate, not a passing one." The Azure Service Bus emulator is that
/// broker: it speaks AMQP 1.0, it settles under peek-lock, it dead-letters, and it enforces the
/// one thing this package's design turns on — that entities exist because a deployment made them.
/// </para>
/// <para>
/// <strong>Isolation is by draining, not by prefixing.</strong> RabbitMQ tests get a private
/// topology per fixture because a consumer may declare one; here it may not
/// (<a href="../../docs/adr/ADR-0074-service-bus-topology-is-created-by-a-deployment-not-by-a-consumer.md">ADR-0074</a>),
/// so the subscriptions are the ones <c>emulator-config.json</c> declares and each fixture empties
/// what it is about to use. That is a weaker isolation and it is the honest one for this
/// transport.
/// </para>
/// </remarks>
internal static class ServiceBusTestNamespace
{
    /// <summary>The variable that supplies a Service Bus connection string.</summary>
    public const string ConnectionVariable = "FLOWX_SERVICEBUS_CONNECTION";

    /// <summary>The topic <c>emulator-config.json</c> declares.</summary>
    public const string Topic = "flowx-events";

    /// <summary>The event type the declared subscriptions filter on.</summary>
    public const string OrderPlaced = "order.placed";

    private static readonly Lazy<Probe> Result = new(Run, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The configured connection string, or null when none was supplied.</summary>
    public static string? ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionVariable) is { Length: > 0 } value
            ? value
            : null;

    /// <summary>Whether the environment promised a namespace, whether or not one answered.</summary>
    public static bool IsPromised => ConnectionString is not null;

    /// <summary>Whether a namespace answered.</summary>
    public static bool IsAvailable => Result.Value.Available;

    /// <summary>Why, in a sentence a reader can act on. Never empty.</summary>
    public static string Reason => Result.Value.Reason;

    /// <summary>The exception a test throws when a promised namespace is not there.</summary>
    /// <returns>The exception to throw.</returns>
    public static InvalidOperationException Unreachable() => new(
        $"{ConnectionVariable} is set, so this run promised an Azure Service Bus namespace, and " +
        $"none answered. This is a failure rather than a skip on purpose: a skip here would " +
        $"report the whole publisher conformance suite as green against a broker that was never " +
        $"reached. {Reason}");

    /// <summary>A connection to the configured namespace, or the skip that stands in for it.</summary>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>The connection. The caller owns it.</returns>
    /// <exception cref="InvalidOperationException">
    /// A namespace was promised by the environment and is not reachable.
    /// </exception>
    public static ValueTask<AzureServiceBusConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            if (IsPromised)
            {
                throw Unreachable();
            }

            Assert.Skip(Reason);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(new AzureServiceBusConnection(ConnectionString!));
    }

    /// <summary>Empties a subscription, so one test cannot read another's leftovers.</summary>
    /// <param name="connection">The namespace.</param>
    /// <param name="subscription">The subscription name, as the emulator declares it.</param>
    /// <param name="cancellationToken">Cancels the drain.</param>
    public static async ValueTask DrainAsync(
        AzureServiceBusConnection connection, string subscription, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var receiver = connection.Client.CreateReceiver(
            Topic,
            subscription,
            new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete });

        await using var closing = receiver.ConfigureAwait(false);

        while (true)
        {
            var batch = await receiver
                .ReceiveMessagesAsync(64, TimeSpan.FromMilliseconds(250), cancellationToken)
                .ConfigureAwait(false);

            if (batch.Count == 0)
            {
                return;
            }
        }
    }

    private static Probe Run()
    {
        if (ConnectionString is not { } connectionString)
        {
            return new Probe(
                false,
                $"Set {ConnectionVariable} to a Service Bus connection string — the emulator's " +
                "\"Endpoint=sb://localhost:5673;SharedAccessKeyName=RootManageSharedAccessKey;" +
                "SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;\" form works — to run " +
                "this suite against a real broker.");
        }

        try
        {
            var client = new ServiceBusClient(connectionString);

            try
            {
                var receiver = client.CreateReceiver(Topic, "conformance--all");

                receiver.PeekMessageAsync().GetAwaiter().GetResult();

                return new Probe(true, "A Service Bus namespace answered.");
            }
            finally
            {
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception failure)
        {
            return new Probe(false, failure.Message);
        }
    }

    private sealed record Probe(bool Available, string Reason);
}
