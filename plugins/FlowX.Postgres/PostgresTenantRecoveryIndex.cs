namespace FlowX.Postgres;

/// <summary>
/// The recovery scan's one query, asked of every tenant's schema and merged.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Schema isolation breaks the node-wide sweeps, and this is where that is
/// repaired.</strong> A recovery scan looks for work nobody is holding, which makes it
/// node-wide by construction — it cannot be scoped to the tenant of the call that triggered it,
/// because there is no such call. Under <see cref="TenantIsolation.Schema"/> the control
/// schema's <c>flow_instance</c> is empty and every row lives somewhere else, so
/// <see cref="PostgresRecoveryIndex"/> over the control data source would sweep correctly, find
/// nothing, report nothing, and leave every abandoned instance in the deployment stranded. That
/// failure is silent and it is total, which is why it is answered rather than documented.
/// </para>
/// <para>
/// <strong>The set comes from the registry, and the tenant of each row comes from the
/// row.</strong> <see cref="PostgresTenantStores.KnownTenantsAsync"/> says which schemas exist;
/// each candidate still carries its own <c>tenant_id</c>, so <c>FlowRecoveryScan</c> resumes
/// through the same tenant-scoped journal it always did and nothing above this class changes.
/// </para>
/// <para>
/// <strong>The contract's two orderings both survive the fan-out.</strong> Staleness ordering is
/// restored by sorting the merged page, so the oldest work in the <em>deployment</em> is at the
/// head rather than the oldest work in whichever tenant answered first. And
/// <see cref="AbandonedInstanceQuery.PerTenantLimit"/> becomes each tenant's own
/// <see cref="AbandonedInstanceQuery.Limit"/>, which is what it means: the window function
/// <see cref="PostgresRecoveryIndex"/> uses to express it cannot see across schemas, and asking
/// each tenant for at most its share is the same cap arrived at from the other side.
/// </para>
/// <para>
/// <strong>One tenant's schema failing does not end the scan</strong>, and
/// <see cref="TenantSweepFanOut"/> carries that argument.
/// </para>
/// </remarks>
public sealed class PostgresTenantRecoveryIndex : IRecoveryIndex
{
    private readonly PostgresTenantStores _stores;

    /// <summary>Creates the fan-out over a tenant store map.</summary>
    /// <param name="stores">The per-tenant pools and the registry that lists them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stores"/> is null.</exception>
    public PostgresTenantRecoveryIndex(PostgresTenantStores stores)
    {
        ArgumentNullException.ThrowIfNull(stores);

        _stores = stores;
    }

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<AbandonedInstance>>> ListAbandonedAsync(
        AbandonedInstanceQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var perTenant = TenantSweepFanOut.PerTenant(query.PerTenantLimit, query.Limit);

        return await TenantSweepFanOut.SweepAsync(
            _stores,
            query.TenantId,
            nameof(ListAbandonedAsync),
            (store, tenant) => new PostgresRecoveryIndex(store).ListAbandonedAsync(
                query with { TenantId = tenant, Limit = perTenant, PerTenantLimit = 0 },
                cancellationToken),
            static (left, right) => left.UpdatedAt.CompareTo(right.UpdatedAt),
            query.Limit,
            cancellationToken)
            .ConfigureAwait(false);
    }
}
