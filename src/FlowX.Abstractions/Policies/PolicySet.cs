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
    /// Caches the result. Tenant-scoped by default; declaring it on a capability with
    /// side effects is a build error (FLOWX1018), because caching a write is a bug.
    /// </summary>
    public PolicySet Cache(TimeSpan ttl, CacheScope scope = CacheScope.Tenant)
        => Add(nameof(Cache), PolicyStage.Efficiency, ("ttl", ttl), ("scope", scope));

    /// <summary>Limits invocation rate. Runs at <see cref="PolicyStage.Admission"/>, before authentication.</summary>
    public PolicySet RateLimit(int permits, TimeSpan window, RateLimitScope scope = RateLimitScope.Tenant)
        => Add(nameof(RateLimit), PolicyStage.Admission, ("permits", permits), ("window", window), ("scope", scope));

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

/// <summary>Idempotency key namespacing. Two tenants may legitimately use the same key.</summary>
public enum IdempotencyScope
{
    /// <summary>Keyed within a tenant. The default.</summary>
    Tenant = 0,

    /// <summary>Keyed across the whole deployment.</summary>
    Global = 1,
}
