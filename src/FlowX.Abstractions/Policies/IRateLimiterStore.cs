namespace FlowX;

/// <summary>What a limiter decided about one caller, and when to come back.</summary>
/// <param name="Admitted">Whether the caller may proceed.</param>
/// <param name="Remaining">
/// Permits left in the bucket after this decision, floored at zero. Advisory: another node may
/// have taken them by the time the caller reads it.
/// </param>
/// <param name="RetryAfter">
/// How long until a permit is available. <see cref="TimeSpan.Zero"/> on admission, and never
/// longer than the declared window — a token bucket refills continuously, so the wait for one
/// token is at most the time one token takes to accrue.
/// </param>
/// <remarks>
/// <see cref="RetryAfter"/> is on the verdict rather than derived by the caller because only the
/// store knows the bucket's level and the server's clock.
/// <c>docs/10-Policy-Framework.md</c> §3 specifies <c>RateLimit</c> as "token bucket; returns
/// 429 + <c>Retry-After</c>", and this is the half of that sentence a store can supply.
/// </remarks>
public readonly record struct RateLimitVerdict(bool Admitted, long Remaining, TimeSpan RetryAfter);

/// <summary>
/// A rate limiter whose budget is shared by every node that consults it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Shared is the whole contract, and it is what makes this a plugin rather than a
/// class.</strong> A circuit breaker that is per process is <em>slower to protect</em> and never
/// wrong — <c>docs/10</c> §6 calls that "the conservative direction — every node discovers an
/// outage for itself". A rate limiter that is per process is <em>anti-conservative</em>: n nodes
/// admit n × the declared rate, the factor is the replica count, and nothing declares it or
/// reports it. Shipping one behind a <c>RateLimit(permits, window)</c> that reads as a
/// deployment-wide limit is the half-executing policy
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md">ADR-0025</a>
/// rejects, in its worst form.
/// </para>
/// <para>
/// So there is no in-memory default and the engine has none to fall back on. A step declaring a
/// <c>RateLimit</c> with no store registered is <strong>refused</strong>, and so is one whose
/// store call fails: a limiter that cannot reach its server does not know whether the caller is
/// inside the budget, and admitting on doubt turns an outage of the limiter into an unbounded
/// flood of whatever it was bounding. See
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0035-a-rate-limit-is-shared-or-it-is-not-a-rate-limit.md">ADR-0035</a>.
/// </para>
/// <para>
/// <strong>One method, and it both decides and consumes.</strong> A separate "may I" and "I did"
/// would be two round trips with a race between them, which is precisely the race a distributed
/// limiter exists to close. Every implementation performs the read, the refill and the
/// decrement as one indivisible step against its own server's clock — Lua against <c>TIME</c>
/// in Redis, one statement against <c>now()</c> in PostgreSQL — for
/// <see cref="ILeaseStore"/>'s reason: a store that trusted the caller's clock would be a
/// limiter whose budget depends on NTP.
/// </para>
/// <para>
/// Every member here is pinned by <c>RateLimiterConformance</c>, whose load-bearing assertion is
/// that two independently constructed clients over one server share one budget. A limiter that
/// passes every other assertion in that file and fails that one is the limiter this contract
/// exists to refuse, and it is the assertion a single-client suite cannot make.
/// </para>
/// </remarks>
public interface IRateLimiterStore
{
    /// <summary>
    /// Takes one permit from <paramref name="key"/>'s bucket if there is one.
    /// </summary>
    /// <param name="key">
    /// What the budget belongs to. Built by the caller from the capability and the declared
    /// <see cref="RateLimitScope"/>; opaque here, and never interpreted by a store.
    /// </param>
    /// <param name="permits">
    /// The bucket's capacity, and the number of permits that accrue over one
    /// <paramref name="window"/>. Greater than zero.
    /// </param>
    /// <param name="window">The period <paramref name="permits"/> are granted over. Positive.</param>
    /// <param name="cancellationToken">Cancels the store call.</param>
    /// <returns>
    /// The verdict. A refusal is a <em>successful</em> call reporting
    /// <see cref="RateLimitVerdict.Admitted"/> <c>false</c> — the store answered, and the answer
    /// was no. A <see cref="Result{T}"/> failure means the store could not answer at all, which
    /// the caller must treat as a refusal rather than as an admission.
    /// </returns>
    ValueTask<Result<RateLimitVerdict>> TryAcquireAsync(
        string key,
        int permits,
        TimeSpan window,
        CancellationToken cancellationToken);
}
