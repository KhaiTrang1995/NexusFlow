using Shouldly;
using Xunit;

namespace FlowX.Conformance;

/// <summary>
/// What an <see cref="IQuotaStore"/> must do. Derive, supply a store, and the whole suite runs
/// against it unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two assertions are the reason this seam is not
/// <see cref="IRateLimiterStore"/> with a longer window.</strong>
/// <see cref="TwoClientsOverOneServerShareOneBudget"/> is the one it inherits from that suite —
/// a budget counted in a process is a plan limit times the replica count — and
/// <see cref="AnExhaustedBudgetStaysExhaustedInsideItsPeriod"/> is the one that is new: a token
/// bucket admits again a fraction of a second after it is spent, which is correct for smoothing
/// a burst and wrong for a plan. <c>TenantFairness.QuotaPerWindow</c>'s remarks name that gap in
/// as many words; this file is where it is closed.
/// </para>
/// <para>
/// <strong>And one is the reason the policy exists at all.</strong>
/// <see cref="BudgetsOnDifferentKeysDoNotInterfere"/> is tenant fairness reduced to its
/// mechanism: the scope's identity is in the key, so one holder exhausting its plan refuses only
/// itself. A store that shared a counter across keys would pass every other assertion here and
/// would let the noisiest tenant spend everybody's budget.
/// </para>
/// <para>
/// The period assertions run in real time, for <see cref="RateLimiterConformance"/>'s reason: a
/// store whose window is enforced by PostgreSQL's <c>now()</c> has no clock to inject, and
/// testing the one store that does would prove nothing about the ones that matter.
/// <see cref="Period"/> is short and a store with coarser granularity overrides it.
/// </para>
/// </remarks>
public abstract class QuotaStoreConformance
{
    /// <summary>A fresh store over an empty key space. Called once per test.</summary>
    protected abstract ValueTask<QuotaStoreUnderTest> CreateAsync();

    /// <summary>The period the reset assertions use. Short, so the suite stays quick.</summary>
    protected virtual TimeSpan Period => TimeSpan.FromSeconds(1);

    /// <summary>A period long enough that nothing resets inside a test that is not about reset.</summary>
    protected virtual TimeSpan LongPeriod => TimeSpan.FromHours(1);

    /// <summary>The ambient test cancellation token.</summary>
    protected static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A key nothing else in this run uses.</summary>
    protected static string FreshKey() => $"conformance:quota:{Guid.NewGuid():n}";

    // ---------------------------------------------------------------- it admits and refuses

    /// <summary>A budget admits exactly its calls and then refuses.</summary>
    /// <remarks>
    /// Three rather than one, so the test distinguishes "the store counts" from "the store
    /// refuses everything" — a quota that always said no would pass a one-call version of this
    /// on its second call and would take every policed step out of service.
    /// </remarks>
    [Fact]
    public async Task ABudgetAdmitsItsCallsAndThenRefuses()
    {
        await using var store = await CreateAsync();
        var key = FreshKey();

        for (var i = 1; i <= 3; i++)
        {
            var verdict = await store.Quota.TryConsumeAsync(key, 3, LongPeriod, Cancellation);

            ShouldSucceed(verdict, $"call {i} of three.");

            verdict.Value.Admitted.ShouldBeTrue(
                $"A budget of three was declared and this is call {i}. A store that refused " +
                "inside its own budget would bill a tenant for a plan it cannot spend.");
        }

        var refused = await store.Quota.TryConsumeAsync(key, 3, LongPeriod, Cancellation);

        ShouldSucceed(refused, "a refusal is a successful call with a negative answer.");

        refused.Value.Admitted.ShouldBeFalse("the fourth call inside one period, against three.");
    }

    /// <summary>A refusal says when the budget comes back, and it is inside the period.</summary>
    /// <remarks>
    /// The bound is the whole period rather than a fraction of it, which is the shape that
    /// differs from a limiter's: a fixed window grants nothing until it turns over, so the wait
    /// is the remainder of the current window and can legitimately be nearly all of it. What it
    /// may never be is longer, because that would send a caller away past a boundary at which
    /// the budget is already granted.
    /// </remarks>
    [Fact]
    public async Task ARefusalCarriesTheRemainderOfThePeriod()
    {
        await using var store = await CreateAsync();
        var key = FreshKey();

        ShouldSucceed(
            await store.Quota.TryConsumeAsync(key, 1, LongPeriod, Cancellation),
            "the only call in the period.");

        var refused = await store.Quota.TryConsumeAsync(key, 1, LongPeriod, Cancellation);

        ShouldSucceed(refused, "and the second is refused.");

        refused.Value.Admitted.ShouldBeFalse();

        refused.Value.RetryAfter.ShouldBeGreaterThan(
            TimeSpan.Zero,
            "a refusal with no wait tells the caller to come back immediately, which against a " +
            "long-window budget is a hot loop that lasts until the period turns over.");

        refused.Value.RetryAfter.ShouldBeLessThanOrEqualTo(
            LongPeriod,
            "the wait is the remainder of the window the call fell in, so it is never longer " +
            "than a whole window.");
    }

    /// <summary>An admission reports no wait.</summary>
    [Fact]
    public async Task AnAdmissionCarriesNoWait()
    {
        await using var store = await CreateAsync();

        var admitted = await store.Quota.TryConsumeAsync(FreshKey(), 2, LongPeriod, Cancellation);

        ShouldSucceed(admitted, "an unspent budget admits.");

        admitted.Value.Admitted.ShouldBeTrue();

        admitted.Value.RetryAfter.ShouldBe(
            TimeSpan.Zero,
            "an admitted caller is not being asked to come back, and a non-zero wait here " +
            "would be rendered as a Retry-After on a response that succeeded.");
    }

    /// <summary>Budgets are per key, which is what makes the scope mean anything.</summary>
    /// <remarks>
    /// <strong>This is tenant fairness, reduced to the one thing a store has to get right.</strong>
    /// The caller puts the scope's identity in the key — <c>docs/16 §4</c> — and a store that
    /// counted across keys would make a per-tenant plan a shared ceiling the noisiest tenant
    /// spends first.
    /// </remarks>
    [Fact]
    public async Task BudgetsOnDifferentKeysDoNotInterfere()
    {
        await using var store = await CreateAsync();
        var first = FreshKey();
        var second = FreshKey();

        ShouldSucceed(
            await store.Quota.TryConsumeAsync(first, 1, LongPeriod, Cancellation),
            "the first holder's only call.");

        var other = await store.Quota.TryConsumeAsync(second, 1, LongPeriod, Cancellation);

        ShouldSucceed(other, "a different key is a different budget.");

        other.Value.Admitted.ShouldBeTrue(
            "One counter for every key would make a per-tenant quota a global one, so a tenant " +
            "that exhausted its plan would refuse every other tenant's calls too.");
    }

    /// <summary>The remaining count falls as the budget is spent.</summary>
    /// <remarks>
    /// Advisory rather than exact, for <c>RateLimitVerdict.Remaining</c>'s reason — another node
    /// may have spent one by the time a caller reads it. What it rules out is a store answering
    /// a constant, which reads as an untouched plan on every dashboard.
    /// </remarks>
    [Fact]
    public async Task RemainingFallsAsTheBudgetIsSpent()
    {
        await using var store = await CreateAsync();
        var key = FreshKey();

        var first = await store.Quota.TryConsumeAsync(key, 3, LongPeriod, Cancellation);
        var second = await store.Quota.TryConsumeAsync(key, 3, LongPeriod, Cancellation);

        ShouldSucceed(first, "the first call.");
        ShouldSucceed(second, "the second.");

        second.Value.Remaining.ShouldBeLessThan(
            first.Value.Remaining,
            "a store reporting a constant would publish a plan that is never consumed.");

        second.Value.Remaining.ShouldBeGreaterThanOrEqualTo(
            0, "the count is what is left, and there is never less than none left.");
    }

    // ------------------------------------------------------------------ the window is fixed

    /// <summary>An exhausted budget stays exhausted for the rest of its period.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The assertion that separates a quota from a rate limit, and the reason this
    /// contract is not <see cref="IRateLimiterStore"/> with a longer window.</strong> A token
    /// bucket refills continuously, so a spent budget admits again after one budget-th of the
    /// period — for a month that is a caller admitted a fraction of a second later at one
    /// thirty-millionth of the plan per second, which is
    /// <c>TenantFairness.QuotaPerWindow</c>'s documented limitation. A fixed window grants
    /// nothing until it turns over, and that is what this checks.
    /// </para>
    /// <para>
    /// Written against <see cref="LongPeriod"/> so the window cannot turn over during the test,
    /// and with several calls rather than one so a store that admitted every other caller would
    /// still be caught.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnExhaustedBudgetStaysExhaustedInsideItsPeriod()
    {
        await using var store = await CreateAsync();
        var key = FreshKey();

        ShouldSucceed(
            await store.Quota.TryConsumeAsync(key, 1, LongPeriod, Cancellation),
            "the only call the plan grants.");

        for (var i = 0; i < 5; i++)
        {
            var refused = await store.Quota.TryConsumeAsync(key, 1, LongPeriod, Cancellation);

            ShouldSucceed(refused, "the store answered.");

            refused.Value.Admitted.ShouldBeFalse(
                "A budget that trickles back inside its period is a token bucket, and a plan " +
                "limit enforced by one is a plan a tenant can exceed by waiting a second " +
                "between calls.");
        }
    }

    /// <summary>The budget is granted again when the period turns over.</summary>
    /// <remarks>
    /// The other half. A store that refused for ever after the budget was spent would take a
    /// step out of service permanently on the first busy period, and every assertion above
    /// would still pass.
    /// </remarks>
    [Fact]
    public async Task AnExhaustedBudgetIsGrantedAgainWhenThePeriodTurnsOver()
    {
        await using var store = await CreateAsync();
        var key = FreshKey();

        ShouldSucceed(
            await store.Quota.TryConsumeAsync(key, 1, Period, Cancellation),
            "the only call in this period.");

        var refused = await store.Quota.TryConsumeAsync(key, 1, Period, Cancellation);

        refused.Value.Admitted.ShouldBeFalse("and the budget is spent.");

        var admitted = await AdmittedWithinAsync(store.Quota, key, 1, Period, Period * 8);

        admitted.ShouldBeTrue(
            $"The period is {Period} and the budget was still refusing eight periods later. A " +
            "plan limit that never resets is a step taken out of service on its first busy " +
            "period.");
    }

    // ---------------------------------------------------------------- it is actually shared

    /// <summary>Two independently constructed clients over one server draw on one budget.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The assertion the seam exists for, inherited from
    /// <see cref="RateLimiterConformance"/> and no weaker here.</strong> Everything above holds
    /// for a counter in a field. This does not: the clients share nothing but the server, so a
    /// store keeping its count locally admits the second client's caller — exactly as a fleet of
    /// n nodes would grant n × the plan, which is a commercial promise the deployment cannot
    /// keep and which every node's own numbers report as correct.
    /// </para>
    /// <para>
    /// The budget is one call, so there is no arithmetic to get wrong.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TwoClientsOverOneServerShareOneBudget()
    {
        await using var store = await CreateAsync();
        var key = FreshKey();

        var mine = await store.Quota.TryConsumeAsync(key, 1, LongPeriod, Cancellation);

        ShouldSucceed(mine, "this client spends the only call.");
        mine.Value.Admitted.ShouldBeTrue();

        var theirs = await store.SecondClient.TryConsumeAsync(key, 1, LongPeriod, Cancellation);

        ShouldSucceed(theirs, "the other client asks for one too.");

        theirs.Value.Admitted.ShouldBeFalse(
            "One call was budgeted and another client already made it. A store that admits " +
            "here is counting in a process, so a fleet of n nodes grants n × the plan.");
    }

    /// <summary>Concurrent callers over one budget are admitted exactly to the budget.</summary>
    /// <remarks>
    /// The atomicity half. A store that read the counter, decided and then wrote it back would
    /// pass every test above and over-grant under contention — and a quota's contention is a
    /// tenant's whole fleet arriving at once, which is precisely the traffic a plan limit
    /// exists to bound. Split across both clients, so the race is between two connections
    /// rather than inside one client's pipelining.
    /// </remarks>
    [Fact]
    public async Task ConcurrentCallersAreAdmittedExactlyToTheBudget()
    {
        await using var store = await CreateAsync();
        var key = FreshKey();

        const int Budget = 5;
        const int Callers = 40;

        var attempts = new Task<Result<QuotaVerdict>>[Callers];

        for (var i = 0; i < Callers; i++)
        {
            var quota = i % 2 == 0 ? store.Quota : store.SecondClient;

            attempts[i] = Task.Run(
                async () => await quota.TryConsumeAsync(key, Budget, LongPeriod, Cancellation),
                Cancellation);
        }

        var verdicts = await Task.WhenAll(attempts);

        foreach (var verdict in verdicts)
        {
            ShouldSucceed(verdict, "every caller got an answer.");
        }

        verdicts.Count(static v => v.Value.Admitted).ShouldBe(
            Budget,
            $"{Callers} callers raced for a budget of {Budget} across two clients. Any other " +
            "number means the read and the write are separable, and a plan limit whose " +
            "decision and consumption can be interleaved is one a burst walks straight past.");
    }

    // ----------------------------------------------------------------------- it is refused

    /// <summary>A store that cannot be reached answers with an error, not an exception.</summary>
    /// <remarks>
    /// The engine turns this into a refusal, which it can only do if it is a value —
    /// <see cref="RateLimiterConformance"/>'s argument, and the same one: a store that threw
    /// would put a policy decision on the defect path, and a caller that caught it would be one
    /// edit away from admitting on doubt.
    /// </remarks>
    [Fact]
    public async Task AnUnreachableStoreAnswersWithAnErrorRatherThanThrowing()
    {
        await using var store = await CreateAsync();
        var unreachable = await store.UnreachableAsync(Cancellation);

        var verdict = await unreachable.TryConsumeAsync(FreshKey(), 1, LongPeriod, Cancellation);

        verdict.IsFailure.ShouldBeTrue(
            "the store is not there, so it did not decide anything — which is a different " +
            "thing from deciding no, and the engine has to be able to tell.");
    }

    // --------------------------------------------------------------------------- helpers

    /// <summary>Polls until the period turns over and the budget admits, or gives up.</summary>
    private static async Task<bool> AdmittedWithinAsync(
        IQuotaStore quota,
        string key,
        int budget,
        TimeSpan period,
        TimeSpan giveUpAfter)
    {
        var deadline = DateTimeOffset.UtcNow + giveUpAfter;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), Cancellation);

            var verdict = await quota.TryConsumeAsync(key, budget, period, Cancellation);

            if (verdict.IsSuccess && verdict.Value.Admitted)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Asserts a store call answered, printing its own refusal if not.</summary>
    protected static void ShouldSucceed<T>(Result<T> result, string because) =>
        result.IsSuccess.ShouldBeTrue(
            $"{because} The store failed instead: {(result.IsFailure ? result.Error.ToString() : "no error")}");
}

/// <summary>
/// The two clients and the failure case a quota suite needs, supplied by the implementer.
/// </summary>
/// <remarks>
/// <see cref="RateLimiterUnderTest"/>'s shape, and for its reason: what "a second client" and
/// "unreachable" mean is the store's business, and what the suite asserts about them is the
/// contract's.
/// </remarks>
public abstract class QuotaStoreUnderTest : IAsyncDisposable
{
    /// <summary>The store under test.</summary>
    public abstract IQuotaStore Quota { get; }

    /// <summary>
    /// A second store, independently constructed, over the same server and key space.
    /// </summary>
    /// <remarks>
    /// <strong>It must share nothing with <see cref="Quota"/> but the server.</strong> Returning
    /// <see cref="Quota"/> itself would make
    /// <c>TwoClientsOverOneServerShareOneBudget</c> pass vacuously and would remove the only
    /// reason this seam is a plugin contract.
    /// </remarks>
    public abstract IQuotaStore SecondClient { get; }

    /// <summary>A store of the same kind, pointed at a server that will not answer.</summary>
    /// <param name="cancellationToken">Cancels the construction.</param>
    /// <returns>The store. Disposed with this harness.</returns>
    public abstract ValueTask<IQuotaStore> UnreachableAsync(CancellationToken cancellationToken);

    /// <summary>Releases whatever the harness opened.</summary>
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
