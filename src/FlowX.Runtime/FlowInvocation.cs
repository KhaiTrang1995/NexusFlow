using System.Security.Claims;

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
/// <param name="Principal">
/// The caller, resolved once by the transport plugin from validated claims and from nothing
/// else, or <c>null</c> for an anonymous activation.
/// </param>
/// <remarks>
/// <para>
/// A readonly record struct, so starting a flow does not allocate an argument object. The
/// principal is a reference the trigger already holds, so carrying it costs a field copy.
/// </para>
/// <para>
/// <strong><see cref="Principal"/> is the whole of where identity comes from</strong>
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0027-identity-arrives-on-the-invocation.md">ADR-0027</a>).
/// It travels the path <see cref="TenantId"/> already travelled —
/// <c>TriggerHeaders</c> to here to <c>FlowContext.Principal</c> — so a stance means the same
/// thing whichever transport activated the flow, which is
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0004-universal-trigger-model.md">ADR-0004</a>'s
/// requirement. There is no ambient principal and no <c>AsyncLocal</c>: an authorisation
/// decision that depended on where the continuation happened to be running would be a
/// different decision on a thread-pool hop.
/// </para>
/// <para>
/// Last in the parameter list so that every existing positional construction still compiles
/// and still means what it did — a trigger that supplies no principal produces an anonymous
/// invocation, which is the truthful reading of one.
/// </para>
/// </remarks>
public readonly record struct FlowInvocation(
    string CorrelationId,
    string IdempotencyKey,
    string? TenantId = null,
    DateTimeOffset? Deadline = null,
    ClaimsPrincipal? Principal = null);

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

    /// <summary>
    /// There were compensations to run and this node did not run them, because it is no
    /// longer the instance's writer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A fenced-out or already-finished instance belongs to another node, which holds the
    /// journal, the frontier and the only compensation stack that describes what the instance
    /// really did. Unwinding here would add a second set of real effects on top of the ones
    /// the lost lease already failed to prevent — an undo racing a redo.
    /// </para>
    /// <para>
    /// Distinct from <see cref="NotRequired"/> on purpose. "Nothing needed undoing" and
    /// "something needed undoing and this node was not the one to do it" are different facts
    /// about an instance, and collapsing them would make a disowned saga indistinguishable
    /// from a query in every metric.
    /// </para>
    /// </remarks>
    Abandoned = 3,
}

/// <summary>The outcome of one flow execution. A struct: the result path allocates nothing.</summary>
public readonly struct FlowExecutionResult
{
    internal FlowExecutionResult(
        Error? error,
        int completedSteps,
        CompensationOutcome compensation,
        bool suspended = false,
        Guid? instanceId = null,
        FlowWake? wake = null)
    {
        Error = error;
        CompletedSteps = completedSteps;
        Compensation = compensation;
        IsSuspended = suspended;
        InstanceId = instanceId;
        Wake = wake;
    }

    /// <summary>The business error, or <c>null</c> when the flow completed.</summary>
    public Error? Error { get; }

    /// <summary>How many steps completed before the flow ended.</summary>
    public int CompletedSteps { get; }

    /// <summary>What happened to the compensations.</summary>
    public CompensationOutcome Compensation { get; }

    /// <summary>
    /// True when the flow stopped at a suspension point rather than ending.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Neither a success nor a failure, and the third answer is the whole of WP-63.</strong>
    /// The instance is <see cref="FlowInstanceState.Suspended"/> in the journal at its resume
    /// frontier: nothing has failed, so nothing is compensated, and nothing has completed, so
    /// the flow's <c>.Return(...)</c> has not run and must not be projected. Collapsing this
    /// into <see cref="IsSuccess"/> would hand a caller an output built from steps that never
    /// executed.
    /// </para>
    /// <para>
    /// A caller that wants the flow to continue delivers the signal it is waiting for; see
    /// <c>FlowHost.SignalAsync</c>. <see cref="InstanceId"/> is what names the instance to
    /// deliver it to.
    /// </para>
    /// </remarks>
    public bool IsSuspended { get; }

    /// <summary>
    /// The journaled instance this execution ran, or <c>null</c> for an ephemeral flow.
    /// </summary>
    /// <remarks>
    /// Recorded because a suspended flow is unreachable without it: the id is minted by the
    /// host, and until this existed nothing gave it back, so a caller had no way to name the
    /// instance a signal belongs to. Present on every journaled outcome rather than only on a
    /// suspended one, because "which instance did that request become" is the same question an
    /// operator asks of a flow that failed.
    /// </remarks>
    public Guid? InstanceId { get; }

    /// <summary>
    /// The wait a suspended flow is parked at and when it is due, or <c>null</c> when it is
    /// not suspended or nothing is due to wake it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recorded on the instance row by the same write that records
    /// <see cref="FlowInstanceState.Suspended"/>, and reported here because a caller that has
    /// just been told "this is waiting" has an obvious next question. A transport answering
    /// <c>202 Accepted</c> has a <c>Retry-After</c> to fill in without asking the journal
    /// again.
    /// </para>
    /// <para>
    /// Not a promise that anything <em>will</em> wake it at that instant: a deployment whose
    /// journal implements no <see cref="ITimerIndex"/>, or that runs no timer sweep, records
    /// this and never reads it. <c>FlowTimerScan.IsEnabled</c> is where that is answered.
    /// </para>
    /// </remarks>
    public FlowWake? Wake { get; }

    /// <summary>True when every step completed.</summary>
    /// <remarks>
    /// False for a suspended flow. It has not failed and it has not finished, and the steps
    /// after its suspension point have not run.
    /// </remarks>
    public bool IsSuccess => Error is null && !IsSuspended;

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
    /// A flow that reached a suspension point and is waiting in the journal.
    /// </summary>
    /// <param name="completedSteps">How many steps ran before the wait.</param>
    /// <param name="wake">The wait it is parked at and when it is due, if anything is.</param>
    /// <remarks>
    /// Nothing failed, so <see cref="CompensationOutcome.NotRequired"/> is the truthful answer
    /// rather than a placeholder: the completed steps are still on the instance's history and
    /// are still undone if the flow fails <em>after</em> it resumes.
    /// </remarks>
    public static FlowExecutionResult Suspended(int completedSteps, FlowWake? wake = null) =>
        new(null, completedSteps, CompensationOutcome.NotRequired, suspended: true, wake: wake);

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

    /// <inheritdoc cref="FlowExecutionResult.IsSuspended" />
    public bool IsSuspended => Outcome.IsSuspended;

    /// <inheritdoc cref="FlowExecutionResult.InstanceId" />
    public Guid? InstanceId => Outcome.InstanceId;

    /// <inheritdoc cref="FlowExecutionResult.Wake" />
    public FlowWake? Wake => Outcome.Wake;

    /// <summary>
    /// The projected output. Reading it on a failed flow is a defect in the caller, so
    /// it throws — the same stance <see cref="Result{T}.Value"/> takes, for the same
    /// reason: a silent <c>default</c> would be serialised to a client as a real answer.
    /// </summary>
    /// <exception cref="InvalidOperationException">The flow did not complete.</exception>
    /// <remarks>
    /// A suspended flow throws too, and is named separately rather than reported as an error
    /// it does not have: its <c>.Return(...)</c> clause reads values the steps after the
    /// suspension point were going to produce, so projecting it would build an answer out of
    /// work that has not happened.
    /// </remarks>
    public TOut Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException(
            IsSuspended
                ? "Cannot read Value of a flow that is suspended at a signal: its steps after " +
                  "the suspension point have not run, so its .Return(...) clause has no values " +
                  "to project. Deliver the signal, then read the resumed execution's Value."
                : $"Cannot read Value of a flow that ended with '{Error!.Code}'.");

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
    /// The code <see cref="DeadlineExceeded"/> raises.
    /// </summary>
    /// <remarks>
    /// A constant because the engine now branches on it: a durable instance that ran out of
    /// budget is recorded as <c>TimedOut</c> rather than <c>Failed</c>, and a terminal state
    /// decided by a string literal repeated in two files is a state that eventually
    /// disagrees with itself.
    /// </remarks>
    public const string DeadlineExceededCode = "flow.deadline_exceeded";

    /// <summary>
    /// The flow ran out of its absolute budget. Retryable: a fresh invocation gets a
    /// fresh deadline, and the work may well succeed.
    /// </summary>
    public static Error DeadlineExceeded(string flowId, DateTimeOffset deadline) =>
        new Error(
            DeadlineExceededCode,
            $"Flow '{flowId}' exceeded its deadline of {deadline:O}.",
            ErrorCategory.Unavailable)
            .With("flowId", flowId)
            .With("deadline", deadline);

    /// <summary>The code <see cref="StepTimedOut"/> raises.</summary>
    /// <remarks>
    /// A constant for <see cref="DeadlineExceededCode"/>'s reason: it is the one policy
    /// failure a caller is most likely to branch on, and a code spelled out at each of its
    /// readers is a code that eventually disagrees with itself.
    /// </remarks>
    public const string StepTimedOutCode = "policy.step_timeout";

    /// <summary>
    /// A step's <c>Timeout</c> policy expired. Retryable: the dependency was slow, not wrong.
    /// </summary>
    /// <param name="capabilityId">What was being called when the budget ran out.</param>
    /// <param name="budget">
    /// The budget that expired — the declared timeout, or what was left of the flow's
    /// deadline when it was shorter.
    /// </param>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="DeadlineExceeded"/> even though both are a clock running out,
    /// because the two say different things to whoever reads them and lead to different
    /// repairs. A deadline names the whole flow's budget and is a statement that the request
    /// as a whole took too long; this names one dependency, and the usual fix is that
    /// dependency rather than the flow.
    /// </para>
    /// <para>
    /// <see cref="ErrorCategory.Unavailable"/>, which is in <c>Retry</c>'s own default
    /// retryable set — deliberately, because a timeout is exactly the failure a retry is for,
    /// and a timeout the retry declined to act on would make the two policies contradict each
    /// other on the same step.
    /// </para>
    /// </remarks>
    public static Error StepTimedOut(string capabilityId, TimeSpan budget) =>
        new Error(
            StepTimedOutCode,
            $"Capability '{capabilityId}' did not complete within its {budget} timeout.",
            ErrorCategory.Unavailable)
            .With("capabilityId", capabilityId)
            .With("timeout", budget);

    /// <summary>The code <see cref="CircuitOpen"/> raises.</summary>
    public const string CircuitOpenCode = "policy.circuit_open";

    /// <summary>
    /// A step's <c>CircuitBreaker</c> is open, so the call was refused without being made.
    /// </summary>
    /// <param name="capabilityId">The capability whose breaker is open.</param>
    /// <param name="until">When the breaker will next let a call through.</param>
    /// <remarks>
    /// <para>
    /// <see cref="ErrorCategory.Unavailable"/>, and the category is the whole point: the
    /// dependency is not saying no, the platform is saying not yet. A caller that retries the
    /// flow later is doing the right thing, which is what this category means everywhere else
    /// in the runtime.
    /// </para>
    /// <para>
    /// <strong>It is retryable in <c>Retry</c>'s default set, and that is not a mistake.</strong>
    /// An open breaker refuses each attempt in turn, so the retries cost the dependency
    /// nothing and end where they would have ended anyway — which is the behaviour
    /// <c>docs/10-Policy-Framework.md §11</c> asks for when it says to always pair a retry
    /// with a breaker: the breaker is what makes the retry safe, not what makes it stop.
    /// </para>
    /// </remarks>
    public static Error CircuitOpen(string capabilityId, DateTimeOffset until) =>
        new Error(
            CircuitOpenCode,
            $"The circuit breaker for capability '{capabilityId}' is open until {until:O}. " +
            "The call was refused without being made.",
            ErrorCategory.Unavailable)
            .With("capabilityId", capabilityId)
            .With("until", until);

    /// <summary>The code <see cref="BulkheadRejected"/> raises.</summary>
    public const string BulkheadRejectedCode = "policy.bulkhead_rejected";

    /// <summary>
    /// A step's <c>Bulkhead</c> had no permit and no room to queue for one.
    /// </summary>
    /// <param name="capabilityId">The capability whose bulkhead is full.</param>
    /// <param name="maxConcurrency">How many callers it admits at once.</param>
    /// <remarks>
    /// <see cref="ErrorCategory.Unavailable"/>: the dependency is healthy and this caller is
    /// simply not getting in right now. Reporting it as <see cref="ErrorCategory.Internal"/>
    /// would attribute a deliberate isolation decision to a defect, and would put it outside
    /// the categories a retry acts on — a bulkhead rejection is the one failure where waiting
    /// and asking again is exactly the right response.
    /// </remarks>
    public static Error BulkheadRejected(string capabilityId, int maxConcurrency) =>
        new Error(
            BulkheadRejectedCode,
            $"The bulkhead for capability '{capabilityId}' admits {maxConcurrency} caller(s) " +
            "at once and has no queue depth left. The call was refused without being made.",
            ErrorCategory.Unavailable)
            .With("capabilityId", capabilityId)
            .With("maxConcurrency", maxConcurrency);

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

    /// <summary>The code <see cref="SignalNotReceived"/> raises.</summary>
    /// <remarks>
    /// A constant because a caller has a reason to branch on it that no other engine error
    /// has: it is the one failure that is a business outcome the author anticipated — the
    /// countersignature did not arrive — rather than something going wrong.
    /// </remarks>
    public const string SignalNotReceivedCode = "flow.signal_not_received";

    /// <summary>
    /// A suspension point's declared timeout expired and the flow declared no
    /// <c>.OnTimeout(...)</c> block to take instead.
    /// </summary>
    /// <param name="flowId">The waiting flow.</param>
    /// <param name="stepIndex">Index of the wait, so the failure names one call site.</param>
    /// <param name="signalType">What it was waiting for.</param>
    /// <param name="timeout">How long the author gave it.</param>
    /// <remarks>
    /// <para>
    /// <strong>A failure, and the completed compensable steps unwind behind it.</strong> The
    /// alternative — carrying on at the next step — would run steps that bind a payload
    /// nothing delivered, which is the same defect as a wait that does not wait, arriving one
    /// step later. An author who wants something else to happen declares it, and then this is
    /// unreachable: the block is what the flow does instead.
    /// </para>
    /// <para>
    /// <see cref="ErrorCategory.Unavailable"/> rather than <see cref="ErrorCategory.Internal"/>,
    /// for the reason a deadline is: nothing is wrong with the flow, something outside it did
    /// not happen in time, and a fresh invocation may well be countersigned.
    /// </para>
    /// <para>
    /// Distinct from <see cref="DeadlineExceededCode"/> on purpose. That one is the flow's
    /// whole budget and says nothing about which step was standing when it ran out; this names
    /// the wait, and the instance is recorded <c>Failed</c> rather than <c>TimedOut</c> because
    /// the flow did not run out of time — one thing it was waiting for did.
    /// </para>
    /// </remarks>
    public static Error SignalNotReceived(
        string flowId, int stepIndex, string signalType, TimeSpan timeout) =>
        new Error(
            SignalNotReceivedCode,
            $"Step {stepIndex} of flow '{flowId}' waited {timeout} for signal " +
            $"'{signalType}' and it did not arrive. The flow declares no OnTimeout block, so " +
            "there is nowhere for the wait to go: continuing would run the steps after it " +
            "against a payload nothing delivered.",
            ErrorCategory.Unavailable)
            .With("flowId", flowId)
            .With("stepIndex", stepIndex)
            .With("signalType", signalType)
            .With("timeout", timeout);

    /// <summary>
    /// An inline composed child reached a suspension point, which its parent cannot wait at.
    /// </summary>
    /// <param name="flowId">The composing parent.</param>
    /// <param name="stepIndex">Index of the composition, so the failure names one call site.</param>
    /// <param name="subFlowId">The child that tried to wait.</param>
    /// <remarks>
    /// <para>
    /// <strong>Refused rather than allowed to double an effect.</strong> A parent records a
    /// composition as one row, written when the child finishes. A child that suspends writes
    /// no such row, so a parent resumed afterwards would reach the composition, find nothing
    /// committed, and compose a <em>second</em> child instance — running every step the first
    /// child had already run, against the same real systems. The journal would then hold two
    /// child instances for one composition and no record that they were meant to be one.
    /// </para>
    /// <para>
    /// <see cref="ErrorCategory.Internal"/> because it is a property of how the two flows are
    /// composed rather than a business outcome: retrying reaches the same wait. The repair is
    /// to give the waiting flow its own trigger and correlate it, or to compose it
    /// <c>Detached</c> — a detached child has its own instance and its own lifecycle, so it is
    /// allowed to wait.
    /// </para>
    /// </remarks>
    public static Error SuspensionInsideComposition(string flowId, int stepIndex, string subFlowId) =>
        new Error(
            "flow.suspension_inside_composition",
            $"Step {stepIndex} of flow '{flowId}' composes '{subFlowId}' inline, and that " +
            "child reached a suspension point. A parent records a composition as one row " +
            "written when the child finishes, so a parent resumed past a waiting child would " +
            "compose a second child instance and repeat its effects. Give the waiting flow " +
            "its own trigger, or compose it with SubFlowMode.Detached.",
            ErrorCategory.Internal)
            .With("flowId", flowId)
            .With("stepIndex", stepIndex)
            .With("subFlowId", subFlowId);

    /// <summary>
    /// A flow declaring <see cref="ExecutionProfile.Durable"/> was started with no journal to
    /// write to.
    /// </summary>
    /// <param name="flowId">The flow that declared durability.</param>
    /// <remarks>
    /// <para>
    /// <strong>Refused rather than run ephemerally, and that is the whole change.</strong>
    /// Until the runtime read the profile, this was the silent default: a flow declared
    /// <c>Durable</c>, ran with no journal, no lease and no resume, and nothing anywhere said
    /// so — the gap <c>FLOWX1028</c> existed to describe. Running it quietly again here would
    /// reintroduce exactly that, one layer lower and with no diagnostic left to raise it.
    /// </para>
    /// <para>
    /// It is a rejection rather than a failure: no step ran, so there is nothing to
    /// compensate. <see cref="ErrorCategory.Internal"/> because it is a wiring defect in the
    /// host and not a business outcome — retrying reaches the same missing journal.
    /// </para>
    /// </remarks>
    public static Error DurabilityNotConfigured(string flowId) =>
        new Error(
            "flow.durability_not_configured",
            $"Flow '{flowId}' declares Profile = ExecutionProfile.Durable, so its step " +
            "boundaries must be journaled — but it was started without a journal. Supply a " +
            "DurableExecution (DurableExecution.BeginAsync for a new instance, ResumeAsync " +
            "for one being picked up), or declare Ephemeral if this flow does not need to " +
            "survive a crash.",
            ErrorCategory.Internal)
            .With("flowId", flowId);

    /// <summary>
    /// A journal was supplied for a flow that did not declare <see cref="ExecutionProfile.Durable"/>.
    /// </summary>
    /// <param name="flowId">The flow that was started.</param>
    /// <param name="profile">What it actually declares.</param>
    /// <remarks>
    /// The mirror of <see cref="DurabilityNotConfigured"/>, and refused for the symmetrical
    /// reason: the profile is the declaration. Journaling a flow whose author declined
    /// durability charges it a store round trip per step for a guarantee it did not ask for,
    /// and quietly ignoring the journal would leave the caller believing an instance exists
    /// that nothing will ever write to.
    /// </remarks>
    public static Error ProfileIsNotDurable(string flowId, ExecutionProfile profile) =>
        new Error(
            "flow.profile_is_not_durable",
            $"Flow '{flowId}' declares Profile = ExecutionProfile.{profile}, so there is " +
            "nothing to journal, but a DurableExecution was supplied. The profile is the " +
            "declaration: set Durable on the flow, or start it through the overload that " +
            "takes no journal.",
            ErrorCategory.Internal)
            .With("flowId", flowId)
            .With("profile", profile.ToString());

    /// <summary>
    /// A resumed instance's journaled state bag could not be read back.
    /// </summary>
    /// <param name="flowId">The flow being resumed.</param>
    /// <param name="instanceId">The instance whose snapshot could not be restored.</param>
    /// <param name="exception">What the dispatcher threw.</param>
    /// <remarks>
    /// Reported rather than tolerated. A resumed flow whose bag could not be rehydrated would
    /// run every step after the frontier against values no step produced — which is worse
    /// than not resuming at all, because the effects would be real. The usual cause is a
    /// deployment whose contracts no longer match the ones the instance was pinned to.
    /// </remarks>
    public static Error StateRestoreFailed(string flowId, Guid instanceId, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new Error(
            "flow.state_restore_failed",
            $"Instance '{instanceId}' of flow '{flowId}' could not be rehydrated from its " +
            $"journaled state bag: {exception.GetType().Name}. Resuming would run the rest " +
            "of the flow against values no step produced, so it is refused instead.",
            ErrorCategory.Internal)
            .With("flowId", flowId)
            .With("instanceId", instanceId)
            .With("exceptionType", exception.GetType().FullName);
    }

    /// <summary>
    /// A step could not be described for the journal.
    /// </summary>
    /// <param name="flowId">The flow whose step was being committed.</param>
    /// <param name="stepIndex">Index of the step, so the failure names one payload.</param>
    /// <param name="exception">What the dispatcher threw.</param>
    /// <remarks>
    /// Its own code rather than <see cref="Unhandled"/>: the capability worked and the effect
    /// happened: what failed is writing it down. That distinction is what an operator needs,
    /// because the two have opposite remedies — one is a broken dependency, the other a
    /// contract that is not in the generated JSON context.
    /// </remarks>
    public static Error JournalPayloadFailed(string flowId, int stepIndex, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new Error(
            "flow.journal_payload_failed",
            $"Step {stepIndex} of flow '{flowId}' completed, but describing it for the " +
            $"journal threw {exception.GetType().Name}. The step's effect has happened and " +
            "no row records it, so this instance must be treated as having an uncommitted " +
            "boundary.",
            ErrorCategory.Internal)
            .With("flowId", flowId)
            .With("stepIndex", stepIndex)
            .With("exceptionType", exception.GetType().FullName);
    }
}
