namespace FlowX.Conformance.InMemory;

/// <summary>
/// An <see cref="IBusConsumer"/> that keeps its messages in a dictionary and behaves like a
/// consumer group while it does.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The counterpart of <see cref="RecordingEventPublisher"/>, and it exists for the same
/// reason.</strong> Every decision <c>FlowBusScan</c> takes — the derived instance id, when a
/// message is acknowledged, whether a partition's order is kept, what happens to a poison entry —
/// is a decision about the <em>runtime</em>, and asserting it against a real Redis would make
/// those assertions cost a server. So they are asserted here, and the same assertions are then
/// run against a real broker by <c>RedisStreamBusConsumerTests</c>, where what is being checked
/// is that Redis behaves the way this double claims a broker does.
/// </para>
/// <para>
/// <strong>It models the two broker behaviours the design depends on and no others.</strong> A
/// delivery is held in a pending set until it is acknowledged or dead-lettered, and it is offered
/// again on the next receive with its delivery count raised — which is at-least-once, and it is
/// the thing that makes <c>ARedeliveredMessageStartsNoSecondFlow</c> a test of something. It does
/// not model latency, partial network failures or a visibility timeout; those are the real
/// broker's to prove.
/// </para>
/// </remarks>
public sealed class RecordingBusConsumer : IBusConsumer
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _acknowledged = [];
    private readonly List<DeadLetterRecord> _deadLettered = [];
    private readonly HashSet<string> _groups = new(StringComparer.Ordinal);
    private int _next;

    /// <inheritdoc />
    public string Transport => "recording";

    /// <summary>The tokens acknowledged, in the order they were.</summary>
    public IReadOnlyList<string> Acknowledged
    {
        get
        {
            lock (_gate)
            {
                return [.. _acknowledged];
            }
        }
    }

    /// <summary>What was diverted, and why.</summary>
    public IReadOnlyList<DeadLetterRecord> DeadLettered
    {
        get
        {
            lock (_gate)
            {
                return [.. _deadLettered];
            }
        }
    }

    /// <summary>The tokens still outstanding, which is what a real broker would redeliver.</summary>
    public IReadOnlyList<string> Pending
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Keys.Order(StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>The groups <c>SubscribeAsync</c> was asked to create.</summary>
    public IReadOnlyCollection<string> Groups
    {
        get
        {
            lock (_gate)
            {
                return [.. _groups];
            }
        }
    }

    /// <summary>Puts a message on the broker.</summary>
    /// <param name="topic">The event type, which is also the subscription's topic.</param>
    /// <param name="partitionKey">The key whose order it belongs to.</param>
    /// <param name="eventId">Its identity. A fresh one by default; pass one to redeliver.</param>
    /// <param name="payload">The body.</param>
    /// <param name="copies">
    /// How many separate entries to stage, all carrying the same identity — which is what a broker
    /// offering one message to ten nodes looks like from here.
    /// </param>
    /// <param name="tenantId">
    /// The tenant the publishing side wrote onto the entry, or null for a message from a
    /// deployment that does not isolate.
    /// </param>
    /// <returns>The identity staged, so a caller can redeliver it.</returns>
    public Guid Stage(
        string topic,
        string? partitionKey = null,
        Guid? eventId = null,
        string? payload = null,
        int copies = 1,
        string? tenantId = null)
    {
        var identity = eventId ?? Guid.NewGuid();

        lock (_gate)
        {
            for (var copy = 0; copy < copies; copy++)
            {
                var token = NextToken();

                _entries[token] = new Entry(
                    partitionKey,
                    new BusMessage(
                        identity, topic, topic, "1.0.0", partitionKey, payload, tenantId),
                    Unreadable: null,
                    Deliveries: 0);
            }
        }

        return identity;
    }

    /// <summary>Puts an entry on the broker that is not a message.</summary>
    /// <param name="topic">The topic whose subscription will be offered it.</param>
    /// <param name="partitionKey">The partition it blocks.</param>
    /// <returns>The token, so a test can assert on exactly this entry.</returns>
    public string StageUnreadable(string topic, string? partitionKey = null)
    {
        ArgumentNullException.ThrowIfNull(topic);

        lock (_gate)
        {
            var token = NextToken();

            _entries[token] = new Entry(
                partitionKey,
                Message: null,
                Unreadable: "its event-id field is absent",
                Deliveries: 0);

            return token;
        }
    }

    /// <inheritdoc />
    public ValueTask<Result<bool>> SubscribeAsync(
        BusSubscription subscription, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        lock (_gate)
        {
            return ValueTask.FromResult(
                Result.Ok(_groups.Add(subscription.Group + "\0" + subscription.Topic)));
        }
    }

    /// <inheritdoc />
    public ValueTask<Result<IReadOnlyList<BusPartitionBatch>>> ReceiveAsync(
        BusSubscription subscription,
        int maxPartitions,
        int maxPerPartition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        lock (_gate)
        {
            var batches = _entries
                .Where(entry => Matches(entry.Value, subscription.Topic))
                .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                .GroupBy(static entry => entry.Value.PartitionKey, StringComparer.Ordinal)
                .Take(maxPartitions)
                .Select(partition => new BusPartitionBatch(
                    partition.Key,
                    [.. partition.Take(maxPerPartition).Select(entry => Offer(entry.Key))]))
                .ToList();

            return ValueTask.FromResult(Result.Ok<IReadOnlyList<BusPartitionBatch>>(batches));
        }
    }

    /// <inheritdoc />
    public ValueTask<Result<bool>> AcknowledgeAsync(
        BusSubscription subscription, BusDelivery delivery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        lock (_gate)
        {
            // Idempotent, as the contract requires: a node may die between the commit and this
            // call, and the redelivery that follows acknowledges the same token again.
            var held = _entries.Remove(delivery.Token);

            if (held)
            {
                _acknowledged.Add(delivery.Token);
            }

            return ValueTask.FromResult(Result.Ok(held));
        }
    }

    /// <inheritdoc />
    public ValueTask<Result<bool>> DeadLetterAsync(
        BusSubscription subscription,
        BusDelivery delivery,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        lock (_gate)
        {
            // Copy first, then acknowledge — the order ADR-0038 requires, modelled here so a
            // broker plugin that got it the wrong way round has something to be compared against.
            _deadLettered.Add(new DeadLetterRecord(delivery.Token, delivery.Message, reason));

            return ValueTask.FromResult(Result.Ok(_entries.Remove(delivery.Token)));
        }
    }

    /// <summary>Hands one entry out, counting the delivery.</summary>
    private BusDelivery Offer(string token)
    {
        var entry = _entries[token] with { Deliveries = _entries[token].Deliveries + 1 };

        _entries[token] = entry;

        return entry.Message is { } message
            ? BusDelivery.Of(message, token, entry.Deliveries)
            : BusDelivery.Unreadable(token, entry.Deliveries, entry.PartitionKey, entry.Unreadable!);
    }

    /// <summary>
    /// Whether this entry belongs to the subscription's topic.
    /// </summary>
    /// <remarks>
    /// An unreadable entry matches every topic, because nothing about it says which topic it was
    /// for — which is the honest model: a real consumer group reading a partition is offered the
    /// entry whether or not it can read the field that would have routed it.
    /// </remarks>
    private static bool Matches(Entry entry, string topic) =>
        entry.Message is null || string.Equals(entry.Message.Type, topic, StringComparison.Ordinal);

    private string NextToken() =>
        (++_next).ToString("d6", System.Globalization.CultureInfo.InvariantCulture);

    private sealed record Entry(
        string? PartitionKey, BusMessage? Message, string? Unreadable, int Deliveries);
}

/// <summary>One entry this broker diverted, and the reason it was given.</summary>
/// <param name="Token">The delivery's token.</param>
/// <param name="Message">The message, or null when the entry was never one.</param>
/// <param name="Reason">Why it was diverted.</param>
public sealed record DeadLetterRecord(string Token, BusMessage? Message, string Reason);
