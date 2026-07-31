namespace FlowX.Postgres;

/// <summary>
/// How the outbox publisher polls, and how much it takes at a time.
/// </summary>
/// <remarks>
/// Polling rather than change data capture, which is the choice
/// <c>docs/11-Distributed-Runtime.md §5</c> records: polling needs no extra infrastructure,
/// and CDC — Debezium against the write-ahead log — scales further and arrives as a plugin
/// when something needs it to. Neither number below is load-bearing for correctness; both
/// are latency-versus-load, which is why they are settings rather than constants.
/// </remarks>
public sealed record PostgresOutboxOptions
{
    private readonly int _batchSize = 500;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many pending events one pass claims.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    /// <remarks>
    /// 500 by default, from <c>docs/11-Distributed-Runtime.md §5</c>'s table, which states
    /// the reason as balancing latency and throughput. The batch is also the unit a claim
    /// holds row locks for, so a larger one keeps more of the table unavailable to a second
    /// publisher for longer.
    /// </remarks>
    public int BatchSize
    {
        get => _batchSize;
        init => _batchSize = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(value), value, "A publisher that claims no rows publishes nothing.");
    }

    /// <summary>
    /// How long <c>RunAsync</c> waits after a pass that found nothing.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    /// <remarks>
    /// Only after an <em>empty</em> pass. A pass that filled its batch is followed
    /// immediately by another, because the interval is a floor on how stale the outbox can
    /// get when it is idle, not a rate limit on draining a backlog.
    /// </remarks>
    public TimeSpan PollInterval
    {
        get => _pollInterval;
        init => _pollInterval = value >= TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(value), value, "A poll interval cannot be negative.");
    }
}
