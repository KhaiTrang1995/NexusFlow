namespace FlowX;

/// <summary>
/// What a step's result is held in between one execution and the next: the store behind
/// <c>docs/10-Policy-Framework.md §5</c>'s <c>Cache</c> policy.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A plugin contract, declared here for
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0009-plugin-contracts.md">ADR-0009</a>'s
/// reason</strong> — a plugin depends on <c>FlowX.Abstractions</c> and on nothing else, so the
/// contract has to live where both the engine and the adapter can see it. It is held by two
/// implementations rather than one, through <c>ResultCacheConformance</c>, exactly as
/// <c>ILeaseStore</c> and <c>IFlowJournal</c> are: a suite with one implementation is a suite
/// shaped like that implementation, and nobody can tell.
/// </para>
/// <para>
/// <strong>The value is a string, and the engine never hands the store anything else.</strong>
/// What the engine puts here is the output of <see cref="JournalPayload.ToJson"/> — the same
/// document the journal stores, produced by the same one exit, redacted by the same pass. A
/// cache is a second sink for a contract value and it inherits the control rather than
/// re-implementing it; there is deliberately no overload taking an object graph, for exactly
/// the reason <c>JournalPayload</c>'s own remarks give.
/// </para>
/// <para>
/// <strong>A miss is a value, not an exception.</strong>
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0007-result-over-exceptions.md">ADR-0007</a>:
/// asking a cache for a key it does not hold is the ordinary case, not an error condition, so
/// it is <see cref="CacheErrors.MissCode"/> with <see cref="ErrorCategory.NotFound"/> — the
/// shape <c>ILeaseStore.ReadAsync</c> already uses for a lease nobody holds.
/// </para>
/// <para>
/// <strong>A store that is down must not fail the flow.</strong> An unavailable cache means
/// the call happens, which is the behaviour the flow had before anything cached it. The engine
/// treats every failure from this contract as a miss and dispatches; the only thing a broken
/// cache costs is the saving. That is why the interface returns a <c>Result</c> rather than
/// throwing, and why <see cref="SetAsync"/>'s failure is discarded rather than propagated.
/// </para>
/// </remarks>
public interface IResultCache
{
    /// <summary>Reads what is held under <paramref name="key"/>.</summary>
    /// <param name="key">The key the engine derived. Opaque to the store.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The entry, or <see cref="CacheErrors.Miss"/> when the key is absent or its time to live
    /// has passed.
    /// </returns>
    /// <remarks>
    /// An entry whose TTL has elapsed reads as a miss whether or not the store has got round to
    /// deleting it. A store that reported a stale entry as a hit would serve a value the author
    /// asked it to stop serving, which is the one thing a TTL is for.
    /// </remarks>
    ValueTask<Result<CacheEntry>> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>Holds <paramref name="value"/> under <paramref name="key"/> for <paramref name="ttl"/>.</summary>
    /// <param name="key">The key the engine derived.</param>
    /// <param name="value">
    /// The document to hold — always what <see cref="JournalPayload.ToJson"/> produced.
    /// </param>
    /// <param name="ttl">How long the entry stays readable. Never zero or negative.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether the entry was stored.</returns>
    /// <remarks>
    /// Overwriting is the contract. Two executions that produce different results for one key
    /// are a flow whose capability is not a function of its input, which
    /// <c>FLOWX1018</c>'s side-effect rule already refuses to let anybody cache; last write
    /// wins is then the only answer that does not require the store to arbitrate.
    /// </remarks>
    ValueTask<Result<bool>> SetAsync(
        string key, string value, TimeSpan ttl, CancellationToken cancellationToken);
}

/// <summary>One value a cache is holding, and when it was put there.</summary>
/// <param name="Value">
/// The document, exactly as <see cref="IResultCache.SetAsync"/> received it.
/// </param>
/// <param name="StoredAt">
/// When the entry was written. Carried so an operator reading a hit can tell a value seconds
/// old from one that has been served for the whole of a long TTL; the engine does not read it.
/// </param>
public readonly record struct CacheEntry(string Value, DateTimeOffset StoredAt);

/// <summary>
/// The errors an <see cref="IResultCache"/> returns, as values rather than exceptions
/// (ADR-0007).
/// </summary>
/// <remarks>
/// The codes are part of the contract rather than of any one store, for
/// <see cref="DurabilityErrors"/>'s reason: <c>ResultCacheConformance</c> asserts the code and
/// the category on every refusal, so a caller can branch without knowing which adapter
/// answered.
/// </remarks>
public static class CacheErrors
{
    /// <summary>Nothing is held under this key.</summary>
    public const string MissCode = "cache.miss";

    /// <summary>The store could not be reached.</summary>
    public const string UnavailableCode = "cache.unavailable";

    /// <summary>Nothing is held under this key.</summary>
    /// <param name="key">The key that was asked for.</param>
    /// <remarks>
    /// <see cref="ErrorCategory.NotFound"/>, and deliberately not
    /// <see cref="ErrorCategory.Internal"/>: a miss is the ordinary answer, and classifying it
    /// as a fault would put every cold cache on an error dashboard.
    /// </remarks>
    public static Error Miss(string key) => new(
        MissCode,
        $"Nothing is cached under '{key}'.",
        ErrorCategory.NotFound);

    /// <summary>The store could not be reached.</summary>
    /// <param name="detail">What the store said.</param>
    /// <remarks>
    /// <see cref="ErrorCategory.Unavailable"/>, which is what tells the engine this is worth
    /// nothing more than a dispatch. A cache that is down is a cache that is not consulted, and
    /// the flow behaves exactly as it did before one existed.
    /// </remarks>
    public static Error Unavailable(string detail) => new(
        UnavailableCode,
        $"The result cache could not be reached, so the step was dispatched instead: {detail}",
        ErrorCategory.Unavailable);
}
