using System.Collections.Immutable;

namespace FlowX;

/// <summary>
/// A step's executed policies, resolved out of its declared <see cref="PolicyChain"/> into the
/// numbers the step loop actually needs.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nine kinds across four stages.</strong> <c>RateLimit</c> (stage 1),
/// <c>Idempotency</c> (stage 3), <c>Timeout</c>, <c>Retry</c>, <c>CircuitBreaker</c>,
/// <c>Bulkhead</c>, <c>Hedge</c> and <c>Fallback</c> (stage 4), and <c>Cache</c> (stage 5) are
/// the kinds this reads. <c>Audit</c> (stage 7) is read past, exactly as
/// <see cref="CompensationPolicy.From"/> reads past everything that is not a compensation retry — it runs after the step's commit and is
/// resolved onto <see cref="StepAudit"/> instead. Which stages a partial engine may skip is
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md">ADR-0025</a>.
/// </para>
/// <para>
/// <strong>Four stages on one object, and deliberately no second object.</strong> Stages 1, 3,
/// 4 and 5 all arrive on one <see cref="PolicyChain"/>, so a second resolved field
/// on the node would be a second walk of the array this one already walks, and
/// <see cref="IsActive"/> would then have to consult two objects to answer one question. It is
/// also why no plan flag was added beside <see cref="ExecutionPlan.HasStepPolicies"/> — see
/// <see cref="IsActive"/>. Stage 7 is the exception, and earns it by running outside the
/// wrapping the other four share.
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
/// <strong>The order the six run in is not this type's.</strong> A chain is ordered by
/// stage, and all six of these share one stage — so their relative nesting is a fixed
/// decision rather than a consequence of declaration order, settled for four kinds by
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0024-stage-four-is-a-fixed-nesting.md">ADR-0024</a>
/// and extended to six by
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0078-stage-four-nests-six-kinds.md">ADR-0078</a>,
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
        int queueDepth,
        int permits,
        TimeSpan rateWindow,
        RateLimitScope rateScope,
        TimeSpan? idempotencyWindow,
        IdempotencyScope idempotencyScope,
        TimeSpan? cacheTtl,
        CacheScope cacheScope,
        TimeSpan hedgeAfter,
        int hedgeAttempts,
        FallbackValue? fallback,
        CapabilityDescriptor? fallbackCapability)
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
        Permits = permits;
        RateWindow = rateWindow;
        RateScope = rateScope;
        IdempotencyWindow = idempotencyWindow;
        IdempotencyScope = idempotencyScope;
        CacheTtl = cacheTtl;
        CacheScope = cacheScope;
        HedgeAfter = hedgeAfter;
        HedgeAttempts = hedgeAttempts;
        Fallback = fallback;
        FallbackCapability = fallbackCapability;
    }

    /// <summary>
    /// Nothing armed: what a step with no executed policy gets, and what every step got before
    /// the policy engine.
    /// </summary>
    public static StepPolicy None { get; } = new(
        null, 1, Backoff.ExponentialJitter(), ImmutableArray<ErrorCategory>.Empty,
        0d, TimeSpan.Zero, TimeSpan.Zero, 0, 0,
        0, TimeSpan.Zero, RateLimitScope.Tenant, null, IdempotencyScope.Tenant,
        null, CacheScope.Tenant, TimeSpan.Zero, 1, null, null);

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

    /// <summary>How many invocations of this step are admitted per <see cref="RateWindow"/>, or zero when none was declared.</summary>
    public int Permits { get; }

    /// <summary>The period <see cref="Permits"/> are granted over.</summary>
    public TimeSpan RateWindow { get; }

    /// <summary>What the rate limit's budget is shared by.</summary>
    public RateLimitScope RateScope { get; }

    /// <summary>How long a recorded result is replayed for, or <c>null</c> when none was declared.</summary>
    /// <remarks>
    /// Nullable rather than <see cref="TimeSpan.Zero"/>, for <see cref="Timeout"/>'s reason: a
    /// declared window of zero is a window that has already closed, which is sharply different
    /// from no window at all, and collapsing the two would make an author's
    /// <c>.Idempotency(TimeSpan.Zero)</c> silently mean "no idempotency".
    /// </remarks>
    public TimeSpan? IdempotencyWindow { get; }

    /// <summary>What a recorded result's key is namespaced by.</summary>
    public IdempotencyScope IdempotencyScope { get; }

    /// <summary>
    /// How long a cached result stays readable, or <c>null</c> when no cache was declared.
    /// </summary>
    /// <remarks>
    /// Nullable rather than <see cref="TimeSpan.Zero"/> for "none", for <see cref="Timeout"/>'s
    /// reason: zero is a value an author can write and it means "hold this for no time", which
    /// <see cref="HasCache"/> deliberately reads as no cache at all rather than as a cache that
    /// is written on every call and never read.
    /// </remarks>
    public TimeSpan? CacheTtl { get; }

    /// <summary>What a cache entry is keyed within. <c>docs/10 §8</c>'s conservative default.</summary>
    public CacheScope CacheScope { get; }

    /// <summary>
    /// How long a call may stay outstanding before the next one is issued beside it, or
    /// <see cref="TimeSpan.Zero"/> when no hedge was declared.
    /// </summary>
    /// <remarks>
    /// Zero rather than nullable, unlike <see cref="Timeout"/>, because zero is not a value an
    /// author can usefully write: a hedge with no delay is not a hedge but a fan-out of
    /// <see cref="HedgeAttempts"/> simultaneous calls, which doubles the load on the dependency
    /// and saves nothing on a call that has not had time to be slow yet. <see cref="HasHedge"/>
    /// reads it as undeclared.
    /// </remarks>
    public TimeSpan HedgeAfter { get; }

    /// <summary>How many calls may be in flight for one attempt at the step, including the first.</summary>
    public int HedgeAttempts { get; }

    /// <summary>The degraded value to answer with, or <c>null</c> when none was declared.</summary>
    /// <remarks>
    /// Null does not mean "no fallback": <see cref="FallbackCapability"/> is the other half of
    /// the catalogued row, and <see cref="HasFallback"/> is the question to ask.
    /// </remarks>
    public FallbackValue? Fallback { get; }

    /// <summary>
    /// The capability to ask instead, or <c>null</c> when the fallback is a constant or absent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolved by <c>PolicyChain.ForStep</c> from the declaration
    /// <see cref="PolicySet.Fallback{TCapability}()"/> leaves in the descriptor — a
    /// <see cref="FallbackCapability"/> holding a <see cref="Type"/> and nothing else. The
    /// engine never sees that type: what it needs is an id and a version to write on the
    /// journal row, which is what a descriptor is.
    /// </para>
    /// <para>
    /// <strong>The id on that row is what makes a degraded execution legible.</strong> It is
    /// the fallback's, not the step's, so the four-part key ADR-0015 fixed keeps its meaning
    /// and the column beside it says which capability answered — the same arrangement
    /// <c>docs/06 §7</c> rule 6 already relies on for a compensation row
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0079-a-fallback-capability-is-a-dispatch-of-its-own.md">ADR-0079</a> §2.2).
    /// </para>
    /// </remarks>
    public CapabilityDescriptor? FallbackCapability { get; }

    /// <summary>True when this policy can ask for the step a second time.</summary>
    public bool IsRetrying => Attempts > 1;

    /// <summary>True when a breaker was declared.</summary>
    public bool HasBreaker => FailureRatio > 0d;

    /// <summary>True when a bulkhead was declared.</summary>
    public bool HasBulkhead => MaxConcurrency > 0;

    /// <summary>True when a rate limit was declared.</summary>
    /// <remarks>
    /// A declared <c>permits: 0</c> leaves this false rather than making a limit that admits
    /// nobody. <see cref="PolicySet.RateLimit"/>'s parameter is a budget, and a budget of zero
    /// is a step no caller could ever run — which is a flow that should not have the step, not a
    /// limit. The engine treats it as undeclared, exactly as <c>Attempts</c> is clamped to one.
    /// </remarks>
    public bool HasRateLimit => Permits > 0 && RateWindow > TimeSpan.Zero;

    /// <summary>True when an idempotency window was declared.</summary>
    public bool HasIdempotency => IdempotencyWindow is { Ticks: > 0 };

    /// <summary>True when a cache with a usable lifetime was declared.</summary>
    public bool HasCache => CacheTtl > TimeSpan.Zero;

    /// <summary>True when a hedge that can actually issue a second call was declared.</summary>
    /// <remarks>
    /// Both terms, and each rules out a degenerate declaration rather than a mistake worth a
    /// diagnostic: <c>maxAttempts: 1</c> is a hedge that never hedges, exactly as
    /// <c>attempts: 1</c> is a retry that never retries, and a zero delay is the simultaneous
    /// fan-out <see cref="HedgeAfter"/> declines to be.
    /// </remarks>
    public bool HasHedge => HedgeAttempts > 1 && HedgeAfter > TimeSpan.Zero;

    /// <summary>True when a degraded answer of either kind was declared.</summary>
    /// <remarks>
    /// One question for both halves of <c>docs/10 §3</c>'s row, because the engine asks it in
    /// one place: the step loop consults a fallback after the retry has stopped, and whether
    /// the answer is a constant to file or a capability to dispatch is decided one level in.
    /// </remarks>
    public bool HasFallback => Fallback is not null || FallbackCapability is not null;

    /// <summary>True when answering for the step takes a dispatch rather than a constant.</summary>
    public bool FallbackDispatches => FallbackCapability is not null;

    /// <summary>
    /// True when this step has anything for the engine to apply.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single question the step loop asks. A step whose chain declares only an
    /// <c>Audit</c> answers <c>false</c> and takes the path it always took — stage 7 runs
    /// after the step and its commit, so it has its own resolved value and its own flag
    /// (<see cref="StepAudit"/>, <see cref="ExecutionPlan.HasAuditedSteps"/>) rather than a
    /// term here. That is what keeps <see cref="ExecutionPlan.HasStepPolicies"/> meaning
    /// "some step will actually be wrapped" rather than "some step declared something".
    /// </para>
    /// <para>
    /// <strong>Seven kinds rather than four since stages 1, 3 and 5 landed</strong>, and no new
    /// plan flag went with them — which is
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0023-policy-stages-hook-through-the-plan.md">ADR-0023</a>'s
    /// "widening is mechanical" being taken up literally, and is why that record's "a third flag
    /// of this shape is proposed" trigger did not fire. ADR-0023 predicted it in those words —
    /// *"implementing stage 5 means adding fields to <c>StepPolicy</c>, widening
    /// <c>IsActive</c>, and nothing else: no new flag, no new read site, no change to the step
    /// loop's shape"* — and the read site is indeed the same one, because stage 5 runs inside
    /// stage 4's nesting. A stance needed <c>ExecutionPlan.HasAuthorizedSteps</c> of its own
    /// because it is resolved from a capability attribute and has no chain to be read out of;
    /// a policy always has one.
    /// </para>
    /// </remarks>
    public bool IsActive =>
        Timeout is not null || IsRetrying || HasBreaker || HasBulkhead
        || HasRateLimit || HasIdempotency || HasCache || HasHedge || HasFallback;

    /// <summary>
    /// Reads the in-line kinds out of a chain, or <see cref="None"/> when it declares none.
    /// </summary>
    /// <param name="policies">The step's own chain, already ordered by stage.</param>
    /// <remarks>
    /// Tolerant of a chain that carries other kinds, for
    /// <see cref="CompensationPolicy.From"/>'s reason: a set may legitimately declare an audit
    /// alongside a timeout, and a stage resolved elsewhere is metadata this reads past rather
    /// than rejects.
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
        var permits = 0;
        var rateWindow = TimeSpan.Zero;
        var rateScope = RateLimitScope.Tenant;
        TimeSpan? idempotencyWindow = null;
        var idempotencyScope = IdempotencyScope.Tenant;
        TimeSpan? cacheTtl = null;
        var cacheScope = CacheScope.Tenant;
        var hedgeAfter = TimeSpan.Zero;
        var hedgeAttempts = 1;
        FallbackValue? fallback = null;
        CapabilityDescriptor? fallbackCapability = null;

        foreach (var policy in policies.Ordered)
        {
            switch (policy.Kind)
            {
                case RateLimitKind:
                    permits = Math.Max(0, Parameter(policy, "permits", 0));
                    rateWindow = Parameter(policy, "window", TimeSpan.Zero);
                    rateScope = Parameter(policy, "scope", RateLimitScope.Tenant);
                    break;

                case IdempotencyKind:
                    idempotencyWindow = Parameter(policy, "window", TimeSpan.Zero);
                    idempotencyScope = Parameter(policy, "scope", IdempotencyScope.Tenant);
                    break;

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

                case CacheKind:
                    // Collapsed to null at zero on purpose: HasCache asks the same question,
                    // and a cache held for no time is a write nothing could ever read.
                    var ttl = Parameter(policy, "ttl", TimeSpan.Zero);

                    cacheTtl = ttl > TimeSpan.Zero ? ttl : null;
                    cacheScope = Parameter(policy, "scope", CacheScope.Tenant);
                    break;

                case HedgeKind:
                    hedgeAfter = Parameter(policy, "afterDelay", TimeSpan.Zero);
                    hedgeAttempts = Math.Max(1, Parameter(policy, "maxAttempts", 1));
                    break;

                case FallbackKind:
                    // Exactly one of the two is present: the builder writes "value" for a
                    // constant and "capability" for a dispatch, and there is no overload that
                    // writes both. Read as two independent lookups anyway, so that a chain
                    // assembled by hand carrying both is a policy with a capability and a
                    // constant rather than a silent discard of one of them — HasFallback is
                    // true either way, and DegradeAsync prefers the dispatch.
                    fallback = Parameter<FallbackValue?>(policy, "value", null);
                    fallbackCapability = Parameter<CapabilityDescriptor?>(policy, "capability", null);
                    break;

                default:
                    // Stage 7's Audit, which StepAudit resolves. Read past rather than
                    // rejected — the chain is the author's whole declaration and this type is
                    // the in-line stages' view of it.
                    break;
            }
        }

        var resolved = new StepPolicy(
            timeout, attempts, backoff, retryOn,
            failureRatio, samplingWindow, breakDuration, maxConcurrency, queueDepth,
            permits, rateWindow, rateScope, idempotencyWindow, idempotencyScope,
            cacheTtl, cacheScope, hedgeAfter, hedgeAttempts, fallback, fallbackCapability);

        return resolved.IsActive ? resolved : None;
    }

    /// <summary>The descriptor kind <see cref="PolicySet.RateLimit"/> emits.</summary>
    public const string RateLimitKind = "RateLimit";

    /// <summary>The descriptor kind <see cref="PolicySet.Idempotency"/> emits.</summary>
    public const string IdempotencyKind = "Idempotency";

    /// <summary>The descriptor kind <see cref="PolicySet.Timeout"/> emits.</summary>
    public const string TimeoutKind = "Timeout";

    /// <summary>The descriptor kind <see cref="PolicySet.Retry"/> emits.</summary>
    public const string RetryKind = "Retry";

    /// <summary>The descriptor kind <see cref="PolicySet.CircuitBreaker"/> emits.</summary>
    public const string CircuitBreakerKind = "CircuitBreaker";

    /// <summary>The descriptor kind <see cref="PolicySet.Bulkhead"/> emits.</summary>
    public const string BulkheadKind = "Bulkhead";

    /// <summary>The descriptor kind <see cref="PolicySet.Cache"/> emits.</summary>
    /// <remarks>
    /// Stage 5 rather than stage 4, and read here anyway. The nesting puts the cache between
    /// the innermost resilience policy and the capability
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md">ADR-0025</a>
    /// §2.5: "adding stage 5 means adding it outside the dispatch and inside stage 4"), so it
    /// is reached from the same call and resolved onto the same value. This type carries the
    /// parameters; it does not decide what wraps what.
    /// </remarks>
    public const string CacheKind = "Cache";

    /// <summary>The descriptor kind <see cref="PolicySet.Hedge"/> emits.</summary>
    /// <remarks>
    /// Stage 4's fifth kind, and the first one added since
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0024-stage-four-is-a-fixed-nesting.md">ADR-0024</a>
    /// fixed the nesting of four — which is the revisit condition that record names. It sits
    /// inside the retry and outside the breaker, so a hedged call is counted, permitted and
    /// timed like any other call
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0078-stage-four-nests-six-kinds.md">ADR-0078</a>).
    /// </remarks>
    public const string HedgeKind = "Hedge";

    /// <summary>The descriptor kind both <c>PolicySet.Fallback</c> overloads emit.</summary>
    /// <remarks>
    /// Stage 4's sixth kind and the outermost of them, which is why the engine applies it in
    /// the step loop rather than in the policed dispatch: everything else in the stage happens
    /// inside one attempt, and this happens after the last of them.
    /// </remarks>
    public const string FallbackKind = "Fallback";

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
