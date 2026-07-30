namespace FlowX.Runtime;

/// <summary>
/// What a trigger supplies when it starts a flow: identity, correlation and the
/// caller's remaining budget.
/// </summary>
/// <param name="CorrelationId">Correlates every step, span and log record of this operation.</param>
/// <param name="IdempotencyKey">
/// Stable across retries and replays. Passed to downstream systems so they can
/// deduplicate — this is what turns at-least-once delivery into effectively-once
/// effects.
/// </param>
/// <param name="TenantId">Resolved from validated claims only, never from a payload or header.</param>
/// <param name="Deadline">
/// An absolute instant that <strong>shortens</strong> the flow's declared deadline.
/// It can never lengthen it: a caller must not be able to buy a flow more budget
/// than its own author gave it.
/// </param>
/// <remarks>
/// A readonly record struct, so starting a flow does not allocate an argument object.
/// </remarks>
public readonly record struct FlowInvocation(
    string CorrelationId,
    string IdempotencyKey,
    string? TenantId = null,
    DateTimeOffset? Deadline = null);

/// <summary>What happened to the compensations after a flow failed.</summary>
public enum CompensationOutcome
{
    /// <summary>The flow succeeded, or no completed step declared a compensation.</summary>
    NotRequired = 0,

    /// <summary>Every registered compensation ran and reported success.</summary>
    Succeeded = 1,

    /// <summary>
    /// At least one compensation failed. The others still ran — abandoning the
    /// remaining undo work because one of them failed leaves strictly more mess.
    /// </summary>
    PartiallyFailed = 2,
}

/// <summary>The outcome of one flow execution. A struct: the result path allocates nothing.</summary>
public readonly struct FlowExecutionResult
{
    internal FlowExecutionResult(Error? error, int completedSteps, CompensationOutcome compensation)
    {
        Error = error;
        CompletedSteps = completedSteps;
        Compensation = compensation;
    }

    /// <summary>The business error, or <c>null</c> when the flow completed.</summary>
    public Error? Error { get; }

    /// <summary>How many steps completed before the flow ended.</summary>
    public int CompletedSteps { get; }

    /// <summary>What happened to the compensations.</summary>
    public CompensationOutcome Compensation { get; }

    /// <summary>True when every step completed.</summary>
    public bool IsSuccess => Error is null;

    /// <summary>True when the flow ended with an error.</summary>
    /// <remarks>
    /// Present for symmetry with <see cref="Result{T}"/>. Call sites that branch on
    /// failure read better than ones that negate success, and an API where one type
    /// offers both and its sibling offers one is an API people have to check.
    /// </remarks>
    public bool IsFailure => Error is not null;

    /// <summary>
    /// A flow that was refused before any step ran — by a draining host, an admission
    /// quota, or anything else that decides not to start work.
    /// </summary>
    /// <remarks>
    /// Distinct from a flow that failed: nothing executed, so there is nothing to
    /// compensate and no completed steps to report. Collapsing the two would make a
    /// rejected flow look like one that ran and failed, which changes what an operator
    /// concludes from the metrics.
    /// </remarks>
    public static FlowExecutionResult Rejected(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new FlowExecutionResult(error, completedSteps: 0, CompensationOutcome.NotRequired);
    }

    /// <summary>
    /// A flow with a declared output that was refused before any step ran.
    /// </summary>
    /// <typeparam name="TOut">The flow's declared output contract.</typeparam>
    /// <param name="error">Why the flow was refused.</param>
    /// <remarks>
    /// Lives here rather than on <see cref="FlowExecutionResult{TOut}"/> because a static
    /// factory belongs on the non-generic type: one call site, not one per instantiation.
    /// </remarks>
    public static FlowExecutionResult<TOut> Rejected<TOut>(Error error)
        => new(Rejected(error), default);
}

/// <summary>
/// The outcome of one flow execution, together with the value its <c>.Return(...)</c>
/// clause projected.
/// </summary>
/// <typeparam name="TOut">The flow's declared output contract.</typeparam>
/// <remarks>
/// <para>
/// Separate from the non-generic <see cref="FlowExecutionResult"/> rather than replacing
/// it: a flow triggered by a queue consumer has no caller to return a value to, and
/// forcing every such call site to name an output type it discards would be ceremony.
/// </para>
/// <para>
/// Also a struct, and the projection runs while the pooled context is still rented, so
/// carrying an output costs one copy and no allocation.
/// </para>
/// </remarks>
public readonly struct FlowExecutionResult<TOut>
{
    private readonly TOut? _value;

    internal FlowExecutionResult(FlowExecutionResult outcome, TOut? value)
    {
        Outcome = outcome;
        _value = value;
    }

    /// <summary>The execution itself: error, completed steps, compensation.</summary>
    public FlowExecutionResult Outcome { get; }

    /// <summary>The business error, or <c>null</c> when the flow completed.</summary>
    public Error? Error => Outcome.Error;

    /// <summary>How many steps completed before the flow ended.</summary>
    public int CompletedSteps => Outcome.CompletedSteps;

    /// <summary>What happened to the compensations.</summary>
    public CompensationOutcome Compensation => Outcome.Compensation;

    /// <summary>True when every step completed.</summary>
    public bool IsSuccess => Outcome.IsSuccess;

    /// <summary>True when the flow ended with an error.</summary>
    public bool IsFailure => Outcome.IsFailure;

    /// <summary>
    /// The projected output. Reading it on a failed flow is a defect in the caller, so
    /// it throws — the same stance <see cref="Result{T}.Value"/> takes, for the same
    /// reason: a silent <c>default</c> would be serialised to a client as a real answer.
    /// </summary>
    /// <exception cref="InvalidOperationException">The flow did not complete.</exception>
    public TOut Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException(
            $"Cannot read Value of a flow that ended with '{Error!.Code}'.");

    /// <summary>Non-throwing accessor, for call sites that branch on the outcome.</summary>
    public bool TryGetValue(out TOut? value)
    {
        value = _value;
        return IsSuccess;
    }
}

/// <summary>Errors the engine itself produces, as opposed to those a capability returns.</summary>
public static class FlowErrors
{
    /// <summary>
    /// The flow ran out of its absolute budget. Retryable: a fresh invocation gets a
    /// fresh deadline, and the work may well succeed.
    /// </summary>
    public static Error DeadlineExceeded(string flowId, DateTimeOffset deadline) =>
        new Error(
            "flow.deadline_exceeded",
            $"Flow '{flowId}' exceeded its deadline of {deadline:O}.",
            ErrorCategory.Unavailable)
            .With("flowId", flowId)
            .With("deadline", deadline);

    /// <summary>The caller cancelled. Not a defect, and not the flow's fault.</summary>
    public static Error Cancelled(string flowId) =>
        new Error(
            "flow.cancelled",
            $"Flow '{flowId}' was cancelled by its caller.",
            ErrorCategory.Unavailable)
            .With("flowId", flowId);

    /// <summary>
    /// A capability threw instead of returning an <see cref="FlowX.Error"/>. That is a
    /// defect in the capability — expected failures are values (ADR-0007) — so it is
    /// reported as <see cref="ErrorCategory.Internal"/> and counted separately.
    /// </summary>
    public static Error Unhandled(string capabilityId, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new Error(
            "capability.unhandled",
            $"Capability '{capabilityId}' threw {exception.GetType().Name}. Expected " +
            "failures must be returned as a Result, not thrown.",
            ErrorCategory.Internal)
            .With("capabilityId", capabilityId)
            .With("exceptionType", exception.GetType().FullName);
    }

    /// <summary>
    /// A <c>When</c> predicate threw instead of answering.
    /// </summary>
    /// <param name="flowId">The flow whose branch failed.</param>
    /// <param name="stepIndex">Index of the branch, so the failure names one condition.</param>
    /// <param name="exception">What the predicate threw.</param>
    /// <remarks>
    /// Its own code rather than <see cref="Unhandled"/>, because the two say different
    /// things to whoever reads them. An unhandled capability error means a dependency
    /// misbehaved; this one means the flow could not decide which way to go, and the
    /// usual cause is a predicate reading a value no step on the path so far produced.
    /// Reported as <see cref="ErrorCategory.Internal"/>: a predicate is pure by
    /// construction, so it throwing is a defect and never a transient fault.
    /// </remarks>
    public static Error PredicateFailed(string flowId, int stepIndex, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new Error(
            "flow.predicate_failed",
            $"The condition at step {stepIndex} of flow '{flowId}' threw " +
            $"{exception.GetType().Name}. A condition may read only the context, the flow " +
            "input and prior step results, and must not throw.",
            ErrorCategory.Internal)
            .With("flowId", flowId)
            .With("stepIndex", stepIndex)
            .With("exceptionType", exception.GetType().FullName);
    }
}
