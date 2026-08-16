using System.Collections.Immutable;

namespace FlowX;

/// <summary>
/// The fixed policy stage order. Not configurable — the ordering guarantees <em>are</em>
/// the safety property (ADR-0011). Five recurring production incident classes become
/// unexpressible rather than merely discouraged.
/// </summary>
public enum PolicyStage
{
    /// <summary>Rate limit, quota, tenant guard, payload size. Runs before any allocation.</summary>
    Admission = 1,

    /// <summary>Authentication, authorisation, consent. Always before caching.</summary>
    Identity = 2,

    /// <summary>Validation, idempotency, dedupe. Always before retry and before the side effect.</summary>
    Integrity = 3,

    /// <summary>Timeout, retry, circuit breaker, bulkhead, hedge, fallback.</summary>
    Resilience = 4,

    /// <summary>Cache, batch, coalesce. Always after authorisation.</summary>
    Efficiency = 5,

    /// <summary>The capability itself.</summary>
    Execution = 6,

    /// <summary>Compensation registration, outbox, audit. Always after the step succeeded.</summary>
    Consistency = 7,
}

/// <summary>
/// How the gap before an attempt grows with the number already made.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One shape for both things that space attempts out.</strong> It was written for
/// <c>Retry</c>, and <c>PollUntil</c> asks the identical question — <em>how long before the
/// next call?</em> — of an identical answer: a base, a ceiling, and whether to decorrelate.
/// A second type would have been the same three numbers under different names, with two
/// implementations of one formula to keep in step, and an author reading a flow would have had
/// to know which <c>Backoff</c> they were looking at.
/// </para>
/// <para>
/// The difference is only in what an attempt is. A retry's attempts are one step recovering
/// from a fault, bounded by a count; a poll's are one step asking a question that has not been
/// answered yet, bounded by a duration. <see cref="After"/> is where both meet.
/// </para>
/// </remarks>
public sealed record Backoff
{
    private Backoff() { }

    /// <summary>Base delay before the first retry.</summary>
    public TimeSpan BaseDelay { get; private init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Upper bound on any single delay.</summary>
    public TimeSpan MaxDelay { get; private init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Full jitter: <c>delay = random(0, base × 2^attempt)</c>, capped. Decorrelates
    /// retries so a shared outage does not produce a synchronised thundering herd.
    /// </summary>
    public bool Jitter { get; private init; } = true;

    /// <summary>Exponential backoff with full jitter — the default and the right default.</summary>
    public static Backoff ExponentialJitter(TimeSpan? baseDelay = null, TimeSpan? maxDelay = null) => new()
    {
        BaseDelay = baseDelay ?? TimeSpan.FromMilliseconds(200),
        MaxDelay = maxDelay ?? TimeSpan.FromSeconds(30),
        Jitter = true,
    };

    /// <summary>Exponential without jitter. Rarely correct at scale; prefer <see cref="ExponentialJitter(TimeSpan?, TimeSpan?)"/>.</summary>
    public static Backoff Exponential(TimeSpan? baseDelay = null, TimeSpan? maxDelay = null) => new()
    {
        BaseDelay = baseDelay ?? TimeSpan.FromMilliseconds(200),
        MaxDelay = maxDelay ?? TimeSpan.FromSeconds(30),
        Jitter = false,
    };

    /// <summary>Exponential, with both bounds written as ISO-8601 durations.</summary>
    /// <param name="from">The gap before the second attempt, e.g. <c>PT5S</c>.</param>
    /// <param name="to">The gap the schedule never exceeds, e.g. <c>PT5M</c>.</param>
    /// <remarks>
    /// <para>
    /// <strong>The overload a <c>PollUntil</c> reaches for, and the reason is where it is
    /// written.</strong> A retry's backoff is configuration — declared in a
    /// <see cref="PolicySet"/> beside attempt counts, where <c>TimeSpan.FromSeconds(5)</c>
    /// reads as the number it is. A poll's is part of the flow's own declaration, next to
    /// <c>[FlowDeadline("PT6H")]</c> and <c>[CronTrigger("0 2 * * *")]</c>, which is how this
    /// DSL already writes a duration an author is stating rather than computing.
    /// </para>
    /// <para>
    /// Both bounds are required, unlike the <see cref="TimeSpan"/> overload's. A default base
    /// of 200&#160;ms and ceiling of 30&#160;s are sensible for recovering from a fault and
    /// wrong for waiting on somebody else's four-hour job, and a poll that silently took them
    /// would make forty thousand calls where the author expected fifty.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">Either bound is not an ISO-8601 duration.</exception>
    public static Backoff Exponential(string from, string to) =>
        Exponential(Iso8601(from, nameof(from)), Iso8601(to, nameof(to)));

    /// <summary>Exponential with full jitter, with both bounds written as ISO-8601 durations.</summary>
    /// <param name="from">The gap before the second attempt, e.g. <c>PT5S</c>.</param>
    /// <param name="to">The gap the schedule never exceeds, e.g. <c>PT5M</c>.</param>
    /// <remarks>
    /// The one to reach for when many instances poll the same dependency. A hundred thousand
    /// documents accepted in the same minute and parked on the same undecorrelated schedule
    /// wake in the same second, which turns "waiting costs nothing" into a load test somebody
    /// else pays for.
    /// </remarks>
    /// <exception cref="ArgumentException">Either bound is not an ISO-8601 duration.</exception>
    public static Backoff ExponentialJitter(string from, string to) =>
        ExponentialJitter(Iso8601(from, nameof(from)), Iso8601(to, nameof(to)));

    /// <summary>How long to wait before attempt number <paramref name="attempt"/>.</summary>
    /// <param name="attempt">
    /// The one-based attempt about to be made. <c>1</c> is the attempt after the first and
    /// waits <see cref="BaseDelay"/>.
    /// </param>
    /// <param name="sample">
    /// A uniform sample in <c>[0, 1]</c>, used only when <see cref="Jitter"/> is set. Passed in
    /// rather than drawn here so that the type stays a pure function and a test can pin a
    /// schedule — the same bargain <c>CompensationPolicy.DelayBefore</c> struck, which is now
    /// this method.
    /// </param>
    /// <remarks>
    /// Computed from the attempt number rather than accumulated, which is what lets a durable
    /// poll resumed on another node an hour later schedule the gap the author declared: the
    /// number comes from the journal's committed rows and this is a pure function of it.
    /// </remarks>
    public TimeSpan After(int attempt, double sample)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);

        // Doubles rather than TimeSpan arithmetic: 2^attempt overflows a tick count long
        // before it stops being a number, and a negative TimeSpan would be a wait that
        // returns immediately rather than the cap the author asked for.
        var ceiling = Math.Min(
            BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1),
            MaxDelay.TotalMilliseconds);

        return TimeSpan.FromMilliseconds(Jitter ? ceiling * Math.Clamp(sample, 0, 1) : ceiling);
    }

    /// <summary>Reads an ISO-8601 duration, refusing anything that is not one.</summary>
    /// <remarks>
    /// <c>XmlConvert</c> rather than a parser of this repository's own, because the emitted
    /// plan already folds a manifest's duration back with exactly that call — two parsers for
    /// one grammar is two chances for a declared <c>PT5M</c> and a published <c>PT5M</c> to
    /// come to mean different things.
    /// </remarks>
    private static TimeSpan Iso8601(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);

        try
        {
            return System.Xml.XmlConvert.ToTimeSpan(value);
        }
        catch (FormatException reason)
        {
            throw new ArgumentException(
                $"'{value}' is not an ISO-8601 duration. Write it the way this DSL writes " +
                "every other duration it declares — PT5S, PT5M, PT1H.",
                parameter,
                reason);
        }
    }
}

/// <summary>
/// The degraded value a <see cref="PolicySet.Fallback{TValue}(TValue)"/> puts into the state
/// bag when the step it wraps has failed for the last time.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A closure over a typed <c>Set&lt;T&gt;</c>, built where the type is known.</strong>
/// A step's result reaches the next step through <see cref="FlowContext.Set{T}"/>, which is
/// generic, and the engine holds a <see cref="FlowContext"/> and no type argument — the same
/// wall that puts <c>DescribeCacheEntry</c> and <c>RestoreState</c> on the dispatcher rather
/// than on the engine. Here the type <em>is</em> available at the one place it matters: the
/// author writes the constant, so <see cref="Of{TValue}"/> captures it under its own type and
/// the engine only ever calls <see cref="ApplyTo"/>. Nothing reflects, nothing boxes on the
/// hot path, and the closure is built once into a <c>static readonly PolicySet</c>.
/// </para>
/// <para>
/// <strong>Which is also why the type has to be checked at build time.</strong>
/// <c>Set&lt;T&gt;</c> keys the bag by <c>typeof(T)</c>, so a constant declared as anything
/// other than the step's own output contract would be filed under a type no later step binds —
/// a degraded mode that answers the next <c>Get&lt;T&gt;</c> with an exception. <c>FLOWX1052</c>
/// refuses that at build time rather than leaving it to a dependency's bad afternoon.
/// </para>
/// </remarks>
public sealed class FallbackValue
{
    private readonly Action<FlowContext> _apply;

    private FallbackValue(object? value, Type contract, Action<FlowContext> apply)
    {
        Value = value;
        Contract = contract;
        _apply = apply;
    }

    /// <summary>The declared constant, boxed. Carried for diagnostics and for the manifest.</summary>
    public object? Value { get; }

    /// <summary>The contract the constant is filed under — the step's output type.</summary>
    public Type Contract { get; }

    /// <summary>Captures <paramref name="value"/> under its own static type.</summary>
    /// <typeparam name="TValue">The step's output contract, inferred from the argument.</typeparam>
    /// <param name="value">The degraded answer. May be <c>null</c> only where the contract allows it.</param>
    public static FallbackValue Of<TValue>(TValue value) =>
        new(value, typeof(TValue), context => context.Set(value));

    /// <summary>Files the degraded value in the flow's state bag, under its contract.</summary>
    /// <param name="context">The scope the failed step ran under — an iteration's, inside a loop.</param>
    public void ApplyTo(FlowContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _apply(context);
    }
}

/// <summary>
/// The capability a <see cref="PolicySet.Fallback{TCapability}()"/> asks instead, when the
/// step it wraps has failed for the last time.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A declaration, and deliberately not yet a resolution.</strong> All this carries is
/// the CLR type the author named, because that is all a <c>PolicySet</c> can know: a set is a
/// <c>static readonly</c> field in a <c>Policies</c> class, built with no step in sight, and
/// the id, version and side effects of the capability it names live on that capability's
/// <c>[Capability]</c> attribute. Reading them from here would mean reflecting over
/// <see cref="Type"/> at run time, which is what
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0002-compile-time-orchestration.md">ADR-0002</a>
/// and constraint C2 both refuse.
/// </para>
/// <para>
/// <strong>So it is bound rather than read.</strong> <c>PolicyChain.ForStep</c> takes the
/// resolved <c>CapabilityDescriptor</c> the generated plan already holds and replaces this
/// declaration with it, exactly as the same method already resolves the step's own capability;
/// what reaches <c>StepPolicy</c> is the descriptor, and what reaches the manifest comes from
/// the compiler's own reading of the same type. Two levels of the one fact, never two readings
/// of it.
/// </para>
/// </remarks>
/// <param name="Capability">The capability type the author named.</param>
public sealed record FallbackCapability(Type Capability);

/// <summary>A single declared policy and its parameters.</summary>
/// <param name="Kind">Policy name, e.g. <c>Retry</c>.</param>
/// <param name="Stage">Fixed stage the policy runs in.</param>
/// <param name="Parameters">Policy-specific settings, surfaced verbatim into the manifest.</param>
public sealed record PolicyDescriptor(
    string Kind,
    PolicyStage Stage,
    ImmutableDictionary<string, object?> Parameters);

/// <summary>
/// A named, reusable set of policies. Declared once as a static constant and applied
/// by name, so resilience configuration cannot drift across a codebase.
/// </summary>
/// <remarks>
/// Composition is resolved at compile time; only parameter <em>values</em> are
/// runtime-configurable. Configuration can change a timeout from 2&#160;s to 3&#160;s;
/// it can never add, remove or reorder a policy, because that would change the graph.
/// </remarks>
public sealed class PolicySet
{
    private PolicySet(string name, ImmutableArray<PolicyDescriptor> policies)
    {
        Name = name;
        Policies = policies;
    }

    /// <summary>Identifier used in the manifest, in telemetry and in review.</summary>
    public string Name { get; }

    /// <summary>The declared policies, in declaration order. Execution order is by <see cref="PolicyStage"/>.</summary>
    public ImmutableArray<PolicyDescriptor> Policies { get; }

    /// <summary>An empty set.</summary>
    public static PolicySet Empty { get; } = new("empty", []);

    /// <summary>Starts a named set.</summary>
    public static PolicySet Named(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new PolicySet(name, []);
    }

    private PolicySet Add(string kind, PolicyStage stage, params (string Key, object? Value)[] parameters)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in parameters)
        {
            builder[key] = value;
        }

        return new PolicySet(Name, Policies.Add(new PolicyDescriptor(kind, stage, builder.ToImmutable())));
    }

    /// <summary>Caps how long a single attempt may take. Never exceeds the flow's remaining deadline.</summary>
    public PolicySet Timeout(TimeSpan duration)
        => Add(nameof(Timeout), PolicyStage.Resilience, ("duration", duration));

    /// <summary>
    /// Retries retryable failures. <strong>Requires the capability to declare
    /// <c>Idempotent = true</c></strong> — otherwise the build fails with FLOWX1014.
    /// A retry reuses the same idempotency key and never outlives the deadline.
    /// </summary>
    public PolicySet Retry(int attempts, Backoff? backoff = null, ErrorCategory[]? retryOn = null)
        => Add(
            nameof(Retry),
            PolicyStage.Resilience,
            ("attempts", attempts),
            ("backoff", backoff ?? Backoff.ExponentialJitter()),
            ("retryOn", retryOn ?? [ErrorCategory.Unavailable, ErrorCategory.Internal]));

    /// <summary>Stops calling a failing dependency. Always pair it with <see cref="Retry"/> — retries without a breaker amplify an outage into a self-DDoS.</summary>
    public PolicySet CircuitBreaker(double failureRatio, TimeSpan breakDuration, TimeSpan? samplingWindow = null)
        => Add(
            nameof(CircuitBreaker),
            PolicyStage.Resilience,
            ("failureRatio", failureRatio),
            ("breakDuration", breakDuration),
            ("samplingWindow", samplingWindow ?? TimeSpan.FromSeconds(30)));

    /// <summary>Bounds concurrency so one slow dependency cannot consume every thread.</summary>
    public PolicySet Bulkhead(int maxConcurrency, int queueDepth = 0)
        => Add(nameof(Bulkhead), PolicyStage.Resilience, ("maxConcurrency", maxConcurrency), ("queueDepth", queueDepth));

    /// <summary>
    /// Cuts the tail by issuing a second call while the first is still outstanding.
    /// <strong>Requires the capability to declare <c>Idempotent = true</c></strong> — otherwise
    /// the build fails with FLOWX1051.
    /// </summary>
    /// <param name="afterDelay">
    /// How long to wait for the outstanding call before issuing the next one. Set it near the
    /// dependency's p95: below it the hedge doubles the load to save nothing, above it the
    /// timeout arrives first and the hedge never fires.
    /// </param>
    /// <param name="maxAttempts">
    /// How many calls may be in flight for one attempt at the step, including the first. Two is
    /// the number that buys nearly all of the latency; a third mostly buys load.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>Not a retry, and the difference is what it reacts to.</strong> A retry answers a
    /// failure and waits before asking again; a hedge answers <em>silence</em> and asks again
    /// while the first call is still running. The first success wins, the calls that lost are
    /// cancelled, and a cancelled loser is not a failure of the step. The two compose —
    /// <c>Retry</c> is the outer loop and each of its attempts is a hedged race
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0078-stage-four-nests-six-kinds.md">ADR-0078</a>).
    /// </para>
    /// <para>
    /// <strong>Two racing calls are two calls.</strong> The effect can happen twice and the
    /// answer that reaches the state bag can be either call's, which is why the capability has
    /// to declare itself idempotent: both calls present the same <c>ctx.IdempotencyKey</c>, so
    /// what <c>Idempotent = true</c> promises is exactly that the two are one request.
    /// </para>
    /// </remarks>
    public PolicySet Hedge(TimeSpan afterDelay, int maxAttempts = 2)
        => Add(
            nameof(Hedge),
            PolicyStage.Resilience,
            ("afterDelay", afterDelay),
            ("maxAttempts", maxAttempts));

    /// <summary>
    /// Answers with a declared constant when the step has failed for the last time — an
    /// explicit degraded mode rather than a failed flow.
    /// </summary>
    /// <typeparam name="TValue">
    /// The step's output contract, inferred from <paramref name="value"/>. Anything else is
    /// refused by FLOWX1052: the value is filed in the state bag under its own type, so a
    /// mismatch would be a degraded mode no later step can read.
    /// </typeparam>
    /// <param name="value">The degraded answer.</param>
    /// <remarks>
    /// <para>
    /// <strong>Outermost of the six stage-4 kinds, and outside the retry.</strong> A fallback
    /// that fired on the first failure would spend the retry the author also declared; it is
    /// consulted once, after every attempt has been made and refused
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0078-stage-four-nests-six-kinds.md">ADR-0078</a>).
    /// </para>
    /// <para>
    /// <strong>Requires the capability to declare no side effects</strong> — FLOWX1053, and
    /// FLOWX1018's argument word for word: a fallback returns a success without performing the
    /// effect. A step that was supposed to change the world and did not cannot be papered over
    /// with a constant, and a degraded step registers no compensation, because there is nothing
    /// to undo.
    /// </para>
    /// <para>
    /// <strong>The other half of <c>docs/10 §3</c>'s "capability or constant" is
    /// <see cref="Fallback{TCapability}()"/>.</strong> Pick this one when the degraded answer
    /// is a value the author can write down, and that one when it takes a call to produce.
    /// </para>
    /// </remarks>
    public PolicySet Fallback<TValue>(TValue value)
        => Add(nameof(Fallback), PolicyStage.Resilience, ("value", FallbackValue.Of(value)));

    /// <summary>
    /// Asks a second capability when the step has failed for the last time — a degraded mode
    /// that answers with a call rather than with a constant.
    /// </summary>
    /// <typeparam name="TCapability">
    /// The capability to ask instead. It must produce the step's own output contract, which
    /// FLOWX1052 checks: the answer is filed in the state bag under its own type, so anything
    /// else is a degraded mode no later step binds. It must also declare no side effects
    /// (FLOWX1053), for the reason below.
    /// </typeparam>
    /// <remarks>
    /// <para>
    /// <strong>Outermost of the six stage-4 kinds, exactly as the constant is.</strong> It is
    /// consulted once, after every attempt at the step has been made and refused, so the
    /// attempts the author declared beside it are all spent first
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0078-stage-four-nests-six-kinds.md">ADR-0078</a> §2.1).
    /// The fallback capability is asked once and is not itself retried, hedged or bulkheaded:
    /// the declared chain wraps the step, and the fallback is the decision to stop asking it.
    /// </para>
    /// <para>
    /// <strong>The type argument, and not a value, is what makes this checkable.</strong> A
    /// <c>Func&lt;FlowContext, T&gt;</c> would be code the compiler cannot check the shape of,
    /// cannot publish in the manifest and cannot keep deterministic under replay — ADR-0078
    /// §2.6 rejects it. A named capability is all three: the compiler resolves its
    /// <c>[Capability]</c> declaration, the manifest publishes it in the same inventory every
    /// other capability appears in, and the generated dispatcher binds its typed output the
    /// same way it binds a step's.
    /// </para>
    /// <para>
    /// <strong>Requires the fallback capability to declare no side effects</strong>, which is
    /// FLOWX1053 asked of the second capability as well as the first. A fallback fires
    /// <em>because</em> a dependency has just failed, so it is the least-exercised path in the
    /// system running at the worst moment; making it the path that writes is backwards, and it
    /// would put an effect on the unwind stack under a step whose own capability produced
    /// none. A degraded mode that has to write is a branch in the flow, where a compensable
    /// effect belongs
    /// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0079-a-fallback-capability-is-a-dispatch-of-its-own.md">ADR-0079</a> §2.3).
    /// </para>
    /// </remarks>
    public PolicySet Fallback<TCapability>()
        => Add(
            nameof(Fallback),
            PolicyStage.Resilience,
            ("capability", new FallbackCapability(typeof(TCapability))));

    /// <summary>
    /// Caches the result. Tenant-scoped by default; declaring it on a capability with
    /// side effects is a build error (FLOWX1018), because caching a write is a bug.
    /// </summary>
    public PolicySet Cache(TimeSpan ttl, CacheScope scope = CacheScope.Tenant)
        => Add(nameof(Cache), PolicyStage.Efficiency, ("ttl", ttl), ("scope", scope));

    /// <summary>Limits invocation rate. Runs at <see cref="PolicyStage.Admission"/>, before authentication.</summary>
    public PolicySet RateLimit(int permits, TimeSpan window, RateLimitScope scope = RateLimitScope.Tenant)
        => Add(nameof(RateLimit), PolicyStage.Admission, ("permits", permits), ("window", window), ("scope", scope));

    /// <summary>
    /// Bounds how many calls a budget holder may make over a long, fixed period. Runs at
    /// <see cref="PolicyStage.Admission"/>, beside <see cref="RateLimit"/>.
    /// </summary>
    /// <param name="budget">How many calls the period grants. A budget of zero declares no quota.</param>
    /// <param name="period">
    /// The fixed window the budget is granted over, and the boundary at which the whole of it
    /// is granted again. Hours and days, not milliseconds — a period short enough to smooth a
    /// burst is a <see cref="RateLimit"/> wearing this one's name.
    /// </param>
    /// <param name="scope">
    /// Whose budget it is. <see cref="QuotaScope.Tenant"/> by default, which is the only scope
    /// that makes the policy do what it exists for.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>The same stage as <see cref="RateLimit"/> and a different job.</strong> A rate
    /// limit is protective — it smooths a burst so a dependency is not knocked over — and a
    /// quota is commercial: it enforces the plan a tenant bought, over a period a human named,
    /// and a tenant that hits one calls its account manager rather than backing off. Both are
    /// stage 1 because both decide whether the call happens at all, and both sit outside the
    /// retry loop for the same reason: a budget spent per attempt would have an effective value
    /// that is a function of how healthy the dependency was.
    /// </para>
    /// <para>
    /// <strong>Tenant-scoped by default, and that is <c>docs/16 §4</c>'s first mechanism read
    /// as a step policy.</strong> A global quota over a shared dependency is a budget the
    /// noisiest tenant spends on everybody's behalf, which is the starvation the option exists
    /// to prevent arriving through the option meant to prevent it.
    /// </para>
    /// <para>
    /// <strong>Needs an <see cref="IQuotaStore"/>.</strong> A step declaring a quota with none
    /// registered is refused rather than admitted, and a host whose plans declare one refuses
    /// to become ready — the stance <see cref="IRateLimiterStore"/> takes, for
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0040-a-rate-limit-is-shared-or-it-is-not-a-rate-limit.md">ADR-0040</a>'s
    /// reason.
    /// </para>
    /// </remarks>
    public PolicySet Quota(int budget, TimeSpan period, QuotaScope scope = QuotaScope.Tenant)
        => Add(nameof(Quota), PolicyStage.Admission, ("budget", budget), ("period", period), ("scope", scope));

    /// <summary>
    /// Refuses the step unless the invocation was made for the purpose named here. Runs at
    /// <see cref="PolicyStage.Identity"/>, beside the capability's authorisation stance.
    /// </summary>
    /// <param name="purpose">
    /// What this step's processing is for — GDPR Article 5(1)(b)'s purpose. An identifier
    /// rather than a sentence: it is compared, published in the manifest and read by whoever
    /// answers "what may this credential be used for", and all three want a token.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>Purpose limitation, and deliberately not consent itself.</strong> A consent is
    /// granted by a person, to an organisation, for a purpose, with an expiry and a
    /// withdrawal — none of which a runtime can know, which is why
    /// <c>samples/healthcare</c> verifies one in a capability against a register and will go
    /// on doing so. What a platform *can* decide, without asking anybody, is the half that is
    /// a comparison: this invocation was made for a stated purpose, and this step is declared
    /// to serve one. Where those disagree the step does not run.
    /// </para>
    /// <para>
    /// <strong>The invocation's purpose comes from validated claims, exactly as its tenant
    /// and its principal do</strong> — <c>FlowInvocation.Purpose</c>, resolved once by the
    /// transport from a <c>purpose</c> claim and from nothing else. A purpose read from a
    /// header or a query string would be a purpose the caller chooses, which is a
    /// purpose-limitation control that limits nothing.
    /// </para>
    /// <para>
    /// <strong>Deny by default, and that is the whole of the policy's value.</strong> An
    /// invocation that asserts no purpose does not satisfy a declared one: it is refused with
    /// <c>policy.consent_purpose_absent</c>. The alternative — admitting an unstated purpose —
    /// is the control failing open on precisely the callers who never thought about it, which
    /// is <c>docs/15 §1</c>'s first row and the defect <c>FLOWX1037</c> exists over one stage
    /// earlier.
    /// </para>
    /// <para>
    /// <strong>Equality, never a hierarchy.</strong> "A treatment consent also covers
    /// research" is a legal and clinical judgement, not a fact about string prefixes, and a
    /// platform that quietly widened one would be wrong in the one place it matters most.
    /// A step that serves two purposes is two steps, or one purpose named for both.
    /// </para>
    /// <para>
    /// <strong>A blank purpose is refused at build time</strong> — <c>FLOWX1057</c>. A
    /// comparison against the empty string is a gate that reads as declared and admits
    /// whatever the caller sends, which is <c>FLOWX1056</c>'s objection one stage up.
    /// </para>
    /// </remarks>
    public PolicySet Consent(string purpose)
        => Add(nameof(Consent), PolicyStage.Identity, ("purpose", purpose));

    /// <summary>
    /// Refuses the step's input when it breaks a rule the contract declares. Runs at
    /// <see cref="PolicyStage.Integrity"/>, before the idempotency window and before the
    /// dispatch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>No parameters, because the rules are on the contract.</strong>
    /// <c>docs/10 §3</c> catalogues this row as "generated from contract annotations", and that
    /// is literal: the compiler reads <c>[Required]</c>, <c>[Range]</c>, <c>[StringLength]</c>,
    /// <c>[MinLength]</c> and <c>[MaxLength]</c> off the step's input contract in the same pass
    /// that builds the manifest, and emits the checks into the generated dispatcher. Nothing
    /// reflects at run time, which is constraint <strong>C2</strong>; and the rules cannot drift
    /// from the contract, because they are not written down twice.
    /// </para>
    /// <para>
    /// <strong>The vocabulary is <c>System.ComponentModel.DataAnnotations</c>' and the
    /// enforcement is FlowX's.</strong> Those attributes ship in the shared framework, so
    /// declaring one costs no package reference and constraint <strong>C6</strong> is untouched;
    /// a FlowX-owned copy of <c>[Required]</c> would have been a second spelling of a word every
    /// C# author already knows. What FlowX does not reuse is
    /// <c>Validator.TryValidateObject</c>, which reflects.
    /// </para>
    /// <para>
    /// <strong>A step whose contract declares no rule is refused at build time</strong> —
    /// <c>FLOWX1055</c>. A validation that checks nothing is a declaration that reads as
    /// satisfied and is not.
    /// </para>
    /// <para>
    /// <strong>Failures reach the caller as RFC 7807 field errors.</strong> The refusal is an
    /// <see cref="ErrorCategory.Validation"/> <see cref="Error"/> carrying an <c>errors</c>
    /// detail, which <c>ProblemDetailsMapper</c> already turns into the problem document's
    /// <c>errors</c> member. No message ever carries a member's value, so a
    /// <c>[Sensitive]</c> member cannot leak through one — see <see cref="FieldError"/>.
    /// </para>
    /// </remarks>
    public PolicySet Validate()
        => Add(nameof(Validate), PolicyStage.Integrity);

    /// <summary>Replays a recorded result for a repeated idempotency key.</summary>
    public PolicySet Idempotency(TimeSpan window, IdempotencyScope scope = IdempotencyScope.Tenant)
        => Add(nameof(Idempotency), PolicyStage.Integrity, ("window", window), ("scope", scope));

    /// <summary>Writes an immutable audit record. Runs after the step succeeded.</summary>
    public PolicySet Audit(string category, params string[] redact)
        => Add(nameof(Audit), PolicyStage.Consistency, ("category", category), ("redact", redact));

    /// <summary>
    /// Retries a failing <em>compensation</em>. <strong>Requires the compensating capability
    /// to declare <c>Idempotent = true</c></strong> — otherwise the build fails with
    /// FLOWX1014, which names the compensation, for the reason <see cref="Retry"/> makes it
    /// name the step.
    /// </summary>
    /// <param name="attempts">How many times the undo may be dispatched, including the first.</param>
    /// <param name="backoff">The wait between attempts. Full-jitter exponential by default.</param>
    /// <param name="retryOn">
    /// Which error categories are worth another attempt. Defaults to the three
    /// <c>docs/10-Policy-Framework.md §5</c> calls retryable — a broker that is busy is worth
    /// asking again, a ledger that says the request was invalid is not.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong><see cref="PolicyStage.Consistency"/>, and not
    /// <see cref="PolicyStage.Resilience"/>.</strong> Stage 7 is where the fixed order puts
    /// compensation registration, and the unwind is that same stage's obligation discharged
    /// later; the retry is a parameter of it. Declaring it at stage 4 would make it a wrapper
    /// around a <em>forward</em> step, and running a stage-4 policy without stages 1–3 is
    /// exactly the class of ordering bug ADR-0011 exists to make unexpressible.
    /// </para>
    /// <para>
    /// Distinct from <see cref="Retry"/> rather than a reuse of it, because the two wrap
    /// different capabilities and are checked against different idempotency declarations: a
    /// non-idempotent <c>payment.capture</c> may legitimately carry an idempotent
    /// <c>payment.refund</c>, and a single kind could not express that.
    /// </para>
    /// </remarks>
    public PolicySet CompensationRetry(int attempts, Backoff? backoff = null, ErrorCategory[]? retryOn = null)
        => Add(
            nameof(CompensationRetry),
            PolicyStage.Consistency,
            ("attempts", attempts),
            ("backoff", backoff ?? Backoff.ExponentialJitter()),
            ("retryOn", retryOn ?? DefaultCompensationRetryOn));

    /// <summary>
    /// The categories a compensation is retried on unless the author says otherwise.
    /// </summary>
    /// <remarks>
    /// One more than <see cref="Retry"/>'s default. <c>docs/06-Execution-Engine.md §7</c>
    /// rule 2 makes compensation retry deliberately more aggressive than forward retry, and a
    /// <see cref="ErrorCategory.Conflict"/> from an undo — the ledger is mid-way through
    /// another write against the same row — is the case where insisting is right and giving
    /// up leaves two systems disagreeing.
    /// </remarks>
    private static readonly ErrorCategory[] DefaultCompensationRetryOn =
        [ErrorCategory.Conflict, ErrorCategory.Unavailable, ErrorCategory.Internal];

    /// <summary>
    /// The documented default compensation policy set: five attempts, full-jitter exponential
    /// backoff.
    /// </summary>
    /// <remarks>
    /// <c>docs/06-Execution-Engine.md §7</c> rule 2 — "compensation retry is more aggressive
    /// than forward retry by default (5 attempts vs 3)". Offered as a named set rather than
    /// applied implicitly to every compensable step: a policy takes effect because it was
    /// declared, and a runtime that retried undeclared policies would be the policy engine
    /// arriving early and unannounced.
    /// </remarks>
    public static PolicySet CompensationDefault { get; } =
        Named("compensation-default").CompensationRetry(attempts: 5);
}

/// <summary>Cache key scoping. Tenant is the default because cross-tenant leakage is unacceptable.</summary>
public enum CacheScope
{
    /// <summary>Keyed by tenant. The default.</summary>
    Tenant = 0,

    /// <summary>Keyed by tenant and principal permission set — prevents privilege-based leakage.</summary>
    Principal = 1,

    /// <summary>Shared across tenants. Requires explicit review; almost always wrong.</summary>
    Global = 2,
}

/// <summary>Rate limit scoping.</summary>
public enum RateLimitScope
{
    /// <summary>Per tenant. The default — a global-only limit lets one tenant starve the rest.</summary>
    Tenant = 0,

    /// <summary>Per authenticated principal.</summary>
    Principal = 1,

    /// <summary>Across the whole deployment.</summary>
    Global = 2,
}

/// <summary>Whose long-window budget a <see cref="PolicySet.Quota"/> spends.</summary>
/// <remarks>
/// The same three choices <see cref="RateLimitScope"/> offers, and a different default would
/// have been wrong for a different reason: a rate limit protects a dependency and a quota
/// enforces a plan, so <see cref="Tenant"/> is the default here because a plan belongs to a
/// tenant, not because a global bound would be unsafe.
/// </remarks>
public enum QuotaScope
{
    /// <summary>Per tenant. The default — a plan limit belongs to whoever bought the plan.</summary>
    Tenant = 0,

    /// <summary>Per authenticated principal, for a budget granted to a person or a key.</summary>
    Principal = 1,

    /// <summary>Across the whole deployment. A platform-wide ceiling rather than a plan.</summary>
    Global = 2,
}

/// <summary>Idempotency key namespacing. Two tenants may legitimately use the same key.</summary>
public enum IdempotencyScope
{
    /// <summary>Keyed within a tenant. The default.</summary>
    Tenant = 0,

    /// <summary>Keyed across the whole deployment.</summary>
    Global = 1,
}
