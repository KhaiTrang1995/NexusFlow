using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Azure.Messaging.ServiceBus;

namespace FlowX.AzureServiceBus;

/// <summary>
/// Serves one Service Bus subscription per group, under peek-lock, with the broker's own
/// dead-letter queue.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It creates nothing.</strong> <c>SubscribeAsync</c> checks that the subscription a
/// group reads can be read and says so; it does not make one, and this package never references
/// the administration client. Entity creation on Service Bus is a management-plane operation with its
/// own rights, its own throttling and its own place in a deployment — which is
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0074-service-bus-topology-is-created-by-a-deployment-not-by-a-consumer.md">ADR-0074</a>,
/// and the difference from <c>RabbitMqBusConsumer</c>, which declares its topology on every pass
/// because AMQP 0-9-1 makes declaration idempotent, cheap and available to the same credentials
/// that consume.
/// </para>
/// <para>
/// <strong>Peek-lock, and the lock is the token.</strong> A received message carries a lock the
/// broker holds for the subscription's <c>LockDuration</c>; completing it settles the delivery
/// and abandoning it — or letting the lock lapse, which is what a node dying does — returns it
/// with <c>DeliveryCount</c> incremented. So <c>IBusConsumer</c>'s at-least-once contract is the
/// broker's own behaviour rather than something this class arranges.
/// </para>
/// <para>
/// <strong>Dead-lettering is first-class here and derived everywhere else.</strong> Every
/// subscription has a dead-letter sub-queue that needs no address, no configuration and no
/// binding; <see cref="DeadLetterAsync"/> is one call, and the reason travels in the broker's own
/// <c>DeadLetterReason</c> field where the portal shows it. ADR-0073 held that a destination is
/// deployment configuration; on this transport there is no destination to configure, which is
/// that ADR's conclusion reached from the other side.
/// </para>
/// <para>
/// <strong>Partitions are built from the plugin's own key property.</strong> Service Bus offers
/// ordering through sessions, which require the subscription to be session-enabled and change the
/// receive API; this package does not use them, so a batch's partitions are grouped exactly as
/// <c>RabbitMqBusConsumer</c> groups them — by the key the publisher wrote — and the per-key
/// promise is the one ADR-0037 states rather than a stronger one.
/// </para>
/// </remarks>
public sealed class AzureServiceBusConsumer : IBusConsumer, IAsyncDisposable
{
    /// <summary>The broker family this consumer serves.</summary>
    /// <remarks>
    /// Compared against <c>BusSubscription.Transport</c> at registration, so a
    /// <c>[BusTrigger(Transport = …)]</c> naming another family is refused loudly rather than
    /// consumed from here while the manifest tells everyone otherwise.
    /// </remarks>
    public const string TransportName = "azure-servicebus";

    /// <summary>What a caller sees when a subscription could not be read.</summary>
    /// <remarks>
    /// <strong>One code, and the message carries what to do about it.</strong> A second code for
    /// "the subscription is not there" would be the more useful contract and it is not one this
    /// package can honour: a namespace answers a missing entity with
    /// <c>MessagingEntityNotFound</c>, and the emulator this package's suite runs against answers
    /// the same condition with a timeout. A branch that only one of the two can reach is a branch
    /// nothing tests, so the distinction is made in the message — where both can carry it — rather
    /// than in a code a caller would branch on.
    /// </remarks>
    public const string ReceiveFailedCode = "servicebus.receive_failed";

    private readonly AzureServiceBusConnection _connection;
    private readonly AzureServiceBusOptions _options;
    private readonly ConcurrentDictionary<string, ServiceBusReceiver> _receivers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ServiceBusReceivedMessage> _held = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Creates a consumer over a connection.</summary>
    /// <param name="connection">The connection. The caller owns it; several adapters may share one.</param>
    /// <param name="options">
    /// Which topic the subscriptions hang off. <strong>Must be the same options the publisher
    /// uses</strong> — a consumer reading a different topic reads nothing and says nothing.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    public AzureServiceBusConsumer(
        AzureServiceBusConnection connection, AzureServiceBusOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _connection = connection;
        _options = options ?? new AzureServiceBusOptions();
    }

    /// <inheritdoc />
    public string Transport => TransportName;

    /// <inheritdoc />
    /// <remarks>
    /// <strong>Always <c>false</c> on success, because this never creates anything.</strong>
    /// <c>IBusConsumer</c> reads the boolean as "did this call make the group", and the honest
    /// answer here is no — a deployment made it. The call is still made on every pass, and it
    /// still reaches the broker: a namespace whose subscription somebody deleted has to fail
    /// loudly rather than read empty for ever.
    /// </remarks>
    public async ValueTask<Result<bool>> SubscribeAsync(
        BusSubscription subscription, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            // Opening the link is the check. There is no cheaper one that does not need the
            // administration client, and the receiver is what the next Receive needs anyway.
            _ = ReceiverFor(subscription);

            await PeekAsync(subscription, cancellationToken).ConfigureAwait(false);

            return false;
        }
        catch (Exception failure) when (IsBrokerFailure(failure))
        {
            Forget(subscription);

            return Result.Fail<bool>(Unreadable(subscription, failure));
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<BusPartitionBatch>>> ReceiveAsync(
        BusSubscription subscription,
        int maxPartitions,
        int maxPerPartition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPartitions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPerPartition);
        ObjectDisposedException.ThrowIf(_disposed, this);

        IReadOnlyList<ServiceBusReceivedMessage> received;

        try
        {
            received = await ReceiverFor(subscription)
                .ReceiveMessagesAsync(
                    maxPartitions * maxPerPartition, _options.ReceiveWait, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception failure) when (IsBrokerFailure(failure))
        {
            Forget(subscription);

            return Result.Fail<IReadOnlyList<BusPartitionBatch>>(Unreadable(subscription, failure));
        }

        return Result.Ok(Partition(subscription, received, maxPartitions, maxPerPartition));
    }

    /// <inheritdoc />
    public async ValueTask<Result<bool>> AcknowledgeAsync(
        BusSubscription subscription, BusDelivery delivery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(delivery);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_held.TryRemove(delivery.Token, out var message))
        {
            // Not an error, and IBusConsumer says why: ADR-0036 acknowledges after a commit, so a
            // node that died between the two acknowledges the same token again on the redelivery
            // that follows. The broker no longer holds it and neither does this consumer.
            return false;
        }

        try
        {
            await ReceiverFor(subscription)
                .CompleteMessageAsync(message, cancellationToken)
                .ConfigureAwait(false);

            return true;
        }
        catch (ServiceBusException failure)
            when (failure.Reason == ServiceBusFailureReason.MessageLockLost)
        {
            // The lock lapsed while the flow was running. The message is back on the
            // subscription and will be redelivered; nothing was lost and nothing is wrong.
            return false;
        }
        catch (Exception failure) when (IsBrokerFailure(failure))
        {
            return Result.Fail<bool>(Unreadable(subscription, failure));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <strong>One call, because the broker owns the destination.</strong> <c>FlowX.Redis</c>
    /// copies to a derived stream and then acknowledges, in that order, because a crash between
    /// the two must redeliver rather than lose; here the move and the settlement are the same
    /// operation and the ordering problem does not arise.
    /// </remarks>
    public async ValueTask<Result<bool>> DeadLetterAsync(
        BusSubscription subscription,
        BusDelivery delivery,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_held.TryRemove(delivery.Token, out var message))
        {
            return false;
        }

        try
        {
            await ReceiverFor(subscription)
                .DeadLetterMessageAsync(
                    message,
                    deadLetterReason: Truncate(reason),
                    deadLetterErrorDescription: reason,
                    cancellationToken)
                .ConfigureAwait(false);

            return true;
        }
        catch (ServiceBusException failure)
            when (failure.Reason == ServiceBusFailureReason.MessageLockLost)
        {
            return false;
        }
        catch (Exception failure) when (IsBrokerFailure(failure))
        {
            return Result.Fail<bool>(Unreadable(subscription, failure));
        }
    }

    /// <summary>Closes every receiver this consumer opened.</summary>
    /// <returns>A task that completes when they are closed.</returns>
    /// <remarks>
    /// <strong>Held messages are not abandoned first, deliberately.</strong> Closing the link
    /// releases their locks, and the broker redelivers them — which is the same outcome an
    /// explicit abandon would reach, over a link that is going away anyway, without a round trip
    /// per message on a shutdown path.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var receiver in _receivers.Values)
        {
            await receiver.DisposeAsync().ConfigureAwait(false);
        }

        _receivers.Clear();
        _held.Clear();
    }

    /// <summary>Groups what was received into partitions, oldest first, within the caller's limits.</summary>
    /// <remarks>
    /// The received order is the order the subscription gave the messages up, so each group is in
    /// that order too — which is what <see cref="BusPartitionBatch"/> promises about its
    /// deliveries. A message beyond a limit is abandoned back rather than dropped: unlike a
    /// RabbitMQ consumer, this one holds nothing across passes it did not offer.
    /// </remarks>
    private IReadOnlyList<BusPartitionBatch> Partition(
        BusSubscription subscription,
        IReadOnlyList<ServiceBusReceivedMessage> received,
        int maxPartitions,
        int maxPerPartition)
    {
        var order = new List<string?>();
        var buckets = new Dictionary<string, List<BusDelivery>>(StringComparer.Ordinal);
        var unkeyed = new List<BusDelivery>();

        foreach (var message in received)
        {
            var key = AzureServiceBusHeaders.Text(message, AzureServiceBusHeaders.PartitionKey);
            var bucket = key is null ? Unkeyed() : Bucket(key);

            if (bucket is null || bucket.Count >= maxPerPartition)
            {
                // Over a limit. The lock is left to lapse rather than held: a message this
                // consumer never offered must go back to the subscription, or a caller asking for
                // one partition would quietly starve every other.
                continue;
            }

            var token = message.LockToken;

            _held[token] = message;

            bucket.Add(Read(message, token, subscription.Topic, key));
        }

        return [.. order.Select(key => new BusPartitionBatch(key, key is null ? unkeyed : buckets[key]))];

        List<BusDelivery>? Bucket(string key)
        {
            if (buckets.TryGetValue(key, out var existing))
            {
                return existing;
            }

            if (order.Count >= maxPartitions)
            {
                return null;
            }

            order.Add(key);

            return buckets[key] = [];
        }

        List<BusDelivery>? Unkeyed()
        {
            if (order.Contains(null))
            {
                return unkeyed;
            }

            if (order.Count >= maxPartitions)
            {
                return null;
            }

            order.Add(null);

            return unkeyed;
        }
    }

    /// <summary>Turns a received message into a delivery, readable or not.</summary>
    /// <remarks>
    /// <strong>An unreadable entry is still a delivery</strong>, for
    /// <see cref="BusDelivery.Unreadable"/>'s reason: a plugin that dropped what it could not
    /// parse would leave the subscription holding it until its delivery count ran out, with
    /// nothing recording why. The two fields that can make an entry unreadable are the ones a
    /// non-FlowX publisher would omit: a message id that is not a GUID, and an absent subject.
    /// </remarks>
    private static BusDelivery Read(
        ServiceBusReceivedMessage message, string token, string topic, string? key)
    {
        // Service Bus already counts the delivery in hand — one on a first delivery — which is
        // exactly what IBusConsumer means by "including now". Adding one here would double-count
        // and would make ADR-0038's bound fire a delivery early on every message.
        var deliveries = message.DeliveryCount < 1 ? 1 : message.DeliveryCount;

        if (!Guid.TryParse(message.MessageId, out var eventId))
        {
            return BusDelivery.Unreadable(
                token,
                deliveries,
                key,
                $"MessageId '{message.MessageId}' is not a GUID, so this message has no event id.");
        }

        if (message.Subject is not { Length: > 0 } type)
        {
            return BusDelivery.Unreadable(
                token, deliveries, key, "The message carries no Subject, so it has no event type.");
        }

        var payload = message.ContentType == AzureServiceBusHeaders.PayloadContentType
            ? Encoding.UTF8.GetString(message.Body)
            : null;

        return BusDelivery.Of(
            new BusMessage(
                eventId,
                topic,
                type,
                AzureServiceBusHeaders.Text(message, AzureServiceBusHeaders.SchemaVersion) ?? "1.0.0",
                key,
                payload,
                AzureServiceBusHeaders.Text(message, AzureServiceBusHeaders.TenantId)),
            token,
            deliveries);
    }

    /// <summary>Reaches the subscription without taking anything from it.</summary>
    private async ValueTask PeekAsync(BusSubscription subscription, CancellationToken cancellationToken) =>
        _ = await ReceiverFor(subscription)
            .PeekMessageAsync(fromSequenceNumber: null, cancellationToken)
            .ConfigureAwait(false);

    private ServiceBusReceiver ReceiverFor(BusSubscription subscription) =>
        _receivers.GetOrAdd(
            _options.SubscriptionFor(subscription.Group, subscription.Topic),
            (name, state) => state._connection.Client.CreateReceiver(
                state._options.Topic,
                name,
                new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock }),
            this);

    private void Forget(BusSubscription subscription) =>
        _receivers.TryRemove(
            _options.SubscriptionFor(subscription.Group, subscription.Topic), out _);

    /// <summary>Whether this is the namespace failing rather than this package.</summary>
    private static bool IsBrokerFailure(Exception failure) =>
        failure is ServiceBusException
            or TimeoutException
            or System.Net.Sockets.SocketException
            or System.IO.IOException;

    /// <summary>The one refusal this consumer produces, and what an operator does about it.</summary>
    /// <remarks>
    /// <strong>The entity the deployment has to have is named on every failure, not only on the
    /// one the broker labelled <c>MessagingEntityNotFound</c>.</strong> A missing subscription and
    /// an unreachable namespace are told apart by the broker's own words, which travel at the end;
    /// what does not vary — and what somebody woken at night needs — is which subscription this
    /// deployment is looking for and what filter it must carry.
    /// </remarks>
    private Error Unreadable(BusSubscription subscription, Exception failure) =>
        new Error(
            ReceiveFailedCode,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Could not read subscription " +
                $"'{_options.SubscriptionFor(subscription.Group, subscription.Topic)}' of topic " +
                $"'{_options.Topic}'. This package creates no entities: a deployment does, with a " +
                $"correlation filter on Subject = '{subscription.Topic}'. {failure.Message}"),
            ErrorCategory.Unavailable)
            .With("topic", _options.Topic)
            .With("group", subscription.Group)
            .With("eventType", subscription.Topic);

    /// <summary>The broker's own reason field is short; the description carries the whole of it.</summary>
    private static string Truncate(string reason) =>
        reason.Length <= 256 ? reason : reason[..256];
}
