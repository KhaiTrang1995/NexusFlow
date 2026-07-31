namespace FlowX.Conformance.InMemory;

/// <summary>
/// The reference <see cref="ILeaseStore"/>: exclusive while live, monotonic for ever.
/// </summary>
/// <remarks>
/// <para>
/// The token counter is the part worth reading. It is per instance and it is never reset —
/// not on release, not on expiry, not when the last holder went away cleanly. A store that
/// restarted the sequence when a lease lapsed would look correct in every test that acquires
/// and releases in order, and would hand a returning zombie a token equal to the one the new
/// owner is using.
/// </para>
/// <para>
/// Time comes from <see cref="DateTimeOffset.UtcNow"/> rather than an injected clock, so the
/// expiry tests in the conformance suite wait real milliseconds. A fake clock here would
/// make those assertions cheaper and would stop them from being about expiry.
/// </para>
/// </remarks>
public sealed class InMemoryLeaseStore : ILeaseStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, FlowLease> _leases = [];
    private readonly Dictionary<Guid, long> _issued = [];

    /// <inheritdoc />
    public ValueTask<Result<FlowLease>> AcquireAsync(
        Guid instanceId,
        string ownerNode,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerNode);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;

            if (TryGetLive(instanceId, now, out var live))
            {
                return Fail<FlowLease>(DurabilityErrors.LeaseHeld(instanceId, live.OwnerNode));
            }

            var next = _issued.TryGetValue(instanceId, out var last) ? last + 1 : 1;
            _issued[instanceId] = next;

            var lease = new FlowLease(instanceId, ownerNode, new FencingToken(next), now + ttl);
            _leases[instanceId] = lease;

            return Ok(lease);
        }
    }

    /// <inheritdoc />
    public ValueTask<Result<FlowLease>> RenewAsync(
        FlowLease lease,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;

            if (!TryGetLive(lease.InstanceId, now, out var live) || live.Token != lease.Token)
            {
                return Fail<FlowLease>(DurabilityErrors.LeaseLost(lease.InstanceId, lease.Token));
            }

            var renewed = live with { ExpiresAt = now + ttl };
            _leases[lease.InstanceId] = renewed;

            return Ok(renewed);
        }
    }

    /// <inheritdoc />
    public ValueTask<Result<bool>> ReleaseAsync(FlowLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!TryGetLive(lease.InstanceId, DateTimeOffset.UtcNow, out var live)
                || live.Token != lease.Token)
            {
                return Fail<bool>(DurabilityErrors.LeaseLost(lease.InstanceId, lease.Token));
            }

            _leases.Remove(lease.InstanceId);

            return Ok(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<Result<FlowLease>> ReadAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return TryGetLive(instanceId, DateTimeOffset.UtcNow, out var live)
                ? Ok(live)
                : Fail<FlowLease>(DurabilityErrors.LeaseNotHeld(instanceId));
        }
    }

    private bool TryGetLive(Guid instanceId, DateTimeOffset now, out FlowLease lease)
    {
        if (_leases.TryGetValue(instanceId, out var found) && found.ExpiresAt > now)
        {
            lease = found;
            return true;
        }

        lease = null!;
        return false;
    }

    private static ValueTask<Result<T>> Ok<T>(T value) => new(Result.Ok(value));

    private static ValueTask<Result<T>> Fail<T>(Error error) => new(Result.Fail<T>(error));
}
