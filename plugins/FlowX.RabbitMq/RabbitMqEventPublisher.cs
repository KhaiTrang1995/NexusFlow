using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace FlowX.RabbitMq;

/// <summary>
/// Publishes staged outbox events to a topic exchange, with the event type as the routing key
/// and publisher confirms on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The second broker <c>PublisherConformance</c> holds to the contract, and the suite did
/// not change to accept it.</strong> That is the finding this class exists to produce.
/// <c>RedisStreamEventPublisher</c> reaches per-key order by writing one stream per key; this one
/// reaches it by publishing serially down one channel. Two mechanisms, one contract, no edit to
/// the suite — which is what makes
/// <see href="../../docs/adr/ADR-0018-outbox-publication-and-ordering.md">ADR-0018</see>'s
/// decision 3 a statement about the runtime rather than about Redis.
/// </para>
/// <para>
/// <strong>Confirms are on and there is no setting to turn them off.</strong>
/// <c>PostgresOutboxPublisher</c> marks a row published when this method returns a count that
/// includes it, so a publish that returned before the broker had the message would mark as
/// delivered something no broker ever accepted — and the outbox would then never offer it again.
/// With confirms off, <c>BasicPublishAsync</c> completes when the bytes reach the socket, which
/// is not the same event. So the channel is created with publisher confirmations <em>and</em>
/// confirmation tracking enabled, and each publish is awaited to its confirm before the next is
/// sent.
/// <see href="../../docs/adr/ADR-0072-a-rabbitmq-queue-is-a-partition-only-while-one-node-holds-it.md">ADR-0072</see>
/// records the cost: one round trip per event, paid inside the outbox's claim transaction.
/// </para>
/// <para>
/// <strong>What a confirm means, stated because it is narrower than it looks.</strong> It means
/// the exchange accepted the message, not that a queue holds it. An event published while nothing
/// is bound to <see cref="RabbitMqOptions.Exchange"/> for its type is confirmed and then dropped,
/// and the outbox marks the row published. That is the same property a Redis stream nobody reads
/// has, and it is where <c>docs/11-Distributed-Runtime.md §5</c> already puts the boundary: the
/// outbox's guarantee ends when the event reaches the broker.
/// </para>
/// <para>
/// <strong>The batch is published one event at a time, in order, and the count is where it
/// stopped</strong> — <c>RedisStreamEventPublisher</c>'s reasoning verbatim, and it is the reason
/// the channel is not shared with anything: interleaving another publisher's sends on this
/// channel would put a second event of one key between two of ours in the exchange's arrival
/// order, which no per-key promise survives.
/// </para>
/// <para>
/// <strong>A refusal is a value and a defect is an exception (ADR-0007).</strong> Every failure a
/// broker that is unreachable, overloaded, refusing or shutting down can produce becomes an
/// <see cref="Error"/> in the <c>Unavailable</c> category, because the outbox is durable and the
/// batch is offered again. Anything else propagates: a publisher handed a null batch, or one
/// whose connection somebody disposed underneath it, is broken rather than blocked.
/// </para>
/// </remarks>
public sealed class RabbitMqEventPublisher : IEventPublisher, IAsyncDisposable
{
    /// <summary>What a caller sees when the broker could not be reached.</summary>
    /// <remarks>
    /// One code rather than one per client exception, for <c>RedisStreamEventPublisher</c>'s
    /// reason: the caller's decision is the same for all of them — leave the rows pending and try
    /// the next pass — and a code is a contract a caller branches on rather than a rendering of a
    /// stack trace. The client's own message travels in <see cref="Error.Message"/>.
    /// </remarks>
    public const string PublishFailedCode = "rabbitmq.publish_failed";

    private readonly RabbitMqConnection _connection;
    private readonly RabbitMqOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel? _channel;
    private bool _disposed;

    /// <summary>Creates a publisher over a connection.</summary>
    /// <param name="connection">
    /// The connection. The caller owns it and its lifetime; several adapters may share one.
    /// </param>
    /// <param name="options">
    /// Which exchange to publish to. Defaults to <c>flowx.events</c>. Must name the same exchange
    /// the consumers' queues are bound to.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    public RabbitMqEventPublisher(RabbitMqConnection connection, RabbitMqOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _connection = connection;
        _options = options ?? new RabbitMqOptions();
    }

    /// <inheritdoc />
    public async ValueTask<Result<int>> PublishAsync(
        IReadOnlyList<OutboxRecord> batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (batch.Count == 0)
        {
            // Nothing to do is not a failure, and it must not be a connect either: an idle outbox
            // polls constantly, and a publisher that opened a channel per empty pass would hold a
            // broker open for a deployment that is publishing nothing.
            return 0;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            IChannel channel;

            try
            {
                channel = await ChannelAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (IsBrokerFailure(failure))
            {
                return Result.Fail<int>(Failed(failure));
            }

            for (var published = 0; published < batch.Count; published++)
            {
                try
                {
                    await PublishOneAsync(channel, batch[published], cancellationToken).ConfigureAwait(false);
                }
                catch (Exception failure) when (IsBrokerFailure(failure))
                {
                    // A channel that took an error is closed by the protocol, so it is dropped
                    // rather than reused: the next pass opens a fresh one and re-declares the
                    // exchange. Reusing it would turn one refusal into a permanent one.
                    await DropChannelAsync().ConfigureAwait(false);

                    // The first event of the batch is reported as a failure and every later one
                    // as a shorter prefix, which is the choice IEventPublisher states: a refusal
                    // that took nothing is worth reporting with its reason, and one that took
                    // something is worth reporting as a count, because that is what leaves the
                    // events which did arrive marked. Both leave the rest pending.
                    return published == 0 ? Result.Fail<int>(Failed(failure)) : published;
                }
            }

            return batch.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the channel this publisher opened.</summary>
    /// <returns>A task that completes when it is closed.</returns>
    /// <remarks>
    /// The connection is not closed: it is the caller's, and a consumer may still be using it.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await DropChannelAsync().ConfigureAwait(false);

        _gate.Dispose();
    }

    /// <summary>Publishes one event and waits for the broker to confirm it.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The routing key is the event type and nothing else.</strong> A topic exchange
    /// routes on it, so <c>lead.created</c> reaches every queue bound to <c>lead.created</c>,
    /// <c>lead.*</c> or <c>lead.#</c> — which is the fan-out this transport exists for, and which
    /// a Redis stream has no equivalent of.
    /// </para>
    /// <para>
    /// <strong>The partition key is a header rather than the routing key.</strong> Routing by key
    /// would make a subscription's queue binding depend on which keys exist, which is unknowable
    /// at declaration time and unbounded at run time. Order per key does not need it: one channel
    /// publishing serially puts a key's events into the exchange in staging order, and a queue
    /// preserves the order it was filled in.
    /// </para>
    /// <para>
    /// <c>PublishedAt</c> is not written at all — it is the outbox's record of its own
    /// bookkeeping and is null on every event handed to a publisher — and the delivery mode is
    /// persistent, because a broker restart that discarded an event the outbox has marked
    /// published would lose it with nothing anywhere recording that it had.
    /// </para>
    /// </remarks>
    private async Task PublishOneAsync(
        IChannel channel, OutboxRecord staged, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal);

        RabbitMqHeaders.Write(headers, RabbitMqHeaders.InstanceId, staged.InstanceId.ToString("d"));
        RabbitMqHeaders.Write(headers, RabbitMqHeaders.SchemaVersion, staged.SchemaVersion);
        RabbitMqHeaders.Write(headers, RabbitMqHeaders.PartitionKey, staged.PartitionKey);

        // The tenant is the one field where the empty string is omitted rather than written: a
        // consumer must be able to tell "this event came from a deployment that does not isolate"
        // from "this event names the empty tenant", because the first starts a flow and the
        // second is a refusal.
        if (staged.TenantId is { Length: > 0 } tenant)
        {
            RabbitMqHeaders.Write(headers, RabbitMqHeaders.TenantId, tenant);
        }

        var properties = new BasicProperties
        {
            MessageId = staged.EventId.ToString("d"),
            Type = staged.Type,
            Persistent = true,
            Headers = headers,
        };

        var body = ReadOnlyMemory<byte>.Empty;

        if (staged.PayloadJson is { } payload)
        {
            properties.ContentType = RabbitMqHeaders.PayloadContentType;
            body = Encoding.UTF8.GetBytes(payload);
        }

        // mandatory: false, because an event nothing is bound for is not a publisher error. The
        // subscriber set is the deployment's business and a return would only give this method a
        // failure it must not report — the exchange did accept the message.
        await channel
            .BasicPublishAsync(_options.Exchange, staged.Type, mandatory: false, properties, body, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The confirming channel, opening and declaring one if there is not one already.</summary>
    private async ValueTask<IChannel> ChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true } live)
        {
            return live;
        }

        await DropChannelAsync().ConfigureAwait(false);

        var connection = await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var channel = await connection
            .CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true),
                cancellationToken)
            .ConfigureAwait(false);

        // Declared here rather than in a deployment script, and declared on every reopen: a
        // publisher that assumed the exchange existed would fail every publish after a broker was
        // restored from an empty state, and the declare is idempotent.
        await channel
            .ExchangeDeclareAsync(
                _options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _channel = channel;

        return channel;
    }

    private async ValueTask DropChannelAsync()
    {
        var channel = _channel;
        _channel = null;

        if (channel is null)
        {
            return;
        }

        try
        {
            await channel.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is RabbitMQClientException or ObjectDisposedException)
        {
            // The channel is being dropped because it is already unusable; closing it politely is
            // a courtesy to the broker's channel count and not something worth failing over.
        }

        channel.Dispose();
    }

    /// <summary>
    /// Whether a failure is the broker being unavailable rather than this publisher being wrong.
    /// </summary>
    /// <remarks>
    /// <see cref="RabbitMQClientException"/> is the client's root for everything the protocol can
    /// go wrong at — unreachable, closed underneath us, refused by the server, timed out waiting
    /// for a continuation — and <c>IOException</c> and <c>SocketException</c> are
    /// what a connection that dies mid-frame surfaces as. Everything else is a defect and
    /// propagates, which is the half of ADR-0007 a catch-all would delete.
    /// </remarks>
    internal static bool IsBrokerFailure(Exception failure) =>
        failure is RabbitMQClientException
            or System.IO.IOException
            or System.Net.Sockets.SocketException
            or TimeoutException;

    /// <summary>Turns a client failure into the value the contract hands back.</summary>
    /// <remarks>
    /// <c>Unavailable</c> rather than <c>Internal</c>, because the category carries the retry
    /// decision and every failure caught here is one a later pass may well succeed at. Reporting
    /// a broker that is merely down as an internal fault would tell a caller to give up on
    /// something the outbox is holding for it.
    /// </remarks>
    private static Error Failed(Exception failure) => new(
        PublishFailedCode,
        $"The batch reached no event on the broker: {failure.Message}",
        ErrorCategory.Unavailable);
}
