namespace FlowX.Redis.Internal;

/// <summary>
/// The stage-1 and stage-3 operations, as Lua that Redis runs as one indivisible step.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Lua rather than <c>INCR</c> with an expiry, and rather than <c>SET NX</c>.</strong>
/// Both of those are one round trip and both are the wrong shape. A counter with a key TTL is a
/// fixed window, so a caller can spend the whole budget at the end of one window and the whole
/// budget again at the start of the next — twice the declared rate across the boundary, which is
/// the failure a token bucket exists to remove. <c>SET NX</c> can claim an idempotency key and
/// cannot then answer "somebody has it, and here is how long they have left" in the same step.
/// </para>
/// <para>
/// <strong>Every decision is taken against <c>TIME</c> on the server</strong>, exactly as
/// <see cref="LeaseScripts"/> does and for its reason: a limiter that trusted the caller's clock
/// would be one whose budget depends on NTP, and two nodes disagreeing about the time would
/// disagree about the rate. <c>PostgresRateLimiterStore</c> makes the same choice with
/// <c>now()</c>, arrived at independently because the argument is not about the store.
/// </para>
/// </remarks>
internal static class PolicyScripts
{
    /// <summary>Milliseconds since the epoch, read from the server's own clock.</summary>
    private const string Now =
        """
        local time = redis.call('TIME')
        local now = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
        """;

    /// <summary>
    /// Takes one token from a bucket that refills continuously, or says how long until one is
    /// there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ARGV[1]</c> is the capacity, <c>ARGV[2]</c> the window in milliseconds. The bucket is
    /// a hash holding the level at the instant it was last touched; the refill is computed on
    /// read rather than by a timer, so an untouched bucket costs Redis nothing and there is no
    /// sweep to run.
    /// </para>
    /// <para>
    /// <strong>The key carries a Redis expiry, unlike a lease's.</strong> The lease store keeps
    /// its key for ever because deleting it loses the fencing counter; a bucket has no counter
    /// to lose — a key that vanishes is a full bucket, which is exactly what a bucket nobody has
    /// touched for a whole window is anyway. The TTL is two windows, so the key survives long
    /// enough to be the state it describes and is collected once it is only describing "full".
    /// </para>
    /// <para>
    /// Returns <c>{admitted, remaining, retryAfterMillis}</c>. <c>remaining</c> is floored at
    /// zero and truncated, so a caller reading it never sees a fractional permit it could not
    /// spend.
    /// </para>
    /// </remarks>
    public const string TryAcquire =
        $$"""
         {{Now}}
         local capacity = tonumber(ARGV[1])
         local window = tonumber(ARGV[2])
         local rate = capacity / window

         local stored = redis.call('HMGET', KEYS[1], 'tokens', 'touched')
         local tokens = tonumber(stored[1])
         local touched = tonumber(stored[2])

         if tokens == nil or touched == nil then
             tokens = capacity
             touched = now
         end

         local elapsed = now - touched

         if elapsed > 0 then
             tokens = math.min(capacity, tokens + elapsed * rate)
             touched = now
         end

         local admitted = 0
         local retryAfter = 0

         if tokens >= 1 then
             tokens = tokens - 1
             admitted = 1
         else
             retryAfter = math.ceil((1 - tokens) / rate)

             if retryAfter < 1 then
                 retryAfter = 1
             end
         end

         redis.call('HSET', KEYS[1], 'tokens', tostring(tokens), 'touched', tostring(touched))
         redis.call('PEXPIRE', KEYS[1], window * 2)

         return {admitted, math.floor(tokens), retryAfter}
         """;

    /// <summary>
    /// Claims an idempotency key, or reports who has it and what they produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ARGV[1]</c> is the in-flight lease in milliseconds. The key is a hash holding either
    /// <c>record</c> (completed) or <c>claimed</c> (in flight, with the claim's own deadline in
    /// <c>until</c>).
    /// </para>
    /// <para>
    /// <strong>The claim's expiry is a value, not a key TTL, and the record's is a key
    /// TTL.</strong> They are two different lifetimes on one key — a claim lapses in seconds, a
    /// record lives for the declared window — and Redis has one TTL per key. So the shorter one
    /// is enforced in the script against <c>TIME</c>, exactly as a lease's is, and the longer one
    /// is the key's own <c>PEXPIRE</c> set when the record is written.
    /// </para>
    /// <para>
    /// Returns <c>{state, record, retryAfterMillis}</c>, where state is 0 started, 1 in flight,
    /// 2 completed.
    /// </para>
    /// </remarks>
    public const string Begin =
        $$"""
         {{Now}}
         local lease = tonumber(ARGV[1])

         local stored = redis.call('HMGET', KEYS[1], 'record', 'until')
         local record = stored[1]
         local heldUntil = tonumber(stored[2])

         if record then
             return {2, record, 0}
         end

         if heldUntil and heldUntil > now then
             return {1, false, heldUntil - now}
         end

         redis.call('HSET', KEYS[1], 'until', tostring(now + lease))
         redis.call('PEXPIRE', KEYS[1], lease)

         return {0, false, 0}
         """;

    /// <summary>Records an outcome against a claimed key, for the declared window.</summary>
    /// <remarks>
    /// <para>
    /// Refuses when a record is already there, which is the only case that matters: two callers
    /// cannot both have claimed the key, but a caller whose claim lapsed can return to find that
    /// its successor already recorded — and overwriting would replace a result somebody has
    /// already replayed with one produced by a different execution.
    /// </para>
    /// <para>
    /// The claim marker is cleared as the record is written, so the key stops being in flight
    /// and starts being completed in one step rather than in two states at once.
    /// </para>
    /// </remarks>
    public const string Complete =
        """
        if redis.call('HGET', KEYS[1], 'record') then
            return 0
        end

        redis.call('HSET', KEYS[1], 'record', ARGV[1])
        redis.call('HDEL', KEYS[1], 'until')
        redis.call('PEXPIRE', KEYS[1], tonumber(ARGV[2]))

        return 1
        """;

    /// <summary>Gives up an in-flight claim without recording anything.</summary>
    /// <remarks>
    /// <strong>Never touches a completed record.</strong> A caller whose claim lapsed, whose
    /// step then failed, and which tidily abandons would otherwise delete the record its
    /// successor wrote — and the next presenter of the key would run a step that has already
    /// happened. This is <c>ASupersededTokenCannotRelease</c>'s shape, one contract across.
    /// </remarks>
    public const string Abandon =
        """
        if redis.call('HGET', KEYS[1], 'record') then
            return 0
        end

        if redis.call('HDEL', KEYS[1], 'until') == 1 then
            redis.call('DEL', KEYS[1])
            return 1
        end

        return 0
        """;
}
