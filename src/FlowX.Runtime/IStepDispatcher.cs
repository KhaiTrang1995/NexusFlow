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
}
