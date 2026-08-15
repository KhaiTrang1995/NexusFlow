namespace FlowX;

/// <summary>What a quota decided about one caller, and when the budget is granted again.</summary>
/// <param name="Admitted">Whether the caller may proceed.</param>
/// <param name="Remaining">
/// Budget left in the current period after this decision, floored at zero. Advisory: another
/// node may have spent it by the time the caller reads it.
/// </param>
/// <param name="RetryAfter">
/// How long is left of the period the budget was exhausted in.
/// <see cref="TimeSpan.Zero"/> on admission, and never longer than the declared period — a
/// fixed window grants the whole budget again at its boundary, so the wait is the remainder of
/// the current window and nothing more.
/// </param>
/// <remarks>
/// <para>
/// <strong>The same three fields <see cref="RateLimitVerdict"/> carries, and one of them means
/// something different.</strong> A limiter's <c>RetryAfter</c> is the time for one token to
/// accrue, because a token bucket refills continuously; a quota's is the remainder of the
/// window, because a fixed window grants nothing until it turns over. That difference is the
/// whole reason the two are separate contracts rather than one — see <see cref="IQuotaStore"/>.
/// </para>
/// </remarks>
public readonly record struct QuotaVerdict(bool Admitted, long Remaining, TimeSpan RetryAfter);

/// <summary>
/// A long-window budget shared by every node that consults it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Shared is the contract, for <see cref="IRateLimiterStore"/>'s reason and with more
/// force.</strong> A budget each node kept for itself would be the declared figure times the
/// replica count, the factor changes on every autoscale, and nothing declares it or reports it
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0040-a-rate-limit-is-shared-or-it-is-not-a-rate-limit.md">ADR-0040</a>
/// §1.1). There is therefore no in-memory default the engine can fall back on: a step declaring
/// a <c>Quota</c> with no store registered is <strong>refused</strong>, and so is one whose
/// store call fails.
/// </para>
/// <para>
/// <strong>A fixed window with a stored counter, and deliberately not a sliding one.</strong>
/// A quota is commercial rather than protective: it enforces the plan a tenant bought, and a
/// plan is written against a period a human can name — a day, a month — with a boundary at
/// which the whole budget is granted again. A token bucket cannot express that and
/// <see cref="TenantFairness.QuotaPerWindow"/> says so in as many words: "a tenant that
/// exhausts a monthly quota on the first day is admitted again a few seconds later at one
/// thirty-millionth of the budget per second". A sliding window would express it and costs a
/// per-caller timestamp log the store has to keep and trim for the length of the period, which
/// for a month of traffic is a table nobody sized; the counter is one row per key per window
/// and its error is bounded and in one direction — a caller that spends the whole budget at the
/// end of one window and the whole of it at the start of the next sees twice the budget across
/// that boundary, and never more than the declared figure inside a window that starts when the
/// period says it does.
/// </para>
/// <para>
/// <strong>One method, and it both decides and consumes</strong>, exactly as
/// <see cref="IRateLimiterStore.TryAcquireAsync"/> does and for the identical reason: a
/// separate "may I" and "I did" would be two round trips with a race between them, which is
/// the race a shared budget exists to close. Every implementation performs the read, the
/// window roll and the increment as one indivisible step against its own server's clock.
/// </para>
/// <para>
/// <strong>Tenant fairness is the point, and it is the key that carries it.</strong> The
/// caller builds the key from the capability and the declared <see cref="QuotaScope"/>, so one
/// tenant exhausting its plan cannot spend another's — <c>docs/16 §4</c>'s first mechanism
/// applied to a step rather than to an admission.
/// </para>
/// </remarks>
public interface IQuotaStore
{
    /// <summary>
    /// Spends one unit of <paramref name="key"/>'s budget for the current period, if there is
    /// one left.
    /// </summary>
    /// <param name="key">
    /// What the budget belongs to. Built by the caller from the capability and the declared
    /// <see cref="QuotaScope"/>; opaque here, and never interpreted by a store.
    /// </param>
    /// <param name="budget">How many calls the period grants. Greater than zero.</param>
    /// <param name="period">
    /// The fixed window the budget is granted over. Positive. The window a call falls in is
    /// derived from the store's own clock, so every node agrees about where the boundary is.
    /// </param>
    /// <param name="cancellationToken">Cancels the store call.</param>
    /// <returns>
    /// The verdict. A refusal is a <em>successful</em> call reporting
    /// <see cref="QuotaVerdict.Admitted"/> <c>false</c> — the store answered, and the answer was
    /// no. A <see cref="Result{T}"/> failure means the store could not answer at all, which the
    /// caller must treat as a refusal rather than as an admission.
    /// </returns>
    ValueTask<Result<QuotaVerdict>> TryConsumeAsync(
        string key,
        int budget,
        TimeSpan period,
        CancellationToken cancellationToken);
}
