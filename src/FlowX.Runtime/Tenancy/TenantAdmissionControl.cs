using System.Collections.Concurrent;
using FlowX.Observability;

namespace FlowX.Runtime;

/// <summary>
/// Stage 1's three per-tenant bounds: a rate limit, a quota and a bulkhead.
/// </summary>
/// <remarks>
/// <para>
/// <strong>All three at admission, in that order, and the order is the design.</strong>
/// <c>docs/16 §4</c>'s flowchart runs rate limit, then quota, then concurrency, and it is
/// cheapest-first for a reason that is not aesthetic: the rate limit is the bound a burst hits,
/// so putting it first means a burst costs one round trip rather than three. The bulkhead is
/// last because it is the only one that has to be <em>released</em>, and acquiring a slot the
/// quota was about to refuse would mean holding a permit for a call that never runs.
/// </para>
/// <para>
/// <strong>Two of the three are shared and one is not, and the asymmetry is deliberate.</strong>
/// The rate limit and the quota go to <see cref="IRateLimiterStore"/> because a budget each node
/// kept for itself would be the declared limit multiplied by the replica count — the
/// anti-conservative limiter
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0040-a-rate-limit-is-shared-or-it-is-not-a-rate-limit.md">ADR-0040</a>
/// refuses to ship. The bulkhead is per node because what it bounds <em>is</em> per node: the
/// threads, sockets and pooled contexts of one process. A shared concurrency counter would cost
/// a round trip on acquire and another on release, and would bound a resource no single node
/// owns.
/// </para>
/// <para>
/// <strong>Nothing here is reached by a deployment that did not ask for it.</strong>
/// <c>FlowHost</c> branches on <see cref="TenantIsolation.None"/> before this type exists, and
/// on <see cref="TenantFairness.BoundsAdmission"/> before it is called. Budget <strong>B2</strong>
/// is untouched structurally rather than carefully.
/// </para>
/// </remarks>
public sealed class TenantAdmissionControl
{
    /// <summary>The <c>scope</c> label a rate-limit refusal carries. See <c>docs/16 §8</c>.</summary>
    /// <remarks>
    /// <c>flowx_ratelimit_rejected_total{scope,tenant}</c> is the series <c>docs/16 §8</c> names
    /// for "is a tenant being throttled", and it already carries a bucketed tenant label. So the
    /// three mechanisms report through it under three scope values rather than through three new
    /// instruments: a fairness mechanism that refuses silently is one an operator discovers from
    /// a customer, and a new metric name for each of three refusals is three dashboards to keep
    /// in step.
    /// </remarks>
    public const string RateScope = "TenantRate";

    /// <summary>The <c>scope</c> label a quota refusal carries.</summary>
    public const string QuotaScope = "TenantQuota";

    /// <summary>The <c>scope</c> label a bulkhead refusal carries.</summary>
    public const string ConcurrencyScope = "TenantConcurrency";

    private readonly TenantFairness _fairness;
    private readonly IRateLimiterStore? _limiter;
    private readonly ConcurrentDictionary<string, TenantPool> _pools = new(StringComparer.Ordinal);

    /// <summary>Builds admission control over one deployment's declared bounds.</summary>
    /// <param name="fairness">What the deployment bounds each tenant to.</param>
    /// <param name="limiter">
    /// The shared budget the rate limit and the quota are spent against. May be null only when
    /// neither is declared; the startup validator is what makes that true.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="fairness"/> is null.</exception>
    public TenantAdmissionControl(TenantFairness fairness, IRateLimiterStore? limiter)
    {
        ArgumentNullException.ThrowIfNull(fairness);

        _fairness = fairness;
        _limiter = limiter;
    }

    /// <summary>
    /// Spends one tenant's budget, or refuses the call before anything is allocated.
    /// </summary>
    /// <param name="tenantId">The resolved tenant.</param>
    /// <param name="ct">Cancels the store calls.</param>
    /// <returns>
    /// A permit that must be released when the flow finishes, or the refusal. The permit is
    /// empty — and releasing it does nothing — when no bulkhead is declared.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="tenantId"/> is null.</exception>
    public async ValueTask<Result<TenantPermit>> AcquireAsync(string tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        if (_fairness.PermitsPerWindow > 0)
        {
            var verdict = await SpendAsync(
                    PolicyKeys.TenantRate(tenantId),
                    _fairness.PermitsPerWindow,
                    _fairness.Window,
                    tenantId,
                    ct)
                .ConfigureAwait(false);

            if (verdict.IsFailure)
            {
                return Result.Fail<TenantPermit>(verdict.Error);
            }

            if (!verdict.Value.Admitted)
            {
                Refused(RateScope, tenantId);

                return Result.Fail<TenantPermit>(
                    TenantErrors.RateLimited(tenantId, verdict.Value.RetryAfter));
            }
        }

        if (_fairness.QuotaPerWindow > 0)
        {
            var verdict = await SpendAsync(
                    PolicyKeys.TenantQuota(tenantId),
                    _fairness.QuotaPerWindow,
                    _fairness.QuotaWindow,
                    tenantId,
                    ct)
                .ConfigureAwait(false);

            if (verdict.IsFailure)
            {
                return Result.Fail<TenantPermit>(verdict.Error);
            }

            if (!verdict.Value.Admitted)
            {
                Refused(QuotaScope, tenantId);

                return Result.Fail<TenantPermit>(
                    TenantErrors.QuotaExhausted(tenantId, verdict.Value.RetryAfter));
            }
        }

        if (_fairness.MaxConcurrency <= 0)
        {
            return Result.Ok(default(TenantPermit));
        }

        var pool = _pools.GetOrAdd(
            tenantId,
            static (_, max) => new TenantPool(max),
            _fairness.MaxConcurrency);

        // A counter rather than a semaphore, because there is nothing to wait on. docs/16 §4 is
        // explicit — "shed early, do not queue" — so the slot is either free now or this call is
        // refused now, and a synchronisation primitive built to make callers wait would only
        // offer an API nothing here may call.
        if (!pool.TryEnter())
        {
            Refused(ConcurrencyScope, tenantId);

            return Result.Fail<TenantPermit>(
                TenantErrors.Saturated(tenantId, _fairness.MaxConcurrency));
        }

        return Result.Ok(new TenantPermit(pool));
    }

    private async ValueTask<Result<RateLimitVerdict>> SpendAsync(
        string key,
        int permits,
        TimeSpan window,
        string tenantId,
        CancellationToken ct)
    {
        if (_limiter is null)
        {
            // Unreachable through a validated configuration, and refused rather than asserted:
            // a limiter that is not wired up must not read as a limit that passed, and a host
            // built by hand rather than by the container is a real caller.
            return Result.Fail<RateLimitVerdict>(TenantErrors.FairnessUnavailable(
                tenantId,
                new Error(
                    "tenant.limiter_missing",
                    "no IRateLimiterStore is registered.",
                    ErrorCategory.Internal)));
        }

        var verdict = await _limiter.TryAcquireAsync(key, permits, window, ct).ConfigureAwait(false);

        return verdict.IsFailure
            ? Result.Fail<RateLimitVerdict>(TenantErrors.FairnessUnavailable(tenantId, verdict.Error))
            : verdict;
    }

    private static void Refused(string scope, string tenantId) =>
        PolicyMetrics.RateLimitRefused(scope, tenantId);
}

/// <summary>One tenant's bulkhead slot, held for as long as the flow runs.</summary>
/// <remarks>
/// A struct over the semaphore rather than an <see cref="IDisposable"/> class, because an
/// admission that declares no bulkhead must allocate nothing at all: the default value holds no
/// semaphore and <see cref="Release"/> on it is a null check. <c>FlowHost</c> releases it in a
/// <c>finally</c>, so a flow that throws does not leak the slot — a leaked bulkhead permit is a
/// tenant that becomes permanently saturated, which is the starvation this exists to prevent
/// arriving through its own mechanism.
/// </remarks>
public readonly struct TenantPermit : IEquatable<TenantPermit>
{
    private readonly TenantPool? _pool;

    internal TenantPermit(TenantPool pool) => _pool = pool;

    /// <summary>Gives the slot back. Safe to call on a permit that holds none.</summary>
    public void Release() => _pool?.Exit();

    /// <inheritdoc />
    public bool Equals(TenantPermit other) => ReferenceEquals(_pool, other._pool);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TenantPermit other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _pool?.GetHashCode() ?? 0;

    /// <summary>Whether two permits hold the same pool.</summary>
    /// <param name="left">One permit.</param>
    /// <param name="right">The other.</param>
    /// <returns>True when both hold the same pool.</returns>
    public static bool operator ==(TenantPermit left, TenantPermit right) => left.Equals(right);

    /// <summary>Whether two permits hold different pools.</summary>
    /// <param name="left">One permit.</param>
    /// <param name="right">The other.</param>
    /// <returns>True when they do not hold the same pool.</returns>
    public static bool operator !=(TenantPermit left, TenantPermit right) => !left.Equals(right);
}

/// <summary>One tenant's slots on this node.</summary>
/// <remarks>
/// A compare-and-swap on one integer rather than a <see cref="SemaphoreSlim"/>. The pool is only
/// ever entered without waiting — <c>docs/16 §4</c>'s "shed early, do not queue" — so a
/// primitive whose purpose is to let callers wait would contribute a queue nothing may use and
/// a handle something must dispose. Bounded above rather than merely counted down, so a
/// double release cannot lift a tenant's ceiling.
/// </remarks>
internal sealed class TenantPool
{
    private readonly int _max;
    private int _held;

    public TenantPool(int max) => _max = max;

    /// <summary>How many slots are held right now. For the tests that assert the bound.</summary>
    public int Held => Volatile.Read(ref _held);

    /// <summary>Takes a slot if one is free, without waiting for one that is not.</summary>
    /// <returns>True when a slot was taken.</returns>
    public bool TryEnter()
    {
        var held = Volatile.Read(ref _held);

        while (held < _max)
        {
            var seen = Interlocked.CompareExchange(ref _held, held + 1, held);

            if (seen == held)
            {
                return true;
            }

            held = seen;
        }

        return false;
    }

    /// <summary>Gives a slot back.</summary>
    public void Exit() => Interlocked.Decrement(ref _held);
}
