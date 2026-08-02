using System.Globalization;
using FlowX.Redis.Internal;
using StackExchange.Redis;

namespace FlowX.Redis;

/// <summary>Where in a Redis key space the policy stores keep their keys.</summary>
/// <remarks>
/// Separate from <see cref="RedisLeaseOptions"/> and shaped identically, for that type's reason:
/// a prefix rather than a database number, because Redis Cluster has one logical database. It is
/// its own record rather than a reuse because a deployment may legitimately want its rate
/// limiter somewhere other than its leases — the two have different memory profiles and, unlike
/// the lease key space, this one is safe under an eviction policy.
/// </remarks>
public sealed record RedisPolicyOptions
{
    /// <summary>The prefix every key these stores write begins with. Defaults to <c>flowx</c>.</summary>
    public string KeyPrefix { get; init; } = "flowx";

    /// <summary>Which logical database to use, or <c>-1</c> for the one the connection selected.</summary>
    public int Database { get; init; } = -1;
}

/// <summary>
/// A token bucket shared by every node that consults it, on Redis.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This class is the reason stage 1 could ship at all.</strong> A process-local limiter
/// is <em>anti-conservative</em>: n nodes admit n × the declared rate, the factor is the replica
/// count, and nothing declares it or reports it. That is the half-executing policy
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md">ADR-0025</a>
/// rejects, in its worst form, and
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0040-a-rate-limit-is-shared-or-it-is-not-a-rate-limit.md">ADR-0040</a>
/// is the record.
/// </para>
/// <para>
/// <strong>The decision and the consumption happen in one script.</strong> A read followed by a
/// write would over-admit under contention — which is the load a limiter exists for, so the
/// defect would appear in production and never in a quiet test.
/// <c>RateLimiterConformance.ConcurrentCallersAreAdmittedExactlyToTheBudget</c> is what holds
/// this and <c>PostgresRateLimiterStore</c> to it.
/// </para>
/// <para>
/// <strong>Unlike a lease key, a bucket key carries a Redis expiry, and the difference is worth
/// stating because <see cref="RedisLeaseStore"/>'s remarks argue the opposite at length.</strong>
/// A lease key is kept for ever because deleting it loses the fencing counter, so this key space
/// must not sit under an <c>allkeys-*</c> eviction policy. A bucket has no counter to lose: a key
/// that vanishes is a full bucket, which is what an untouched bucket becomes anyway. So this key
/// space is safe under eviction, and the keys collect themselves.
/// </para>
/// </remarks>
public sealed class RedisRateLimiterStore : IRateLimiterStore
{
    private readonly IDatabase _database;
    private readonly string _keyPrefix;

    /// <summary>Creates a limiter over a connection.</summary>
    /// <param name="connection">The multiplexer. The caller owns it and its lifetime.</param>
    /// <param name="options">Where in the key space the buckets live. Defaults to <c>flowx</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    public RedisRateLimiterStore(IConnectionMultiplexer connection, RedisPolicyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var settings = options ?? new RedisPolicyOptions();

        _database = connection.GetDatabase(settings.Database);
        _keyPrefix = settings.KeyPrefix;
    }

    /// <inheritdoc />
    public async ValueTask<Result<RateLimitVerdict>> TryAcquireAsync(
        string key,
        int permits,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permits);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        try
        {
            var result = await _database.ScriptEvaluateAsync(
                PolicyScripts.TryAcquire,
                [RedisPolicyKeys.Bucket(_keyPrefix, key)],
                [
                    permits.ToString(CultureInfo.InvariantCulture),
                    Milliseconds(window),
                ])
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            var fields = Fields(result, "the rate-limit script returns {admitted, remaining, retryAfter}");

            return Result.Ok(new RateLimitVerdict(
                (long)fields[0] == 1,
                (long)fields[1],
                TimeSpan.FromMilliseconds((long)fields[2])));
        }
        catch (RedisException failure)
        {
            return PolicyStoreErrors.RateLimiterUnreachable(failure);
        }
        catch (TimeoutException failure)
        {
            return PolicyStoreErrors.RateLimiterUnreachable(failure);
        }
    }

    internal static RedisValue Milliseconds(TimeSpan value) =>
        ((long)value.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

    internal static RedisResult[] Fields(RedisResult result, string expected) =>
        (RedisResult[]?)result ?? throw new RedisException(
            $"A policy script returned something other than an array; {expected}. A different " +
            "shape means the server ran a different script than this package sent.");
}

/// <summary>
/// Idempotency records, shared by every node that consults them, on Redis.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One key holds two lifetimes and Redis has one TTL per key</strong>, so the shorter of
/// the two is enforced in Lua against <c>TIME</c> and the longer is the key's own
/// <c>PEXPIRE</c>. An in-flight claim lapses in seconds — it is a lease, and a node that takes a
/// key and dies must not wedge every repeat of it for a declared <c>PT24H</c> — while a
/// completed record lives for the whole declared window. See <see cref="PolicyScripts.Begin"/>.
/// </para>
/// <para>
/// <strong><c>BeginAsync</c> is one script, not a read and then a claim.</strong> Two callers
/// presenting one key at the same instant is the case the whole policy exists for, and
/// <c>docs/10 §7</c> calls the missing atomicity "the most common bug in hand-rolled
/// idempotency".
/// </para>
/// <para>
/// <strong>What this store is handed is already redacted and it has no way to tell.</strong> The
/// record is an opaque string that has been through <see cref="JournalPayload"/>'s single exit,
/// and the engine will not write one that exit had to change
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0042-a-recorded-result-is-replayed-only-when-recording-lost-nothing.md">ADR-0042</a>).
/// A store that parsed the record would be a store that could leak it, which is why this one
/// stores bytes.
/// </para>
/// </remarks>
public sealed class RedisIdempotencyStore : IIdempotencyStore
{
    private readonly IDatabase _database;
    private readonly string _keyPrefix;

    /// <summary>Creates a store over a connection.</summary>
    /// <param name="connection">The multiplexer. The caller owns it and its lifetime.</param>
    /// <param name="options">Where in the key space the records live. Defaults to <c>flowx</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    public RedisIdempotencyStore(IConnectionMultiplexer connection, RedisPolicyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var settings = options ?? new RedisPolicyOptions();

        _database = connection.GetDatabase(settings.Database);
        _keyPrefix = settings.KeyPrefix;
    }

    /// <inheritdoc />
    public async ValueTask<Result<IdempotencyEntry>> BeginAsync(
        string key,
        TimeSpan window,
        TimeSpan inFlightFor,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(inFlightFor, TimeSpan.Zero);

        try
        {
            var result = await _database.ScriptEvaluateAsync(
                PolicyScripts.Begin,
                [RedisPolicyKeys.Record(_keyPrefix, key)],
                [RedisRateLimiterStore.Milliseconds(inFlightFor)])
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            var fields = RedisRateLimiterStore.Fields(
                result, "the begin script returns {state, record, retryAfter}");

            var state = (IdempotencyState)(long)fields[0];

            return Result.Ok(new IdempotencyEntry(
                state,
                state == IdempotencyState.Completed ? (string?)fields[1] : null,
                TimeSpan.FromMilliseconds((long)fields[2])));
        }
        catch (RedisException failure)
        {
            return PolicyStoreErrors.IdempotencyUnreachable(failure);
        }
        catch (TimeoutException failure)
        {
            return PolicyStoreErrors.IdempotencyUnreachable(failure);
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result<bool>> CompleteAsync(
        string key,
        string record,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        return await RunAsync(
            PolicyScripts.Complete,
            key,
            [record, RedisRateLimiterStore.Milliseconds(window)],
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<Result<bool>> AbandonAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return await RunAsync(PolicyScripts.Abandon, key, [], cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<bool>> RunAsync(
        string script,
        string key,
        RedisValue[] arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _database.ScriptEvaluateAsync(
                script,
                [RedisPolicyKeys.Record(_keyPrefix, key)],
                arguments)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            return Result.Ok((long)result == 1);
        }
        catch (RedisException failure)
        {
            return PolicyStoreErrors.IdempotencyUnreachable(failure);
        }
        catch (TimeoutException failure)
        {
            return PolicyStoreErrors.IdempotencyUnreachable(failure);
        }
    }
}

/// <summary>The two key shapes the policy stores occupy.</summary>
/// <remarks>
/// Beside <see cref="RedisKeys"/> rather than inside it, because these keys are built from a
/// caller-supplied string rather than from a <see cref="Guid"/> — so the cluster hash tag
/// argument that type makes does not apply and would be wrong here: tagging on a rate-limit key
/// would route every bucket for one tenant into one slot, which is the opposite of what a
/// limiter wants.
/// </remarks>
internal static class RedisPolicyKeys
{
    /// <summary>One rate-limit bucket.</summary>
    public static string Bucket(string keyPrefix, string key) =>
        string.Concat(keyPrefix, ":ratelimit:", key);

    /// <summary>One idempotency record.</summary>
    public static string Record(string keyPrefix, string key) =>
        string.Concat(keyPrefix, ":idempotency:", key);
}

/// <summary>What the policy stores report when Redis does not answer.</summary>
/// <remarks>
/// An <see cref="Error"/> rather than an exception, because the engine turns it into a refusal —
/// and it can only do that if it is a value. A store that threw would put a policy decision on
/// the defect path beside <c>capability.unhandled</c>, and a caller that caught it would be one
/// edit away from admitting on doubt.
/// </remarks>
internal static class PolicyStoreErrors
{
    public static Error RateLimiterUnreachable(Exception failure) => new(
        "redis.ratelimit_unavailable",
        $"The Redis rate limiter did not answer: {failure.Message}",
        ErrorCategory.Unavailable);

    public static Error IdempotencyUnreachable(Exception failure) => new(
        "redis.idempotency_unavailable",
        $"The Redis idempotency store did not answer: {failure.Message}",
        ErrorCategory.Unavailable);
}
