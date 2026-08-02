namespace FlowX.Postgres;

/// <summary>
/// The timer sweep's one query, asked of every tenant's schema and merged.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A second fan-out rather than a second query on the first</strong>, for the reason
/// <see cref="PostgresTimerIndex"/> is a second class: the two sweeps ask opposite questions of
/// disjoint sets of rows, and a deployment may run either without the other. What they share is
/// the failure this pair exists to remove — under <see cref="TenantIsolation.Schema"/> a sweep
/// over the control schema finds nothing, reports nothing, and every parked instance in the
/// deployment sleeps for ever.
/// </para>
/// <para>
/// <see cref="PostgresTenantRecoveryIndex"/> carries the argument for the ordering and the
/// per-tenant cap, and <see cref="TenantSweepFanOut"/> the one for stepping over a tenant whose
/// schema will not answer; the only difference here is which instant the merged page is ordered
/// by, and it is the one <see cref="ITimerIndex"/> names.
/// </para>
/// </remarks>
public sealed class PostgresTenantTimerIndex : ITimerIndex
{
    private readonly PostgresTenantStores _stores;

    /// <summary>Creates the fan-out over a tenant store map.</summary>
    /// <param name="stores">The per-tenant pools and the registry that lists them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stores"/> is null.</exception>
    public PostgresTenantTimerIndex(PostgresTenantStores stores)
    {
        ArgumentNullException.ThrowIfNull(stores);

        _stores = stores;
    }

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<DueInstance>>> ListDueAsync(
        DueInstanceQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var perTenant = TenantSweepFanOut.PerTenant(query.PerTenantLimit, query.Limit);

        return await TenantSweepFanOut.SweepAsync(
            _stores,
            query.TenantId,
            nameof(ListDueAsync),
            (store, tenant) => new PostgresTimerIndex(store).ListDueAsync(
                query with { TenantId = tenant, Limit = perTenant, PerTenantLimit = 0 },
                cancellationToken),
            static (left, right) => left.WakeAt.CompareTo(right.WakeAt),
            query.Limit,
            cancellationToken)
            .ConfigureAwait(false);
    }
}
