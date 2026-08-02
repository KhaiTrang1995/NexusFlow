namespace FlowX.Conformance.InMemory;

/// <summary>
/// The per-tenant cap the two index queries declare, applied to an already-ordered list.
/// </summary>
/// <remarks>
/// Shared by <see cref="InMemoryTimerIndex"/> and <see cref="InMemoryRecoveryIndex"/> because
/// the rule is the same one — <c>PostgreSQL</c> writes it once as a window function and these
/// two would otherwise write it twice and drift. The ordering is the caller's: this only
/// decides which rows of it survive.
/// </remarks>
internal static class FairPage
{
    /// <summary>Takes at most <paramref name="limit"/> rows, at most a cap from any one tenant.</summary>
    /// <typeparam name="T">The candidate row.</typeparam>
    /// <param name="ordered">The candidates, already in the order the contract promises.</param>
    /// <param name="tenantOf">Reads a candidate's tenant.</param>
    /// <param name="perTenantLimit">The cap, or zero for none.</param>
    /// <param name="limit">The page size.</param>
    /// <returns>The page, in the caller's order.</returns>
    public static List<T> Take<T>(
        List<T> ordered,
        Func<T, string?> tenantOf,
        int perTenantLimit,
        int limit)
    {
        if (perTenantLimit <= 0)
        {
            return ordered.Count <= limit ? ordered : ordered[..limit];
        }

        // A null tenant is a key here rather than being skipped. An untenanted row on a
        // deployment that caps per tenant is one tenant's worth of work — folding every such row
        // past the cap would let the untenanted backlog do exactly what the cap exists to stop.
        var taken = new Dictionary<string, int>(StringComparer.Ordinal);
        var page = new List<T>(Math.Min(limit, ordered.Count));

        foreach (var candidate in ordered)
        {
            if (page.Count == limit)
            {
                break;
            }

            var tenant = tenantOf(candidate) ?? string.Empty;

            taken.TryGetValue(tenant, out var count);

            if (count >= perTenantLimit)
            {
                continue;
            }

            taken[tenant] = count + 1;
            page.Add(candidate);
        }

        return page;
    }
}
