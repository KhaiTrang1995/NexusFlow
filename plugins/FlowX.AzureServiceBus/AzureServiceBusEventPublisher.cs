using System.Text;
using Azure.Messaging.ServiceBus;

namespace FlowX.AzureServiceBus;

/// <summary>
/// Publishes staged outbox events to one topic, with the event type as the message subject.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The third broker <c>PublisherConformance</c> holds to the contract, and the suite did
/// not change to accept it.</strong> <c>RedisStreamEventPublisher</c> reaches per-key order by
/// writing one stream per key; <c>RabbitMqEventPublisher</c> by publishing serially down one
/// channel with confirms; this one by sending serially down one sender. Three mechanisms, one
/// contract, no edit to the suite.
/// </para>
/// <para>
/// <strong>There is no confirm setting here, and that is a difference worth stating.</strong>
/// AMQP 1.0 settles a transfer as part of the protocol, so <c>SendMessageAsync</c> completing
/// <em>is</em> the broker having taken the message — the thing RabbitMQ needs publisher confirms
/// switched on to mean. <c>PostgresOutboxPublisher</c> marks a row published when this method
/// returns a count including it, and here that is honest without an option to get wrong.
/// </para>
/// <para>
/// <strong>What acceptance means, stated because it is narrower than it looks.</strong> The topic
/// took the message; it does not follow that a subscription holds it. An event whose type no
/// subscription's filter matches is accepted and then dropped, and the outbox marks the row
/// published. That is the same property a Redis stream nobody reads has, and
/// <c>docs/11-Distributed-Runtime.md §5</c> is where the boundary already sits: the outbox's
/// guarantee ends when the event reaches the broker.
/// </para>
/// <para>
/// <strong>One message at a time, in order, and the count is where it stopped</strong> — the two
/// other publishers' reasoning verbatim. A batch send would be faster and would report one
/// outcome for the whole batch, which is precisely the thing <c>IEventPublisher</c>'s prefix
/// contract exists to avoid: a partial failure has to leave the events that did arrive marked.
/// </para>
/// <para>
/// <strong>A refusal is a value and a defect is an exception (ADR-0007).</strong> Every failure a
/// namespace that is unreachable, throttling, full or shutting down can produce becomes an
/// <see cref="Error"/> in the <c>Unavailable</c> category, because the outbox is durable and the
/// batch is offered again. Anything else propagates.
/// </para>
/// </remarks>
public sealed class AzureServiceBusEventPublisher : IEventPublisher, IAsyncDisposable
{
    /// <summary>What a caller sees when the namespace could not be reached.</summary>
    /// <remarks>
    /// One code rather than one per client exception, for the other publishers' reason: the
    /// caller's decision is the same for all of them — leave the rows pending and try the next
    /// pass. The client's own message travels in <see cref="Error.Message"/>.
    /// </remarks>
    public const string PublishFailedCode = "servicebus.publish_failed";

    private readonly AzureServiceBusConnection _connection;
    private readonly AzureServiceBusOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ServiceBusSender? _sender;
    private bool _disposed;

    /// <summary>Creates a publisher over a connection.</summary>
    /// <param name="connection">
    /// The connection. The caller owns it and its lifetime; several adapters may share one.
    /// </param>
    /// <param name="options">
    /// Which topic to send to. Defaults to <c>flowx-events</c>. Must name the topic the
    /// consumers' subscriptions hang off.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    public AzureServiceBusEventPublisher(
        AzureServiceBusConnection connection, AzureServiceBusOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _connection = connection;
        _options = options ?? new AzureServiceBusOptions();
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
            // polls constantly, and a publisher that opened a link per empty pass would hold a
            // namespace open for a deployment publishing nothing.
            return 0;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var sender = _sender ??= _connection.Client.CreateSender(_options.Topic);

            for (var published = 0; published < batch.Count; published++)
            {
                try
                {
                    await sender
                        .SendMessageAsync(Message(batch[published]), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception failure) when (IsBrokerFailure(failure))
                {
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

    /// <summary>Closes the sender this publisher opened.</summary>
    /// <returns>A task that completes when it is closed.</returns>
    /// <remarks>The connection is not closed: it is the caller's, and a consumer may share it.</remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_sender is { } sender)
        {
            _sender = null;

            await sender.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    /// <summary>Turns a staged row into a message.</summary>
    /// <remarks>
    /// <para>
    /// <strong><c>Subject</c> is the event type, because that is what a filter can see.</strong> A
    /// subscription selects with a correlation filter, and a correlation filter matches on the
    /// broker's own properties — not on application properties, which are matchable only through
    /// a SQL filter the broker evaluates per message. Putting the type where the cheap filter
    /// looks is what makes one topic serve every contract.
    /// </para>
    /// <para>
    /// <strong><c>MessageId</c> is the event id</strong>, so a namespace with duplicate detection
    /// switched on deduplicates on the identity the outbox already assigns rather than on a hash
    /// of a body. This package does not switch it on: that is a property of the entity and
    /// therefore a deployment's (ADR-0074).
    /// </para>
    /// <para>
    /// <c>PublishedAt</c> is not written at all — it is the outbox's record of its own
    /// bookkeeping and is null on every event handed to a publisher.
    /// </para>
    /// </remarks>
    private static ServiceBusMessage Message(OutboxRecord staged)
    {
        var message = new ServiceBusMessage
        {
            MessageId = staged.EventId.ToString("d"),
            Subject = staged.Type,
            PartitionKey = staged.PartitionKey,
        };

        if (staged.PayloadJson is { } payload)
        {
            message.Body = new BinaryData(Encoding.UTF8.GetBytes(payload));
            message.ContentType = AzureServiceBusHeaders.PayloadContentType;
        }

        AzureServiceBusHeaders.Write(
            message, AzureServiceBusHeaders.InstanceId, staged.InstanceId.ToString("d"));

        AzureServiceBusHeaders.Write(
            message, AzureServiceBusHeaders.SchemaVersion, staged.SchemaVersion);

        AzureServiceBusHeaders.Write(
            message, AzureServiceBusHeaders.PartitionKey, staged.PartitionKey);

        AzureServiceBusHeaders.Write(
            message, AzureServiceBusHeaders.TenantId, staged.TenantId);

        return message;
    }

    /// <summary>Whether this is the namespace failing rather than this package.</summary>
    /// <remarks>
    /// <see cref="ServiceBusException"/> covers the broker's own refusals, and the socket and
    /// timeout exceptions cover a namespace that is not answering at all. An
    /// <see cref="ObjectDisposedException"/> is deliberately not here: a sender somebody disposed
    /// underneath this publisher is a defect in the host's wiring, not a broker that is down.
    /// </remarks>
    private static bool IsBrokerFailure(Exception failure) =>
        failure is ServiceBusException
            or TimeoutException
            or System.Net.Sockets.SocketException
            or System.IO.IOException;

    private static Error Failed(Exception failure) =>
        new Error(
            PublishFailedCode,
            "The Service Bus namespace did not accept the event: " + failure.Message,
            ErrorCategory.Unavailable);
}
