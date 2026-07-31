namespace FlowX;

/// <summary>Time-bounded exclusive ownership of one flow instance, with a fencing token.</summary>
/// <param name="InstanceId">The instance this lease covers.</param>
/// <param name="OwnerNode">Which node holds it, for operator queries and for its own renewals.</param>
/// <param name="Token">
/// The fencing token issued at this acquisition. Strictly greater than every token issued
/// for this instance before it.
/// </param>
/// <param name="ExpiresAt">
/// When the lease lapses if it is not renewed. Recovery latency is bounded by this, which is
/// a tuning trade-off; correctness is bounded by <paramref name="Token"/>, which is not.
/// </param>
public sealed record FlowLease(Guid InstanceId, string OwnerNode, FencingToken Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Issues and tracks the leases that make exactly-one-writer true, and the monotonic tokens
/// that make it true even when a clock is wrong.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A TTL is not a correctness mechanism, and this interface is shaped so that it
/// cannot be mistaken for one.</strong> Expiry decides how quickly a crashed node's work is
/// picked up — a latency question. What decides whether a paused node can corrupt an
/// instance is <see cref="FencingToken"/>, which the journal checks on every write. A store
/// that issues a well-behaved TTL and a token that does not increase has implemented the
/// unsafe half of ADR-0006.
/// </para>
/// <para>
/// <strong>The caller has one obligation this interface cannot enforce:</strong> after a
/// successful <see cref="AcquireAsync"/>, raise the journal's fence with the new token via
/// <see cref="IFlowJournal.FenceAsync"/> before doing anything else. A lease store and a
/// journal are separate plugins with no shared transaction, so the token has to be carried
/// between them by the node that won it.
/// </para>
/// <para>
/// Every member here is pinned by <c>LeaseStoreConformance</c>. Redis and Postgres are
/// expected to differ in every respect except the behaviour that suite describes.
/// </para>
/// </remarks>
public interface ILeaseStore
{
    /// <summary>
    /// Takes ownership of an instance for a bounded time, issuing a new fencing token.
    /// </summary>
    /// <param name="instanceId">The instance to take.</param>
    /// <param name="ownerNode">The acquiring node's identity.</param>
    /// <param name="ttl">How long ownership lasts without a renewal.</param>
    /// <param name="cancellationToken">Cancels the store call.</param>
    /// <returns>
    /// The new lease, or <see cref="DurabilityErrors.LeaseHeld"/> while one is live —
    /// including when the caller is the node that already holds it. A holder renews; it does
    /// not acquire again.
    /// </returns>
    ValueTask<Result<FlowLease>> AcquireAsync(
        Guid instanceId,
        string ownerNode,
        TimeSpan ttl,
        CancellationToken cancellationToken);

    /// <summary>
    /// Extends a lease the caller still holds, keeping its token.
    /// </summary>
    /// <param name="lease">The lease as it was issued or last renewed.</param>
    /// <param name="ttl">How much longer ownership should last.</param>
    /// <param name="cancellationToken">Cancels the store call.</param>
    /// <returns>
    /// The extended lease, or <see cref="DurabilityErrors.LeaseLost"/> if it expired or
    /// another node has acquired since. Renewal never resurrects a lost lease — that is
    /// precisely the split brain the token exists to prevent.
    /// </returns>
    ValueTask<Result<FlowLease>> RenewAsync(
        FlowLease lease,
        TimeSpan ttl,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gives up a lease so another node can take it without waiting for expiry.
    /// </summary>
    /// <param name="lease">The lease to release.</param>
    /// <param name="cancellationToken">Cancels the store call.</param>
    /// <returns>
    /// True when this call released it, or <see cref="DurabilityErrors.LeaseLost"/> when the
    /// caller no longer owns it. A stale token must not be able to release the lease its
    /// successor is holding.
    /// </returns>
    /// <remarks>
    /// Releasing on shutdown is what makes a rolling update a non-event: recovery starts
    /// immediately instead of after a TTL (<c>docs/11-Distributed-Runtime.md §7</c>).
    /// </remarks>
    ValueTask<Result<bool>> ReleaseAsync(FlowLease lease, CancellationToken cancellationToken);

    /// <summary>Reads the live lease on an instance.</summary>
    /// <param name="instanceId">The instance to look at.</param>
    /// <param name="cancellationToken">Cancels the store call.</param>
    /// <returns>
    /// The live lease, or <see cref="DurabilityErrors.LeaseNotHeld"/> when nobody holds one —
    /// including when the last one has expired, so that expiry is observable rather than
    /// inferred.
    /// </returns>
    ValueTask<Result<FlowLease>> ReadAsync(Guid instanceId, CancellationToken cancellationToken);
}
