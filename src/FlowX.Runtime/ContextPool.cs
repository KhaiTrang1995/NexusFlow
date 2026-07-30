namespace FlowX.Runtime;

/// <summary>
/// A bounded, allocation-free pool of <see cref="FlowExecutionContext"/> instances.
/// </summary>
/// <remarks>
/// <para>
/// A single-slot fast path plus a fixed array, each slot claimed with
/// <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>. The shape is the same
/// one <c>Microsoft.Extensions.ObjectPool.DefaultObjectPool</c> uses, reproduced here
/// rather than taken as a dependency because it is thirty lines and the pool is on
/// the hottest path the runtime has.
/// </para>
/// <para>
/// The first version of this used a <see cref="System.Collections.Concurrent.ConcurrentBag{T}"/>,
/// which was the obvious choice and the wrong one: <c>ConcurrentBag.Add</c> allocates
/// a node per item, so the pool that exists to avoid allocating allocated on every
/// return. That cost 304 B per execution and was found by measurement, not by review.
/// </para>
/// <para>
/// The array is bounded on purpose. An unbounded pool under a load spike retains one
/// context per concurrent flow forever, turning a transient burst into permanent
/// resident memory — a pool that never shrinks is a memory leak with a respectable
/// name. Above the cap, contexts are dropped for the GC.
/// </para>
/// </remarks>
internal sealed class ContextPool
{
    private readonly FlowExecutionContext?[] _items;
    private FlowExecutionContext? _fastItem;

    public ContextPool(int maxRetained)
    {
        // One slot is the fast path; the array holds the rest.
        _items = new FlowExecutionContext?[Math.Max(1, maxRetained - 1)];
    }

    /// <summary>Takes a context from the pool, or creates one if none is free.</summary>
    public FlowExecutionContext Rent()
    {
        var item = _fastItem;

        if (item is not null && Interlocked.CompareExchange(ref _fastItem, null, item) == item)
        {
            return item;
        }

        var items = _items;

        for (var i = 0; i < items.Length; i++)
        {
            item = items[i];

            if (item is not null && Interlocked.CompareExchange(ref items[i], null, item) == item)
            {
                return item;
            }
        }

        return new FlowExecutionContext();
    }

    /// <summary>Resets a context and returns it to the pool, if there is room.</summary>
    public void Return(FlowExecutionContext context)
    {
        // Reset before the slot, never after: a context sitting in the pool must not
        // hold the previous tenant's data even for the instant before it is reused.
        context.Reset();

        if (_fastItem is null && Interlocked.CompareExchange(ref _fastItem, context, null) is null)
        {
            return;
        }

        var items = _items;

        for (var i = 0; i < items.Length; i++)
        {
            if (Interlocked.CompareExchange(ref items[i], context, null) is null)
            {
                return;
            }
        }

        // Pool is full. Dropping the context is the correct outcome — see the bound
        // discussion above.
    }
}
