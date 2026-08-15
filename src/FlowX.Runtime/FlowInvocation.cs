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
/// <param name="IsContinuation">
/// True when the platform is continuing an instance it already admitted — a timer sweep or a
/// recovery scan — rather than a caller asking for something.
/// </param>
/// <param name="TenantAttested">
/// True when <see cref="TenantId"/> was derived by the platform from a source no caller can
/// set — the schema a change was read from, a broker field a FlowX producer wrote, a
/// schedule's declared tenant — rather than asserted by whoever is calling.
/// </param>
/// <remarks>
/// <para>
/// A readonly record struct, so starting a flow does not allocate an argument object. The
/// principal is a reference the trigger already holds, so carrying it costs a field copy.
/// </para>
/// <para>
/// <strong><see cref="Principal"/> is the whole of where identity comes from</strong>
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0028-identity-arrives-on-the-invocation.md">ADR-0028</a>).
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
/// <para>
/// <strong><see cref="IsContinuation"/> is what stops a <c>.Delay</c> revoking a grant.</strong>
/// A resumed instance's invocation is rebuilt from its journal row, which carries no claims by
/// design, so a timer sweep and a recovery scan have no principal and never will. Deciding a
/// stance against that absence would refuse every step after a wait — making
/// <c>.Delay(TimeSpan.FromHours(1))</c> a construct no flow could place before an
/// authenticated step, and turning a node restart into a refusal.
/// </para>
/// <para>
/// The distinction it draws is a real one rather than an escape hatch: the steps of an
/// instance already admitted are the flow author's declared sequence, not a new request, and
/// nobody is asking for anything. A <em>signal</em> is the opposite — somebody is delivering
/// something now — so <c>FlowHost.SignalAsync</c> takes a principal and leaves this false,
/// and the steps after that wait are decided against the deliverer.
/// </para>
/// <para>
/// <strong><see cref="TenantAttested"/> is what lets a trigger with no caller start a flow, and
/// it is deliberately not <see cref="IsContinuation"/>.</strong> A change, a broker message and
/// a cron occurrence each know a tenant without anybody having claimed one — a change was read
/// out of that tenant's schema, a message carries a field this platform's own publisher wrote,
/// a schedule declared <c>PerTenant</c> and was fanned out over the tenant directory. Reusing
/// <see cref="IsContinuation"/> to carry that would have been one field fewer and one guarantee
/// fewer: that flag also suppresses the step-authorisation decision, which is right for a sweep
/// resuming an instance already admitted and wrong for a start. An attested start is a start —
/// every stance is decided, against a principal that is absent, so a step declaring
/// <c>Authenticated</c> under a cron trigger is refused rather than run.
/// </para>
/// <para>
/// Nothing a caller can reach sets it: <c>HttpTriggerReader</c> leaves it false, and
/// <c>OnlyAPlatformTriggerAttestsATenant</c> is the gate that keeps it that way.
/// </para>
/// </remarks>
public readonly record struct FlowInvocation(
    string CorrelationId,
    string IdempotencyKey,
    string? TenantId = null,
    DateTimeOffset? Deadline = null,
    ClaimsPrincipal? Principal = null,
    bool IsContinuation = false,
    bool TenantAttested = false);

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

    /// <summary>The code <see cref="RateLimited"/> raises.</summary>
    /// <remarks>
    /// A constant for <see cref="StepTimedOutCode"/>'s reason, and one more: this is the row
    /// <c>docs/10-Policy-Framework.md</c> §3 says a transport turns into a <c>429</c> with a
    /// <c>Retry-After</c>, so it is the policy failure most likely to be branched on outside
    /// this repository.
    /// </remarks>
    public const string RateLimitedCode = "policy.rate_limited";

    /// <summary>
    /// A step's <c>RateLimit</c> had no permit left for this caller, so the call was refused
    /// without being made.
    /// </summary>
    /// <param name="capabilityId">The capability whose budget is spent.</param>
    /// <param name="retryAfter">How long until a permit accrues, as the store reported it.</param>
    /// <remarks>
    /// <para>
    /// <see cref="ErrorCategory.Unavailable"/>, matching <see cref="BulkheadRejected"/> and for
    /// the same reason: the dependency is healthy and this caller is simply not getting in right
    /// now, so waiting and asking again is the correct response and the category is the half of
    /// the error that says so.
    /// </para>
    /// <para>
    /// <strong>It is retryable in <c>Retry</c>'s default set, and stage 1 sits outside the retry
    /// loop, so a refused caller is refused once.</strong> The two facts are consistent because
    /// they are about different loops: a step's own retry never sees this error — the limiter
    /// runs before the loop opens — and a <em>caller</em> retrying the whole flow is the party
    /// <c>retryAfter</c> is addressed to.
    /// </para>
    /// </remarks>
    public static Error RateLimited(string capabilityId, TimeSpan retryAfter) =>
        new Error(
            RateLimitedCode,
            $"The rate limit for capability '{capabilityId}' has no permit left. Retry after " +
            $"{retryAfter}. The call was refused without being made.",
            ErrorCategory.Unavailable)
            .With("capabilityId", capabilityId)
            .With("retryAfter", retryAfter);

    /// <summary>The code <see cref="RateLimiterUnavailable"/> raises.</summary>
    public const string RateLimiterUnavailableCode = "policy.ratelimit_unavailable";

    /// <summary>
    /// A step declares a <c>RateLimit</c> and the limiter could not decide — none was
    /// registered, or the one that was did not answer.
    /// </summary>
    /// <param name="capabilityId">The capability whose limit could not be consulted.</param>
    /// <param name="cause">What the store reported, when there was a store.</param>
    /// <remarks>
    /// <para>
    /// <strong>A refusal, and the whole of what makes the policy honest.</strong> Admitting when
    /// the limiter is absent would put the declaration's meaning in a registration nobody can see
    /// from the flow; admitting when the limiter is unreachable would turn an outage of the
    /// limiter into an unbounded flood of whatever it was bounding. Both are the
    /// half-executing policy
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md">ADR-0025</a>
    /// rejects, arriving through the configuration rather than through the algorithm.
    /// </para>
    /// <para>
    /// Distinct from <see cref="RateLimited"/> even though both stop the same call, because the
    /// repairs are opposite: one is "register an <c>IRateLimiterStore</c>" or "fix Redis", the
    /// other is "send fewer requests". An operator reading a refusal must not have to guess which.
    /// </para>
    /// </remarks>
    public static Error RateLimiterUnavailable(string capabilityId, Error? cause = null) =>
        new Error(
            RateLimiterUnavailableCode,
            cause is null
                ? $"Capability '{capabilityId}' declares a RateLimit and no IRateLimiterStore is " +
                  "registered, so no budget could be consulted. The call was refused rather than " +
                  "admitted: a limiter that is not wired up must not read as a limit that passed."
                : $"Capability '{capabilityId}' declares a RateLimit and its store did not answer: " +
                  $"{cause.Message} The call was refused rather than admitted — a limiter that " +
                  "cannot reach its server does not know whether this caller is inside the budget.",
            ErrorCategory.Unavailable)
            .With("capabilityId", capabilityId);

    /// <summary>The code <see cref="QuotaExhausted"/> raises.</summary>
    /// <remarks>
    /// A sibling of <see cref="RateLimitedCode"/> and deliberately not the same code, because
    /// the two lead to opposite responses. A rate limit says "slow down" and a caller obeys it
    /// by waiting; a quota says "the plan you bought is spent for this period", and a caller
    /// that treated it as backpressure would spend the remainder of a month retrying. The
    /// <c>retryAfter</c> both carry is what the two have in common, and it means different
    /// things: milliseconds there, whatever is left of the period here.
    /// </remarks>
    public const string QuotaExhaustedCode = "policy.quota_exhausted";

    /// <summary>
    /// A step's <c>Quota</c> has no budget left for this holder in the current period.
    /// </summary>
    /// <param name="capabilityId">The capability whose budget is spent.</param>
    /// <param name="retryAfter">What is left of the period, as the store reported it.</param>
    /// <remarks>
    /// <para>
    /// <see cref="ErrorCategory.Forbidden"/> rather than <see cref="ErrorCategory.Unavailable"/>,
    /// which is where this parts company with <see cref="RateLimited"/>. Nothing is down and
    /// nothing is temporarily busy: the caller has spent what it is entitled to, and no amount
    /// of waiting inside this period changes the answer. The category is also what keeps the
    /// error out of <c>Retry</c>'s default retryable set — a forward retry of an exhausted plan
    /// limit would spend the flow's deadline re-asking a question whose answer is fixed until
    /// the window turns over — and what makes a transport render it as a <c>403</c> rather than
    /// a <c>429</c>, which is the honest status for "you may not", as opposed to "not now".
    /// </para>
    /// <para>
    /// <c>retryAfter</c> is carried anyway, because the one thing a refused caller can act on is
    /// when the budget comes back, and that is a fact only the store's clock knows.
    /// </para>
    /// </remarks>
    public static Error QuotaExhausted(string capabilityId, TimeSpan retryAfter) =>
        new Error(
            QuotaExhaustedCode,
            $"The quota for capability '{capabilityId}' is spent for this period. It is granted " +
            $"again in {retryAfter}. The call was refused without being made.",
            ErrorCategory.Forbidden)
            .With("capabilityId", capabilityId)
            .With("retryAfter", retryAfter);

    /// <summary>The code <see cref="QuotaStoreUnavailable"/> raises.</summary>
    public const string QuotaUnavailableCode = "policy.quota_unavailable";

    /// <summary>
    /// A step declares a <c>Quota</c> and the budget could not be consulted — no store was
    /// registered, or the one that was did not answer.
    /// </summary>
    /// <param name="capabilityId">The capability whose budget could not be consulted.</param>
    /// <param name="cause">What the store reported, when there was a store.</param>
    /// <remarks>
    /// A refusal, for <see cref="RateLimiterUnavailable"/>'s reason word for word: admitting
    /// when the store is absent puts the declaration's meaning in a registration nobody can see
    /// from the flow, and admitting when it is unreachable turns an outage of the counter into
    /// an unmetered month.
    /// </remarks>
    public static Error QuotaStoreUnavailable(string capabilityId, Error? cause = null) =>
        new Error(
            QuotaUnavailableCode,
            cause is null
                ? $"Capability '{capabilityId}' declares a Quota and no IQuotaStore is " +
                  "registered, so no budget could be consulted. The call was refused rather " +
                  "than admitted: a budget that is not wired up must not read as a budget that " +
                  "had room."
                : $"Capability '{capabilityId}' declares a Quota and its store did not answer: " +
                  $"{cause.Message} The call was refused rather than admitted — a counter that " +
                  "cannot reach its server does not know what this holder has already spent.",
            ErrorCategory.Unavailable)
            .With("capabilityId", capabilityId);

    /// <summary>The code <see cref="ValidationFailed"/> raises.</summary>
    /// <remarks>
    /// <c>docs/10-Policy-Framework.md</c> §3's "field errors → RFC 7807". The code is spelled
    /// with the policy's prefix like every other policy failure, and the field errors ride in
    /// the error's structured detail under <see cref="FieldErrorsDetail"/> — which
    /// <c>ProblemDetailsMapper</c> already copies into the problem document's extensions, so
    /// there is one mapping from an <see cref="Error"/> to a 7807 body and this extends it
    /// rather than adding a second.
    /// </remarks>
    public const string ValidationFailedCode = "policy.validation_failed";

    /// <summary>
    /// The detail key the field errors travel under, and the member RFC 7807 renders them as.
    /// </summary>
    /// <remarks>
    /// <c>errors</c>, which is the name a validation problem document carries by convention —
    /// an object of field name to messages. Naming it anything else would mean every client
    /// library that already understands a validation problem would have to learn a FlowX
    /// spelling of it.
    /// </remarks>
    public const string FieldErrorsDetail = "errors";

    /// <summary>
    /// A step's <c>Validate</c> found the input broke rules its contract declares.
    /// </summary>
    /// <param name="capabilityId">The capability the input was destined for.</param>
    /// <param name="failures">Which members broke which rules. Never empty.</param>
    /// <remarks>
    /// <para>
    /// <see cref="ErrorCategory.Validation"/>, which is what makes this a <c>400</c> and keeps
    /// it out of every retry set: <c>docs/10 §11</c>'s first anti-pattern is retrying a
    /// validation error, because "the input will never become valid".
    /// </para>
    /// <para>
    /// <strong>The message counts the failures and does not quote them.</strong> The detail is
    /// the structured list, which is the half a caller can act on; a message that concatenated
    /// the field messages would be a second, lossier rendering of the same facts, and the
    /// summary is what belongs in a log line.
    /// </para>
    /// </remarks>
    public static Error ValidationFailed(string capabilityId, IReadOnlyList<FieldError> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);

        return new Error(
            ValidationFailedCode,
            $"The input to '{capabilityId}' broke {failures.Count} rule(s) its contract " +
            "declares. The step was refused without being dispatched — docs/10 §2: validation " +
            "after the side effect is corrupt data written and then rejected.",
            ErrorCategory.Validation)
            .With("capabilityId", capabilityId)
            .With(FieldErrorsDetail, failures);
    }

    /// <summary>The code <see cref="ValidationUnavailable"/> raises.</summary>
    public const string ValidationUnavailableCode = "policy.validation_unavailable";

    /// <summary>
    /// A step declares a <c>Validate</c> and its dispatcher has no generated checks to run.
    /// </summary>
    /// <param name="capabilityId">The capability whose input went unchecked.</param>
    /// <remarks>
    /// <para>
    /// <strong>A refusal, and the reason is the one every stage-1 and stage-3 seam gives.</strong>
    /// The checks are generated from the contract's annotations, so a dispatcher that answers
    /// <see cref="ValidationOutcome.Unavailable"/> is one that was written by hand or compiled
    /// from a source the generator did not see. Admitting there would make a declared
    /// <c>Validate</c> mean nothing on exactly the builds where nobody would notice.
    /// </para>
    /// <para>
    /// A compiled flow cannot reach it: <c>FLOWX1056</c> refuses a <c>Validate</c> on a contract
    /// with no rule to check at build time, and the generator emits a case for every step that
    /// passes.
    /// </para>
    /// </remarks>
    public static Error ValidationUnavailable(string capabilityId) =>
        new Error(
            ValidationUnavailableCode,
            $"Capability '{capabilityId}' declares a Validate and its dispatcher generated no " +
            "checks for it, so the input was never examined. The step was refused rather than " +
            "dispatched: a validation nothing ran must not read as a validation that passed.",
            ErrorCategory.Internal)
            .With("capabilityId", capabilityId);

    /// <summary>The code <see cref="IdempotencyStoreUnavailable"/> raises.</summary>
    public const string IdempotencyUnavailableCode = "policy.idempotency_unavailable";

    /// <summary>
    /// A step declares an <c>Idempotency</c> window and the store could not answer — none was
    /// registered, or the one that was did not respond.
    /// </summary>
    /// <param name="capabilityId">The capability whose window could not be consulted.</param>
    /// <param name="cause">What the store reported, when there was a store.</param>
    /// <remarks>
    /// Dispatching on doubt is the duplicate the policy was declared to prevent, so this is a
    /// refusal for <see cref="RateLimiterUnavailable"/>'s reason.
    /// </remarks>
    public static Error IdempotencyStoreUnavailable(string capabilityId, Error? cause = null) =>
        new Error(
            IdempotencyUnavailableCode,
            cause is null
                ? $"Capability '{capabilityId}' declares an Idempotency window and no " +
                  "IIdempotencyStore is registered, so no record could be read or written. The " +
                  "step was refused rather than dispatched: dispatching on doubt is the duplicate " +
                  "the window was declared to prevent."
                : $"Capability '{capabilityId}' declares an Idempotency window and its store did " +
                  $"not answer: {cause.Message} The step was refused rather than dispatched.",
            ErrorCategory.Unavailable)
            .With("capabilityId", capabilityId);

    /// <summary>The code <see cref="IdempotencyInProgress"/> raises.</summary>
    /// <remarks>
    /// <c>docs/10-Policy-Framework.md</c> §7's third arm, which that section renders as
    /// <c>409 idempotency.in_progress + Retry-After</c>. The code is spelled with the policy's
    /// prefix like every other policy failure so that one <c>policy.*</c> family covers the lot.
    /// </remarks>
    public const string IdempotencyInProgressCode = "policy.idempotency_in_progress";

    /// <summary>
    /// Another caller is running this step under the same idempotency key right now.
    /// </summary>
    /// <param name="capabilityId">The capability somebody else is inside.</param>
    /// <param name="retryAfter">How long the holder's claim has left.</param>
    /// <remarks>
    /// <see cref="ErrorCategory.Conflict"/> rather than <see cref="ErrorCategory.Unavailable"/>,
    /// and the distinction is the useful one: nothing is down and nothing is over budget — two
    /// callers are contending for one outcome, and the right answer is to come back and read it
    /// rather than to run it again. It is also in the compensation retry's default retryable set
    /// and not in the forward retry's, which is right in both directions: an undo racing another
    /// writer should insist, and a forward step should not spend its deadline re-asking a
    /// question whose answer is "somebody else is doing it".
    /// </remarks>
    public static Error IdempotencyInProgress(string capabilityId, TimeSpan retryAfter) =>
        new Error(
            IdempotencyInProgressCode,
            $"Another caller is executing '{capabilityId}' under this idempotency key. Retry " +
            $"after {retryAfter}. Running it concurrently is exactly what the window forbids.",
            ErrorCategory.Conflict)
            .With("capabilityId", capabilityId)
            .With("retryAfter", retryAfter);

    /// <summary>The code <see cref="IdempotencyNotReplayable"/> raises.</summary>
    public const string IdempotencyNotReplayableCode = "policy.idempotency_not_replayable";

    /// <summary>
    /// The step succeeded and what it produced cannot be recorded as what it produced.
    /// </summary>
    /// <param name="capabilityId">The capability whose result could not be recorded.</param>
    /// <param name="describedNothing">
    /// Whether the dispatcher described no state bag at all, as opposed to describing one the
    /// redaction pass had to change.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>The guard
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0042-a-recorded-result-is-replayed-only-when-recording-lost-nothing.md">ADR-0042</a>
    /// exists for, and it fails a step that worked.</strong> That is the correct direction and
    /// it is genuinely a cost: the capability has been dispatched, the effect happened, and the
    /// step is then reported as failed with the completed compensable steps unwinding behind it.
    /// The alternative is a record that says a marked member's value is the literal
    /// <c>[redacted]</c>, replayed to a later caller as if somebody had computed it.
    /// </para>
    /// <para>
    /// <see cref="ErrorCategory.Internal"/>, because it is a defect in the flow's declaration
    /// rather than anything the caller did or the dependency failed to do — and because
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1040.md">FLOWX1040</a>
    /// should have caught it at build time. Reaching this at run time means the rule was silent:
    /// a set the compiler could not read, or a hand-built plan.
    /// </para>
    /// </remarks>
    public static Error IdempotencyNotReplayable(string capabilityId, bool describedNothing) =>
        new Error(
            IdempotencyNotReplayableCode,
            describedNothing
                ? $"Capability '{capabilityId}' declares an Idempotency window and its dispatcher " +
                  "describes no state bag, so there is nothing a later caller could be answered " +
                  "with. Recording an empty result would make the declaration look satisfied " +
                  "while every step after the frontier bound values no step produced."
                : $"Capability '{capabilityId}' declares an Idempotency window and its flow " +
                  "declares a [Sensitive] member, so the recorded result would carry " +
                  $"'{JournalPayload.Redacted}' where a value was. Replaying that would hand a " +
                  "later step a placeholder as if it were the value — see FLOWX1040, which " +
                  "reports this at build time whenever the compiler can read the policy set.",
            ErrorCategory.Internal)
            .With("capabilityId", capabilityId);

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

    /// <summary>
    /// A resumed instance found its step already answered for by the fallback capability, so
    /// the primary is not asked again.
    /// </summary>
    /// <param name="capabilityId">The step's own capability, which finished failing elsewhere.</param>
    /// <param name="fallbackId">The capability that owns the step now.</param>
    /// <remarks>
    /// <para>
    /// <strong>A failure that exists to be replaced, and usually is.</strong> The step loop
    /// needs a non-null error to carry into <c>DegradeAsync</c> — that is what "the step has
    /// finished failing" is spelled as — and a resumed instance no longer has the one the dead
    /// node saw, because a journal row records an outcome and never an <see cref="Error"/>.
    /// This says exactly what the committed history supports and nothing more. When the
    /// fallback then answers, it is discarded; it reaches a caller only when the fallback fails
    /// too, which is the case where naming both capabilities is precisely what an operator
    /// needs.
    /// </para>
    /// <para>
    /// <see cref="ErrorCategory.Unavailable"/> rather than <see cref="ErrorCategory.Internal"/>:
    /// what is known is that a dependency stopped answering, which is the category the primary's
    /// own exhaustion would almost always have carried. Guessing <c>Internal</c> would turn a
    /// dependency's outage into this platform's defect in every trace of a resumed degradation.
    /// </para>
    /// </remarks>
    public static Error StepAlreadyDegrading(string capabilityId, string fallbackId) =>
        new Error(
            "flow.step_already_degrading",
            $"Capability '{capabilityId}' had already failed for the last time when this " +
            $"instance was resumed, and its declared fallback '{fallbackId}' has a committed " +
            "row. The step is answered by the fallback rather than by asking the primary " +
            "again, so the attempts the author declared are not spent a second time.",
            ErrorCategory.Unavailable)
            .With("capabilityId", capabilityId)
            .With("fallbackCapabilityId", fallbackId);

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

    /// <summary>The code <see cref="PollNotSatisfied"/> raises.</summary>
    /// <remarks>
    /// A constant for <see cref="SignalNotReceivedCode"/>'s reason, and it is the same reason
    /// twice: this is a business outcome the author anticipated — the document was still not
    /// ready — rather than something going wrong.
    /// </remarks>
    public const string PollNotSatisfiedCode = "flow.poll_not_satisfied";

    /// <summary>
    /// A poll's declared timeout ran out with its predicate still false, and the flow declared
    /// no <c>.OnTimeout(...)</c> block to take instead.
    /// </summary>
    /// <param name="flowId">The polling flow.</param>
    /// <param name="stepIndex">Index of the poll, so the failure names one call site.</param>
    /// <param name="attempts">How many attempts were made before the budget ran out.</param>
    /// <param name="timeout">How long the author gave it.</param>
    /// <remarks>
    /// <para>
    /// <strong>A failure, for the reason <see cref="SignalNotReceived"/> is one.</strong>
    /// Carrying on at the step after the poll would run it against whatever the last
    /// unsuccessful attempt happened to leave in the bag — a document's fields extracted from
    /// an OCR job that never finished — which is worse than stopping, and is the defect the
    /// predicate exists to prevent.
    /// </para>
    /// <para>
    /// The attempt count is carried because it is the one number an operator needs and cannot
    /// derive from the declaration: it says whether the poll was starved of time or the thing
    /// it was polling never changed.
    /// </para>
    /// </remarks>
    public static Error PollNotSatisfied(string flowId, int stepIndex, int attempts, TimeSpan timeout) =>
        new Error(
            PollNotSatisfiedCode,
            $"Step {stepIndex} of flow '{flowId}' polled {attempts} time(s) over {timeout} " +
            "and its condition never held. The flow declares no OnTimeout block, so there is " +
            "nowhere for the poll to go: continuing would run the steps after it against the " +
            "last attempt's unfinished result.",
            ErrorCategory.Unavailable)
            .With("flowId", flowId)
            .With("stepIndex", stepIndex)
            .With("attempts", attempts)
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

    /// <summary>The code <see cref="AuditSinkNotConfigured"/> raises.</summary>
    public const string AuditSinkNotConfiguredCode = "policy.audit_sink_not_configured";

    /// <summary>The code <see cref="AuditNotRecorded"/> raises.</summary>
    public const string AuditNotRecordedCode = "policy.audit_not_recorded";

    /// <summary>
    /// A step declared an <c>Audit</c> and the engine was built with nowhere to write it.
    /// </summary>
    /// <param name="flowId">The flow whose step is audited.</param>
    /// <param name="capabilityId">The capability that ran and cannot be recorded.</param>
    /// <param name="category">The category the author declared.</param>
    /// <remarks>
    /// <para>
    /// <strong>Refused rather than skipped, and it is the one seam on this path that
    /// refuses.</strong> An unconfigured cache dispatches, an unconfigured alert sink reports
    /// nowhere, and both are the honest older behaviour. There is no honest older behaviour
    /// here: a step that happened with no record that it happened is the state
    /// the deleted <c>FLOWX1032</c>'s third remedy said not to ship — "a regulated
    /// write whose audit record is the reason it is allowed to happen".
    /// </para>
    /// <para>
    /// The step has already succeeded and already committed when this is raised, so the flow
    /// fails on the failure path and the compensable work behind it — including this step —
    /// unwinds. That is the correct end: the effect is reversed rather than left standing with
    /// nothing describing it.
    /// </para>
    /// <para>
    /// <see cref="ErrorCategory.Internal"/> because it is a wiring defect in the host rather
    /// than a business outcome, and retrying reaches the same missing sink.
    /// </para>
    /// </remarks>
    public static Error AuditSinkNotConfigured(string flowId, string capabilityId, string category) =>
        new Error(
            AuditSinkNotConfiguredCode,
            $"Step '{capabilityId}' of flow '{flowId}' declares an Audit in category " +
            $"'{category}', so it must produce an immutable audit record — but the engine was " +
            "built with no IAuditSink. Supply one, or remove the Audit from the policy set if " +
            "this step does not need to be recorded.",
            ErrorCategory.Internal)
            .With("flowId", flowId)
            .With("capabilityId", capabilityId)
            .With("category", category);

    /// <summary>An audit sink refused, or a payload could not be described.</summary>
    /// <param name="capabilityId">The capability that ran and cannot be recorded.</param>
    /// <param name="category">The category the author declared.</param>
    /// <param name="cause">What went wrong.</param>
    /// <remarks>
    /// <see cref="ErrorCategory.Unavailable"/> rather than <see cref="ErrorCategory.Internal"/>:
    /// a store that was busy is worth asking again, and this is the one audit failure a caller
    /// can act on. The message names the category so that an operator reading a failed transfer
    /// knows which regime's record is missing rather than only that one is.
    /// </remarks>
    public static Error AuditNotRecorded(string capabilityId, string category, Exception cause)
    {
        ArgumentNullException.ThrowIfNull(cause);

        return new Error(
            AuditNotRecordedCode,
            $"The '{category}' audit record for '{capabilityId}' could not be written, so the " +
            $"step is reversed rather than left unrecorded: {cause.Message}",
            ErrorCategory.Unavailable)
            .With("capabilityId", capabilityId)
            .With("category", category)
            .With("exception", cause.GetType().FullName ?? cause.GetType().Name);
    }

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
