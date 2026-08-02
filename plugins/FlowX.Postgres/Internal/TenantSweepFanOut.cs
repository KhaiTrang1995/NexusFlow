using System.Data.Common;
using FlowX.Observability;
using Npgsql;

namespace FlowX.Postgres;

/// <summary>
/// One node-wide sweep asked of every tenant schema, with a tenant that cannot answer recorded
/// and stepped over.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One tenant must not end the pass.</strong> A recovery scan and a timer sweep are
/// node-wide, so the set they visit is every tenant on the node; abandoning the page at the
/// first schema that will not answer means one tenant's dropped schema, revoked grant or
/// unreachable pool stops recovery and timers for every other tenant here. That is the same
/// conclusion <c>PostgresOutboxPublisher</c>'s fan-out reached, and it is reached again rather
/// than shared because the two loops disagree about what to do next.
/// </para>
/// <para>
/// <strong>Where it differs from the drain, and why.</strong> A pass of the publisher returns
/// an <c>OutboxPass</c>, which has a slot for the tenant it stepped over;
/// <see cref="Result{T}"/> has no such slot — it is the page or it is nothing — so a partial
/// page cannot carry its own bad news. So the page is returned and the tenant that failed is
/// reported through <see cref="FlowXLog"/> instead. The publisher also rotates its starting
/// point, and this does not: a drain does work per tenant in visiting order, whereas a sweep
/// only reads and the merged page is ordered by staleness, so which schema was asked first
/// cannot decide whose rows survive the <c>LIMIT</c>.
/// </para>
/// <para>
/// <strong>Failing every tenant is not a quiet empty page.</strong> An empty list means "there
/// is no abandoned work", and answering that when nothing was successfully looked at would
/// report a healthy deployment at the moment none of it can be swept. So a pass in which no
/// tenant answered returns the first failure, which is also exactly what a sweep scoped to one
/// tenant did before it was fanned out.
/// </para>
/// <para>
/// <strong>A tenant that fails for ever costs one round trip a pass and no page budget.</strong>
/// It is asked once per pass and never retried inside one, each tenant's share is asked of that
/// tenant alone, and the rows it did not return are not a hole another tenant has to fill —
/// so the healthy tenants' pages are the same pages they would have had. The retry is the next
/// sweep, on the caller's own interval, which is the only place a wait belongs.
/// </para>
/// </remarks>
internal static class TenantSweepFanOut
{
    /// <summary>
    /// Asks every tenant — or the one the query named — and merges what answered.
    /// </summary>
    /// <typeparam name="TCandidate">The row type the sweep pages over.</typeparam>
    /// <param name="stores">The per-tenant pools, and the registry that lists them.</param>
    /// <param name="tenantId">The one tenant to visit, or null to visit them all.</param>
    /// <param name="operation">
    /// The sweep, by method name, as the <see cref="FlowXLog.JournalRefused"/> record's
    /// <c>operation</c> — so an operator can tell a recovery scan that stepped over a tenant
    /// from a timer sweep that did.
    /// </param>
    /// <param name="ask">The single-schema query, applied to one tenant's data source.</param>
    /// <param name="order">How the merged page is sorted, which the contract fixes.</param>
    /// <param name="limit">How many rows the merged page may carry.</param>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    /// <returns>The merged page, or the first failure when no tenant answered at all.</returns>
    public static async ValueTask<Result<IReadOnlyList<TCandidate>>> SweepAsync<TCandidate>(
        PostgresTenantStores stores,
        string? tenantId,
        string operation,
        Func<NpgsqlDataSource, string, ValueTask<Result<IReadOnlyList<TCandidate>>>> ask,
        Comparison<TCandidate> order,
        int limit,
        CancellationToken cancellationToken)
    {
        // A sweep that already names a tenant is not a fan-out: it visits one schema, which is
        // the shape FlowRecoveryScan uses when it is asked about one tenant and the shape the
        // isolation tests assert in both directions.
        var tenants = tenantId is { Length: > 0 } only
            ? [only]
            : await stores.KnownTenantsAsync(cancellationToken).ConfigureAwait(false);

        var merged = new List<TCandidate>();
        var answered = 0;
        Error? failure = null;

        foreach (var tenant in tenants)
        {
            try
            {
                var store = await stores.ForAsync(tenant, cancellationToken).ConfigureAwait(false);
                var listed = await ask(store, tenant).ConfigureAwait(false);

                if (listed.IsFailure)
                {
                    failure ??= listed.Error;
                    Skipped(operation, tenant, listed.Error);

                    continue;
                }

                answered++;
                merged.AddRange(listed.Value);
            }
            catch (Exception unreachable)
                when (unreachable is DbException or InvalidOperationException or TimeoutException
                    && !cancellationToken.IsCancellationRequested)
            {
                // The tenant fails, not the sweep — and a throw is the shape this failure
                // actually arrives in: PostgresRecoveryIndex and PostgresTimerIndex answer or
                // propagate (ADR-0007), so a dropped schema reaches here as a PostgresException
                // rather than as a refused Result. Letting it out would abandon the page, and
                // FlowRecoveryService swallows what escapes — which is how one tenant's broken
                // schema stopped a node's recovery with nothing said anywhere.
                var error = PostgresSweepErrors.TenantUnreachable(tenant, unreachable);

                failure ??= error;
                Skipped(operation, tenant, error);
            }
        }

        if (answered == 0 && failure is { } total)
        {
            return total;
        }

        merged.Sort(order);

        return merged.Count <= limit
            ? merged
            : merged.GetRange(0, limit);
    }

    /// <summary>How many rows one tenant may contribute to the merged page.</summary>
    /// <param name="perTenantLimit">The declared share, or zero when none was declared.</param>
    /// <param name="limit">The page the caller asked for.</param>
    /// <returns>The per-tenant limit to issue the single-schema query with.</returns>
    /// <remarks>
    /// A query's per-tenant cap becomes each tenant's own limit, which is what it means: the
    /// window function the single-schema indexes express it with cannot see across schemas, and
    /// asking each tenant for at most its share is the same cap arrived at from the other side.
    /// </remarks>
    public static int PerTenant(int perTenantLimit, int limit) =>
        perTenantLimit > 0 ? Math.Min(perTenantLimit, limit) : limit;

    /// <summary>
    /// Says which tenant was stepped over, on the channel a refused store call already uses.
    /// </summary>
    /// <remarks>
    /// <see cref="FlowXLog.JournalRefused"/> rather than a channel of this package's own: it is
    /// already "a store refused a call", it already carries the tenant, the operation and the
    /// error code, and a host that subscribed to it to see fenced-out writes sees this without
    /// wiring anything new. Written on every pass a tenant is broken, not only the first —
    /// an event that stops arriving reads as a tenant that recovered.
    /// </remarks>
    private static void Skipped(string operation, string tenantId, Error error) =>
        FlowXLog.WriteJournalCall(
            operation, instanceId: null, error.Code, error.Category.ToString(), tenantId);
}

/// <summary>What a fan-out reports when one tenant's schema could not be swept.</summary>
/// <remarks>
/// Named apart from <c>PostgresOutboxErrors</c> so that an operator grepping for a tenant whose
/// instances stopped being recovered finds one code, and finds it distinct from the tenant whose
/// events stopped being published — the two have different repairs.
/// </remarks>
internal static class PostgresSweepErrors
{
    public static Error TenantUnreachable(string tenantId, Exception failure) => new Error(
        "postgres.tenant_sweep_unavailable",
        $"Tenant '{tenantId}' could not be swept on this pass: {failure.Message}. Every other " +
        "tenant was still visited; this one is retried on the next sweep.",
        ErrorCategory.Unavailable)
        .With("tenantId", tenantId);
}
