using System.Globalization;
using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace FlowX.RabbitMq;

/// <summary>
/// Consumes the exchange <see cref="RabbitMqEventPublisher"/> writes to, one quorum queue per
/// subscription, manual acknowledgement, a dead-letter exchange.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The broker does the fan-out, which is the whole of what this buys over
/// <c>RedisStreamBusConsumer</c>.</strong> That class scans a key space for streams and filters
/// each entry by its <c>type</c> field, because Redis has no topic. Here the queue is bound to
/// the subscription's topic as a routing key, so a message that arrives is a message this
/// subscription asked for, three subscriptions to one event type are three queues the broker
/// fills, and a redelivery to one of them does not re-run the other two.
/// </para>
/// <para>
/// <strong>A queue is not a partition, and this class is what stands in for the difference.</strong>
/// A Redis stream <em>is</em> a partition, so reading one front to back reproduces the publisher's
/// order for one key. A queue carries every key interleaved, so the partitions are made here: one
/// pass takes messages in queue order, groups them by their <c>flowx-partition-key</c> header and
/// hands each group back as its own <see cref="BusPartitionBatch"/>, in the order the queue held
/// them.
/// <see href="../../docs/adr/ADR-0072-a-rabbitmq-queue-is-a-partition-only-while-one-node-holds-it.md">ADR-0072</see>
/// is that decision and states exactly where it stops holding.
/// </para>
/// <para>
/// <strong>A delivery this node did not dispose of is held, never requeued.</strong> This is the
/// design's load-bearing detail and it is measured rather than assumed: on RabbitMQ 3.12 a quorum
/// queue returns a requeued message to the <em>back</em> of the queue, so a consumer that nacked
/// what a failing flow left behind would let that key's later events overtake it — the exact
/// reordering <c>FlowBusScan</c> stops its partition loop to prevent. So an un-acknowledged
/// message stays un-acknowledged on this channel, where the broker will not offer it to anyone
/// else, and the next <see cref="ReceiveAsync"/> offers it again first. That is
/// <c>XAUTOCLAIM</c>-before-<c>XREADGROUP</c>, kept in this consumer's own bookkeeping because
/// AMQP has no pending-entries list to keep it in.
/// </para>
/// <para>
/// <strong>Acknowledgement is idempotent because this class makes it so, not because AMQP is.</strong>
/// A second <c>basic.ack</c> for one delivery tag is a channel-level <c>PRECONDITION_FAILED</c>
/// that closes the channel and takes every other outstanding tag with it — measured, not
/// inferred. <see cref="AcknowledgeAsync"/> therefore answers from the held set and touches the
/// broker only for a tag it is still holding, which is what
/// <see href="../../docs/adr/ADR-0036-a-message-is-acknowledged-when-its-flow-is-journalled.md">ADR-0036</see>
/// needs: it acknowledges after a commit, and a node that died between the two acknowledges the
/// same token again on the redelivery that follows.
/// </para>
/// <para>
/// <strong>A refusal is a value and a defect is an exception (ADR-0007)</strong>, exactly as in
/// <see cref="RabbitMqEventPublisher"/>.
/// </para>
/// </remarks>
public sealed class RabbitMqBusConsumer : IBusConsumer, IAsyncDisposable
{
    /// <summary>The broker family this consumer serves.</summary>
    /// <remarks>
    /// Compared against a subscription's declared transport at registration, so a
    /// <c>[KafkaTrigger]</c> wired to this is a startup failure rather than a subscription
    /// consumed from a broker the manifest does not name. A <c>[BusTrigger]</c> names none and is
    /// served by whatever the host wired, which is the point of it.
    /// </remarks>
    public const string TransportName = "rabbitmq";

    /// <summary>What a caller sees when the broker could not be reached.</summary>
    /// <remarks>
    /// One code rather than one per client exception, for
    /// <see cref="RabbitMqEventPublisher.PublishFailedCode"/>'s reason: the caller's decision is
    /// the same for all of them — leave the messages held and try the next pass.
    /// </remarks>
    public const string ConsumeFailedCode = "rabbitmq.consume_failed";

    private readonly RabbitMqConnection _connection;
    private readonly RabbitMqOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Creates a consumer over a connection.</summary>
    /// <param name="connection">
    /// The connection. The caller owns it and its lifetime; the publisher may share it.
    /// </param>
    /// <param name="options">
    /// Which exchange the queues are bound to and what they are called. <strong>Must be the same
    /// options the publisher uses</strong> — the exchange name is what makes one deployment's
    /// events findable and another's invisible, and a consumer bound to a different exchange
    /// reads nothing and says nothing.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    public RabbitMqBusConsumer(RabbitMqConnection connection, RabbitMqOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _connection = connection;
        _options = options ?? new RabbitMqOptions();
    }

    /// <inheritdoc />
    public string Transport => TransportName;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>Six declarations, all idempotent, and the whole topology is here rather than in a
    /// deployment script.</strong> The exchange, the dead-letter exchange, the subscription's
    /// queue, its binding, the dead-letter queue and its binding. A broker restored from an empty
    /// state has forgotten all six, and a consumer that declared them once at startup would then
    /// read nothing for ever — silently, which is the failure this interface's own remarks say
    /// the per-pass call exists to prevent.
    /// </para>
    /// <para>
    /// <strong>The queue is a quorum queue and it has to be.</strong> RabbitMQ 3.12 supports
    /// <c>x-delivery-limit</c> and reports <c>x-delivery-count</c> on a quorum queue and on
    /// neither for a classic one, and both are what
    /// <see href="../../docs/adr/ADR-0038-a-poison-message-is-dead-lettered.md">ADR-0038</see>'s
    /// bound is computed from. A classic queue would leave <see cref="BusDelivery.DeliveryCount"/>
    /// as a boolean dressed up as a number and the poison rule unenforceable.
    /// </para>
    /// <para>
    /// <strong>Whether this call created the subscription is answered before anything is declared,
    /// on a channel of its own.</strong> <c>queue.declare-ok</c> does not say whether the queue was
    /// made or found, and a passive declare of an absent queue is a channel-level 404 that closes
    /// the channel it was asked on — so it is asked on one this method then throws away.
    /// </para>
    /// </remarks>
    public async ValueTask<Result<bool>> SubscribeAsync(
        BusSubscription subscription, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var connection = await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // Asked before StateAsync, because StateAsync declares the queue and the answer would
            // then be "it existed" on the very pass that created it.
            var existed = await ExistsAsync(
                    connection,
                    _options.QueueFor(subscription.Group, subscription.Topic),
                    cancellationToken)
                .ConfigureAwait(false);

            await StateAsync(subscription, cancellationToken).ConfigureAwait(false);

            return !existed;
        }
        catch (Exception failure) when (RabbitMqEventPublisher.IsBrokerFailure(failure))
        {
            await ForgetAsync(subscription).ConfigureAwait(false);

            return Result.Fail<bool>(Failed("The subscription could not be created", failure));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>Held deliveries first, then new ones</strong> — <c>RedisStreamBusConsumer</c>'s
    /// "reclaimed first" with the pending-entries list replaced by this object's own record. A
    /// message the host did not dispose of last pass is at the front of its key's order, and
    /// offering a newer sibling ahead of it would break exactly what
    /// <see href="../../docs/adr/ADR-0037-the-consumer-offers-per-key-order.md">ADR-0037</see>
    /// promises.
    /// </para>
    /// <para>
    /// <strong>The limits shape what is offered, not what is taken.</strong> Messages beyond
    /// <paramref name="maxPartitions"/> keys or <paramref name="maxPerPartition"/> per key stay
    /// held and are offered on the next pass, ahead of anything newer. Nacking them back to the
    /// queue would be the requeue this class exists to avoid.
    /// </para>
    /// </remarks>
    public async ValueTask<Result<IReadOnlyList<BusPartitionBatch>>> ReceiveAsync(
        BusSubscription subscription,
        int maxPartitions,
        int maxPerPartition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (maxPartitions <= 0 || maxPerPartition <= 0)
        {
            return Result.Ok<IReadOnlyList<BusPartitionBatch>>([]);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var state = await StateAsync(subscription, cancellationToken).ConfigureAwait(false);

            await FillAsync(state, maxPartitions * maxPerPartition, cancellationToken).ConfigureAwait(false);

            return Result.Ok(Partition(state, maxPartitions, maxPerPartition));
        }
        catch (Exception failure) when (RabbitMqEventPublisher.IsBrokerFailure(failure))
        {
            await ForgetAsync(subscription).ConfigureAwait(false);

            return Result.Fail<IReadOnlyList<BusPartitionBatch>>(Failed("No message could be read", failure));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <strong><c>false</c> is answered from the held set and never from the broker.</strong> A
    /// token this consumer is no longer holding belongs to a channel that has since been replaced
    /// — the ordinary outcome of ADR-0036 acknowledging after a commit and a node dying between
    /// the two — and asking the broker about it would close the channel every other outstanding
    /// delivery is on.
    /// </remarks>
    public async ValueTask<Result<bool>> AcknowledgeAsync(
        BusSubscription subscription, BusDelivery delivery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(delivery);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var (state, held) = Find(subscription, delivery.Token);

            if (state is null || held is null)
            {
                return false;
            }

            await state.Channel!
                .BasicAckAsync(held.DeliveryTag, multiple: false, cancellationToken)
                .ConfigureAwait(false);

            state.Release(delivery.Token);

            return true;
        }
        catch (Exception failure) when (RabbitMqEventPublisher.IsBrokerFailure(failure))
        {
            await ForgetAsync(subscription).ConfigureAwait(false);

            return Result.Fail<bool>(Failed("The message could not be acknowledged", failure));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>Publish the copy, then acknowledge the original, and the order is the whole of the
    /// guarantee.</strong> A crash between the two redelivers the original, which at-least-once
    /// already permits and which produces at worst a duplicate in the dead-letter queue. The
    /// other order loses the message.
    /// </para>
    /// <para>
    /// <strong>A copy rather than a <c>basic.reject</c>, and the reason is the reason.</strong>
    /// Rejecting without requeue would let the queue's own <c>x-dead-letter-exchange</c> move the
    /// message for nothing — no extra publish, no risk of a duplicate — and RabbitMQ would record
    /// the cause in <c>x-death</c> as the single word <c>rejected</c>. <see cref="IBusConsumer"/>
    /// asks for "why, in a sentence an operator can act on", and ADR-0038's whole value is that
    /// the sentence names which of its two conditions fired. So the sentence is carried on the
    /// copy, and the queue's <c>x-dead-letter-exchange</c> stays declared for the failures this
    /// method is never called for.
    /// </para>
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

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var (state, held) = Find(subscription, delivery.Token);

            if (state is null || held is null)
            {
                return false;
            }

            await state.Channel!
                .BasicPublishAsync(
                    _options.DeadLetterExchange,
                    subscription.Topic,
                    mandatory: false,
                    Diverted(held, reason),
                    held.Body,
                    cancellationToken)
                .ConfigureAwait(false);

            await state.Channel
                .BasicAckAsync(held.DeliveryTag, multiple: false, cancellationToken)
                .ConfigureAwait(false);

            state.Release(delivery.Token);

            return true;
        }
        catch (Exception failure) when (RabbitMqEventPublisher.IsBrokerFailure(failure))
        {
            await ForgetAsync(subscription).ConfigureAwait(false);

            return Result.Fail<bool>(Failed("The message could not be dead-lettered", failure));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes every channel this consumer opened.</summary>
    /// <returns>A task that completes when they are closed.</returns>
    /// <remarks>
    /// Closing a channel requeues everything still held on it, which is what a node shutting down
    /// should do: another node takes the work. The connection is not closed — it is the caller's.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var state in _subscriptions.Values)
        {
            await state.DisposeAsync().ConfigureAwait(false);
        }

        _subscriptions.Clear();
        _gate.Dispose();
    }

    // ------------------------------------------------------------------ topology

    /// <summary>The per-subscription state, with an open channel, creating it if needed.</summary>
    private async ValueTask<Subscription> StateAsync(
        BusSubscription subscription, CancellationToken cancellationToken)
    {
        var key = Key(subscription);

        if (_subscriptions.TryGetValue(key, out var existing))
        {
            if (existing.Channel is { IsOpen: true })
            {
                return existing;
            }

            // The channel died, so every delivery tag it issued is void and the broker has
            // requeued whatever was held on it. Dropping the held set here is what stops this
            // consumer acknowledging a tag a later channel never issued.
            await existing.DisposeAsync().ConfigureAwait(false);
            _subscriptions.Remove(key);
        }

        var connection = await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // One channel per subscription, never one shared: a channel-level error closes the
        // channel, and a shared one would take every other subscription's outstanding deliveries
        // down with the subscription that made the mistake. Confirms are on because the
        // dead-letter copy goes out on this channel, and a copy that was not confirmed before the
        // original is acknowledged is the loss ADR-0038 exists to prevent.
        var channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true),
                cancellationToken)
            .ConfigureAwait(false);

        var state = new Subscription(
            channel,
            _options.QueueFor(subscription.Group, subscription.Topic),
            _options.DeadLetterQueueFor(subscription.Group, subscription.Topic),
            subscription.Topic);

        _subscriptions[key] = state;

        await DeclareAsync(state, cancellationToken).ConfigureAwait(false);

        return state;
    }

    /// <summary>Declares the whole of one subscription's topology, idempotently.</summary>
    private async ValueTask DeclareAsync(Subscription state, CancellationToken cancellationToken)
    {
        var channel = state.Channel!;

        await channel.ExchangeDeclareAsync(
                _options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await channel.ExchangeDeclareAsync(
                _options.DeadLetterExchange, ExchangeType.Topic, durable: true, autoDelete: false,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await channel.QueueDeclareAsync(
                state.Queue, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    // Quorum, for x-delivery-count and x-delivery-limit — see SubscribeAsync.
                    ["x-queue-type"] = "quorum",
                    ["x-dead-letter-exchange"] = _options.DeadLetterExchange,
                    ["x-delivery-limit"] = _options.DeliveryLimit,
                },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // The binding key is the topic exactly, not a pattern. A subscription's address is the
        // event type the manifest published (ADR-0039), and a wildcard here would let a
        // deployment's binding decide which events a compiled flow receives.
        await channel.QueueBindAsync(
                state.Queue, _options.Exchange, state.Topic, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await channel.QueueDeclareAsync(
                state.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["x-queue-type"] = "quorum",
                },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await channel.QueueBindAsync(
                state.DeadLetterQueue, _options.DeadLetterExchange, state.Topic,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Whether the queue is already there, asked on a channel this can afford to lose.</summary>
    private static async ValueTask<bool> ExistsAsync(
        IConnection connection, string queue, CancellationToken cancellationToken)
    {
        var probe = await connection
            .CreateChannelAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await probe.QueueDeclarePassiveAsync(queue, cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (OperationInterruptedException absent) when (absent.ShutdownReason?.ReplyCode == 404)
        {
            return false;
        }
        finally
        {
            probe.Dispose();
        }
    }

    // ------------------------------------------------------------------ receiving

    /// <summary>Takes messages off the queue until the held set is full or the queue is empty.</summary>
    /// <remarks>
    /// <c>basic.get</c> rather than <c>basic.consume</c>, and it is the same choice
    /// <see cref="IBusConsumer"/>'s own remarks make one level up: a push consumer would put the
    /// flow-starting decision inside a plugin and would deliver on the client's dispatch threads,
    /// where the lease, the journal and <c>FlowHost</c> are not. The cost is one round trip per
    /// message, which is stated rather than hidden.
    /// </remarks>
    private static async ValueTask FillAsync(
        Subscription state, int capacity, CancellationToken cancellationToken)
    {
        while (state.Count < capacity)
        {
            var got = await state.Channel!
                .BasicGetAsync(state.Queue, autoAck: false, cancellationToken)
                .ConfigureAwait(false);

            if (got is null)
            {
                return;
            }

            state.Hold(new Held(
                state.TokenFor(got.DeliveryTag),
                got.DeliveryTag,
                got.BasicProperties,
                got.Body,
                RabbitMqHeaders.Text(got.BasicProperties, RabbitMqHeaders.PartitionKey),
                RedeliveriesOf(got.BasicProperties)));
        }
    }

    /// <summary>How many times the broker had already delivered this message before now.</summary>
    /// <remarks>
    /// Read from the quorum queue's <c>x-delivery-count</c> header, which is where RabbitMQ keeps
    /// it: absent on a first delivery and <c>n</c> on the delivery after the <c>n</c>th. A count
    /// that has run past <see cref="int"/> is reported at the ceiling rather than wrapping into a
    /// small number, because ADR-0038's bound is a "greater than" test and a wrapped count would
    /// silently stop dead-lettering the most poisonous message on the queue.
    /// </remarks>
    private static int RedeliveriesOf(IReadOnlyBasicProperties properties)
    {
        var redeliveries = RabbitMqHeaders.Number(properties, RabbitMqHeaders.DeliveryCount) ?? 0L;

        return redeliveries >= int.MaxValue ? int.MaxValue : (int)redeliveries;
    }

    /// <summary>Groups what is held into partitions, oldest first, within the caller's limits.</summary>
    /// <remarks>
    /// The held set is in the order the queue gave the messages up, so each group is in that
    /// order too, which is what <see cref="BusPartitionBatch"/> promises about its deliveries. A
    /// message beyond a limit is skipped rather than dropped: it is still held, and the next pass
    /// finds it in front of anything taken since.
    /// </remarks>
    private static IReadOnlyList<BusPartitionBatch> Partition(
        Subscription state, int maxPartitions, int maxPerPartition)
    {
        var order = new List<string?>();
        var buckets = new Dictionary<string, List<BusDelivery>>(StringComparer.Ordinal);
        var unkeyed = new List<BusDelivery>();

        foreach (var held in state.Holdings)
        {
            var bucket = held.PartitionKey is { } key ? Bucket(key) : Unkeyed();

            if (bucket is null || bucket.Count >= maxPerPartition)
            {
                continue;
            }

            // Offering it is a delivery, and it is counted here rather than at the fetch: a
            // message this consumer holds across passes is never handed back to the broker, so
            // RabbitMQ's own count does not move and ADR-0038's bound would never be reached. See
            // Held.Deliveries.
            bucket.Add(Read(held.Offered(), state.Topic));
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

            var bucket = new List<BusDelivery>();

            buckets[key] = bucket;
            order.Add(key);

            return bucket;
        }

        List<BusDelivery>? Unkeyed()
        {
            if (unkeyed.Count > 0)
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

    /// <summary>Reads one held message into a delivery, or into the reason it is not one.</summary>
    /// <remarks>
    /// <strong>A message that cannot be read is returned rather than skipped.</strong> Skipping it
    /// would leave it held for ever with nothing willing to take it, which is the head-of-line
    /// block ADR-0038 exists to end. It comes back as <see cref="BusDelivery.Unreadable"/> and the
    /// caller dead-letters it on its first delivery.
    /// </remarks>
    private static BusDelivery Read(Held held, string topic)
    {
        var rawId = held.Properties.MessageId;

        if (rawId is null or { Length: 0 })
        {
            return BusDelivery.Unreadable(
                held.Token, held.Deliveries, held.PartitionKey,
                "it carries no AMQP 'message-id', so no instance id can be derived from it and a " +
                "redelivery could not be recognised");
        }

        if (!Guid.TryParse(rawId, out var eventId))
        {
            return BusDelivery.Unreadable(
                held.Token, held.Deliveries, held.PartitionKey,
                $"its AMQP 'message-id' is '{rawId}', which is not a GUID");
        }

        if (held.Properties.Type is not { Length: > 0 } type)
        {
            return BusDelivery.Unreadable(
                held.Token, held.Deliveries, held.PartitionKey,
                "it carries no AMQP 'type', so nothing says what it is");
        }

        return BusDelivery.Of(
            new BusMessage(
                eventId,
                topic,
                type,
                RabbitMqHeaders.Text(held.Properties, RabbitMqHeaders.SchemaVersion) ?? string.Empty,
                held.PartitionKey,

                // The content type says whether there is a body at all, so an event whose contract
                // has none is null here rather than the empty string a zero-length body would
                // otherwise be indistinguishable from.
                held.Properties.ContentType is null ? null : Encoding.UTF8.GetString(held.Body.Span),

                // Absent is null and not a refusal here: whether an untenanted message may start a
                // flow is admission's decision, and a consumer that refused it would be deciding
                // isolation in a plugin.
                RabbitMqHeaders.Text(held.Properties, RabbitMqHeaders.TenantId)),
            held.Token,
            held.Deliveries);
    }

    /// <summary>The properties a dead-letter copy carries: the original's, plus why and when.</summary>
    private static BasicProperties Diverted(Held held, string reason)
    {
        var headers = held.Properties.Headers is { } original
            ? new Dictionary<string, object?>(original, StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal);

        headers[RabbitMqHeaders.DeadLetterReason] = reason;
        headers[RabbitMqHeaders.DeadLetterAt] =
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        return new BasicProperties
        {
            // The original's identity, deliberately: a dead-lettered event is the event, and an
            // operator returning it to the source queue by hand must not have to mint a new id
            // that the derived instance id would then fail to recognise as a redelivery.
            MessageId = held.Properties.MessageId,
            Type = held.Properties.Type,
            ContentType = held.Properties.ContentType,
            Persistent = true,
            Headers = headers,
        };
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The subscription and the message a token names, or nulls when it is not held.</summary>
    private (Subscription? State, Held? Held) Find(BusSubscription subscription, string token) =>
        _subscriptions.TryGetValue(Key(subscription), out var state) && state.Find(token) is { } held
            ? (state, held)
            : (null, null);

    /// <summary>Forgets a subscription's channel and everything held on it.</summary>
    /// <remarks>
    /// Called on every broker failure, because a channel that raised one is closed by the
    /// protocol and its delivery tags are void. The messages are not lost: the broker requeues
    /// what an unclosed channel was holding.
    /// </remarks>
    private async ValueTask ForgetAsync(BusSubscription subscription)
    {
        if (_subscriptions.Remove(Key(subscription), out var state))
        {
            await state.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What makes two subscriptions the same subscription here: the pair the queue is named for.
    /// </summary>
    private static string Key(BusSubscription subscription) =>
        subscription.Group + " " + subscription.Topic;

    /// <summary>Turns a client failure into the value the contract hands back.</summary>
    private static Error Failed(string what, Exception failure) => new(
        ConsumeFailedCode,
        $"{what}: {failure.Message}",
        ErrorCategory.Unavailable);

    /// <summary>One message this consumer is holding un-acknowledged.</summary>
    /// <param name="Token">What the caller quotes back to acknowledge or divert it.</param>
    /// <param name="DeliveryTag">The channel-scoped tag that acknowledges it.</param>
    /// <param name="Properties">Its AMQP properties, kept so a dead-letter copy carries them.</param>
    /// <param name="Body">Its body, kept for the same reason.</param>
    /// <param name="PartitionKey">The key whose order it belongs to, or null.</param>
    /// <param name="Redeliveries">
    /// How many times RabbitMQ had already delivered it when this consumer took it — the quorum
    /// queue's <c>x-delivery-count</c>, which counts the times it went back to the broker and came
    /// out again.
    /// </param>
    private sealed record Held(
        string Token,
        ulong DeliveryTag,
        IReadOnlyBasicProperties Properties,
        ReadOnlyMemory<byte> Body,
        string? PartitionKey,
        int Redeliveries)
    {
        private int _offers;

        /// <summary>
        /// How many times this message has been handed out, including now, in the sense
        /// <see cref="BusDelivery.DeliveryCount"/> means it.
        /// </summary>
        /// <remarks>
        /// <strong>The broker's count plus this consumer's own, and the second half is not
        /// optional.</strong> A held delivery is never returned to the queue, so RabbitMQ's
        /// <c>x-delivery-count</c> stops moving the moment this consumer takes it — and a message
        /// whose flow refuses every pass would then be re-offered for ever with the count stuck at
        /// one, which is ADR-0038's bound quietly disabled and its head-of-line block back.
        /// </remarks>
        public int Deliveries => Redeliveries + _offers;

        /// <summary>Records that this message is being offered, and returns it.</summary>
        public Held Offered()
        {
            _offers++;

            return this;
        }
    }

    /// <summary>One subscription's channel, queue names and held deliveries.</summary>
    /// <remarks>
    /// <strong>The held set is a list and not a dictionary, and that is a correctness choice
    /// rather than a size one.</strong> The order the queue gave these messages up is the order
    /// they must be offered back in, and a dictionary reuses the slot an acknowledged entry left
    /// behind — so the next message taken off the queue would be enumerated where an older one
    /// used to be. The set is bounded by <c>maxPartitions × maxPerPartition</c>, so a linear
    /// lookup on a token is a scan of a few dozen entries.
    /// </remarks>
    private sealed class Subscription(
        IChannel channel, string queue, string deadLetterQueue, string topic) : IAsyncDisposable
    {
        private static long _epochs;

        private readonly List<Held> _held = [];

        /// <summary>
        /// Which channel a delivery tag belongs to, so a token from a dead one cannot be mistaken
        /// for a live tag on a new one. Tags restart at 1 on every channel.
        /// </summary>
        private readonly long _epoch = Interlocked.Increment(ref _epochs);

        public IChannel? Channel { get; private set; } = channel;

        public string Queue { get; } = queue;

        public string DeadLetterQueue { get; } = deadLetterQueue;

        public string Topic { get; } = topic;

        public int Count => _held.Count;

        public IReadOnlyList<Held> Holdings => _held;

        public string TokenFor(ulong deliveryTag) =>
            _epoch.ToString(CultureInfo.InvariantCulture) + ":" +
            deliveryTag.ToString(CultureInfo.InvariantCulture);

        public void Hold(Held held) => _held.Add(held);

        public Held? Find(string token) =>
            _held.Find(held => string.Equals(held.Token, token, StringComparison.Ordinal));

        public void Release(string token) =>
            _held.RemoveAll(held => string.Equals(held.Token, token, StringComparison.Ordinal));

        public async ValueTask DisposeAsync()
        {
            var open = Channel;

            Channel = null;
            _held.Clear();

            if (open is null)
            {
                return;
            }

            try
            {
                await open.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is RabbitMQClientException or ObjectDisposedException)
            {
                // Already gone, which is why it is being disposed.
            }

            open.Dispose();
        }
    }
}
