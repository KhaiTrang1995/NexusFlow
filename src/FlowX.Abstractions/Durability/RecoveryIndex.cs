namespace FlowX;

/// <summary>
/// The one query a recovery scan needs and <see cref="IFlowJournal"/> deliberately does not
/// answer: which instances look like a node died holding them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Separate from <see cref="IFlowJournal"/>, and separate on purpose.</strong> The
/// journal's contract is what an <em>executing</em> instance needs — start, fence, commit,
/// complete, read back — and every member of it is pinned by <c>JournalConformance</c>. A
/// scan is not part of executing an instance: it is a background sweep whose shape depends
/// on how a store indexes, whether it can page, and what a table scan costs there. Folding
/// it in would widen a contract every store has to implement in order to serve a job some
/// deployments do not run at all.
/// </para>
/// <para>
/// <strong>It answers "looks abandoned", not "is abandoned", and cannot do better.</strong>
/// A journal knows nothing about leases — they live in a different store, possibly a
/// different technology (<c>docs/11-Distributed-Runtime.md §1</c>) — so the most it can say
/// is that an instance is unfinished and has not been written to for a while.
/// <see cref="ILeaseStore.AcquireAsync"/> is the arbiter that turns that into a decision,
/// and losing that race is free: <see cref="DurabilityErrors.LeaseHeld"/> means another node
/// got there first, which is a skip and not a failure.
/// </para>
/// <para>
/// Optional. A store implements this when it can serve the query cheaply; a host whose
/// journal does not is a host that runs no recovery scan, which is the state P2 was in
/// before WP-55 and a defensible state for a single-node deployment to stay in.
/// </para>
/// </remarks>
public interface IRecoveryIndex
{
    /// <summary>Lists instances that are unfinished and have not been written to recently.</summary>
    /// <param name="query">Which instances to consider, and how many to return.</param>
    /// <param name="cancellationToken">Cancels the store call.</param>
    /// <returns>
    /// The candidates, oldest first, or the store's error. An empty list is the ordinary
    /// answer and is not an error.
    /// </returns>
    /// <remarks>
    /// Ordering by staleness rather than by id is what keeps a permanently unrecoverable
    /// instance — one whose flow version this deployment no longer carries — from occupying
    /// the head of every page for ever: it is stale, so are the ones behind it, and a bounded
    /// page still advances as instances are resumed and their rows are touched.
    /// </remarks>
    ValueTask<Result<IReadOnlyList<AbandonedInstance>>> ListAbandonedAsync(
        AbandonedInstanceQuery query,
        CancellationToken cancellationToken);
}

/// <summary>Which unfinished instances a scan is asking about.</summary>
/// <remarks>
/// A record rather than a parameter list because the shape will grow — a shard predicate and
/// a flow filter are both foreseeable — and adding a property is a smaller change to every
/// store than adding a parameter.
/// </remarks>
public sealed record AbandonedInstanceQuery
{
    /// <summary>
    /// Only instances whose row has not changed since this instant.
    /// </summary>
    /// <remarks>
    /// <strong>This is the first and cheapest of the anti-stampede measures.</strong> A node
    /// executing an instance touches its row at every step boundary, so a healthy instance
    /// falls out of the candidate set at the store rather than being fetched, rejected by the
    /// lease store and discarded. Set it a lease TTL into the past: an instance that has not
    /// been written to for longer than a lease lives has either lost its owner or is inside
    /// a single step that is outliving its own lease, and the second is a case only
    /// acquisition can settle.
    /// </remarks>
    public required DateTimeOffset IdleBefore { get; init; }

    /// <summary>How many candidates to return at most.</summary>
    /// <remarks>
    /// Bounded with no way to ask for everything. A scan that pulled an entire backlog would
    /// turn one node's recovery into every node's memory pressure, and the work a node can
    /// actually take is bounded by its recovery concurrency anyway.
    /// </remarks>
    public int Limit { get; init; } = 64;

    /// <summary>Restrict the scan to one tenant, or null for every tenant this node serves.</summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// How many of <see cref="Limit"/> one tenant may occupy, or zero for no cap.
    /// </summary>
    /// <remarks>
    /// <see cref="DueInstanceQuery.PerTenantLimit"/>'s reason, applied to the other sweep: a
    /// page ordered by staleness and bounded by <see cref="Limit"/> is owned by whichever tenant
    /// has the longest backlog, and a candidate that is not in the page cannot be scheduled
    /// fairly out of it. Zero — the default — is the page this contract always returned.
    /// </remarks>
    public int PerTenantLimit { get; init; }
}

/// <summary>An unfinished instance a scan found, and the little a scan needs to decide.</summary>
/// <param name="InstanceId">The instance to try to take over.</param>
/// <param name="FlowId">The flow it is an instance of.</param>
/// <param name="FlowVersion">
/// The version it is pinned to for its whole life. A node that does not carry this exact
/// version must not resume it — pinning is what stops a mid-flight deployment from changing
/// what an instance means (<c>docs/11-Distributed-Runtime.md §7</c>).
/// </param>
/// <param name="TenantId">The partition key, for a scan that is scoped to one tenant.</param>
/// <param name="State">What the row says it was doing.</param>
/// <param name="UpdatedAt">When the row last changed — how stale this candidate is.</param>
/// <remarks>
/// Deliberately not <see cref="FlowInstanceRecord"/>. A scan runs on a timer against a table
/// of unfinished work, and the full row carries the input and the state-bag snapshot — two
/// JSON columns a candidate list has no use for and a store would pay to read on every
/// sweep. Everything else the resume needs is read once, after the lease is won, by
/// <see cref="IFlowJournal.ReadResumeFrontierAsync"/>.
/// </remarks>
public sealed record AbandonedInstance(
    Guid InstanceId,
    string FlowId,
    string FlowVersion,
    string? TenantId,
    FlowInstanceState State,
    DateTimeOffset UpdatedAt);
