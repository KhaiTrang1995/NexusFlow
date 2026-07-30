using BenchmarkDotNet.Attributes;

namespace FlowX.Benchmarks;

/// <summary>
/// Budget <strong>B1 — 4-step ephemeral flow overhead, p50 ≤ 1.5 µs / p99 ≤ 5 µs</strong>.
/// </summary>
/// <remarks>
/// <para>
/// The flow engine arrives at WP-4, so this measures the <em>skeleton</em>: walking a
/// compiled <see cref="ExecutionPlan"/> and dispatching each step, with no policies,
/// no telemetry and no context pooling. Whatever the engine adds on top of this
/// number is the platform's real overhead, and it has ~5 µs to spend.
/// </para>
/// <para>
/// Committing this baseline now means every later commit is measured against a
/// number that already exists, rather than against whatever the engine happened to
/// cost on the day it was finished.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[HideColumns("Job", "Error", "StdDev", "Median", "RatioSD")]
public class StepLoopBenchmarks
{
    private ExecutionPlan _plan = null!;
    private EchoCapability _capability = null!;
    private CapabilityContext _context = null!;

    [GlobalSetup]
    public void Setup()
    {
        var validate = CapabilityDescriptor.Create("order.validate", "1.0.0", true);
        var reserve = CapabilityDescriptor.Create("inventory.reserve", "1.0.0", true, "inventory-ledger");
        var release = CapabilityDescriptor.Create("inventory.release", "1.0.0", true, "inventory-ledger");
        var capture = CapabilityDescriptor.Create("payment.capture", "2.1.0", false, "payment-gateway");

        _plan = ExecutionPlan.Create(
            FlowDescriptor.Create("order.place", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([
                StepNode.ForCapability(0, validate),
                StepNode.ForCapability(1, reserve, release),
                StepNode.ForCapability(2, capture),
                StepNode.ForEmit(3, "order.placed"),
            ]));

        _capability = new EchoCapability();
        _context = new StubContext();
    }

    /// <summary>Traversal only — the cost of the loop the engine will be built around.</summary>
    [Benchmark(Baseline = true, Description = "Walk a 4-step plan")]
    public int WalkPlan()
    {
        var steps = _plan.Graph.Steps;
        var compensable = 0;

        for (var i = 0; i < steps.Length; i++)
        {
            if (steps[i].Kind == StepKind.Capability && steps[i].IsCompensable)
            {
                compensable++;
            }
        }

        return compensable;
    }

    /// <summary>
    /// Traversal plus a dispatch per capability step. This is the closest thing to
    /// B1 that exists before WP-4, and the number the engine must not blow past.
    /// </summary>
    [Benchmark(Description = "Walk + dispatch each step")]
    public async ValueTask<int> WalkAndDispatch()
    {
        var steps = _plan.Graph.Steps;
        var total = 0;

        for (var i = 0; i < steps.Length; i++)
        {
            if (steps[i].Kind != StepKind.Capability)
            {
                continue;
            }

            var result = await _capability
                .ExecuteAsync(i, _context, CancellationToken.None)
                .ConfigureAwait(false);

            if (result.IsFailure)
            {
                break;
            }

            total += result.Value;
        }

        return total;
    }

    /// <summary>
    /// The failure path with compensation unwind. Measured because this is where
    /// latency matters most and where an allocation is most likely to hide — an
    /// incident is already in progress by the time this code runs.
    /// </summary>
    [Benchmark(Description = "Failure path + compensation unwind")]
    public int CompensateAll()
    {
        var stack = new CompensationStack();
        var steps = _plan.Graph.Steps;

        for (var i = 0; i < steps.Length; i++)
        {
            stack.RecordCompleted(steps[i]);
        }

        var undone = 0;

        foreach (var step in stack.Unwind())
        {
            undone += step.Index;
        }

        return undone;
    }

    /// <summary>
    /// Plan construction. Build-time cost, not run-time — measured so a validation
    /// rule added later cannot quietly turn a fast build into a slow one.
    /// </summary>
    [Benchmark(Description = "Build a 4-step plan (build-time)")]
    public int BuildPlan()
    {
        var plan = ExecutionPlan.Create(_plan.Flow, _plan.Graph);
        return plan.Graph.Count;
    }
}
