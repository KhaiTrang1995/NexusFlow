using Shouldly;
using Xunit;

namespace FlowX.Conformance;

/// <summary>
/// What an <see cref="IResultCache"/> must do. Derive, supply a store, and the whole suite
/// runs against it unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Written before the second store, for <c>LeaseStoreConformance</c>'s reason.</strong>
/// A suite written once two implementations exist is a suite shaped like those two
/// implementations, and nobody can tell. What is being held here is a claim about the
/// <em>contract</em> — a hit is a hit, an expiry is an expiry, a miss is a value rather than an
/// exception — and one implementation cannot demonstrate that any of those is a property of the
/// contract rather than of Redis.
/// </para>
/// <para>
/// <strong>Expiry is tested in real time, deliberately.</strong> The suite waits out a short
/// TTL rather than advancing an injected clock, exactly as <c>LeaseStoreConformance</c> does
/// and for the same reason: a store whose expiry is enforced by Redis or by <c>now()</c> in
/// PostgreSQL has no clock to inject, and testing the one store that does would prove nothing
/// about the two that matter. A store with coarser granularity overrides <see cref="ShortTtl"/>.
/// </para>
/// <para>
/// <strong>The engine's own use of a cache is not tested here.</strong> That a hit skips a
/// dispatch, that a redacted document is never stored, that two tenants do not share a key —
/// those are <c>FlowX.Runtime.Tests.CachePolicyTests</c>, against a recording double, because
/// they are assertions about the engine and a real store would make them assertions about a
/// store. The division is <c>JournalConformance</c>'s.
/// </para>
/// </remarks>
public abstract class ResultCacheConformance
{
    /// <summary>A fresh, empty cache. Called once per test.</summary>
    /// <remarks>
    /// No state may survive between calls. A store that namespaced by prefix or by schema
    /// satisfies this exactly as well as one that flushed, and far more politely — see
    /// <c>RedisTestKeySpace</c>.
    /// </remarks>
    protected abstract ValueTask<IResultCache> CreateCacheAsync();

    /// <summary>The TTL the expiry assertions use. Short, so the suite stays quick.</summary>
    protected virtual TimeSpan ShortTtl => TimeSpan.FromMilliseconds(300);

    /// <summary>How long an entry is held in the assertions that are not about expiry.</summary>
    protected virtual TimeSpan LongTtl => TimeSpan.FromMinutes(5);

    /// <summary>The ambient test cancellation token.</summary>
    protected static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A key nobody has written reads as a miss, and a miss is a value.</summary>
    /// <remarks>
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0007-result-over-exceptions.md">ADR-0007</a>:
    /// asking a cache for a key it does not hold is the ordinary case and not an error
    /// condition. The code and the category are both part of the contract — a caller branches
    /// on the code, and the category is what tells the engine this is worth a dispatch rather
    /// than a retry.
    /// </remarks>
    [Fact]
    public async Task ReadingAnAbsentKeyIsAMiss()
    {
        var cache = await CreateCacheAsync();

        ShouldFailWith(
            await cache.GetAsync(Key(), Cancellation),
            CacheErrors.MissCode,
            ErrorCategory.NotFound,
            "a cold cache is the ordinary state of a cache, not a fault.");
    }

    /// <summary>What was stored is what comes back, byte for byte.</summary>
    /// <remarks>
    /// The engine stores what <c>JournalPayload.ToJson</c> produced and puts what comes back
    /// through the generated context, so a store that normalised, re-encoded or trimmed the
    /// document would hand a flow a value its own deserialiser had never seen. Asserted with a
    /// document carrying non-ASCII text and both quote styles for that reason.
    /// </remarks>
    [Fact]
    public async Task WhatWasStoredIsWhatComesBack()
    {
        var cache = await CreateCacheAsync();
        var key = Key();

        const string document = """{"schemaVersion":"1.0.0","Quote":{"symbol":"MSFT","note":"café — \"quoted\""}}""";

        ShouldSucceed(await cache.SetAsync(key, document, LongTtl, Cancellation), "the entry is new.");

        var read = await cache.GetAsync(key, Cancellation);

        ShouldSucceed(read, "it was just written.");

        read.Value.Value.ShouldBe(
            document,
            "verbatim. The engine puts this back through the flow's generated JSON context, " +
            "so a store that re-encoded it would hand a flow a document its own deserialiser " +
            "never produced.");
    }

    /// <summary>An entry says when it was written.</summary>
    /// <remarks>
    /// The engine does not read it; an operator does. A hit that is seconds old and a hit that
    /// has been served for the whole of a long TTL are different facts about a dependency, and
    /// a store that returned <c>default</c> here would make the difference invisible.
    /// </remarks>
    [Fact]
    public async Task AnEntryRecordsWhenItWasStored()
    {
        var cache = await CreateCacheAsync();
        var key = Key();

        var before = DateTimeOffset.UtcNow.AddSeconds(-5);

        ShouldSucceed(await cache.SetAsync(key, "{}", LongTtl, Cancellation), "the entry is new.");

        var read = await cache.GetAsync(key, Cancellation);

        ShouldSucceed(read, "it was just written.");

        read.Value.StoredAt.ShouldBeGreaterThan(
            before,
            "a stored-at in the distant past reads as an entry nobody has refreshed.");

        read.Value.StoredAt.ShouldBeLessThan(DateTimeOffset.UtcNow.AddSeconds(5));
    }

    /// <summary>Two keys are two entries.</summary>
    /// <remarks>
    /// The most basic property a cache has, and the one whose absence is most catastrophic: the
    /// engine derives a key that separates two tenants, two principals and two inputs, and a
    /// store that collapsed keys would serve one caller another's result whatever the engine
    /// did.
    /// </remarks>
    [Fact]
    public async Task TwoKeysDoNotShareAnEntry()
    {
        var cache = await CreateCacheAsync();
        var first = Key();
        var second = Key();

        ShouldSucceed(await cache.SetAsync(first, "\"one\"", LongTtl, Cancellation), "the first.");
        ShouldSucceed(await cache.SetAsync(second, "\"two\"", LongTtl, Cancellation), "the second.");

        (await cache.GetAsync(first, Cancellation)).Value.Value.ShouldBe("\"one\"");
        (await cache.GetAsync(second, Cancellation)).Value.Value.ShouldBe("\"two\"");
    }

    /// <summary>The last write under one key wins.</summary>
    /// <remarks>
    /// Overwriting is the contract rather than an accident. Two executions producing different
    /// results for one key are a flow whose capability is not a function of its input, which
    /// <c>FLOWX1018</c>'s side-effect rule already refuses to let anybody cache; last write
    /// wins is then the only answer that does not require the store to arbitrate.
    /// </remarks>
    [Fact]
    public async Task TheLastWriteWins()
    {
        var cache = await CreateCacheAsync();
        var key = Key();

        ShouldSucceed(await cache.SetAsync(key, "\"first\"", LongTtl, Cancellation), "the first.");
        ShouldSucceed(await cache.SetAsync(key, "\"second\"", LongTtl, Cancellation), "and again.");

        (await cache.GetAsync(key, Cancellation)).Value.Value.ShouldBe("\"second\"");
    }

    /// <summary>An entry whose TTL has passed reads as a miss.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The assertion the whole policy depends on.</strong> A TTL is the only thing an
    /// author declares about how stale an answer may be, and a store that reported a lapsed
    /// entry as a hit would serve a value the author asked it to stop serving — indefinitely,
    /// because nothing else in this runtime ever evicts.
    /// </para>
    /// <para>
    /// A miss and not an error: whether the store has got round to deleting the key is its own
    /// business, and the caller's answer is the same either way.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnExpiredEntryReadsAsAMiss()
    {
        var cache = await CreateCacheAsync();
        var key = Key();

        ShouldSucceed(await cache.SetAsync(key, "\"stale\"", ShortTtl, Cancellation), "the entry is new.");

        ShouldSucceed(await cache.GetAsync(key, Cancellation), "and while it is live, it reads as a hit.");

        await WaitOutAsync(ShortTtl);

        var read = await ReadAfterExpiryAsync(cache, key);

        read.Code.ShouldBe(
            CacheErrors.MissCode,
            "an expired entry is not an entry. A store that never enforced a TTL would serve " +
            "the first answer this flow ever produced, for ever.");
    }

    /// <summary>Writing again extends the entry's life.</summary>
    /// <remarks>
    /// The engine writes on every miss, so a store that kept the original expiry would let an
    /// entry that is being refreshed constantly still lapse on a fixed schedule — a cache that
    /// goes cold under load, which is the opposite of what it is for.
    /// </remarks>
    [Fact]
    public async Task RewritingExtendsTheLife()
    {
        var cache = await CreateCacheAsync();
        var key = Key();

        ShouldSucceed(await cache.SetAsync(key, "\"first\"", ShortTtl, Cancellation), "briefly.");
        ShouldSucceed(await cache.SetAsync(key, "\"second\"", LongTtl, Cancellation), "and then for longer.");

        await WaitOutAsync(ShortTtl);

        var read = await cache.GetAsync(key, Cancellation);

        ShouldSucceed(read, "the second write's TTL is the one in force.");
        read.Value.Value.ShouldBe("\"second\"");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A key nobody else in this suite uses.</summary>
    /// <remarks>
    /// The engine's keys are SHA-256 hex, and this mimics the shape rather than the derivation:
    /// a store must not care what a key means, and a suite that used readable keys would not
    /// notice a store that had opinions about their format.
    /// </remarks>
    protected static string Key() => Guid.NewGuid().ToString("n") + Guid.NewGuid().ToString("n");

    /// <summary>Waits until a TTL has certainly passed, with a margin for timer granularity.</summary>
    private static Task WaitOutAsync(TimeSpan ttl) =>
        Task.Delay(ttl + TimeSpan.FromMilliseconds(100), Cancellation);

    /// <summary>
    /// Reads a key whose TTL has passed, tolerating clock skew between this process and the
    /// store but never tolerating an entry that does not expire at all.
    /// </summary>
    private static async Task<Error> ReadAfterExpiryAsync(IResultCache cache, string key)
    {
        var giveUpAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);

        while (true)
        {
            var read = await cache.GetAsync(key, Cancellation);

            if (read.IsFailure)
            {
                return read.Error;
            }

            if (DateTimeOffset.UtcNow > giveUpAt)
            {
                read.IsFailure.ShouldBeTrue(
                    "the entry's TTL passed five seconds ago and the store still serves it. " +
                    "A TTL that is never enforced makes every cached answer permanent.");

                return read.Error;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), Cancellation);
        }
    }

    /// <summary>Asserts a store call succeeded, printing the store's own refusal if not.</summary>
    protected static void ShouldSucceed<T>(Result<T> result, string because) =>
        result.IsSuccess.ShouldBeTrue(
            $"{because} The store refused instead: {(result.IsFailure ? result.Error.ToString() : "no error")}");

    /// <summary>Asserts a store call was refused with the documented code and category.</summary>
    protected static Error ShouldFailWith<T>(
        Result<T> result,
        string code,
        ErrorCategory category,
        string because)
    {
        result.IsFailure.ShouldBeTrue($"{because} The store answered instead.");

        result.Error.Code.ShouldBe(
            code,
            $"{because} A caller branches on the code, so it is part of the contract and not " +
            "of any one store.");

        result.Error.Category.ShouldBe(
            category,
            $"{because} The category carries the retry decision, which is the half that " +
            "changes what a caller does next.");

        return result.Error;
    }
}
