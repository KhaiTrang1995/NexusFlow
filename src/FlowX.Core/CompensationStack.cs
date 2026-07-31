namespace FlowX;

/// <summary>
/// One completed compensable step, and the scope it completed in.
/// </summary>
/// <param name="Step">The step whose inverse is pending.</param>
/// <param name="Scope">
/// The context the step ran under, or <c>null</c> when it ran under the flow's own.
/// Non-null only inside a <see cref="StepKind.ForEach"/>, where it is the iteration's
/// view of the context.
/// </param>
/// <param name="JournalScope">
/// The same iteration, as the journal key spells it. <see cref="StepScope.Root"/> for the
/// flow body and for every ephemeral execution.
/// </param>
/// <remarks>
/// <para>
/// <strong>Why the scope has to be recorded rather than recomputed.</strong> A
/// compensation binds to the step's own input — the thing it has to undo — and inside an
/// iteration that input is the element being processed. By the time the unwind runs, the
/// loop is over and the flow's context holds whatever the last iteration left there, so
/// undoing "the line this step reserved" would undo the same line <em>n</em> times and
/// leave the other <em>n − 1</em> reserved. Carrying the scope is what makes
/// <c>.ForEach(…, line =&gt; line.Step&lt;ReserveLine&gt;().CompensateWith&lt;ReleaseLine&gt;())</c>
/// mean what it reads as.
/// </para>
/// <para>
/// A struct, so recording one costs nothing beyond the push it was already doing.
/// </para>
/// <para>
/// <strong><see cref="JournalScope"/> is the same fact <see cref="Scope"/> carries, said in
/// the journal's vocabulary.</strong> The two cannot be derived from one another: a
/// <c>FlowContext</c> is a typed view the dispatcher builds and the journal has never heard
/// of, and a <see cref="StepScope"/> is a rendered path a store persists. Carrying both is
/// what lets a compensation row for the third line be keyed as the third line rather than
/// colliding with the first — the same problem the stack's own duplicate check met, answered
/// the same way.
/// </para>
/// </remarks>
public readonly record struct CompensationEntry(
    StepNode Step,
    FlowContext? Scope,
    StepScope JournalScope = default)
{
    /// <summary>The step's flat index, matching the plan, the manifest and traces.</summary>
    public int Index => Step.Index;
}

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
    private readonly Stack<CompensationEntry> _completed = new();
    private readonly HashSet<(int Index, FlowContext? Scope)> _recorded = [];

    /// <summary>How many compensable steps are pending undo.</summary>
    public int Count => _completed.Count;

    /// <summary>True when there is nothing to compensate.</summary>
    public bool IsEmpty => _completed.Count == 0;

    /// <summary>
    /// Records a step that completed successfully. Steps without a compensation are
    /// ignored, so callers can record every step without filtering.
    /// </summary>
    /// <param name="step">The completed step.</param>
    /// <param name="scope">
    /// The iteration scope the step ran under, or <c>null</c> outside a
    /// <see cref="StepKind.ForEach"/>. It is what the unwind hands back to the dispatcher,
    /// so the compensation binds to the element its step processed.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The step was already recorded <em>in this scope</em> — the engine's loop visited it
    /// twice, which is a defect. Failing here beats compensating the same effect twice.
    /// </exception>
    /// <remarks>
    /// <strong>The identity is the pair, not the index.</strong> A step inside a
    /// <c>ForEach</c> body legitimately completes once per element, and keying on the index
    /// alone would have made the second element throw. Two completions of the same step in
    /// the same scope remain a defect, which is the case the check was written for.
    /// </remarks>
    /// <param name="journalScope">
    /// The same iteration as the journal keys it, so a row written for this step's undo lands
    /// under the element it undid. <see cref="StepScope.Root"/> outside a loop and for every
    /// ephemeral execution, which costs an unjournaled flow nothing.
    /// </param>
    public void RecordCompleted(StepNode step, FlowContext? scope = null, StepScope journalScope = default)
    {
        ArgumentNullException.ThrowIfNull(step);

        if (!step.IsCompensable)
        {
            return;
        }

        if (!_recorded.Add((step.Index, scope)))
        {
            throw new InvalidOperationException(
                $"Step {step.Index} was recorded as completed more than once in the same " +
                "scope. A step completes exactly once per flow instance — or once per " +
                "element inside an iteration — and recording it twice would compensate " +
                "its effect twice.");
        }

        _completed.Push(new CompensationEntry(step, scope, journalScope));
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
        _recorded.Clear();
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
    public IEnumerable<CompensationEntry> Unwind()
    {
        while (_completed.Count > 0)
        {
            var entry = _completed.Pop();
            _recorded.Remove((entry.Index, entry.Scope));
            yield return entry;
        }
    }
}
