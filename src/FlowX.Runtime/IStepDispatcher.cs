namespace FlowX.Runtime;

/// <summary>The result of one step. A struct, so the step loop allocates nothing.</summary>
public readonly struct StepOutcome
{
    private StepOutcome(Error? error) => Error = error;

    /// <summary>A step that completed.</summary>
    public static StepOutcome Success => default;

    /// <summary>A step that produced a business error.</summary>
    public static StepOutcome Failed(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new StepOutcome(error);
    }

    /// <summary>The error, or <c>null</c> on success.</summary>
    public Error? Error { get; }

    /// <summary>True when the step completed.</summary>
    public bool IsSuccess => Error is null;
}

/// <summary>
/// Invokes the capability behind a step index. <strong>This is the contract the
/// source generator implements</strong>, and the reason the engine can be
/// reflection-free.
/// </summary>
/// <remarks>
/// <para>
/// The engine's loop cannot know each step's input and output types — a flow's steps
/// have different ones, and there is exactly one loop. The obvious workaround, boxing
/// them into <c>object</c>, allocates per step and would lose budget B2 on the first
/// commit.
/// </para>
/// <para>
/// So the split is: <em>the engine owns control flow, the dispatcher owns types.</em>
/// A generated implementation switches on the step index and holds each
/// step's typed input and output in its own fields, which is why nothing here is
/// generic and nothing here is boxed. The engine sees only whether the step worked.
/// </para>
/// <para>
/// Until WP-5 exists, hand-written implementations stand in. That is deliberate: if
/// this interface is awkward to implement by hand, the generated version would have
/// been awkward to debug.
/// </para>
/// </remarks>
public interface IStepDispatcher
{
    /// <summary>Runs the step at <paramref name="stepIndex"/>.</summary>
    /// <param name="stepIndex">Position in the plan's step graph.</param>
    /// <param name="ctx">The flow's pooled context.</param>
    /// <param name="ct">Cancellation linked to the caller's token.</param>
    ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct);

    /// <summary>
    /// Runs the compensation registered for the step at <paramref name="stepIndex"/>.
    /// Only called for steps that both completed and declared one.
    /// </summary>
    ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct);

    /// <summary>
    /// Evaluates the predicate of the <see cref="StepKind.Branch"/> step at
    /// <paramref name="stepIndex"/>.
    /// </summary>
    /// <param name="stepIndex">
    /// Position in the plan's step graph. Always a branch — the engine calls this for no
    /// other kind, so an implementation is free to treat any other index as a defect.
    /// </param>
    /// <param name="ctx">The flow's pooled context.</param>
    /// <returns>
    /// <c>true</c> to continue at the next step, <c>false</c> to continue at the branch's
    /// <see cref="StepNode.Target"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Synchronous, returning <c>bool</c> rather than
    /// <c>ValueTask&lt;bool&gt;</c>.</strong> Both halves of that are load-bearing.
    /// </para>
    /// <para>
    /// An awaitable predicate would put an async state machine on the hot path. The
    /// engine's loop completes synchronously whenever its steps do — that is what
    /// <c>EngineAllocationTests</c> asserts and what makes budget B2 a hard zero — so a
    /// single awaited predicate would cost an allocation on every execution of every
    /// flow that branches, whether or not the predicate ever actually waits for
    /// anything.
    /// </para>
    /// <para>
    /// An awaitable predicate is also an invitation to do IO in one, and the determinism
    /// rules forbid it: a condition may read only the context, the flow input and prior
    /// step results (FLOWX1011), so that a durable replay takes the branch it took the
    /// first time. A signature that cannot express IO costs nothing to enforce; a
    /// diagnostic that reports it has to be written, kept accurate, and can be
    /// suppressed.
    /// </para>
    /// <para>
    /// There is no cancellation token for the same reason — a pure predicate has nothing
    /// to cancel, and the engine checks the deadline at the step the branch lands on.
    /// </para>
    /// </remarks>
    bool Evaluate(int stepIndex, FlowContext ctx);

    /// <summary>
    /// Runs the selector of the <see cref="StepKind.Switch"/> step at
    /// <paramref name="stepIndex"/> and reports which case matched.
    /// </summary>
    /// <param name="stepIndex">
    /// Position in the plan's step graph. Always a switch — the engine calls this for no
    /// other kind, so an implementation is free to treat any other index as a defect.
    /// </param>
    /// <param name="ctx">The flow's pooled context.</param>
    /// <returns>
    /// The zero-based position of the matching case in <see cref="StepNode.CaseTargets"/>,
    /// or <c>-1</c> when none matched, which sends control to
    /// <see cref="StepNode.Target"/> — the <c>Default</c> block, or the join when the
    /// author declared none.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>An <c>int</c>, not the value that was selected.</strong> Returning the
    /// value would mean returning it as <c>object</c>, which boxes an <c>enum</c> or an
    /// <c>int</c> on every switch a flow takes and loses budget B2 on the first commit —
    /// or making this method generic, which the engine cannot call because it does not
    /// know the type. An arm number is the one shape that carries the answer and no type.
    /// </para>
    /// <para>
    /// Synchronous and cancellation-free for exactly the reasons given on
    /// <see cref="Evaluate"/>: a selector may read only the context, the flow input and
    /// prior step results, so a durable replay selects the arm it selected before.
    /// </para>
    /// </remarks>
    int Select(int stepIndex, FlowContext ctx);
}
