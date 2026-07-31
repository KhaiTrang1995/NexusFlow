using System.Globalization;
using FlowX.Redis.Internal;
using StackExchange.Redis;

namespace FlowX.Redis;

/// <summary>
/// Fenced, time-bounded ownership of one flow instance, on Redis.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The lease key carries no Redis expiry, and that is the whole design.</strong> The
/// idiomatic Redis lease is <c>SET key owner NX PX ttl</c>: the server deletes the key when
/// the TTL runs out, which is elegant, one round trip, and loses the fencing-token counter
/// every time it fires. A counter that restarts hands a returning zombie a token equal to the
/// one its successor is using, and every fence check downstream then passes. This is the same
/// trap <see href="../../docs/adr/ADR-0016-postgres-journal-adapter.md">ADR-0016</see> records
/// the PostgreSQL adapter hitting through <c>DELETE</c> on release, wearing Redis's clothes:
/// there, deleting the row loses the counter; here, so does letting the server expire the key.
/// <strong>Expiry is therefore a value in the hash, not a property of the key.</strong> Both
/// endings — a clean <see cref="ReleaseAsync"/> and a lapse nobody was awake for — move
/// <c>expires</c> into the past and leave <c>token</c> exactly where it is.
/// </para>
/// <para>
/// <strong>What that costs, stated.</strong> The key outlives the lease, so a Redis holding
/// leases for <em>n</em> instances holds <em>n</em> small hashes indefinitely rather than for
/// a TTL. That is the same shape as <c>PostgresLeaseStore</c>'s never-deleted row and it is
/// answered the same way — by retention, which is a decision about when an instance's history
/// stops mattering, not something a lease store is entitled to take on its own. Two operational
/// consequences follow and neither is hypothetical: the key space this store writes to
/// <strong>must not be subject to an <c>allkeys-*</c> eviction policy</strong>, because
/// eviction is deletion by another name and would silently restore the reset counter; and
/// <c>maxmemory</c> sizing has to account for leases that are no longer live.
/// </para>
/// <para>
/// <strong>Expiry is Redis's clock, not the caller's.</strong> Every script begins with
/// <c>TIME</c> on the server and compares against that. A lease store that trusted the
/// caller's clock would be one whose exclusivity depends on NTP, and the zombie in ADR-0006's
/// story is precisely a node whose sense of time is wrong.
/// </para>
/// <para>
/// <strong>A live lease is refused to everyone, including its holder.</strong> A holder that
/// re-acquired would be issued a second token for work it is already doing, and its own
/// in-flight writes would then be fenced out by itself. A holder renews.
/// </para>
/// <para>
/// <strong>This store shares no transaction with the journal.</strong>
/// <see cref="ILeaseStore"/>'s own remarks describe the arrangement — a Redis lease store and
/// a PostgreSQL journal — and this class is the half that makes it real. Nothing here can be
/// committed together with a journal write, so the token has to be carried between them by the
/// node that won it: after a successful <see cref="AcquireAsync"/>, raise the journal's fence
/// with <see cref="IFlowJournal.FenceAsync"/> <em>before</em> doing anything else. Until that
/// call lands, a node holds a lease the journal has never heard of, and its predecessor's
/// token is still the highest one the journal will accept — so the window between acquiring
/// and fencing is a window in which the previous owner's writes are still admitted. It is
/// bounded by one round trip and it is not zero, which is why the ordering is an obligation
/// rather than a suggestion.
/// </para>
/// </remarks>
public sealed class RedisLeaseStore : ILeaseStore
{
    private readonly IDatabase _database;
    private readonly string _keyPrefix;

    /// <summary>Creates a lease store over a connection.</summary>
    /// <param name="connection">The multiplexer. The caller owns it and its lifetime.</param>
    /// <param name="options">Where in the key space the leases live. Defaults to <c>flowx</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    public RedisLeaseStore(IConnectionMultiplexer connection, RedisLeaseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var settings = options ?? new RedisLeaseOptions();

        _database = connection.GetDatabase(settings.Database);
        _keyPrefix = settings.KeyPrefix;
    }

    /// <inheritdoc />
    public async ValueTask<Result<FlowLease>> AcquireAsync(
        Guid instanceId,
        string ownerNode,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownerNode);

        var outcome = await EvaluateAsync(
            LeaseScripts.Acquire,
            instanceId,
            [ownerNode, Milliseconds(ttl)],
            cancellationToken).ConfigureAwait(false);

        if (!outcome.Granted)
        {
            return DurabilityErrors.LeaseHeld(instanceId, outcome.OwnerNode);
        }

        return outcome.ToLease(instanceId);
    }

    /// <inheritdoc />
    public async ValueTask<Result<FlowLease>> RenewAsync(
        FlowLease lease,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);

        var outcome = await EvaluateAsync(
            LeaseScripts.Renew,
            lease.InstanceId,
            [TokenArgument(lease.Token), Milliseconds(ttl)],
            cancellationToken).ConfigureAwait(false);

        if (!outcome.Granted)
        {
            // Expired, or another node has acquired since. The caller's own copy of its
            // expiry is exactly the thing that cannot be trusted here.
            return DurabilityErrors.LeaseLost(lease.InstanceId, lease.Token);
        }

        return outcome.ToLease(lease.InstanceId);
    }

    /// <inheritdoc />
    public async ValueTask<Result<bool>> ReleaseAsync(FlowLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);

        var outcome = await EvaluateAsync(
            LeaseScripts.Release,
            lease.InstanceId,
            [TokenArgument(lease.Token)],
            cancellationToken).ConfigureAwait(false);

        if (!outcome.Granted)
        {
            return DurabilityErrors.LeaseLost(lease.InstanceId, lease.Token);
        }

        return true;
    }

    /// <inheritdoc />
    public async ValueTask<Result<FlowLease>> ReadAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var outcome = await EvaluateAsync(
            LeaseScripts.Read,
            instanceId,
            [],
            cancellationToken).ConfigureAwait(false);

        if (!outcome.Granted)
        {
            return DurabilityErrors.LeaseNotHeld(instanceId);
        }

        return outcome.ToLease(instanceId);
    }

    /// <summary>Runs one lease script against one instance's key.</summary>
    /// <remarks>
    /// <para>
    /// <c>ScriptEvaluateAsync</c> sends <c>EVALSHA</c> and falls back to <c>EVAL</c> the first
    /// time a server has not seen the script, so the Lua travels once per connection rather
    /// than once per call.
    /// </para>
    /// <para>
    /// The cancellation token is applied to the wait rather than to the command, because
    /// StackExchange.Redis has no per-command cancellation: a multiplexed connection cannot
    /// withdraw a request already written to the socket. Cancelling therefore stops this
    /// caller waiting and does not stop the script — which for these four scripts is
    /// harmless, because each is idempotent in the only sense that matters. The one that is
    /// not read-only, <see cref="AcquireAsync"/>, may issue a token nobody collects; a gap in
    /// the sequence costs nothing, since the contract is that tokens increase, not that they
    /// are consecutive.
    /// </para>
    /// </remarks>
    private async ValueTask<LeaseOutcome> EvaluateAsync(
        string script,
        Guid instanceId,
        RedisValue[] arguments,
        CancellationToken cancellationToken)
    {
        RedisKey[] keys = [RedisKeys.Lease(_keyPrefix, instanceId)];

        var result = await _database.ScriptEvaluateAsync(script, keys, arguments)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        return LeaseOutcome.From(result);
    }

    private static RedisValue Milliseconds(TimeSpan ttl) =>
        ((long)ttl.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

    private static RedisValue TokenArgument(FencingToken token) =>
        token.Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>What a lease script returned, before it is turned into a result.</summary>
    /// <param name="Granted">Whether the script did the thing it was asked to do.</param>
    /// <param name="Token">The instance's current token, granted or not.</param>
    /// <param name="OwnerNode">Who owns it, or an empty string when nobody ever has.</param>
    /// <param name="ExpiresAtMilliseconds">The expiry, on the server's clock.</param>
    private readonly record struct LeaseOutcome(
        bool Granted,
        long Token,
        string OwnerNode,
        long ExpiresAtMilliseconds)
    {
        public static LeaseOutcome From(RedisResult result)
        {
            var fields = (RedisResult[]?)result
                ?? throw new RedisException(
                    "A lease script returned something other than an array. The scripts in " +
                    "LeaseScripts all return {granted, token, owner, expires}; a different " +
                    "shape means the server ran a different script than this package sent.");

            return new LeaseOutcome(
                (long)fields[0] == 1,
                Parse(fields[1]),
                (string?)fields[2] ?? string.Empty,
                Parse(fields[3]));
        }

        /// <summary>Turns this into the lease the contract hands back.</summary>
        /// <param name="instanceId">The instance the script was run against.</param>
        /// <returns>The lease.</returns>
        public FlowLease ToLease(Guid instanceId) => new(
            instanceId,
            OwnerNode,
            new FencingToken(Token),
            DateTimeOffset.FromUnixTimeMilliseconds(ExpiresAtMilliseconds));

        /// <summary>
        /// Reads a number the script rendered as text.
        /// </summary>
        /// <remarks>
        /// The scripts return <c>tostring(n)</c> rather than <c>n</c> because Lua's only
        /// numeric type is a double, and the RESP conversion of a Lua number truncates. Both
        /// values here are integers today and neither is anywhere near the precision limit;
        /// rendering them as text means that stays a fact about the values rather than a
        /// property the transport has to preserve.
        /// </remarks>
        private static long Parse(RedisResult field) =>
            long.Parse((string?)field ?? "0", CultureInfo.InvariantCulture);
    }
}
