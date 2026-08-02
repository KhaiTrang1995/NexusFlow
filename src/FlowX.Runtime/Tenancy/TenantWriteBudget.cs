using System.Collections.Concurrent;
using FlowX.Observability;

namespace FlowX.Runtime;

/// <summary>
/// <c>docs/16 §4</c>'s sixth fairness mechanism: how many journal rows one tenant may write,
/// counted across the fleet and spent a block at a time.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Neither of the two obvious shapes is correct, and this is the third.</strong>
/// Spending a shared budget on every commit puts a limiter round trip in front of the write it
/// exists to protect, which roughly doubles the latency of the thing being defended. Keeping the
/// budget per process is the anti-conservative limiter
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0040-a-rate-limit-is-shared-or-it-is-not-a-rate-limit.md">ADR-0040</a>
/// refuses — n nodes admit n × the declared rate — and for a write budget that means the store
/// it names is not protected at all. What is built here draws
/// <see cref="TenantFairness.JournalWriteBlock"/> rows of credit from the shared bucket in one
/// call and then spends them locally, one per row. Every credit was removed from the one shared
/// bucket before it could be spent, so the fleet never writes more per window than was declared;
/// what varies with the node count is only how much declared budget goes <em>unspent</em>, which
/// is the conservative direction. See
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0055-a-write-budget-is-drawn-in-blocks-and-paces-rather-than-refuses.md">ADR-0055</a>.
/// </para>
/// <para>
/// <strong>It is not a cache in front of the limiter, which is what that record refuses as an
/// optimisation.</strong> A cache lets two nodes each answer <em>yes</em> from one token; a draw
/// takes the tokens out of the bucket and gives them to exactly one node, which can then spend
/// each of them once. The distinction is the whole argument, and it is why this is a block size
/// and never a time-to-live.
/// </para>
/// <para>
/// <strong>Exhaustion paces; it does not refuse.</strong> The other three bounds are spent at
/// admission, where a refusal costs the caller nothing that had started. A journal write happens
/// in the middle of a durable flow, and refusing one there does not apply backpressure — it
/// abandons a saga halfway with its compensations unrun, which is the reason
/// <c>FlowHost.AdmitAsync</c> already exempts a continuation from the stage-1 bounds. So a
/// tenant over its budget waits for credit to accrue, bounded by the caller's cancellation
/// token, and the flow completes late rather than not at all.
/// </para>
/// <para>
/// <strong>Nothing here is reached by a deployment that did not ask for it.</strong>
/// <c>FlowHost</c> branches on <see cref="TenantIsolation.None"/> and on
/// <see cref="TenantFairness.BoundsJournalWrites"/> before this type exists, and it wraps the
/// journal only where both hold — so a single-tenant deployment, and a multi-tenant one that
/// declares no write budget, reach the store by exactly the call they always did.
/// </para>
/// </remarks>
public sealed class TenantWriteBudget
{
    /// <summary>The <c>scope</c> label a paced tenant is counted under.</summary>
    /// <remarks>
    /// <c>flowx_ratelimit_rejected_total{scope,tenant}</c>, as the other three bounds report —
    /// one series with four scope values rather than four instruments and four dashboards. It
    /// counts a <em>draw</em> the shared bucket refused, which is the moment a tenant starts
    /// waiting, and not each row that waits behind it.
    /// </remarks>
    public const string Scope = "TenantJournalWrites";

    private readonly TenantFairness _fairness;
    private readonly IRateLimiterStore? _limiter;
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<string, TenantCredit> _credit = new(StringComparer.Ordinal);

    /// <summary>Builds a write budget over one deployment's declared bound.</summary>
    /// <param name="fairness">What the deployment bounds each tenant to.</param>
    /// <param name="limiter">
    /// The shared bucket blocks are drawn from. May be null only when no budget is declared;
    /// the startup validator is what makes that true.
    /// </param>
    /// <param name="clock">What a paced tenant waits through.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="fairness"/> or <paramref name="clock"/> is null.
    /// </exception>
    public TenantWriteBudget(TenantFairness fairness, IRateLimiterStore? limiter, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(fairness);
        ArgumentNullException.ThrowIfNull(clock);

        _fairness = fairness;
        _limiter = limiter;
        _clock = clock;
    }

    /// <summary>Binds a journal to one tenant's write budget.</summary>
    /// <param name="journal">The journal this execution would otherwise commit to.</param>
    /// <param name="tenantId">Whose budget its rows are spent against.</param>
    /// <returns>
    /// The journal, charged. The one it was given when no budget is declared or no tenant was
    /// resolved, so the wrapper is absent rather than inert.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="journal"/> is null.</exception>
    public IFlowJournal Bind(IFlowJournal journal, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(journal);

        return _fairness.BoundsJournalWrites && tenantId is { Length: > 0 } tenant
            ? new BudgetedJournal(journal, tenant, this)
            : journal;
    }

    /// <summary>
    /// Spends one row of one tenant's budget, waiting for credit when it has none left.
    /// </summary>
    /// <param name="tenantId">Whose budget the row is written against.</param>
    /// <param name="ct">Bounds the wait, and is the only thing that does.</param>
    /// <returns>
    /// <c>null</c> once the row may be written, or the refusal when the shared bucket could not
    /// be consulted at all.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="tenantId"/> is null.</exception>
    public async ValueTask<Error?> SpendAsync(string tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        var credit = _credit.GetOrAdd(tenantId, static _ => new TenantCredit());

        while (!credit.TrySpend(_clock.UtcNow))
        {
            if (await DrawAsync(credit, tenantId, ct).ConfigureAwait(false) is { } refusal)
            {
                return refusal;
            }
        }

        return null;
    }

    /// <summary>
    /// Takes one block out of the shared bucket, waiting for it if the bucket is empty.
    /// </summary>
    /// <remarks>
    /// Serialised per tenant, so that a hundred rows arriving at an exhausted node produce one
    /// draw and not a hundred. That is not only a saving: an unserialised burst would take a
    /// hundred blocks out of the shared bucket to satisfy a hundred rows, spend a block's worth
    /// and lose the rest, which turns the amortisation into waste at exactly the moment the
    /// tenant is busiest.
    /// </remarks>
    private async ValueTask<Error?> DrawAsync(
        TenantCredit credit,
        string tenantId,
        CancellationToken ct)
    {
        if (_limiter is null)
        {
            // Unreachable through a validated configuration, and refused rather than asserted,
            // for TenantAdmissionControl's reason: a budget that is not wired up must not read
            // as a budget that was spent.
            return TenantErrors.FairnessUnavailable(
                tenantId,
                new Error(
                    "tenant.limiter_missing",
                    "no IRateLimiterStore is registered.",
                    ErrorCategory.Internal));
        }

        await credit.Gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            while (!credit.HasCredit(_clock.UtcNow))
            {
                var verdict = await _limiter
                    .TryAcquireAsync(
                        PolicyKeys.TenantWrites(tenantId),
                        _fairness.JournalWriteBlocksPerWindow,
                        _fairness.JournalWriteWindow,
                        ct)
                    .ConfigureAwait(false);

                if (verdict.IsFailure)
                {
                    return TenantErrors.FairnessUnavailable(tenantId, verdict.Error);
                }

                if (verdict.Value.Admitted)
                {
                    var now = _clock.UtcNow;

                    credit.Grant(_fairness.JournalWriteBlock, now, now + _fairness.JournalWriteWindow);

                    return null;
                }

                PolicyMetrics.RateLimitRefused(Scope, tenantId);

                await _clock.DelayAsync(Pace(verdict.Value.RetryAfter), ct).ConfigureAwait(false);
            }

            return null;
        }
        finally
        {
            credit.Gate.Release();
        }
    }

    /// <summary>How long to wait before asking the bucket again.</summary>
    /// <remarks>
    /// Clamped below because a store reporting no wait would turn pacing into a spin against the
    /// limiter, and clamped above because a token bucket refills continuously — waiting longer
    /// than the window cannot make more credit available than waiting one window does.
    /// </remarks>
    private TimeSpan Pace(TimeSpan retryAfter)
    {
        var window = _fairness.JournalWriteWindow;

        if (retryAfter <= TimeSpan.Zero)
        {
            return window;
        }

        return retryAfter > window ? window : retryAfter;
    }

    /// <summary>
    /// One tenant's unspent credit on this node, and the gate that keeps its draws to one.
    /// </summary>
    /// <remarks>
    /// <strong>Credit expires with the window it was drawn in, and that is what stops it
    /// banking.</strong> Without an expiry a node that drew a block and then went quiet would
    /// hold it indefinitely and could spend a whole block at any later instant, so a fleet of
    /// n nodes could burst n blocks above the declared pace after an arbitrarily long silence.
    /// Expiring it bounds the burst to one window's worth and puts the error where every other
    /// error in this type is — on the side of writing less than was declared, never more.
    /// </remarks>
    private sealed class TenantCredit
    {
        private readonly object _sync = new();
        private int _remaining;
        private DateTimeOffset _expiresAt;

        /// <summary>Admits one draw at a time for this tenant.</summary>
        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>Takes one row of credit if there is unexpired credit to take.</summary>
        /// <param name="now">The instant to measure expiry against.</param>
        /// <returns>True when a row may be written.</returns>
        public bool TrySpend(DateTimeOffset now)
        {
            lock (_sync)
            {
                if (now >= _expiresAt)
                {
                    _remaining = 0;

                    return false;
                }

                if (_remaining == 0)
                {
                    return false;
                }

                _remaining--;

                return true;
            }
        }

        /// <summary>Whether unexpired credit is available, without taking any.</summary>
        /// <param name="now">The instant to measure expiry against.</param>
        /// <returns>True when a caller behind the gate no longer needs to draw.</returns>
        public bool HasCredit(DateTimeOffset now)
        {
            lock (_sync)
            {
                return _remaining > 0 && now < _expiresAt;
            }
        }

        /// <summary>Records a block the shared bucket has already given up.</summary>
        /// <param name="rows">The block size.</param>
        /// <param name="now">The instant the block was drawn at.</param>
        /// <param name="expiresAt">When what is left of it stops counting.</param>
        /// <remarks>
        /// Credit that has not expired is added to rather than replaced, so a block is never
        /// silently dropped; credit that has expired is discarded, so a window's leftovers
        /// cannot be spent in the next one.
        /// </remarks>
        public void Grant(int rows, DateTimeOffset now, DateTimeOffset expiresAt)
        {
            lock (_sync)
            {
                _remaining = now >= _expiresAt ? rows : _remaining + rows;
                _expiresAt = expiresAt;
            }
        }
    }
}

/// <summary>
/// A journal that spends one row of its tenant's write budget before each row it writes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A decorator rather than a check inside the engine, so that "a deployment without one
/// pays nothing" is structural.</strong> The engine holds an <see cref="IFlowJournal"/> and
/// commits through it; a host that declares no write budget hands it the store's own journal and
/// there is no branch on the commit path to be predicted, no field to read and nothing to
/// disable. It is the bargain <c>ExecutionPlan.HasStepPolicies</c> struck for the step loop,
/// made one layer up.
/// </para>
/// <para>
/// <strong>Three members are charged and three are not.</strong> The budget counts rows written:
/// opening an instance, committing a step boundary and coming to rest. Reads are free because
/// they are not what a write budget protects, and <c>FenceAsync</c> is free because it is one
/// statement a node issues to <em>take over</em> an instance — charging it would let an
/// exhausted tenant's recovery wait on the budget that its stuck instances are what filled.
/// </para>
/// </remarks>
internal sealed class BudgetedJournal : IFlowJournal
{
    private readonly IFlowJournal _inner;
    private readonly string _tenantId;
    private readonly TenantWriteBudget _budget;

    public BudgetedJournal(IFlowJournal inner, string tenantId, TenantWriteBudget budget)
    {
        _inner = inner;
        _tenantId = tenantId;
        _budget = budget;
    }

    /// <inheritdoc />
    public async ValueTask<Result<FlowInstanceRecord>> StartAsync(
        FlowInstanceStart start,
        CancellationToken cancellationToken)
    {
        return await _budget.SpendAsync(_tenantId, cancellationToken).ConfigureAwait(false) is { } refusal
            ? Result.Fail<FlowInstanceRecord>(refusal)
            : await _inner.StartAsync(start, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<Result<FencingToken>> FenceAsync(
        Guid instanceId,
        FencingToken token,
        CancellationToken cancellationToken) =>
        _inner.FenceAsync(instanceId, token, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<Result<JournalStep>> CommitAsync(
        StepCommit commit,
        CancellationToken cancellationToken)
    {
        return await _budget.SpendAsync(_tenantId, cancellationToken).ConfigureAwait(false) is { } refusal
            ? Result.Fail<JournalStep>(refusal)
            : await _inner.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<Result<FlowInstanceRecord>> CompleteAsync(
        Guid instanceId,
        FencingToken token,
        FlowInstanceState state,
        JournalPayload stateBag,
        FlowWake? wake,
        CancellationToken cancellationToken)
    {
        return await _budget.SpendAsync(_tenantId, cancellationToken).ConfigureAwait(false) is { } refusal
            ? Result.Fail<FlowInstanceRecord>(refusal)
            : await _inner
                .CompleteAsync(instanceId, token, state, stateBag, wake, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<Result<FlowInstanceRecord>> ReadInstanceAsync(
        Guid instanceId,
        CancellationToken cancellationToken) =>
        _inner.ReadInstanceAsync(instanceId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<Result<ResumeFrontier>> ReadResumeFrontierAsync(
        Guid instanceId,
        CancellationToken cancellationToken) =>
        _inner.ReadResumeFrontierAsync(instanceId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<Result<IReadOnlyList<OutboxRecord>>> ReadOutboxAsync(
        Guid instanceId,
        CancellationToken cancellationToken) =>
        _inner.ReadOutboxAsync(instanceId, cancellationToken);
}
