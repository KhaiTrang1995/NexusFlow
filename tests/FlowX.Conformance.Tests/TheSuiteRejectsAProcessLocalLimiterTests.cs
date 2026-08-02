using System.Collections.Concurrent;
using Shouldly;
using Xunit;

namespace FlowX.Conformance;

/// <summary>
/// Proves the two policy suites can fail, by running them against the stores a first
/// implementation actually is.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The limiter below is the one that was started and abandoned, and it is the whole
/// reason <see cref="IRateLimiterStore"/> is a plugin contract.</strong> It is not absurd: it is
/// a correct token bucket, atomic within its process, with a real refill and a real
/// <c>Retry-After</c>. It passes every assertion in <see cref="RateLimiterConformance"/> except
/// one — and the one it fails is the one that says the budget is shared. A fleet of n nodes
/// running it admits n × the declared rate, every node's own numbers look correct while it
/// happens, and nothing anywhere reports the multiplier.
/// </para>
/// <para>
/// <strong>The idempotency store below is the one <c>docs/10 §7</c> names.</strong> It reads,
/// decides, and then claims — three lines that look like two — and it deduplicates perfectly
/// under every sequential test. §7 calls the missing atomicity "the most common bug in
/// hand-rolled idempotency", and this is what it looks like when a suite catches it.
/// </para>
/// <para>
/// One test per suite is the control, for
/// <c>TheSuiteRejectsAStoreThatIsWrongTests</c>'s reason: a suite that rejected everything would
/// be as uninformative as one that accepted everything. Both naive stores pass the assertions
/// they get right.
/// </para>
/// </remarks>
public sealed class TheSuiteRejectsAProcessLocalLimiterTests
{
    /// <summary>
    /// A limiter that counts in a process is rejected by name.
    /// </summary>
    /// <remarks>
    /// <strong>This is the assertion that made stage 1 shippable.</strong>
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0040-a-rate-limit-is-shared-or-it-is-not-a-rate-limit.md">ADR-0040</a>
    /// argues that a process-local limiter is not merely weaker than a shared one but
    /// <em>anti-conservative</em> — it admits more than the declaration says, by a factor nothing
    /// declares — and that shipping one behind a <c>RateLimit(permits, window)</c> is the
    /// half-executing policy ADR-0025 rejects. This test is that argument as a gate.
    /// </remarks>
    [Fact]
    public async Task AProcessLocalLimiterFailsTwoClientsOverOneServerShareOneBudget()
    {
        var suite = new ProcessLocalLimiterUnderTest();

        var failure = await CaughtByAsync(
            nameof(suite.TwoClientsOverOneServerShareOneBudget),
            suite.TwoClientsOverOneServerShareOneBudget);

        failure.Message.ShouldContain(
            "counting in a process rather than in the store",
            Case.Sensitive,
            "the failure must say which guarantee broke, not merely that something did. " +
            $"It said: {failure.Message}");
    }

    /// <summary>
    /// The same limiter passes everything else, which is what makes the test above worth having.
    /// </summary>
    /// <remarks>
    /// The control. A limiter that failed the whole suite would be a limiter nobody would ship,
    /// and the suite would be proving nothing about the defect that actually gets shipped: one
    /// that is right about admission, refusal, refill, scope and <c>Retry-After</c>, and wrong
    /// about who it is counting for.
    /// </remarks>
    [Fact]
    public async Task TheSameLimiterPassesEveryAssertionThatIsNotAboutSharing()
    {
        var suite = new ProcessLocalLimiterUnderTest();

        await suite.ABucketAdmitsItsPermitsAndThenRefuses();
        await suite.ARefusalCarriesAWaitInsideTheWindow();
        await suite.AnAdmissionCarriesNoWait();
        await suite.BudgetsOnDifferentKeysDoNotInterfere();
        await suite.RemainingFallsAsPermitsAreTaken();
        await suite.ASpentBucketRefillsOverItsWindow();
    }

    /// <summary>
    /// An idempotency store that checks and then claims is rejected by name.
    /// </summary>
    [Fact]
    public async Task ANonAtomicClaimFailsConcurrentCallersOfOneKeyProduceExactlyOneClaim()
    {
        var suite = new RacyIdempotencyStoreUnderTest();

        var failure = await CaughtByAsync(
            nameof(suite.ConcurrentCallersOfOneKeyProduceExactlyOneClaim),
            suite.ConcurrentCallersOfOneKeyProduceExactlyOneClaim);

        failure.Message.ShouldContain(
            "the check and the claim are separable",
            Case.Sensitive,
            $"the failure must name the guarantee. It said: {failure.Message}");
    }

    /// <summary>The same store passes the sequential assertions, which is the point.</summary>
    [Fact]
    public async Task TheSameStorePassesEveryAssertionThatIsNotAboutRacing()
    {
        var suite = new RacyIdempotencyStoreUnderTest();

        await suite.AnUnheldKeyIsClaimed();
        await suite.ACompletedKeyReplaysExactlyWhatWasRecorded();
        await suite.AClaimedKeyReadsAsInFlight();
        await suite.AnAbandonedClaimFreesTheKey();
        await suite.AbandoningDoesNotRemoveACompletedRecord();
    }

    private static async Task<ShouldAssertException> CaughtByAsync(string assertion, Func<Task> run)
    {
        ShouldAssertException? failure = null;

        try
        {
            await run();
        }
        catch (ShouldAssertException caught)
        {
            failure = caught;
        }

        failure.ShouldNotBeNull(
            $"{assertion} passed against a store that violates the guarantee it describes. " +
            "A suite that accepts a store it was written to reject is not a gate.");

        return failure;
    }

    private sealed class ProcessLocalLimiterUnderTest : RateLimiterConformance
    {
        protected override ValueTask<RateLimiterUnderTest> CreateAsync() =>
            new(new ProcessLocalHarness());
    }

    private sealed class RacyIdempotencyStoreUnderTest : IdempotencyStoreConformance
    {
        protected override ValueTask<IdempotencyStoreUnderTest> CreateAsync() =>
            new(new RacyHarness());
    }

    /// <summary>Two limiters that each keep their own counts — which is two processes.</summary>
    private sealed class ProcessLocalHarness : RateLimiterUnderTest
    {
        public override IRateLimiterStore Limiter { get; } = new ProcessLocalLimiter();

        public override IRateLimiterStore SecondClient { get; } = new ProcessLocalLimiter();

        public override ValueTask<IRateLimiterStore> UnreachableAsync(CancellationToken cancellationToken) =>
            new(new UnreachableLimiter());
    }

    private sealed class RacyHarness : IdempotencyStoreUnderTest
    {
        private readonly ConcurrentDictionary<string, Entry> _shared = new(StringComparer.Ordinal);

        public RacyHarness()
        {
            Store = new RacyIdempotencyStore(_shared);
            SecondClient = new RacyIdempotencyStore(_shared);
        }

        public override IIdempotencyStore Store { get; }

        public override IIdempotencyStore SecondClient { get; }

        public override ValueTask<IIdempotencyStore> UnreachableAsync(CancellationToken cancellationToken) =>
            new(new UnreachableIdempotencyStore());
    }

    /// <summary>
    /// A correct token bucket that is correct for one process.
    /// </summary>
    /// <remarks>
    /// The refill is real, the arithmetic is right, the wait is right and the decision and the
    /// consumption happen under one lock. The defect is the field: it is this object's, so a
    /// second node's budget is a second field.
    /// </remarks>
    private sealed class ProcessLocalLimiter : IRateLimiterStore
    {
        private readonly Dictionary<string, (double Tokens, DateTimeOffset Touched)> _buckets =
            new(StringComparer.Ordinal);

        private readonly Lock _gate = new();

        public ValueTask<Result<RateLimitVerdict>> TryAcquireAsync(
            string key,
            int permits,
            TimeSpan window,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                var rate = permits / window.TotalSeconds;

                var bucket = _buckets.TryGetValue(key, out var stored) ? stored : (permits, now);

                var level = Math.Min(permits, bucket.Tokens + ((now - bucket.Touched).TotalSeconds * rate));

                if (level >= 1)
                {
                    _buckets[key] = (level - 1, now);

                    return new(Result.Ok(new RateLimitVerdict(true, (long)Math.Floor(level - 1), TimeSpan.Zero)));
                }

                _buckets[key] = (level, now);

                var wait = TimeSpan.FromMilliseconds(Math.Max(1d, Math.Ceiling((1 - level) / rate * 1000d)));

                return new(Result.Ok(new RateLimitVerdict(false, 0, wait)));
            }
        }
    }

    private sealed class UnreachableLimiter : IRateLimiterStore
    {
        public ValueTask<Result<RateLimitVerdict>> TryAcquireAsync(
            string key,
            int permits,
            TimeSpan window,
            CancellationToken cancellationToken) =>
            new(Result.Fail<RateLimitVerdict>(
                new Error("naive.unreachable", "no server", ErrorCategory.Unavailable)));
    }

    private sealed record Entry(string? Record, DateTimeOffset? ClaimedUntil, DateTimeOffset? ExpiresAt);

    /// <summary>
    /// An idempotency store that reads, decides, and then claims.
    /// </summary>
    /// <remarks>
    /// Shared across both clients, so it is not the sharing that is wrong — it is the gap
    /// between the read and the write, which is <c>docs/10 §7</c>'s named bug. The
    /// <see cref="Task.Yield"/> makes the gap reliably observable rather than merely present;
    /// without it the test would pass or fail depending on the scheduler, which is how this
    /// defect survives in real code.
    /// </remarks>
    private sealed class RacyIdempotencyStore(ConcurrentDictionary<string, Entry> shared) : IIdempotencyStore
    {
        public async ValueTask<Result<IdempotencyEntry>> BeginAsync(
            string key,
            TimeSpan window,
            TimeSpan inFlightFor,
            CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;

            if (shared.TryGetValue(key, out var existing))
            {
                if (existing.Record is { } record && existing.ExpiresAt > now)
                {
                    return Result.Ok(new IdempotencyEntry(IdempotencyState.Completed, record, TimeSpan.Zero));
                }

                if (existing.ClaimedUntil > now)
                {
                    return Result.Ok(new IdempotencyEntry(
                        IdempotencyState.InFlight, null, existing.ClaimedUntil.Value - now));
                }
            }

            // The gap. Twenty milliseconds rather than a Task.Yield, because a yield leaves the
            // window at the mercy of how quickly the thread pool ramps — the suite passed against
            // this store with a yield, which is precisely how this defect survives review: it is
            // not that the race never happens, it is that it does not happen while anybody is
            // watching.
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);

            shared[key] = new Entry(null, now + inFlightFor, null);

            return Result.Ok(new IdempotencyEntry(IdempotencyState.Started, null, TimeSpan.Zero));
        }

        public ValueTask<Result<bool>> CompleteAsync(
            string key,
            string record,
            TimeSpan window,
            CancellationToken cancellationToken)
        {
            shared[key] = new Entry(record, null, DateTimeOffset.UtcNow + window);

            return new(Result.Ok(true));
        }

        public ValueTask<Result<bool>> AbandonAsync(string key, CancellationToken cancellationToken)
        {
            if (shared.TryGetValue(key, out var existing) &&
                existing.Record is not null &&
                existing.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return new(Result.Ok(false));
            }

            return new(Result.Ok(shared.TryRemove(key, out _)));
        }
    }

    private sealed class UnreachableIdempotencyStore : IIdempotencyStore
    {
        private static readonly Error Down = new("naive.unreachable", "no server", ErrorCategory.Unavailable);

        public ValueTask<Result<IdempotencyEntry>> BeginAsync(
            string key,
            TimeSpan window,
            TimeSpan inFlightFor,
            CancellationToken cancellationToken) =>
            new(Result.Fail<IdempotencyEntry>(Down));

        public ValueTask<Result<bool>> CompleteAsync(
            string key,
            string record,
            TimeSpan window,
            CancellationToken cancellationToken) => new(Result.Fail<bool>(Down));

        public ValueTask<Result<bool>> AbandonAsync(string key, CancellationToken cancellationToken) =>
            new(Result.Fail<bool>(Down));
    }
}
