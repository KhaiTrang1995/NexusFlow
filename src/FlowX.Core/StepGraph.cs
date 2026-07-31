using System.Collections.Immutable;

namespace FlowX;

/// <summary>
/// The compiled shape of a flow body: an ordered, gap-free sequence of steps.
/// </summary>
/// <remarks>
/// Every invariant is checked once, here, at construction. That is what allows the
/// engine's step loop to be a plain indexed walk with no bounds check and no null
/// check — a validated graph is the precondition the hot path is written against.
/// </remarks>
public sealed class StepGraph
{
    private readonly ImmutableArray<StepNode> _steps;

    private StepGraph(ImmutableArray<StepNode> steps) => _steps = steps;

    /// <summary>The steps, ordered by index.</summary>
    public ImmutableArray<StepNode> Steps => _steps;

    /// <summary>How many steps the graph contains. Always at least one.</summary>
    public int Count => _steps.Length;

    /// <summary>The step at <paramref name="index"/>.</summary>
    public StepNode this[int index] => _steps[index];

    /// <summary>
    /// Builds a validated graph. Steps may be supplied in any order; they are sorted
    /// by <see cref="StepNode.Index"/>.
    /// </summary>
    /// <param name="steps">The steps. Copied, never aliased.</param>
    /// <exception cref="InvalidFlowPlanException">
    /// The graph is empty, has duplicate indices, has indices that are not contiguous
    /// from zero, or contains a jump target that is out of range or points backwards.
    /// </exception>
    public static StepGraph Create(IEnumerable<StepNode> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var ordered = steps.OrderBy(static s => s.Index).ToImmutableArray();

        if (ordered.IsEmpty)
        {
            throw new InvalidFlowPlanException(
                "A flow must declare at least one step. An empty flow has no observable " +
                "behaviour and is almost certainly a Define method that returned early.");
        }

        for (var expected = 0; expected < ordered.Length; expected++)
        {
            var actual = ordered[expected].Index;

            if (actual == expected)
            {
                continue;
            }

            throw actual < expected
                ? new InvalidFlowPlanException(
                    $"Step index {actual} appears more than once. Each step occupies exactly " +
                    "one position in the graph.")
                : new InvalidFlowPlanException(
                    $"Step indices must be contiguous from zero; expected {expected} but found " +
                    $"{actual}. A gap means a step was emitted and then dropped, which would " +
                    "silently skip business logic at run time.");
        }

        ValidateTargets(ordered);

        return new StepGraph(ordered);
    }

    /// <summary>
    /// Checks every control transfer's target: in range, and forward.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The range check can only happen here. <see cref="StepNode.ForBranch"/> rejects a
    /// backward target — it can, because it knows the node's own index — but it cannot
    /// know how many steps the graph ends up with, so an out-of-range target survives
    /// node construction and would first be noticed as an <c>IndexOutOfRangeException</c>
    /// thrown from the middle of a flow, after some of its steps had already run.
    /// </para>
    /// <para>
    /// A target equal to <see cref="Count"/> is deliberately allowed: it is one past the
    /// last step, which ends the flow. That is the layout of a conditional written at the
    /// tail of a <c>Define</c> chain, so rejecting it would forbid a shape the DSL can
    /// express.
    /// </para>
    /// <para>
    /// A <see cref="StepKind.Switch"/> has one target per case <em>as well as</em> its
    /// default, and every one of them is checked. Checking only <see cref="StepNode.Target"/>
    /// would leave the termination proof holding for the arm nobody takes and not for the
    /// arms they do — which is the wrong way round.
    /// </para>
    /// <para>
    /// A <see cref="StepKind.Parallel"/> is checked the same way, and the check matters more
    /// there than anywhere else. Its branches are executed as sub-ranges of this same array,
    /// so a branch target past the end is not a wrong answer but an
    /// <c>IndexOutOfRangeException</c> thrown from a thread-pool thread partway through a
    /// concurrent fork. <see cref="StepNode.ForParallel"/> has already established that the
    /// targets ascend and that the join lies past the last of them; this adds the one fact
    /// only the graph knows, which is that they all fit.
    /// </para>
    /// <para>
    /// A <see cref="StepKind.SubFlow"/> passes this loop trivially, because it carries no
    /// target at all. That is not an omission: it names another flow's plan rather than a
    /// position in this array, so there is nothing here for it to point at and the
    /// termination argument for <em>this</em> graph is unaffected. What bounds the sub-flow
    /// graph is <c>FLOWX1021</c> at build time and the engine's nesting cap at run time;
    /// neither is a property of one graph, so neither belongs here.
    /// </para>
    /// </remarks>
    private static void ValidateTargets(ImmutableArray<StepNode> ordered)
    {
        foreach (var step in ordered)
        {
            // The per-block targets before the node's own, so a malformed layout is
            // reported against the block that is wrong rather than against the join that
            // was merely dragged out of range behind it. For a fork the join is by
            // construction the largest target, so checking it first would mean every
            // message named the join and none ever named the branch.
            foreach (var caseTarget in step.CaseTargets)
            {
                ValidateTarget(step, caseTarget, ordered.Length);
            }

            foreach (var branchTarget in step.BranchTargets)
            {
                ValidateTarget(step, branchTarget, ordered.Length);
            }

            if (step.Target is { } target)
            {
                ValidateTarget(step, target, ordered.Length);
            }
        }
    }

    private static void ValidateTarget(StepNode step, int target, int length)
    {
        if (target > length)
        {
            throw new InvalidFlowPlanException(
                $"Step {step.Index} targets step {target}, but the graph has only " +
                $"{length} step(s). A target may be at most {length} — " +
                "one past the last step, which ends the flow.");
        }

        if (target <= step.Index)
        {
            throw new InvalidFlowPlanException(
                $"Step {step.Index} targets step {target}, which does not point forward. " +
                "A backward target is a loop, and the conditional DSL cannot express " +
                "one — so this is a layout bug that would make the step loop run forever.");
        }
    }
}
