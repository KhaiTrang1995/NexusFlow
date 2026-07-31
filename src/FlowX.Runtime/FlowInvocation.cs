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

    /// <summary>
    /// A <c>Parallel</c> fork's merge strategy was not met.
    /// </summary>
    /// <param name="flowId">The flow whose fork failed.</param>
    /// <param name="stepIndex">Index of the fork, so the failure names one merge point.</param>
    /// <param name="required">How many successes the strategy demanded.</param>
    /// <param name="succeeded">How many it got.</param>
    /// <param name="branches">How many branches the fork declared.</param>
    /// <remarks>
    /// <para>
    /// Raised for <c>FirstSuccess</c> and <c>Quorum(n)</c> only. <c>AllMustSucceed</c>
    /// reports the branch's own error instead, because there is exactly one failure to
    /// point at and replacing it with a count would throw away the reason; and
    /// <c>AllSettled</c> never fails the flow at all.
    /// </para>
    /// <para>
    /// <see cref="ErrorCategory.Conflict"/> rather than <see cref="ErrorCategory.Internal"/>:
    /// three of four fraud checks failing is a business outcome the flow declared a rule
    /// for, not a defect in the platform.
    /// </para>
    /// </remarks>
    public static Error MergeNotSatisfied(
        string flowId,
        int stepIndex,
        int required,
        int succeeded,
        int branches)
    {
        return new Error(
            "flow.merge_not_satisfied",
            $"Step {stepIndex} of flow '{flowId}' needed {required} of its {branches} " +
            $"parallel branches to succeed and got {succeeded}. Branches still running " +
            "were cancelled; work they had already completed is compensated with the rest " +
            "of the flow.",
            ErrorCategory.Conflict)
            .With("flowId", flowId)
            .With("stepIndex", stepIndex)
            .With("required", required)
            .With("succeeded", succeeded)
            .With("branches", branches);
    }

    /// <summary>
    /// A <c>Switch</c> selector threw instead of producing a value.
    /// </summary>
    /// <param name="flowId">The flow whose switch failed.</param>
    /// <param name="stepIndex">Index of the switch, so the failure names one selector.</param>
    /// <param name="exception">What the selector threw.</param>
    /// <remarks>
    /// Its own code rather than <see cref="PredicateFailed"/>, because the two point at
    /// different lines and at different mistakes: a predicate answers yes or no, a
    /// selector produces the value the cases are matched against. Reported as
    /// <see cref="ErrorCategory.Internal"/> for the same reason — a selector is pure by
    /// construction, so it throwing is a defect and never a transient fault.
    /// </remarks>
    public static Error SelectorFailed(string flowId, int stepIndex, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new Error(
            "flow.selector_failed",
            $"The switch selector at step {stepIndex} of flow '{flowId}' threw " +
            $"{exception.GetType().Name}. A selector may read only the context, the flow " +
            "input and prior step results, and must not throw.",
            ErrorCategory.Internal)
            .With("flowId", flowId)
            .With("stepIndex", stepIndex)
            .With("exceptionType", exception.GetType().FullName);
    }

    /// <summary>
    /// A <c>ForEach</c> selector threw instead of producing a collection.
    /// </summary>
    /// <param name="flowId">The flow whose iteration failed.</param>
    /// <param name="stepIndex">Index of the iteration, so the failure names one selector.</param>
    /// <param name="exception">What the selector threw.</param>
    /// <remarks>
    /// Its own code rather than <see cref="SelectorFailed"/>, for the reason that one is
    /// distinct from <see cref="PredicateFailed"/>: the three point at different lines and
    /// at different mistakes, and "the switch selector at step 4 threw" would be actively
    /// misleading when step 4 is a loop. Reported as <see cref="ErrorCategory.Internal"/>
    /// on the same grounds — the selector is pure by construction, so it throwing is a
    /// defect rather than a transient fault, and no element has run when it happens.
    /// </remarks>
    public static Error IterationFailed(string flowId, int stepIndex, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new Error(
            "flow.iteration_failed",
            $"The collection selector at step {stepIndex} of flow '{flowId}' threw " +
            $"{exception.GetType().Name}. A selector may read only the context, the flow " +
            "input and prior step results, and must not throw.",
            ErrorCategory.Internal)
            .With("flowId", flowId)
            .With("stepIndex", stepIndex)
            .With("exceptionType", exception.GetType().FullName);
    }

    /// <summary>
    /// A <c>SubFlow</c>'s input mapping threw instead of producing the child's input.
    /// </summary>
    /// <param name="flowId">The parent flow.</param>
    /// <param name="stepIndex">Index of the sub-flow step, so the failure names one mapping.</param>
    /// <param name="exception">What the mapping threw.</param>
    /// <remarks>
    /// Its own code rather than <see cref="IterationFailed"/> or
    /// <see cref="SelectorFailed"/>, for the reason those are distinct from each other:
    /// they point at different lines and at different mistakes, and no element or arm is
    /// involved here. Reported as <see cref="ErrorCategory.Internal"/> on the same grounds —
    /// the mapping is pure by construction, so it throwing is a defect and never a
    /// transient fault, and the child has not started when it happens.
    /// </remarks>
    public static Error SubFlowMappingFailed(string flowId, int stepIndex, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new Error(
            "flow.subflow_mapping_failed",
            $"The sub-flow input mapping at step {stepIndex} of flow '{flowId}' threw " +
            $"{exception.GetType().Name}. A mapping may read only the context, the flow " +
            "input and prior step results, and must not throw.",
            ErrorCategory.Internal)
            .With("flowId", flowId)
            .With("stepIndex", stepIndex)
            .With("exceptionType", exception.GetType().FullName);
    }

    /// <summary>
    /// A synchronous sub-flow failed, and its failure is the parent's.
    /// </summary>
    /// <param name="flowId">The parent flow.</param>
    /// <param name="stepIndex">Index of the sub-flow step.</param>
    /// <param name="subFlowId">The child flow.</param>
    /// <param name="cause">The child's own error, kept as the reason.</param>
    /// <remarks>
    /// <para>
    /// <strong>The child's error is carried, not replaced.</strong> A parent that reported
    /// only "the sub-flow failed" would throw away the one fact an operator needs — the
    /// payment was declined, the address did not validate — and would make every
    /// composition look identical in the logs. So the code, the message and the category
    /// are the child's; what this adds is where it happened, which the child cannot know.
    /// </para>
    /// <para>
    /// The category in particular has to be the child's: a declined payment is a
    /// <see cref="ErrorCategory.Conflict"/> whether or not it happened one flow down, and
    /// relabelling it <see cref="ErrorCategory.Internal"/> at the boundary would change
    /// what the HTTP mapping returns and whether a caller retries.
    /// </para>
    /// </remarks>
    public static Error SubFlowFailed(string flowId, int stepIndex, string subFlowId, Error cause)
    {
        ArgumentNullException.ThrowIfNull(cause);

        return cause
            .With("subFlowOf", flowId)
            .With("subFlowStepIndex", stepIndex)
            .With("subFlowId", subFlowId);
    }

    /// <summary>
    /// Sub-flow composition nested deeper than the runtime will follow.
    /// </summary>
    /// <param name="flowId">The flow that tried to compose one level too many.</param>
    /// <param name="subFlowId">The child it tried to compose.</param>
    /// <param name="depth">The cap that was reached.</param>
    /// <remarks>
    /// <para>
    /// <strong>This is the run-time half of the DAG guarantee.</strong> <c>FLOWX1021</c>
    /// refuses a cycle at build time, and does so soundly for every edge it can see — but
    /// it can only see the flows whose <c>Define</c> bodies are in the compilation. A cycle
    /// closed through a referenced assembly is invisible to it, and without a cap it would
    /// be an unbounded recursion: a stack overflow, which kills the process rather than
    /// failing one flow.
    /// </para>
    /// <para>
    /// <see cref="ErrorCategory.Internal"/> because it is a defect in the composition, not
    /// a transient fault: retrying reaches exactly the same depth.
    /// </para>
    /// </remarks>
    public static Error SubFlowTooDeep(string flowId, string subFlowId, int depth) =>
        new Error(
            "flow.subflow_too_deep",
            $"Flow '{flowId}' composes '{subFlowId}' more than {depth} sub-flows deep. " +
            "The flow graph is a DAG and FLOWX1021 refuses a cycle it can see, but a cycle " +
            "closed through a referenced assembly is not visible at build time — this cap " +
            "is what turns that into one failed flow instead of a stack overflow.",
            ErrorCategory.Internal)
            .With("flowId", flowId)
            .With("subFlowId", subFlowId)
            .With("maxDepth", depth);
}
