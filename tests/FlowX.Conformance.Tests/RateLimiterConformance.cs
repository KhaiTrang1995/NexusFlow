using Shouldly;
using Xunit;

namespace FlowX.Conformance;

/// <summary>
/// What an <see cref="IRateLimiterStore"/> must do. Derive, supply a store, and the whole suite
/// runs against it unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One assertion in this file is the reason the seam exists at all, and every other one
/// is scaffolding around it.</strong>
/// <see cref="TwoClientsOverOneServerShareOneBudget"/> is what separates a rate limiter from a
/// counter in a process. A circuit breaker that is per process is <em>slower to protect</em> and
/// never wrong; a rate limiter that is per process is <em>anti-conservative</em> — n nodes admit
/// n × the declared rate, the factor is the replica count, and nothing declares it or reports
/// it. A store that passes every other test here and fails that one is exactly the
/// implementation
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0035-a-rate-limit-is-shared-or-it-is-not-a-rate-limit.md">ADR-0035</a>
/// refuses.
/// </para>
/// <para>
/// <strong>Two clients rather than two processes, and the difference is worth stating.</strong>
/// A test process cannot fork; what it can do is construct two clients that share nothing but
/// the server, which is the whole of what two nodes share. For Redis that is two multiplexers,
/// for PostgreSQL two connection sources, and for the reference double a second object over one
/// backing store. Anything a client caches locally — a token count, a window start — is
/// invisible to the second client, so a limiter that cached would fail this exactly as a second
/// process would.
/// </para>
/// <para>
/// <strong>Refill is tested in real time, deliberately</strong>, for
/// <see cref="LeaseStoreConformance"/>'s reason: a store whose window is enforced by Redis's
/// <c>TIME</c> or PostgreSQL's <c>now()</c> has no clock to inject, and testing the one store
/// that does would prove nothing about the two that matter. <see cref="Window"/> is short and a
/// store with coarser granularity overrides it.
/// </para>
/// </remarks>
public abstract class RateLimiterConformance
{
    /// <summary>A fresh limiter over an empty key space. Called once per test.</summary>
    protected abstract ValueTask<RateLimiterUnderTest> CreateAsync();

    /// <summary>The window the refill assertions use. Short, so the suite stays quick.</summary>
    protected virtual TimeSpan Window => TimeSpan.FromMilliseconds(500);

    /// <summary>A window long enough that nothing refills inside a test that is not about refill.</summary>
    protected virtual TimeSpan LongWindow => TimeSpan.FromMinutes(5);

    /// <summary>The ambient test cancellation token.</summary>
    protected static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A key nothing else in this run uses.</summary>
    protected static string FreshKey() => $"conformance:{Guid.NewGuid():n}";

    // ------------------------------------------------------------------ it admits and refuses

    /// <summary>A bucket admits exactly its permits and then refuses.</summary>
    /// <remarks>
    /// Three permits rather than one, so the test distinguishes "the store counts" from "the
    /// store refuses everything" — a limiter that always said no would pass a one-permit version
    /// of this on its second call and would be catastrophically wrong.
    /// </remarks>
    [Fact]
    public async Task ABucketAdmitsItsPermitsAndThenRefuses()
    {
        await using var store = await CreateAsync();
        var key = FreshKey();

        for (var i = 1; i <= 3; i++)
        {
            var verdict = await store.Limiter.TryAcquireAsync(key, 3, LongWindow, Cancellation);

            ShouldSucceed(verdict, $"permit {i} of three.");

            verdict.Value.Admitted.ShouldBeTrue(
                $"Three permits were declared and this is caller {i}. A store that refused " +
                "inside its own budget would make a declared limit mean less than it says, " +
                "which is the one direction an operator cannot detect from traffic.");
        }

        var refused = await store.Limiter.TryAcquireAsync(key, 3, LongWindow, Cancellation);

        ShouldSucceed(refused, "a refusal is a successful call with a negative answer.");

        refused.Value.Admitted.ShouldBeFalse(
            "the fourth caller inside one window, against a budget of three.");
    }

    /// <summary>A refusal says when to come back, and the wait is inside the window.</summary>
    /// <remarks>
    /// <c>docs/10-Policy-Framework.md</c> §3 specifies <c>RateLimit</c> as "token bucket;
    /// returns 429 + <c>Retry-After</c>", and this is the half a store supplies. Bounded above
    /// by the window because a token bucket refills continuously: the wait for one token is at
    /// most the time one token takes to accrue, which is never longer than the whole window.
    /// A store answering a longer wait is either not refilling or is reporting the window rather
    /// than the wait, and both send a caller away for longer than the budget requires.
    /// </remarks>
    [Fact]
    public async Task ARefusalCarriesAWaitInsideTheWindow()
    {
        await using var store = await CreateAsync();
        var key = FreshKey();

        ShouldSucceed(
            await store.Limiter.TryAcquireAsync(key, 1, LongWindow, Cancellation),
            "the only permit.");

        var refused = await store.Limiter.TryAcquireAsync(key, 1, LongWindow, Cancellation);

        ShouldSucceed(refused, "and the second caller is refused.");

        refused.Value.Admitted.ShouldBeFalse();

        refused.Value.RetryAfter.ShouldBeGreaterThan(
            TimeSpan.Zero,
            "a refusal with no wait tells the caller to come back immediately, which is a " +
            "refusal that produces a hot loop.");

        refused.Value.RetryAfter.ShouldBeLessThanOrEqualTo(
            LongWindow,
            "a token bucket refills continuously, so the wait for one token is never longer " +
            "than the window the whole budget accrues over.");
    }

    /// <summary>An admission reports a wait of zero.</summary>
    [Fact]
    public async Task AnAdmissionCarriesNoWait()
    {
        await using var store = await CreateAsync();

        var admitted = await store.Limiter.TryAcquireAsync(FreshKey(), 2, LongWindow, Cancellation);

        ShouldSucceed(admitted, "an empty bucket admits.");

        admitted.Value.Admitted.ShouldBeTrue();

        admitted.Value.RetryAfter.ShouldBe(
            TimeSpan.Zero,
            "an admitted caller is not being asked to come back; a non-zero wait here would be " +
            "rendered as a Retry-After on a response that succeeded.");
    }

    /// <summary>Budgets are per key.</summary>
    /// <remarks>
    /// The obvious over-correction, and the one that would quietly serialise a whole deployment
    /// behind one bucket — <c>docs/10 §11</c>'s "rate limiting only globally: one tenant starves
    /// the rest", arriving from the store rather than from the declaration.
    /// </remarks>
    [Fact]
    public async Task BudgetsOnDifferentKeysDoNotInterfere()
    {
        await using var store = await CreateAsync();
        var first = FreshKey();
        var second = FreshKey();

        ShouldSucceed(
            await store.Limiter.TryAcquireAsync(first, 1, LongWindow, Cancellation),
            "the first key's only permit.");

        var other = await store.Limiter.TryAcquireAsync(second, 1, LongWindow, Cancellation);

        ShouldSucceed(other, "a different key is a different budget.");

        other.Value.Admitted.ShouldBeTrue(
            "One bucket for every key would make a per-tenant declaration a global limit, " +
            "which is the anti-pattern the scope exists to avoid.");
    }

    /// <summary>The remaining count falls as permits are taken.</summary>
    /// <remarks>
    /// Advisory rather than exact — another node may have taken one by the time a caller reads
    /// it — so the assertion is monotonic rather than a value. What it rules out is a store that
    /// answers a constant, which reads as a healthy budget on every dashboard.
    /// </remarks>
    [Fact]
    public async Task RemainingFallsAsPermitsAreTaken()
    {
        await using var store = await CreateAsync();
        var key = FreshKey();

        var first = await store.Limiter.TryAcquireAsync(key, 3, LongWindow, Cancellation);
        var second = await store.Limiter.TryAcquireAsync(key, 3, LongWindow, Cancellation);

        ShouldSucceed(first, "the first permit.");
        ShouldSucceed(second, "the second.");

        second.Value.Remaining.ShouldBeLessThan(
            first.Value.Remaining,
            "a store reporting a constant would publish a budget that never moves.");

        second.Value.Remaining.ShouldBeGreaterThanOrEqualTo(
            0, "the count is what is left, and there is never less than none left.");
    }

    // -------------------------------------------------------------------------- it refills

    /// <summary>A spent bucket admits again once its window has passed.</summary>
    /// <remarks>
    /// The assertion that stops a limiter being a quota. A store that refused for ever after
    /// the budget was spent would take a step out of service permanently on the first burst, and
    /// every test above would pass.
    /// </remarks>
    [Fact]
    public async Task ASpentBucketRefillsOverItsWindow()
    {
        await using var store = await CreateAsync();
        var key = FreshKey();

        ShouldSucceed(
            await store.Limiter.TryAcquireAsync(key, 1, Window, Cancellation),
            "the only permit in the window.");

        var refused = await store.Limiter.TryAcquireAsync(key, 1, Window, Cancellation);

        refused.Value.Admitted.ShouldBeFalse("and the bucket is empty.");

        var admitted = await AdmittedWithinAsync(store.Limiter, key, 1, Window, Window * 8);

        admitted.ShouldBeTrue(
            $"The window is {Window} and the bucket was still refusing eight windows later. A " +
            "budget that never comes back is a quota, and it takes the step out of service for " +
            "good on the first burst.");
    }

    // ----------------------------------------------------------------- it is actually shared

    /// <summary>
    /// Two independently constructed clients over one server draw on one budget.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the assertion the seam exists for, and the one a single-client suite
    /// cannot make.</strong> Everything above holds for a limiter that counts in a field. This
    /// does not: the two clients share nothing but the server, so a store that keeps its count
    /// locally admits the second client's caller and fails here — exactly as it would admit
    /// n × the declared rate across n nodes, which is the failure mode that is invisible in
    /// production because every node's own numbers look correct.
    /// </para>
    /// <para>
    /// The budget is one permit, so there is no arithmetic to get wrong: either the second
    /// client sees that the first spent it, or it does not.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TwoClientsOverOneServerShareOneBudget()
    {
        await using var store = await CreateAsync();
        var key = FreshKey();

        var mine = await store.Limiter.TryAcquireAsync(key, 1, LongWindow, Cancellation);

        ShouldSucceed(mine, "this client takes the only permit.");
        mine.Value.Admitted.ShouldBeTrue();

        var theirs = await store.SecondClient.TryAcquireAsync(key, 1, LongWindow, Cancellation);

        ShouldSucceed(theirs, "the other client asks for one too.");

        theirs.Value.Admitted.ShouldBeFalse(
            "One permit was declared and another client already has it. A limiter that admits " +
            "here is counting in a process rather than in the store, so a fleet of n nodes " +
            "admits n × the declared rate — and every node's own numbers look correct while it " +
            "happens.");
    }

    /// <summary>
    /// Concurrent callers over one budget are admitted exactly to the budget.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The atomicity half. A store that read the bucket, decided, and then wrote it back would
    /// pass every test above and would over-admit under contention — which is the load the
    /// limiter exists for, so the defect appears exactly when it matters and never in a quiet
    /// test.
    /// </para>
    /// <para>
    /// Split across both clients, so the race is between two connections rather than inside one
    /// client's own pipelining.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ConcurrentCallersAreAdmittedExactlyToTheBudget()
    {
        await using var store = await CreateAsync();
        var key = FreshKey();

        const int Permits = 5;
        const int Callers = 40;

        var attempts = new Task<Result<RateLimitVerdict>>[Callers];

        for (var i = 0; i < Callers; i++)
        {
            var limiter = i % 2 == 0 ? store.Limiter : store.SecondClient;

            attempts[i] = Task.Run(
                async () => await limiter.TryAcquireAsync(key, Permits, LongWindow, Cancellation),
                Cancellation);
        }

        var verdicts = await Task.WhenAll(attempts);

        foreach (var verdict in verdicts)
        {
            ShouldSucceed(verdict, "every caller got an answer.");
        }

        verdicts.Count(static v => v.Value.Admitted).ShouldBe(
            Permits,
            $"{Callers} callers raced for {Permits} permits across two clients. Any other " +
            "number means the read and the write are separable, and a limiter whose decision " +
            "and consumption can be interleaved over-admits under exactly the load it exists " +
            "to bound.");
    }

    // ------------------------------------------------------------------------ it is refused

    /// <summary>A store that cannot be reached answers with an error, not an exception.</summary>
    /// <remarks>
    /// The engine turns this into a refusal (ADR-0035 §2.2), which it can only do if it is a
    /// value. A store that threw would take the failure onto the defect path beside
    /// <c>capability.unhandled</c>, where a policy decision does not belong — and a caller that
    /// caught it would be one edit away from admitting on doubt.
    /// </remarks>
    [Fact]
    public async Task AnUnreachableStoreAnswersWithAnErrorRatherThanThrowing()
    {
        await using var store = await CreateAsync();
        var unreachable = await store.UnreachableAsync(Cancellation);

        var verdict = await unreachable.TryAcquireAsync(FreshKey(), 1, LongWindow, Cancellation);

        verdict.IsFailure.ShouldBeTrue(
            "the store is not there, so it did not decide anything — which is a different " +
            "thing from deciding no, and the engine has to be able to tell.");
    }

    // ---------------------------------------------------------------------------- helpers

    /// <summary>Polls until the bucket admits, or gives up.</summary>
    /// <remarks>
    /// Anchored on a generous multiple of the window rather than on a single sleep, so a store
    /// whose clock is the server's is waited out correctly and a slow CI machine does not turn a
    /// refill into a failure. What it will not tolerate is a bucket that never refills.
    /// </remarks>
    private static async Task<bool> AdmittedWithinAsync(
        IRateLimiterStore limiter,
        string key,
        int permits,
        TimeSpan window,
        TimeSpan giveUpAfter)
    {
        var deadline = DateTimeOffset.UtcNow + giveUpAfter;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), Cancellation);

            var verdict = await limiter.TryAcquireAsync(key, permits, window, Cancellation);

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
/// The two clients and the failure case a limiter suite needs, supplied by the implementer.
/// </summary>
/// <remarks>
/// The shape <c>BrokerUnderTest</c> established, and for its reason: what "a second client" and
/// "unreachable" mean is the store's business, and what the suite asserts about them is the
/// contract's.
/// </remarks>
public abstract class RateLimiterUnderTest : IAsyncDisposable
{
    /// <summary>The limiter under test.</summary>
    public abstract IRateLimiterStore Limiter { get; }

    /// <summary>
    /// A second limiter, independently constructed, over the same server and the same key space.
    /// </summary>
    /// <remarks>
    /// <strong>It must share nothing with <see cref="Limiter"/> but the server.</strong> Not a
    /// connection, not a cache, not a counter — that is the whole point: the relationship
    /// between these two objects is the relationship between two nodes, and it is the only one a
    /// single test process can construct. Returning <see cref="Limiter"/> itself would make
    /// <c>TwoClientsOverOneServerShareOneBudget</c> pass vacuously and would remove the only
    /// reason this seam is a plugin contract.
    /// </remarks>
    public abstract IRateLimiterStore SecondClient { get; }

    /// <summary>A limiter of the same kind, pointed at a server that will not answer.</summary>
    /// <param name="cancellationToken">Cancels the construction.</param>
    /// <returns>The limiter. Disposed with this harness.</returns>
    public abstract ValueTask<IRateLimiterStore> UnreachableAsync(CancellationToken cancellationToken);

    /// <summary>Releases whatever the harness opened.</summary>
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
