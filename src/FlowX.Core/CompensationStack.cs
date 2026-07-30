namespace FlowX;

/// <summary>
/// Records the compensable steps a flow instance has completed, so they can be
/// undone in strict reverse order when a later step fails.
/// </summary>
/// <remarks>
/// <para>
/// Unlike everything else in this assembly, this type is <strong>mutable</strong>
/// and belongs to a single flow instance. It is execution state, not plan structure,
/// and it is not thread-safe: one instance, one flow, one thread at a time.
/// </para>
/// <para>
/// Reverse order is the whole point. Releasing inventory before refunding the
/// payment that reserved it leaves two systems disagreeing about the same order —
/// which is the failure mode a saga exists to prevent.
/// </para>
/// </remarks>
public sealed class CompensationStack
{
    private readonly Stack<StepNode> _completed = new();
    private readonly HashSet<int> _recordedIndices = [];

    /// <summary>How many compensable steps are pending undo.</summary>
    public int Count => _completed.Count;

    /// <summary>True when there is nothing to compensate.</summary>
    public bool IsEmpty => _completed.Count == 0;

    /// <summary>
    /// Records a step that completed successfully. Steps without a compensation are
    /// ignored, so callers can record every step without filtering.
    /// </summary>
    /// <param name="step">The completed step.</param>
    /// <exception cref="InvalidOperationException">
    /// The step was already recorded — the engine's loop visited it twice, which is a
    /// defect. Failing here beats compensating the same effect twice.
    /// </exception>
    public void RecordCompleted(StepNode step)
    {
        ArgumentNullException.ThrowIfNull(step);

        if (!step.IsCompensable)
        {
            return;
        }

        if (!_recordedIndices.Add(step.Index))
        {
            throw new InvalidOperationException(
                $"Step {step.Index} was recorded as completed more than once. A step " +
                "completes exactly once per flow instance; recording it twice would " +
                "compensate its effect twice.");
        }

        _completed.Push(step);
    }

    /// <summary>
    /// Clears the stack without releasing its buffers, so a pooled owner can reuse it
    /// across executions.
    /// </summary>
    /// <remarks>
    /// <c>Clear</c> on the underlying collections keeps their backing arrays, which is
    /// the entire point: constructing a fresh stack per execution cost 288 B and was
    /// the last thing standing between the engine and budget B2. The owner is
    /// responsible for calling this — an un-reset stack would compensate a previous
    /// flow's steps, which is far worse than allocating.
    /// </remarks>
    public void Reset()
    {
        _completed.Clear();
        _recordedIndices.Clear();
    }

    /// <summary>
    /// Yields the completed compensable steps newest-first, draining the stack as it
    /// goes.
    /// </summary>
    /// <remarks>
    /// Draining is deliberate: a second unwind — from a retry of the failure path, or
    /// a resumed durable instance — must not re-run a compensation that already ran.
    /// The enumeration is lazy, so a compensation that throws leaves the remaining
    /// steps on the stack for the caller to decide about.
    /// </remarks>
    public IEnumerable<StepNode> Unwind()
    {
        while (_completed.Count > 0)
        {
            var step = _completed.Pop();
            _recordedIndices.Remove(step.Index);
            yield return step;
        }
    }
}
