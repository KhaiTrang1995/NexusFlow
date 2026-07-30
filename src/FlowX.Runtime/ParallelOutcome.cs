namespace FlowX.Runtime;

/// <summary>
/// What each branch of an <see cref="MergeKind.AllSettled"/> fork produced.
/// </summary>
/// <remarks>
/// <para>
/// <c>06-Execution-Engine.md</c> §9 says of <c>AllSettled</c> that "the flow continues;
/// branch errors are available in the context". This is that. The engine writes one of
/// these into the flow's state bag when an <c>AllSettled</c> fork joins, so the step after
/// the fork can read <c>ctx.Get&lt;ParallelOutcome&gt;()</c> and decide what a partial
/// success means — which is a business question the engine has no standing to answer.
/// </para>
/// <para>
/// <strong>Only <c>AllSettled</c> writes one.</strong> The other three strategies end the
/// flow when the merge is not satisfied, so there is no later step to read it and the
/// allocation would be pure waste on a path that already failed.
/// </para>
/// <para>
/// <strong>A flow with two <c>AllSettled</c> forks keeps the later one.</strong> The state
/// bag is keyed by type, so the second write replaces the first — which is why
/// <see cref="StepIndex"/> is here rather than implied: a reader can tell which fork it is
/// holding, and a flow that needs both must consume the first before the second runs.
/// Keying by step index instead would mean a generic lookup the DSL has no way to spell.
/// </para>
/// </remarks>
public sealed class ParallelOutcome
{
    private readonly Error?[] _branches;

    internal ParallelOutcome(int stepIndex, Error?[] branches)
    {
        StepIndex = stepIndex;
        _branches = branches;
    }

    /// <summary>The flat index of the fork that produced this, matching the plan and traces.</summary>
    public int StepIndex { get; }

    /// <summary>How many branches the fork declared.</summary>
    public int BranchCount => _branches.Length;

    /// <summary>How many branches completed every step.</summary>
    public int SucceededCount
    {
        get
        {
            var count = 0;

            foreach (var error in _branches)
            {
                if (error is null)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>True when every branch completed.</summary>
    public bool AllSucceeded => SucceededCount == _branches.Length;

    /// <summary>True when no branch completed.</summary>
    public bool AllFailed => SucceededCount == 0;

    /// <summary>The error branch <paramref name="branch"/> ended with, or <c>null</c> when it completed.</summary>
    /// <param name="branch">Zero-based position in declaration order, matching the manifest's <c>branches</c> array.</param>
    /// <exception cref="ArgumentOutOfRangeException">There is no such branch.</exception>
    public Error? ErrorFor(int branch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(branch);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(branch, _branches.Length);

        return _branches[branch];
    }

    /// <summary>The errors, in branch declaration order, skipping the branches that succeeded.</summary>
    public IEnumerable<Error> Errors
    {
        get
        {
            foreach (var error in _branches)
            {
                if (error is not null)
                {
                    yield return error;
                }
            }
        }
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"parallel step {StepIndex}: {SucceededCount}/{_branches.Length} branch(es) succeeded";
}
