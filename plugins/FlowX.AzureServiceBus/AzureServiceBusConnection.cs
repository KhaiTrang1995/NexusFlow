using Azure.Messaging.ServiceBus;

namespace FlowX.AzureServiceBus;

/// <summary>
/// One <see cref="ServiceBusClient"/>, shared by whichever adapters a host registered.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The same arrangement <c>RabbitMqConnection</c> has, for the same reason.</strong> A
/// Service Bus client owns an AMQP connection with its own heartbeat and its own reconnection
/// state; one per adapter is the standard way to spend a namespace's connection allowance twice
/// over. The container owns exactly one and disposes it with the provider.
/// </para>
/// <para>
/// <strong>It is a class rather than the client itself so the container has something of this
/// package's own to key on.</strong> Registering <see cref="ServiceBusClient"/> directly would
/// make any other library's registration of the same type either win or lose silently, and a
/// deployment talking to two namespaces would have no way to say which is which.
/// </para>
/// </remarks>
public sealed class AzureServiceBusConnection : IAsyncDisposable
{
    private readonly ServiceBusClient _client;

    /// <summary>Opens a client against a namespace.</summary>
    /// <param name="connectionString">
    /// A Service Bus connection string, e.g.
    /// <c>Endpoint=sb://…;SharedAccessKeyName=…;SharedAccessKey=…</c>. The emulator's
    /// <c>UseDevelopmentEmulator=true</c> form works unchanged.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is null or blank.</exception>
    /// <remarks>
    /// <strong>Nothing connects here.</strong> The client is lazy: the first send or receive is
    /// what reaches the namespace, which is why an unreachable broker surfaces as an
    /// <c>Unavailable</c> error from a publish rather than as an exception from a constructor a
    /// host called during start-up.
    /// </remarks>
    public AzureServiceBusConnection(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _client = new ServiceBusClient(connectionString);
    }

    /// <summary>The client the adapters send and receive on.</summary>
    public ServiceBusClient Client => _client;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
