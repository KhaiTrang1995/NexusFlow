namespace FlowX.Redis;

/// <summary>Where in a Redis key space the event streams live, and how long they are kept.</summary>
/// <remarks>
/// Separate from <see cref="RedisLeaseOptions"/> rather than folded into it, because the two
/// adapters are wired independently: a deployment may take its leases from Redis and its broker
/// from somewhere else, or the reverse. Sharing one options record would make the prefix of one
/// a property of the other, and a host that wired only the publisher would still be configuring
/// a lease store.
/// </remarks>
public sealed record RedisStreamOptions
{
    /// <summary>The prefix every key this publisher writes begins with. Defaults to <c>flowx</c>.</summary>
    /// <remarks>
    /// Namespacing matters more here than in a broker with named topics: a Redis stream is a key,
    /// so two deployments sharing one Redis instance share one topic space unless somebody says
    /// otherwise — and a consumer reading the wrong deployment's events is a silent fan-out
    /// rather than an error.
    /// </remarks>
    public string KeyPrefix { get; init; } = "flowx";

    /// <summary>
    /// Which logical database to use, or <c>-1</c> for the one the connection selected.
    /// </summary>
    /// <remarks>
    /// Left at <c>-1</c> by default, for the reason <see cref="RedisLeaseOptions.Database"/>
    /// gives: Redis Cluster rejects <c>SELECT</c> for anything other than database 0, so a
    /// non-default value here is a single-node-only configuration.
    /// </remarks>
    public int Database { get; init; } = -1;

    /// <summary>
    /// Roughly how many entries to keep per stream, or null to keep every entry for ever.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Null by default, and that is the safe default rather than the tidy one.</strong>
    /// Trimming a stream deletes events a consumer may not have read yet, and this package has
    /// no way to know whether one has: the outbox's guarantee ends when the event reaches the
    /// broker (<c>docs/11-Distributed-Runtime.md §5</c>), and what happens after that is a
    /// retention decision belonging to whoever runs the consumers. A publisher that silently
    /// discarded unread events would convert an operational choice into data loss.
    /// </para>
    /// <para>
    /// When set, the trim is approximate — <c>MAXLEN ~ n</c> — because the exact form makes
    /// <c>XADD</c> O(n) in the number of entries it removes, and an exact bound on a queue whose
    /// purpose is to be drained buys nothing. The stream may therefore hold somewhat more than
    /// this number; it will not hold unboundedly more.
    /// </para>
    /// </remarks>
    public int? MaxStreamLength { get; init; }
}
