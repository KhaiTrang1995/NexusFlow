using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace FlowX.RabbitMq;

/// <summary>
/// One AMQP connection, opened when it is first needed and reopened after it is lost.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists at all, when <c>FlowX.Redis</c> takes an
/// <c>IConnectionMultiplexer</c> straight from the container.</strong> StackExchange.Redis
/// returns a multiplexer that is not yet connected and reconnects underneath every command;
/// <c>ConnectionFactory.CreateConnectionAsync</c> throws
/// <see cref="BrokerUnreachableException"/> and returns nothing. Handing the container a
/// factory call would therefore make "the broker was down when the host started" a startup
/// crash rather than a batch that is offered again next pass — which is exactly the failure
/// <c>IEventPublisher</c> says must be an <c>Error</c> and not an exception (ADR-0007).
/// </para>
/// <para>
/// <strong>Automatic recovery is off, deliberately.</strong> The client's recovery reopens a
/// channel and re-declares its topology, and in doing so silently voids every delivery tag the
/// consumer is still holding — a message would then be acknowledged by tag number against a
/// channel that never delivered it. <c>RabbitMqBusConsumer</c> is built around holding tags
/// across passes, so it needs a shutdown to be final and visible. Everything above here already
/// retries on a schedule, so a connection that is reopened on the next pass is the same
/// behaviour with none of the ambiguity.
/// </para>
/// <para>
/// <strong>The caller owns this, and it is a singleton.</strong> An AMQP connection is a TCP
/// connection with a heartbeat; one per scope is the standard way to exhaust a broker's
/// connection limit, which is the same reason <c>ServiceCollectionExtensions</c> gives for
/// registering it once.
/// </para>
/// </remarks>
public sealed class RabbitMqConnection : IAsyncDisposable
{
    private readonly ConnectionFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private bool _disposed;

    /// <summary>Creates a connection over an AMQP URI.</summary>
    /// <param name="uri">
    /// An AMQP URI, e.g. <c>amqp://guest:guest@localhost:5672/</c>. The virtual host is the
    /// URI's path; an empty path means the default <c>/</c>.
    /// </param>
    /// <param name="clientName">
    /// What this process calls itself on the broker's connection list. Defaults to the machine
    /// name, which is what an operator looking at <c>rabbitmqctl list_connections</c> needs.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="uri"/> is null or blank.</exception>
    /// <exception cref="UriFormatException"><paramref name="uri"/> is not a URI.</exception>
    public RabbitMqConnection(string uri, string? clientName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        _factory = new ConnectionFactory
        {
            Uri = new Uri(uri),
            AutomaticRecoveryEnabled = false,
            TopologyRecoveryEnabled = false,
            ClientProvidedName = clientName ?? Environment.MachineName,
        };
    }

    /// <summary>The open connection, opening one if there is not one already.</summary>
    /// <param name="cancellationToken">Cancels the connect.</param>
    /// <returns>The connection. This object owns it; do not dispose it.</returns>
    /// <exception cref="ObjectDisposedException">This holder has been disposed.</exception>
    /// <exception cref="BrokerUnreachableException">The broker did not answer.</exception>
    /// <remarks>
    /// A connection that has been shut down is discarded rather than returned, so a caller never
    /// has to ask whether the thing it was handed is usable. The next call opens a fresh one and
    /// pays the connect cost once, on the pass that needed it.
    /// </remarks>
    public async ValueTask<IConnection> OpenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_connection is { IsOpen: true } live)
        {
            return live;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_connection is { IsOpen: true } current)
            {
                return current;
            }

            if (_connection is { } dead)
            {
                _connection = null;
                dead.Dispose();
            }

            _connection = await _factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);

            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the connection, if one is open.</summary>
    /// <returns>A task that completes when it is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var connection = _connection;
        _connection = null;

        if (connection is not null)
        {
            try
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is RabbitMQClientException or ObjectDisposedException)
            {
                // Closing a connection the broker has already taken away is the ordinary outcome
                // of a shutdown that began at the other end, and there is nothing left to do
                // about it. Throwing here would fail a host's shutdown over a socket that was
                // going to close anyway.
            }

            connection.Dispose();
        }

        _gate.Dispose();
    }
}
