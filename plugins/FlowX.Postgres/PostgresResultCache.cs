using Npgsql;

namespace FlowX.Postgres;

/// <summary>
/// The result cache, in PostgreSQL: one row per entry, expiring against the database's clock.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The second implementation of <see cref="IResultCache"/>, and that is its
/// job.</strong> <c>ResultCacheConformance</c> is a claim about the contract — a hit is a hit,
/// an expiry is an expiry, a miss is a value rather than an exception — and a suite with one
/// implementation is a suite shaped like that implementation. This one and
/// <c>RedisResultCache</c> disagree about almost everything internally: Redis expires a key and
/// this compares an instant, Redis has one round trip and this has a connection to open.
/// Passing the same suite unchanged is what makes the contract a contract
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0009-plugin-contracts.md">ADR-0009</a>).
/// </para>
/// <para>
/// <strong>A relational cache is a deployment decision, not a performance one, and it is worth
/// saying so.</strong> Redis will be faster at this for any load worth caching under. What
/// this is for is the team already running PostgreSQL for the journal: turning a declared
/// <c>Cache</c> on should not require introducing a second piece of infrastructure, and an
/// engine that only worked with one store would make the policy a Redis feature.
/// </para>
/// <para>
/// <strong>Expiry is the database's clock.</strong> Every statement compares against
/// <c>now()</c> on the server rather than against an instant the caller computed, for
/// <c>PostgresLeaseStore</c>'s reason: a writer and a reader on two nodes share the store's
/// clock and share no other, and a TTL enforced by the caller's would be one that depends on
/// NTP.
/// </para>
/// <para>
/// <strong>Nothing here sweeps, and a read still refuses a lapsed row.</strong> An entry past
/// its expiry is invisible to <see cref="GetAsync"/> whether or not anything has deleted it, so
/// a deployment that never sweeps is correct and merely wastes space. When data stops mattering
/// is an operator's decision — the position <c>PostgresRetention</c> already takes about the
/// journal — and the index on <c>expires_at</c> is what makes acting on it a range scan.
/// </para>
/// <para>
/// <strong>Every failure is a value.</strong> A database that is unreachable answers
/// <see cref="CacheErrors.Unavailable"/> and the engine dispatches, which is what the step did
/// before anything cached it. The alternative would make a cache outage an outage of every flow
/// that declared one.
/// </para>
/// </remarks>
public sealed class PostgresResultCache : IResultCache
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the cache over a configured data source.</summary>
    /// <param name="dataSource">
    /// The data source, with its <c>search_path</c> already selecting the schema the migrator
    /// created <c>flowx_cache_entry</c> in — the arrangement every store in this package uses,
    /// and the reason no statement here names a schema.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is null.</exception>
    public PostgresResultCache(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <summary>Reads an entry that has not lapsed.</summary>
    /// <remarks>
    /// The <c>expires_at &gt; now()</c> predicate is on the read rather than left to a sweep,
    /// which is what makes "a store that never sweeps is correct" true. A store that returned
    /// the row and let the caller compare would be a store whose TTL is enforced by whoever
    /// remembered to.
    /// </remarks>
    private const string Read =
        """
        SELECT payload, stored_at
        FROM flowx_cache_entry
        WHERE cache_key = @key AND expires_at > now()
        """;

    /// <summary>Writes an entry, replacing whatever was there.</summary>
    /// <remarks>
    /// <para>
    /// Last write wins, which is the contract: two executions producing different results for
    /// one key are a flow whose capability is not a function of its input, and
    /// <c>FLOWX1018</c>'s side-effect rule already refuses to let anybody cache one. An
    /// <c>ON CONFLICT DO NOTHING</c> would leave a lapsed row in place and make the entry
    /// permanently unrefreshable.
    /// </para>
    /// <para>
    /// The expiry is computed on the server from the TTL, so the row's instant and the
    /// predicate that reads it are taken from one clock.
    /// </para>
    /// </remarks>
    private const string Write =
        """
        INSERT INTO flowx_cache_entry (cache_key, payload, stored_at, expires_at)
        VALUES (@key, @payload, now(), now() + @ttl)
        ON CONFLICT (cache_key) DO UPDATE SET
            payload    = EXCLUDED.payload,
            stored_at  = EXCLUDED.stored_at,
            expires_at = EXCLUDED.expires_at
        """;

    /// <inheritdoc />
    public async ValueTask<Result<CacheEntry>> GetAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        try
        {
            var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            await using var closing = connection.ConfigureAwait(false);

            using var command = connection.CreateCommand();

            command.CommandText = Read;
            command.Parameters.Add(Db.Text("key", key));

            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            await using var closingReader = reader.ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Absent, or present and lapsed. The predicate makes the two one answer, and
                // what the caller does next is identical either way.
                return Result.Fail<CacheEntry>(CacheErrors.Miss(key));
            }

            return Result.Ok(new CacheEntry(
                reader.GetString(0),
                Db.ReadTimestamp(reader, 1)));
        }
        catch (NpgsqlException failure)
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

        try
        {
            var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            await using var closing = connection.ConfigureAwait(false);

            using var command = connection.CreateCommand();

            command.CommandText = Write;
            command.Parameters.Add(Db.Text("key", key));
            command.Parameters.Add(Db.Text("payload", value));
            command.Parameters.Add(Db.Interval("ttl", ttl));

            var written = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return Result.Ok(written > 0);
        }
        catch (NpgsqlException failure)
        {
            return Result.Fail<bool>(CacheErrors.Unavailable(failure.Message));
        }
    }
}
