namespace FlowX.Redis.Internal;

/// <summary>
/// The four lease operations, as Lua that Redis runs as one indivisible step.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Lua rather than <c>WATCH</c>/<c>MULTI</c>, and rather than <c>SET NX</c>.</strong>
/// Every one of these operations is a read followed by a conditional write — read the current
/// token, decide whether the lease is live, write its successor — and the two halves must not
/// be separable by another node doing the same thing. Optimistic retry over <c>WATCH</c> would
/// also be correct and would turn an uncontended acquisition into two round trips; the
/// idiomatic <c>SET key value NX PX ttl</c> cannot express it at all, because it has nowhere
/// to put a counter that survives the key.
/// </para>
/// <para>
/// <strong>Every decision is taken against <c>TIME</c> on the server.</strong> A lease store
/// that trusted the caller's clock would be a lease store whose exclusivity depends on NTP,
/// and the zombie in ADR-0006's story is precisely a node whose sense of time is wrong. This
/// is the same choice <c>PostgresLeaseStore</c> makes with <c>now()</c>, arrived at
/// independently because the argument is not about the store.
/// </para>
/// </remarks>
internal static class LeaseScripts
{
    /// <summary>Milliseconds since the epoch, read from the server's own clock.</summary>
    private const string Now =
        """
        local time = redis.call('TIME')
        local now = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
        """;

    /// <summary>Reads the stored triple, defaulting a missing key to an expired lease at token 0.</summary>
    private const string Load =
        """
        local stored = redis.call('HMGET', KEYS[1], 'token', 'owner', 'expires')
        local token = tonumber(stored[1]) or 0
        local owner = stored[2] or ''
        local expires = tonumber(stored[3]) or 0
        """;

    /// <summary>Takes the lease if nobody holds it, and issues the next token when it does.</summary>
    /// <remarks>
    /// The counter is incremented from whatever the key holds and the key is never removed, so
    /// the sequence is per instance and never restarts. A live lease is refused to everyone,
    /// including its holder: a holder that re-acquired would be issued a second token for work
    /// it is already doing, and its own in-flight writes would then be fenced out by itself.
    /// </remarks>
    public const string Acquire =
        $$"""
         {{Now}}
         {{Load}}

         if expires > now then
             return {0, tostring(token), owner, tostring(expires)}
         end

         token = token + 1
         expires = now + tonumber(ARGV[2])

         redis.call('HSET', KEYS[1], 'token', token, 'owner', ARGV[1], 'expires', expires)

         return {1, tostring(token), ARGV[1], tostring(expires)}
         """;

    /// <summary>Extends a lease the caller still holds, keeping its token.</summary>
    public const string Renew =
        $$"""
         {{Now}}
         {{Load}}

         if token ~= tonumber(ARGV[1]) or expires <= now then
             return {0, tostring(token), owner, tostring(expires)}
         end

         expires = now + tonumber(ARGV[2])

         redis.call('HSET', KEYS[1], 'expires', expires)

         return {1, tostring(token), owner, tostring(expires)}
         """;

    /// <summary>Gives up a lease by expiring it in place.</summary>
    /// <remarks>
    /// <strong>The key is not deleted.</strong> Deleting it is the quiet way to lose
    /// exclusivity two acquisitions later, because the counter goes with it. The token field
    /// stays exactly where it is and only <c>expires</c> moves.
    /// </remarks>
    public const string Release =
        $$"""
         {{Now}}
         {{Load}}

         if token ~= tonumber(ARGV[1]) or expires <= now then
             return {0, tostring(token), owner, tostring(expires)}
         end

         redis.call('HSET', KEYS[1], 'expires', now)

         return {1, tostring(token), owner, tostring(now)}
         """;

    /// <summary>Reads the live lease, if there is one.</summary>
    public const string Read =
        $$"""
         {{Now}}
         {{Load}}

         if expires <= now then
             return {0, tostring(token), owner, tostring(expires)}
         end

         return {1, tostring(token), owner, tostring(expires)}
         """;
}
