using System.Collections.Concurrent;

namespace FlowX.Runtime.Tests;

/// <summary>
/// A rate limiter with a fixed budget per key and no refill, so a test can say exactly how many
/// callers get in.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deliberately not a token bucket.</strong> The real stores refill continuously and are
/// held to that by <c>RateLimiterConformance</c>; this one exists to let an engine test assert
/// "the third caller was refused" without the answer depending on how long the test took to run.
/// What it shares with a real store is the only thing these tests are about: the decision and
/// the consumption happen together, and a refusal is a successful call reporting <c>false</c>.
/// </para>
/// <para>
/// It counts its calls, so a test can also assert that the engine asked <em>once</em> per
/// execution of the policed step — which is what puts stage 1 outside the retry loop rather
/// than inside it.
/// </para>
/// </remarks>
internal sealed class CountingRateLimiter(int budget) : IRateLimiterStore
{
    private readonly ConcurrentDictionary<string, int> _taken = new(StringComparer.Ordinal);
    private int _calls;

    /// <summary>How many decisions were asked for, over every key.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Every key the engine built, in the order it built them.</summary>
    public ConcurrentQueue<string> Keys { get; } = new();

    /// <summary>The permits and window the engine passed on the last call.</summary>
    public (int Permits, TimeSpan Window) Declared { get; private set; }

    /// <summary>When set, every call fails with this rather than deciding.</summary>
    public Error? Unreachable { get; set; }

    /// <inheritdoc />
    public ValueTask<Result<RateLimitVerdict>> TryAcquireAsync(
        string key,
        int permits,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        Keys.Enqueue(key);
        Declared = (permits, window);

        if (Unreachable is { } failure)
        {
            return new ValueTask<Result<RateLimitVerdict>>(Result.Fail<RateLimitVerdict>(failure));
        }

        var used = _taken.AddOrUpdate(key, 1, static (_, count) => count + 1);

        var verdict = used <= budget
            ? new RateLimitVerdict(true, budget - used, TimeSpan.Zero)
            : new RateLimitVerdict(false, 0, window);

        return new ValueTask<Result<RateLimitVerdict>>(Result.Ok(verdict));
    }
}

/// <summary>
/// An idempotency store in a dictionary, with the three states and no expiry.
/// </summary>
/// <remarks>
/// Expiry is left out because an engine test that waited out a window would be a test about the
/// clock. <c>IdempotencyStoreConformance</c> is where a window is waited out, against stores
/// whose expiry is their own server's.
/// </remarks>
internal sealed class RecordingIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, string?> _entries = new(StringComparer.Ordinal);

    /// <summary>Every key the engine built, in the order it built them.</summary>
    public ConcurrentQueue<string> Keys { get; } = new();

    /// <summary>Every record written, in the order it was written.</summary>
    public ConcurrentQueue<string> Records { get; } = new();

    /// <summary>How many claims were released without a record.</summary>
    public int Abandoned { get; private set; }

    /// <summary>The window and in-flight lease the engine passed on the last <c>BeginAsync</c>.</summary>
    public (TimeSpan Window, TimeSpan InFlightFor) Declared { get; private set; }

    /// <summary>When set, every call fails with this rather than deciding.</summary>
    public Error? Unreachable { get; set; }

    /// <summary>
    /// Makes every key read as somebody else's live claim, whatever it is.
    /// </summary>
    /// <remarks>
    /// A flag rather than a seeded key, so a test does not have to reconstruct the key the
    /// engine builds — which would make the test pass or fail on the key derivation rather than
    /// on the in-flight behaviour it is about. <c>TheKeyIsTheInvocationsOwnNarrowedByCapability</c>
    /// is where the derivation is asserted, once.
    /// </remarks>
    public bool EverythingIsHeldBysomebodyElse { get; set; }

    /// <inheritdoc />
    public ValueTask<Result<IdempotencyEntry>> BeginAsync(
        string key,
        TimeSpan window,
        TimeSpan inFlightFor,
        CancellationToken cancellationToken)
    {
        Keys.Enqueue(key);
        Declared = (window, inFlightFor);

        if (Unreachable is { } failure)
        {
            return new ValueTask<Result<IdempotencyEntry>>(Result.Fail<IdempotencyEntry>(failure));
        }

        if (EverythingIsHeldBysomebodyElse)
        {
            return new ValueTask<Result<IdempotencyEntry>>(
                Result.Ok(new IdempotencyEntry(IdempotencyState.InFlight, null, inFlightFor)));
        }

        // TryAdd is the compare-and-set the contract asks for: exactly one caller inserts, and
        // every other one reads what is already there. A read followed by a write would be the
        // race docs/10 §7 names as the common bug.
        var claimed = _entries.TryAdd(key, null);

        var found = claimed
            ? new IdempotencyEntry(IdempotencyState.Started, null, TimeSpan.Zero)
            : _entries[key] is { } record
                ? new IdempotencyEntry(IdempotencyState.Completed, record, TimeSpan.Zero)
                : new IdempotencyEntry(IdempotencyState.InFlight, null, inFlightFor);

        return new ValueTask<Result<IdempotencyEntry>>(Result.Ok(found));
    }

    /// <inheritdoc />
    public ValueTask<Result<bool>> CompleteAsync(
        string key,
        string record,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        if (Unreachable is { } failure)
        {
            return new ValueTask<Result<bool>>(Result.Fail<bool>(failure));
        }

        Records.Enqueue(record);
        _entries[key] = record;

        return new ValueTask<Result<bool>>(Result.Ok(true));
    }

    /// <inheritdoc />
    public ValueTask<Result<bool>> AbandonAsync(string key, CancellationToken cancellationToken)
    {
        if (Unreachable is { } failure)
        {
            return new ValueTask<Result<bool>>(Result.Fail<bool>(failure));
        }

        Abandoned++;

        return new ValueTask<Result<bool>>(Result.Ok(_entries.TryRemove(key, out _)));
    }
}

/// <summary>
/// A quota with a fixed budget per key per window, and a window that only turns over when a
/// test says so.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The window is advanced by hand rather than by the clock, which is the whole
/// difference from <see cref="CountingRateLimiter"/>'s "no refill at all".</strong> A quota's
/// defining behaviour is that the budget comes back at a boundary and not before, so an engine
/// test has to be able to cross that boundary — and one that crossed it by sleeping would be a
/// test about how long the suite takes to run. What holds a real store to a real boundary is
/// <c>QuotaStoreConformance</c>, whose reset assertion runs in real time against a server's own
/// clock.
/// </para>
/// <para>
/// It counts its calls, so a test can assert that the engine asked <em>once</em> per execution
/// of the policed step — which is what puts stage 1 outside the retry loop.
/// </para>
/// </remarks>
internal sealed class CountingQuotaStore(int allowed) : IQuotaStore
{
    private readonly ConcurrentDictionary<string, int> _spent = new(StringComparer.Ordinal);
    private int _calls;

    /// <summary>How many decisions were asked for, over every key.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Every key the engine built, in the order it built them.</summary>
    public ConcurrentQueue<string> Keys { get; } = new();

    /// <summary>The budget and period the engine passed on the last call.</summary>
    public (int Budget, TimeSpan Period) Declared { get; private set; }

    /// <summary>When set, every call fails with this rather than deciding.</summary>
    public Error? Unreachable { get; set; }

    /// <summary>Turns the window over: every key's budget is granted again.</summary>
    /// <remarks>
    /// Clears the counters rather than moving a stored boundary, because that is what a fixed
    /// window does to a counter and it is the only part of the behaviour an engine test is
    /// about. The arithmetic that decides <em>when</em> is the store's, and is asserted where a
    /// store's clock is real.
    /// </remarks>
    public void TurnTheWindowOver() => _spent.Clear();

    /// <inheritdoc />
    public ValueTask<Result<QuotaVerdict>> TryConsumeAsync(
        string key,
        int budget,
        TimeSpan period,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        Keys.Enqueue(key);
        Declared = (budget, period);

        if (Unreachable is { } failure)
        {
            return new ValueTask<Result<QuotaVerdict>>(Result.Fail<QuotaVerdict>(failure));
        }

        var used = _spent.AddOrUpdate(key, 1, static (_, count) => count + 1);

        var verdict = used <= allowed
            ? new QuotaVerdict(true, allowed - used, TimeSpan.Zero)
            : new QuotaVerdict(false, 0, period);

        return new ValueTask<Result<QuotaVerdict>>(Result.Ok(verdict));
    }
}
