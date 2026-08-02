using System.Globalization;
using StackExchange.Redis;

namespace FlowX.Redis;

/// <summary>Where in a Redis key space the result cache keeps its entries.</summary>
/// <remarks>
/// A separate options type from <see cref="RedisLeaseOptions"/> and
/// <see cref="RedisStreamOptions"/>, and separate for their reason: the three occupy different
/// key spaces with different retention and different memory characteristics, and a deployment
/// that wanted its cache on a Redis it is happy to let evict — which is exactly what a cache
/// is for, and exactly what a lease store forbids — has no way to say so through one shared
/// prefix.
/// </remarks>
public sealed record RedisCacheOptions
{
    /// <summary>The prefix every key this cache writes begins with. Defaults to <c>flowx</c>.</summary>
    public string KeyPrefix { get; init; } = "flowx";

    /// <summary>
    /// Which logical database to use, or <c>-1</c> for the one the connection selected.
    /// </summary>
    public int Database { get; init; } = -1;
}

/// <summary>
/// The result cache, in Redis: one key per entry, expiring by Redis's own TTL.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Redis's TTL is the expiry, and that is the whole reason this store is short.</strong>
/// <c>RedisLeaseStore</c> deliberately does <em>not</em> use a key TTL — a lease's expiry is a
/// value in a hash, because Redis deleting the key would delete the fencing-token counter with
/// it and reset a sequence that must never restart
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0019-redis-lease-store.md">ADR-0019</a>).
/// A cache entry is the opposite case in every respect: Redis deleting it is exactly the
/// behaviour, nothing is lost when it happens early, and a store that has to compare an expiry
/// itself has to be asked at read time whether the answer it is holding is still allowed.
/// </para>
/// <para>
/// <strong>So this key space <em>may</em> live under an <c>allkeys-lru</c> eviction
/// policy</strong>, which is the sentence <c>RedisLeaseStore</c>'s remarks refuse to write
/// about theirs. Evicting a cache entry early costs a dispatch; evicting a lease key loses
/// exclusivity. Sharing one Redis between the two is therefore a configuration decision worth
/// making deliberately, and the separate <see cref="RedisCacheOptions"/> is what lets a
/// deployment split them.
/// </para>
/// <para>
/// <strong>The stored value is one string with the timestamp in front of it.</strong> A hash
/// with two fields would be a second round trip to set the expiry, or a Lua script to make the
/// pair atomic — which is a lot of machinery for a value whose loss costs a dispatch. The
/// document itself cannot contain a newline outside a string literal, because it is what
/// <c>JournalPayload.ToJson</c> wrote and <c>Utf8JsonWriter</c> emits none; the split is on
/// the first one and the rest is the document, so a document containing an escaped newline is
/// unaffected.
/// </para>
/// <para>
/// <strong>Every failure is a value.</strong> A Redis that is down answers
/// <see cref="CacheErrors.Unavailable"/> and the engine dispatches, which is what the step did
/// before anything cached it. Nothing here throws for an operational condition — the contract's
/// own remarks say why, and it is the difference between a cache outage costing latency and a
/// cache outage costing every flow that declared one.
/// </para>
/// </remarks>
public sealed class RedisResultCache : IResultCache
{
    private readonly IConnectionMultiplexer _connection;
    private readonly RedisCacheOptions _options;

    /// <summary>Creates the cache over an existing connection.</summary>
    /// <param name="connection">The multiplexer. Owned by the caller, as everywhere else.</param>
    /// <param name="options">Where in the key space to write.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public RedisResultCache(IConnectionMultiplexer connection, RedisCacheOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _connection = connection;
        _options = options ?? new RedisCacheOptions();
    }

    /// <summary>The key an entry is held under.</summary>
    /// <param name="keyPrefix">The configured prefix.</param>
    /// <param name="key">The key the engine derived. Opaque: SHA-256 hex in practice.</param>
    /// <returns>The Redis key.</returns>
    /// <exception cref="ArgumentException">Either argument is null or empty.</exception>
    /// <remarks>
    /// <strong>No cluster hash tag, unlike every key in <see cref="RedisKeys"/>.</strong> Those
    /// tag by instance id so that a future multi-key operation over one flow instance stays a
    /// single-slot operation. A cache entry belongs to no instance — that is the point of
    /// caching it — and tagging by anything would concentrate a whole capability's entries into
    /// one slot, which is the one thing a cache must not do to a cluster.
    /// </remarks>
    public static string EntryKey(string keyPrefix, string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyPrefix);
        ArgumentException.ThrowIfNullOrEmpty(key);

        return string.Concat(keyPrefix, ":cache:", key);
    }

    /// <inheritdoc />
    public async ValueTask<Result<CacheEntry>> GetAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        try
        {
            var stored = await Database().StringGetAsync(EntryKey(_options.KeyPrefix, key))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            if (stored.IsNullOrEmpty)
            {
                // Absent, or expired and swept: Redis reports both the same way, and so does
                // this contract. What the caller does next is identical either way.
                return Result.Fail<CacheEntry>(CacheErrors.Miss(key));
            }

            return Parse(stored!, key);
        }
        catch (RedisException failure)
        {
            return Result.Fail<CacheEntry>(CacheErrors.Unavailable(failure.Message));
        }
        catch (TimeoutException failure)
        {
            return Result.Fail<CacheEntry>(CacheErrors.Unavailable(failure.Message));
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result<bool>> SetAsync(
        string key, string value, TimeSpan ttl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);

        var stamped = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\n{value}");

        try
        {
            // Unconditional: last write wins, which is the contract. The TTL rides the same
            // command, so there is no window in which an entry exists without an expiry — the
            // window a SET followed by an EXPIRE would open, and the one that leaves a permanent
            // key behind when a process dies between the two.
            var written = await Database()
                .StringSetAsync(EntryKey(_options.KeyPrefix, key), stamped, ttl)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            return Result.Ok(written);
        }
        catch (RedisException failure)
        {
            return Result.Fail<bool>(CacheErrors.Unavailable(failure.Message));
        }
        catch (TimeoutException failure)
        {
            return Result.Fail<bool>(CacheErrors.Unavailable(failure.Message));
        }
    }

    /// <summary>Splits a stored value back into its timestamp and its document.</summary>
    /// <remarks>
    /// A value this store did not write — a key collision with something else in the key space —
    /// is a miss rather than a parse failure. The engine would dispatch either way, and
    /// reporting it as an error would put an operator's own stray key on a fault dashboard.
    /// </remarks>
    private static Result<CacheEntry> Parse(string stored, string key)
    {
        var split = stored.IndexOf('\n', StringComparison.Ordinal);

        if (split <= 0 ||
            !long.TryParse(
                stored.AsSpan(0, split), NumberStyles.None, CultureInfo.InvariantCulture, out var millis))
        {
            return Result.Fail<CacheEntry>(CacheErrors.Miss(key));
        }

        return Result.Ok(new CacheEntry(
            stored[(split + 1)..],
            DateTimeOffset.FromUnixTimeMilliseconds(millis)));
    }

    private IDatabase Database() => _connection.GetDatabase(_options.Database);
}
