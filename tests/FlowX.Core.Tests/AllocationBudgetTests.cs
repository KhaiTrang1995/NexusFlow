using Shouldly;
using Xunit;

namespace FlowX.Core.Tests;

/// <summary>
/// Budget B2 — <strong>zero allocations per step</strong> — enforced as a unit test
/// rather than a benchmark.
/// </summary>
/// <remarks>
/// <para>
/// These are the parts of the hot path that exist today. The step loop itself
/// arrives at WP-4; when it does, its allocation assertion belongs here beside
/// these, not in the benchmark project. A budget checked once a night is a budget
/// that regresses for a day before anyone notices.
/// </para>
/// <para>
/// "Zero" means zero. Not "small", not "acceptable" — B2 is a hard zero in
/// <a href="../../docs/14-Performance.md">14-Performance</a>, and a hard zero is
/// the only allocation budget that cannot be quietly eroded one field at a time.
/// </para>
/// </remarks>
public sealed class AllocationBudgetTests
{
    private static readonly ExecutionPlan Plan = ExecutionPlan.Create(
        Fixtures.PlaceOrder,
        StepGraph.Create([
            StepNode.ForCapability(0, Fixtures.ValidateOrder),
            StepNode.ForCapability(1, Fixtures.ReserveInventory, Fixtures.ReleaseInventory),
            StepNode.ForCapability(2, Fixtures.CapturePayment),
            StepNode.ForEmit(3, "order.placed"),
        ]));

    /// <summary>
    /// The positive control. Without it, every zero below could be a broken
    /// measurement rather than a fast code path — and a green gate that cannot fail
    /// is worse than no gate, because people trust it.
    /// </summary>
    [Fact]
    public void TheMeasurementCanDetectAnAllocationItShouldSee()
    {
        var allocated = Allocation.Measure(static () => _ = new object());

        allocated.ShouldBeGreaterThan(0,
            "If this reports zero, the harness is broken and every other assertion " +
            "in this class is meaningless.");
    }

    [Fact]
    public void WalkingTheStepGraphByIndexAllocatesNothing()
    {
        var allocated = Allocation.Measure(static () =>
        {
            var steps = Plan.Graph.Steps;
            var count = 0;

            for (var i = 0; i < steps.Length; i++)
            {
                if (steps[i].IsCompensable)
                {
                    count++;
                }
            }

            _ = count;
        });

        allocated.ShouldBe(0, "The engine's step loop walks the graph once per execution.");
    }

    [Fact]
    public void ForeachOverTheStepArrayAllocatesNothing()
    {
        // ImmutableArray<T> exposes a struct enumerator, so foreach over the concrete
        // type does not box. Asserted because a refactor to IEnumerable<StepNode>
        // would silently start allocating on every execution.
        var allocated = Allocation.Measure(static () =>
        {
            foreach (var step in Plan.Graph.Steps)
            {
                _ = step.Index;
            }
        });

        allocated.ShouldBe(0);
    }

    [Fact]
    public void ExposingTheStepsAsIEnumerableWouldAllocate()
    {
        // The counter-example, kept so the rule above has a visible reason. This is
        // what the previous test is protecting against.
        var allocated = Allocation.Measure(static () =>
        {
            IEnumerable<StepNode> boxed = Plan.Graph.Steps;

            foreach (var step in boxed)
            {
                _ = step.Index;
            }
        });

        allocated.ShouldBeGreaterThan(0,
            "Boxing the struct enumerator allocates. This is why StepGraph.Steps is " +
            "typed as ImmutableArray<StepNode> and not as IEnumerable<StepNode>.");
    }

    [Fact]
    public void ReadingPrecomputedPlanFactsAllocatesNothing()
    {
        var allocated = Allocation.Measure(static () =>
        {
            _ = Plan.HasCompensation;
            _ = Plan.CompensableStepIndices.Length;
            _ = Plan.SideEffects.Length;
            _ = Plan.Flow.Profile;
            _ = Plan.Graph.Count;
        });

        allocated.ShouldBe(0,
            "These are read on the failure path, where an incident is already under way.");
    }

    [Fact]
    public void TheSuccessPathOfResultAllocatesNothing()
    {
        var allocated = Allocation.Measure(static () =>
        {
            var result = Result.Ok(42);
            _ = result.IsSuccess;
            _ = result.Value;
            _ = result.Map(static x => x + 1).Value;
        });

        allocated.ShouldBe(0);
    }

    [Fact]
    public void TheFailurePathOfResultAllocatesNothing()
    {
        var error = new Error("test.failed", "boom", ErrorCategory.Unavailable);

        var allocated = Allocation.Measure(() =>
        {
            var result = Result.Fail<int>(error);
            _ = result.IsFailure;
            _ = result.Map(static x => x + 1).Error;
            _ = result.TryGetValue(out _, out _);
        });

        allocated.ShouldBe(0,
            "ADR-0007: errors are values precisely so the failure path costs nothing. " +
            "Latency matters most when things are already going wrong.");
    }

    /// <summary>
    /// Documents a real cost rather than asserting a zero that is not true today.
    /// </summary>
    /// <remarks>
    /// Constructing a <see cref="CompensationStack"/> allocates: <c>Stack&lt;T&gt;</c>
    /// grows its backing array, <c>HashSet&lt;T&gt;</c> builds buckets, and
    /// <c>Unwind</c> is an iterator.
    /// <para>
    /// WP-4 resolved this, and not the way this tripwire predicted. The prediction was
    /// that the type would change; what actually happened is that the type gained a
    /// <c>Reset</c> and the engine's pooled context now owns one instance for its whole
    /// life. Constructing one still allocates — that is what this test measures — but
    /// the engine never does. See
    /// <c>CompensationStackAllocatesNothingWhenReused</c> below for the path the
    /// runtime actually takes.
    /// </para>
    /// </remarks>
    [Fact]
    public void CompensationStackAllocatesTodayAndWP4MustPoolIt()
    {
        var step = StepNode.ForCapability(0, Fixtures.ReserveInventory, Fixtures.ReleaseInventory);

        var allocated = Allocation.Measure(() =>
        {
            var stack = new CompensationStack();
            stack.RecordCompleted(step);

            foreach (var pending in stack.Unwind())
            {
                _ = pending.Index;
            }
        });

        allocated.ShouldBeGreaterThan(0,
            "Recorded, not hidden. Constructing a stack costs; the engine avoids the " +
            "cost by never constructing one per execution.");
    }

    /// <summary>
    /// The path the runtime actually takes on a <em>successful</em> flow: record
    /// completed steps, reset, reuse. No unwind, because nothing failed.
    /// </summary>
    /// <remarks>
    /// This is the assertion that made WP-4's exit criterion reachable. Measurement
    /// found the 288 B; review had signed off on the code that contained it.
    /// </remarks>
    [Fact]
    public void RecordingAndResettingAReusedStackAllocatesNothing()
    {
        var step = StepNode.ForCapability(0, Fixtures.ReserveInventory, Fixtures.ReleaseInventory);
        var stack = new CompensationStack();

        var allocated = Allocation.Measure(() =>
        {
            stack.RecordCompleted(step);
            stack.Reset();
        });

        allocated.ShouldBe(0,
            "Clear keeps the backing arrays, so a pooled owner pays the construction " +
            "cost once per pooled context rather than once per flow. This is the " +
            "success path, which is the one budget B2 governs.");
    }

    /// <summary>
    /// <c>Unwind</c> is an iterator, so enumerating it allocates a state machine.
    /// </summary>
    /// <remarks>
    /// Deliberately not fixed. It costs one small object per <em>failed</em> flow, on a
    /// path where a compensation is about to make a network call anyway; converting the
    /// iterator into a hand-rolled struct enumerator would trade real readability for
    /// an allocation nobody will ever profile. Recorded so the trade is a decision
    /// rather than an oversight.
    /// </remarks>
    [Fact]
    public void UnwindingAllocatesOneIteratorPerFailedFlow()
    {
        var step = StepNode.ForCapability(0, Fixtures.ReserveInventory, Fixtures.ReleaseInventory);
        var stack = new CompensationStack();

        var allocated = Allocation.Measure(() =>
        {
            stack.RecordCompleted(step);

            foreach (var pending in stack.Unwind())
            {
                _ = pending.Index;
            }
        });

        allocated.ShouldBeGreaterThan(0);
        allocated.ShouldBeLessThan(256, "One iterator, not a data structure.");
    }
}
