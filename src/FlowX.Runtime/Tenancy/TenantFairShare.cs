namespace FlowX.Runtime;

/// <summary>
/// The order a sweep walks a page of candidates in, so that a tenant with a long backlog does
/// not spend every slot before a quiet tenant is reached.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What starves, precisely.</strong> Both sweeps ask their index for the oldest work in
/// the table, bounded, and then spend at most <c>MaxConcurrentRecoveries</c> slots on it. The
/// page is therefore first-in-first-out across every tenant, and the slots are handed out by
/// walking it from a random offset. Under load that is <em>proportional</em> share, not fair
/// share: a tenant holding nine tenths of the page gets nine tenths of the slots, and its
/// backlog is drained at nine times the rate of the tenant behind it, whose oldest instance
/// therefore waits on a queue it did not create. Proportional share degrades gracefully at low
/// load and not at all at high load — which is exactly where quality goal <strong>Q8</strong>
/// is measured.
/// </para>
/// <para>
/// <strong>What this does instead.</strong> The page is split into one queue per tenant,
/// preserving the index's ordering inside each — the anti-starvation property
/// <em>within</em> a tenant is the store's <c>ORDER BY</c> and is not this type's to change —
/// and the queues are then drained one round at a time, a tenant's weight deciding how many it
/// contributes per round. Every tenant present in the page therefore appears in the first
/// <c>tenants × weight</c> positions of the order, so a quiet tenant is reached on the first
/// sweep rather than after the noisy one has drained.
/// </para>
/// <para>
/// <strong>It reorders and never filters.</strong> Every candidate in the page appears exactly
/// once in the result, because the caller skips candidates it cannot run — a flow version this
/// node does not carry — and must be able to keep walking. A scheduler that dropped the tail
/// would turn "this node cannot run these four" into four instances nobody looks at again.
/// </para>
/// <para>
/// <strong>The rotation is preserved, and it has to be.</strong> The offset the sweeps already
/// pass exists so that identical nodes handed an identical page do not all contend for its first
/// row; it is applied here to the tenant order rather than to the row order, which keeps the
/// property and adds fairness to it. Without it every node would attempt the same tenant's head
/// candidate on every sweep and lose to the same winner.
/// </para>
/// </remarks>
public static class TenantFairShare
{
    /// <summary>
    /// The order to walk a page in: one round per pass, a tenant's weight of candidates each.
    /// </summary>
    /// <typeparam name="T">The candidate row.</typeparam>
    /// <param name="candidates">The page, in the order the index returned it.</param>
    /// <param name="tenantOf">Reads a candidate's tenant.</param>
    /// <param name="weightOf">
    /// What a tenant's share of one round is worth. Values below one are read as one — a weight
    /// of zero would express "never schedule this tenant", which is the starvation this exists
    /// to prevent.
    /// </param>
    /// <param name="offset">
    /// Which tenant's queue leads. Spreads a fleet of identical nodes across the page's tenants
    /// rather than across its rows.
    /// </param>
    /// <returns>
    /// Indices into <paramref name="candidates"/>, every index exactly once. The page itself when
    /// one tenant or none is present, because a single-tenant page has nothing to be fair
    /// between and reordering it would only lose the index's ordering.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="candidates"/>, <paramref name="tenantOf"/> or <paramref name="weightOf"/>
    /// is null.
    /// </exception>
    public static int[] Order<T>(
        IReadOnlyList<T> candidates,
        Func<T, string?> tenantOf,
        Func<string?, int> weightOf,
        int offset)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(tenantOf);
        ArgumentNullException.ThrowIfNull(weightOf);

        var count = candidates.Count;
        var order = new int[count];

        // One queue per tenant, in first-appearance order — which is the index's order, so the
        // tenant whose work is oldest leads the rotation before the offset turns it.
        var queues = new List<List<int>>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < count; i++)
        {
            // Untenanted candidates share one queue. On a deployment that isolates by tenant
            // there are none; on one that does not, this type is never reached.
            var tenant = tenantOf(candidates[i]) ?? string.Empty;

            if (!index.TryGetValue(tenant, out var queue))
            {
                queue = queues.Count;
                index[tenant] = queue;
                queues.Add([]);
            }

            queues[queue].Add(i);
        }

        if (queues.Count <= 1)
        {
            for (var i = 0; i < count; i++)
            {
                order[i] = i;
            }

            return order;
        }

        var weights = new int[queues.Count];

        foreach (var (tenant, queue) in index)
        {
            weights[queue] = Math.Max(1, weightOf(tenant.Length == 0 ? null : tenant));
        }

        var taken = new int[queues.Count];
        var start = queues.Count == 0 ? 0 : ((offset % queues.Count) + queues.Count) % queues.Count;
        var written = 0;

        while (written < count)
        {
            for (var round = 0; round < queues.Count && written < count; round++)
            {
                var queue = (start + round) % queues.Count;
                var rows = queues[queue];

                for (var share = 0; share < weights[queue] && taken[queue] < rows.Count; share++)
                {
                    order[written++] = rows[taken[queue]++];
                }
            }
        }

        return order;
    }
}
