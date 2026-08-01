using System.Collections.Immutable;

namespace FlowX;

/// <summary>
/// A step's <see cref="PolicyStage.Resilience"/> policies, resolved out of its declared
/// <see cref="PolicyChain"/> into the numbers the step loop actually needs.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Stage 4, and only stage 4.</strong> <c>Timeout</c>, <c>Retry</c>,
/// <c>CircuitBreaker</c> and <c>Bulkhead</c> are the kinds this reads; <c>RateLimit</c>
/// (stage 1), <c>Idempotency</c> (stage 3), <c>Cache</c> (stage 5) and <c>Audit</c> (stage 7)
/// are read past, exactly as <see cref="CompensationPolicy.From"/> reads past everything that
/// is not a compensation retry. Which stages a partial engine may skip, and why skipping
/// these four is safe, is
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md">ADR-0025</a>.
/// </para>
/// <para>
/// <strong>Resolved once, when the plan is built.</strong> It hangs off
/// <see cref="StepNode.StepPolicy"/>, so the step loop reads four fields off a node it already
/// has rather than walking an <see cref="ImmutableArray{T}"/> of descriptors per step — the
/// same reason <see cref="StepNode.CompensationRetry"/> and
/// <see cref="ExecutionPlan.CompensableStepIndices"/> are precomputed, and the reason the
/// engine's hot path can gate the whole of this behind one comparison. See
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0023-policy-stages-hook-through-the-plan.md">ADR-0023</a>.
/// </para>
/// <para>
/// <strong>The order the four run in is not this type's.</strong> A chain is ordered by
/// stage, and all four of these share one stage — so their relative nesting is a fixed
/// decision rather than a consequence of declaration order, settled by
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0024-stage-four-is-a-fixed-nesting.md">ADR-0024</a>
/// and implemented in <c>FlowEngine</c>. This type carries the parameters; it does not decide
/// what wraps what.
/// </para>
/// </remarks>
public sealed class StepPolicy
{
    private StepPolicy(
        TimeSpan? timeout,
        int attempts,
        Backoff backoff,
        ImmutableArray<ErrorCategory> retryOn,
        double failureRatio,
        TimeSpan samplingWindow,
        TimeSpan breakDuration,
        int maxConcurrency,
        int queueDepth)
    {
        Timeout = timeout;
        Attempts = attempts;
        Backoff = backoff;
        RetryOn = retryOn;
        FailureRatio = failureRatio;
        SamplingWindow = samplingWindow;
        BreakDuration = breakDuration;
        MaxConcurrency = maxConcurrency;
        QueueDepth = queueDepth;
    }

    /// <summary>
    /// Nothing armed: what a step with no stage-4 policy gets, and what every step got before
    /// the policy engine.
    /// </summary>
    public static StepPolicy None { get; } = new(
        null, 1, Backoff.ExponentialJitter(), ImmutableArray<ErrorCategory>.Empty,
        0d, TimeSpan.Zero, TimeSpan.Zero, 0, 0);

    /// <summary>
    /// How many calls a breaker's sampling window must hold before its ratio is evidence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>docs/10-Policy-Framework.md §3</c> lists <c>minimumThroughput</c> among
    /// <c>CircuitBreaker</c>'s key parameters and <see cref="PolicySet.CircuitBreaker"/> has no
    /// parameter for it, so it is a constant rather than a declaration. That is the honest
    /// shape of the gap: inventing a builder overload to carry it would change the DSL in a
    /// package about the runtime, and defaulting it to one would mean the first failure after
    /// a deployment opens the breaker for everybody — a ratio computed over a single call is
    /// not a ratio.
    /// </para>
    /// <para>
    /// Ten rather than a larger number because a FlowX step is a business call rather than an
    /// HTTP request in a hot loop: a breaker that needs a hundred failures before it acts
    /// would protect nothing on a dependency invoked a few times a minute.
    /// </para>
    /// </remarks>
    public const int DefaultMinimumThroughput = 10;

    /// <summary>How long one attempt may take, or <c>null</c> when none was declared.</summary>
    /// <remarks>
    /// Nullable rather than <see cref="TimeSpan.Zero"/> for "none", because zero is a value an
    /// author can write and it means something sharply different: a step that has already run
    /// out of budget before it starts. Collapsing the two would make a declared
    /// <c>Timeout(TimeSpan.Zero)</c> silently mean "no timeout", which is the opposite of what
    /// it says.
    /// </remarks>
    public TimeSpan? Timeout { get; }

    /// <summary>How many times the step may be dispatched, including the first. Never below one.</summary>
    public int Attempts { get; }

    /// <summary>The wait between attempts.</summary>
    public Backoff Backoff { get; }

    /// <summary>Which error categories are worth another attempt.</summary>
    public ImmutableArray<ErrorCategory> RetryOn { get; }

    /// <summary>The failure fraction at which the breaker opens, or zero when none was declared.</summary>
    public double FailureRatio { get; }

    /// <summary>How far back the breaker counts.</summary>
    public TimeSpan SamplingWindow { get; }

    /// <summary>How long the breaker stays open before it lets one call through.</summary>
    public TimeSpan BreakDuration { get; }

    /// <summary>How many callers may be inside the step at once, or zero when none was declared.</summary>
    public int MaxConcurrency { get; }

    /// <summary>How many callers may wait for a permit before one is refused outright.</summary>
    public int QueueDepth { get; }

    /// <summary>True when this policy can ask for the step a second time.</summary>
    public bool IsRetrying => Attempts > 1;

    /// <summary>True when a breaker was declared.</summary>
    public bool HasBreaker => FailureRatio > 0d;

    /// <summary>True when a bulkhead was declared.</summary>
    public bool HasBulkhead => MaxConcurrency > 0;

    /// <summary>
    /// True when this step has anything for the engine to apply.
    /// </summary>
    /// <remarks>
    /// The single question the step loop asks. A step whose chain declares only a
    /// <c>Cache</c> and an <c>Audit</c> answers <c>false</c> and takes the path it always
    /// took — which is what stops a declaration that is still inert from costing the flow
    /// anything, and what makes <see cref="ExecutionPlan.HasStepPolicies"/> mean "some step
    /// will actually be wrapped" rather than "some step declared something".
    /// </remarks>
    public bool IsActive => Timeout is not null || IsRetrying || HasBreaker || HasBulkhead;

    /// <summary>
    /// Reads the stage-4 kinds out of a chain, or <see cref="None"/> when it declares none.
    /// </summary>
    /// <param name="policies">The step's own chain, already ordered by stage.</param>
    /// <remarks>
    /// Tolerant of a chain that carries other kinds, for
    /// <see cref="CompensationPolicy.From"/>'s reason: a set may legitimately declare a rate
    /// limit and an audit alongside a timeout, and the stages that do not execute yet are
    /// metadata this reads past rather than rejects.
    /// </remarks>
    public static StepPolicy From(PolicyChain policies)
    {
        ArgumentNullException.ThrowIfNull(policies);

        TimeSpan? timeout = null;
        var attempts = 1;
        var backoff = Backoff.ExponentialJitter();
        var retryOn = ImmutableArray<ErrorCategory>.Empty;
        var failureRatio = 0d;
        var samplingWindow = TimeSpan.Zero;
        var breakDuration = TimeSpan.Zero;
        var maxConcurrency = 0;
        var queueDepth = 0;

        foreach (var policy in policies.Ordered)
        {
            switch (policy.Kind)
            {
                case TimeoutKind:
                    timeout = Parameter(policy, "duration", TimeSpan.Zero);
                    break;

                case RetryKind:
                    attempts = Math.Max(1, Parameter(policy, "attempts", 1));
                    backoff = Parameter(policy, "backoff", Backoff.ExponentialJitter());
                    retryOn = [.. Parameter(policy, "retryOn", Array.Empty<ErrorCategory>())];
                    break;

                case CircuitBreakerKind:
                    failureRatio = Parameter(policy, "failureRatio", 0d);
                    breakDuration = Parameter(policy, "breakDuration", TimeSpan.Zero);
                    samplingWindow = Parameter(policy, "samplingWindow", TimeSpan.FromSeconds(30));
                    break;

                case BulkheadKind:
                    maxConcurrency = Math.Max(0, Parameter(policy, "maxConcurrency", 0));
                    queueDepth = Math.Max(0, Parameter(policy, "queueDepth", 0));
                    break;

                default:
                    // Stage 1, 3, 5 and 7. Read past rather than rejected — the chain is the
                    // author's whole declaration and this type is one stage's view of it.
                    break;
            }
        }

        var resolved = new StepPolicy(
            timeout, attempts, backoff, retryOn,
            failureRatio, samplingWindow, breakDuration, maxConcurrency, queueDepth);

        return resolved.IsActive ? resolved : None;
    }

    /// <summary>The descriptor kind <see cref="PolicySet.Timeout"/> emits.</summary>
    public const string TimeoutKind = "Timeout";

    /// <summary>The descriptor kind <see cref="PolicySet.Retry"/> emits.</summary>
    public const string RetryKind = "Retry";

    /// <summary>The descriptor kind <see cref="PolicySet.CircuitBreaker"/> emits.</summary>
    public const string CircuitBreakerKind = "CircuitBreaker";

    /// <summary>The descriptor kind <see cref="PolicySet.Bulkhead"/> emits.</summary>
    public const string BulkheadKind = "Bulkhead";

    /// <summary>
    /// Whether the step is worth dispatching again after <paramref name="attemptsMade"/>
    /// attempts have failed with <paramref name="failure"/>.
    /// </summary>
    /// <param name="failure">What the last attempt reported.</param>
    /// <param name="attemptsMade">How many attempts have already been made, counting from one.</param>
    /// <remarks>
    /// Both halves of <c>docs/10-Policy-Framework.md §5</c>'s decision tree, and no more — the
    /// same two questions <see cref="CompensationPolicy.AllowsAnotherAttempt"/> asks, because
    /// they are the same two questions. The deadline is the caller's to check, because it is
    /// the caller that knows what the planned backoff would cost.
    /// </remarks>
    public bool AllowsAnotherAttempt(Error failure, int attemptsMade)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return attemptsMade < Attempts && RetryOn.Contains(failure.Category);
    }

    /// <summary>
    /// How long to wait before attempt <paramref name="attempt"/>, given a uniform
    /// <paramref name="sample"/> in <c>[0, 1]</c>.
    /// </summary>
    /// <param name="attempt">The attempt about to be armed, counting from one.</param>
    /// <param name="sample">The jitter draw. Ignored when the backoff declares no jitter.</param>
    /// <remarks>
    /// Full jitter, exactly as <c>docs/10-Policy-Framework.md §5</c> specifies:
    /// <c>delay = random(0, base × 2^attempt)</c>, capped at <see cref="FlowX.Backoff.MaxDelay"/>.
    /// The arithmetic is <see cref="CompensationPolicy.DelayBefore"/>'s, and deliberately the
    /// same one: a forward retry and a compensation retry that decorrelated differently would
    /// be two answers to one question.
    /// </remarks>
    public TimeSpan DelayBefore(int attempt, double sample)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);

        var ceiling = Math.Min(
            Backoff.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1),
            Backoff.MaxDelay.TotalMilliseconds);

        var milliseconds = Backoff.Jitter ? ceiling * Math.Clamp(sample, 0, 1) : ceiling;

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    /// <summary>
    /// The timeout to arm for one attempt: the declared duration, never past what is left of
    /// the flow's budget.
    /// </summary>
    /// <param name="now">The current instant.</param>
    /// <param name="deadline">The flow's absolute deadline.</param>
    /// <returns>
    /// The effective budget for this attempt, or <c>null</c> when no timeout was declared.
    /// Zero or negative means there is none left and the attempt must not start.
    /// </returns>
    /// <remarks>
    /// <c>docs/10-Policy-Framework.md §5</c>: "a retry never outlives the deadline… the policy
    /// engine subtracts elapsed time before arming the next attempt", and §11's anti-pattern
    /// "timeout longer than the flow deadline — the step is killed by the deadline anyway; the
    /// timeout is a lie". Taking the minimum here is what makes the second sentence false
    /// rather than merely discouraged.
    /// </remarks>
    public TimeSpan? EffectiveTimeout(DateTimeOffset now, DateTimeOffset deadline)
    {
        if (Timeout is not { } declared)
        {
            return null;
        }

        var remaining = deadline - now;

        return declared < remaining ? declared : remaining;
    }

    private static T Parameter<T>(PolicyDescriptor policy, string key, T fallback) =>
        policy.Parameters.TryGetValue(key, out var value) && value is T typed ? typed : fallback;
}
