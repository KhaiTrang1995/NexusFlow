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
    /// The graph is empty, has duplicate indices, or has indices that are not
    /// contiguous from zero.
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

        return new StepGraph(ordered);
    }
}
