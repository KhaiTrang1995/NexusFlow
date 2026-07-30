using System.Runtime.CompilerServices;

namespace FlowX.Core.Tests;

/// <summary>
/// Measures GC-heap allocations of a single operation, deterministically.
/// </summary>
/// <remarks>
/// <para>
/// This exists because budgets B2 and B6 in
/// <a href="../../docs/14-Performance.md">14-Performance</a> are <em>hard zeros</em>,
/// and a hard zero is the one performance property that can be gated without a
/// benchmark: allocation counts are deterministic, while timings on a shared CI
/// runner are not. Gating B2 here means every pull request checks it in
/// milliseconds, instead of nightly on hardware nobody controls.
/// </para>
/// <para>
/// The measurement is only as trustworthy as its ability to detect an allocation it
/// should see — which is why <c>AllocationBudgetTests</c> includes a deliberate
/// positive control.
/// </para>
/// </remarks>
internal static class Allocation
{
    private const int WarmupIterations = 64;

    /// <summary>Bytes allocated on the GC heap by one invocation of <paramref name="operation"/>.</summary>
    /// <param name="operation">
    /// The operation to measure. Pass a cached delegate — creating it inside the call
    /// would measure the closure allocation rather than the work.
    /// </param>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static long Measure(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // Warm up so the JIT has promoted the method and any lazy statics are built;
        // otherwise first-call initialisation is charged to the operation.
        for (var i = 0; i < WarmupIterations; i++)
        {
            operation();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        operation();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
