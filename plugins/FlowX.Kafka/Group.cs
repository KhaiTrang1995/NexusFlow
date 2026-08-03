using Confluent.Kafka;

namespace FlowX.Kafka;

/// <summary>
/// One consumer group, and the per-partition ledger that decides how far its offset may move.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The ledger exists because an offset is a watermark and an acknowledgement is
/// not.</strong> <c>IBusConsumer</c> settles deliveries one at a time and in whatever order their
/// flows finish; committing offset <em>n</em> tells Kafka every offset below <em>n</em> is done.
/// Committing on each acknowledgement would therefore declare unfinished work complete the first
/// time two flows finished out of order — and the records skipped would never be redelivered,
/// which is silent loss rather than a visible failure.
/// </para>
/// <para>
/// <strong>So a commit moves to the highest settled offset with no gap below it.</strong> Records
/// settled ahead of a gap stay in the ledger until the gap closes, and then the watermark jumps
/// over all of them at once. A partition whose oldest record never settles never commits, which is
/// correct: that record has to be redelivered, and <c>BusMaxDeliveries</c> is what eventually
/// dead-letters it.
/// </para>
/// <para>
/// <strong>Everything here is under one lock and that is deliberate.</strong> A group serves one
/// subscription and <c>FlowBusScan</c> drives it from one loop; the lock is what makes an
/// acknowledgement arriving from a flow's completion safe against a poll in progress, and a
/// <c>Consume</c> call is not made under it.
/// </para>
/// </remarks>
internal sealed class Group
{
    private readonly object _gate = new();
    private readonly Dictionary<TopicPartition, Ledger> _ledgers = [];
    private readonly IConsumer<string?, byte[]?> _consumer;

    private Group(IConsumer<string?, byte[]?> consumer) => _consumer = consumer;

    /// <summary>The client this group polls with.</summary>
    public IConsumer<string?, byte[]?> Consumer => _consumer;

    /// <summary>Joins a consumer group and subscribes it to the deployment's topic.</summary>
    /// <param name="group">The group id, from <c>[BusTrigger(Group = …)]</c>.</param>
    /// <param name="options">Where the cluster and the topic are.</param>
    /// <returns>The joined group.</returns>
    public static Group Join(string group, KafkaOptions options)
    {
        var consumer = new ConsumerBuilder<string?, byte[]?>(new ConsumerConfig
        {
            BootstrapServers = options.BootstrapServers,
            GroupId = group,

            // Everything from the beginning, once. A group joining a topic that already has a
            // backlog is a deployment that was down, not one that wants to skip what it missed.
            AutoOffsetReset = AutoOffsetReset.Earliest,

            // The whole point of this class: nothing commits except SettleAndCommit.
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,

            // A poll that finds nothing must return rather than block the host's scan loop.
            EnablePartitionEof = true,
        })
        .SetLogHandler(static (_, _) => { })
        .SetErrorHandler(static (_, _) => { })
        .SetPartitionsRevokedHandler(static (_, _) => { })
        .Build();

        consumer.Subscribe(options.Topic);

        return new Group(consumer);
    }

    /// <summary>Records that a record was polled, and counts the delivery.</summary>
    /// <param name="record">What the poll returned.</param>
    public void Hold(ConsumeResult<string?, byte[]?> record)
    {
        lock (_gate)
        {
            LedgerFor(record.TopicPartition).Hold(record);
        }
    }

    /// <summary>How many times this exact offset has been handed out, including now.</summary>
    /// <param name="position">The record's position.</param>
    /// <returns>The count, one on a first delivery.</returns>
    /// <remarks>
    /// <strong>Counted here rather than read off the broker, because Kafka does not count.</strong>
    /// A record has no delivery counter: a rebalance or a restart simply replays from the last
    /// commit. So the count is this process's own, and it resets when the process does — which is
    /// stated rather than hidden, and is why ADR-0038's bound is a backstop on this transport
    /// rather than a guarantee.
    /// </remarks>
    public int DeliveriesOf(TopicPartitionOffset position)
    {
        lock (_gate)
        {
            return LedgerFor(position.TopicPartition).DeliveriesOf(position.Offset.Value);
        }
    }

    /// <summary>The record held at a position, or null when nothing is held there.</summary>
    /// <param name="position">The position.</param>
    /// <returns>The record.</returns>
    public ConsumeResult<string?, byte[]?>? Held(TopicPartitionOffset position)
    {
        lock (_gate)
        {
            return LedgerFor(position.TopicPartition).Held(position.Offset.Value);
        }
    }

    /// <summary>Marks a position done without committing anything.</summary>
    /// <param name="position">The position.</param>
    /// <remarks>
    /// Used for a record of another event type: this group has nothing to do with it, and it must
    /// not hold the watermark. The commit itself waits for the next acknowledgement, so a pass
    /// that read nothing but other types costs no round trip to the coordinator.
    /// </remarks>
    public void Settle(TopicPartitionOffset position)
    {
        lock (_gate)
        {
            LedgerFor(position.TopicPartition).Settle(position.Offset.Value);
        }
    }

    /// <summary>Marks a position done and commits as far as the ledger allows.</summary>
    /// <param name="position">The position.</param>
    /// <returns>True when this call settled something the group was still holding.</returns>
    public bool SettleAndCommit(TopicPartitionOffset position)
    {
        long? commit;
        bool settled;

        lock (_gate)
        {
            var ledger = LedgerFor(position.TopicPartition);

            settled = ledger.Settle(position.Offset.Value);
            commit = ledger.Watermark();
        }

        if (commit is { } offset)
        {
            // The committed offset is the *next* record to read, which is one past the last one
            // finished — Kafka's convention, and getting it wrong replays the last record for ever
            // or skips it.
            _consumer.Commit(
            [
                new TopicPartitionOffset(position.TopicPartition, new Offset(offset + 1)),
            ]);
        }

        return settled;
    }

    /// <summary>Leaves the group cleanly.</summary>
    public void Close()
    {
        try
        {
            _consumer.Close();
        }
        catch (KafkaException)
        {
            // A cluster that has gone away cannot be left politely, and a host shutting down must
            // not fail because of it. The coordinator times the member out instead.
        }

        _consumer.Dispose();
    }

    private Ledger LedgerFor(TopicPartition partition)
    {
        if (!_ledgers.TryGetValue(partition, out var ledger))
        {
            _ledgers[partition] = ledger = new Ledger();
        }

        return ledger;
    }

    /// <summary>What one partition is holding, and how far it may be committed.</summary>
    private sealed class Ledger
    {
        private readonly Dictionary<long, ConsumeResult<string?, byte[]?>> _open = [];
        private readonly Dictionary<long, int> _deliveries = [];
        private readonly SortedSet<long> _settled = [];

        /// <summary>The last offset committed, or null before this partition has held anything.</summary>
        /// <remarks>
        /// <strong>Seeded from the first record polled, not from zero.</strong> A group that
        /// restarts resumes at its last commit, so the first record it sees is offset <em>n</em>
        /// and not offset 0 — and a watermark that insisted on starting from zero would never
        /// move again for the life of the process.
        /// </remarks>
        private long? _committed;

        public void Hold(ConsumeResult<string?, byte[]?> record)
        {
            var offset = record.Offset.Value;

            _committed ??= offset - 1;

            _open[offset] = record;
            _deliveries[offset] = _deliveries.TryGetValue(offset, out var seen) ? seen + 1 : 1;
        }

        public int DeliveriesOf(long offset) =>
            _deliveries.TryGetValue(offset, out var seen) ? seen : 1;

        public ConsumeResult<string?, byte[]?>? Held(long offset) =>
            _open.TryGetValue(offset, out var record) ? record : null;

        public bool Settle(long offset)
        {
            if (!_open.Remove(offset))
            {
                // Already settled, or from a previous assignment of this partition. Not an error:
                // ADR-0036 acknowledges after a commit, so the same token can arrive twice.
                return false;
            }

            _settled.Add(offset);

            return true;
        }

        /// <summary>The highest settled offset with every offset below it settled, or null.</summary>
        public long? Watermark()
        {
            long? highest = null;

            while (_settled.Count > 0)
            {
                var lowest = _settled.Min;

                if (lowest != (_committed ?? lowest - 1) + 1)
                {
                    // A gap. Everything above it stays in the ledger until the missing record is
                    // settled, and the watermark does not move — which is the whole rule.
                    break;
                }

                _settled.Remove(lowest);
                _deliveries.Remove(lowest);

                _committed = lowest;
                highest = lowest;
            }

            return highest;
        }
    }
}
