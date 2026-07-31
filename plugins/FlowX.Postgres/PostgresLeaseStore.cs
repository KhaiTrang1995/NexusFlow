using Npgsql;

namespace FlowX.Postgres;

/// <summary>
/// Fenced, time-bounded ownership of one flow instance, on PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The row is never deleted, and that is the whole design.</strong> The obvious
/// implementation removes the row on release and on expiry, which loses the token counter
/// with it — and a counter that restarts hands a returning zombie a token equal to the one
/// its successor is using, so every fence check downstream passes. Release and expiry
/// therefore move <c>expires_at</c> into the past and leave <c>fencing_token</c> where it
/// is. <c>EveryAcquisitionIssuesAStrictlyGreaterToken</c> checks both endings.
/// </para>
/// <para>
/// <strong>Expiry is the database's clock, not the caller's.</strong> Every statement
/// compares against <c>now()</c> on the server. A lease store that trusted the caller's
/// clock would be a lease store whose exclusivity depends on NTP, and the zombie in the
/// story is precisely a node whose sense of time is wrong.
/// </para>
/// <para>
/// <strong>A live lease is refused to everyone, including its holder.</strong> A holder
/// that re-acquired would be issued a second token for work it is already doing, and its
/// own in-flight writes would then be fenced out by itself. A holder renews.
/// </para>
/// </remarks>
public sealed class PostgresLeaseStore : ILeaseStore
{
    /// <summary>
    /// Takes the lease if nobody holds it, and issues the next token when it does.
    /// </summary>
    /// <remarks>
    /// One statement, so the read of the current token and the write of its successor
    /// cannot interleave with another node doing the same thing. The <c>WHERE</c> on the
    /// conflict path is what makes a live lease exclusive: it matches nothing while the
    /// lease is live, so the upsert writes nothing and returns no row.
    /// </remarks>
    private const string Acquire =
        """
        INSERT INTO flow_lease (instance_id, owner_node, fencing_token, expires_at)
        VALUES (@instance, @owner, 1, now() + @ttl)
        ON CONFLICT (instance_id) DO UPDATE
           SET owner_node    = EXCLUDED.owner_node,
               fencing_token = flow_lease.fencing_token + 1,
               expires_at    = EXCLUDED.expires_at
         WHERE flow_lease.expires_at <= now()
        RETURNING owner_node, fencing_token, expires_at
        """;

    /// <summary>Extends a lease the caller still holds, keeping its token.</summary>
    private const string Renew =
        """
        UPDATE flow_lease
           SET expires_at = now() + @ttl
         WHERE instance_id = @instance
           AND fencing_token = @token
           AND expires_at > now()
        RETURNING owner_node, fencing_token, expires_at
        """;

    /// <summary>
    /// Gives up a lease by expiring it in place.
    /// </summary>
    /// <remarks>
    /// The token stays. Deleting the row here is the quiet way to lose exclusivity two
    /// acquisitions later, and it is also how a superseded token could release its
    /// successor's lease — which the <c>fencing_token</c> guard refuses.
    /// </remarks>
    private const string Release =
        """
        UPDATE flow_lease
           SET expires_at = now()
         WHERE instance_id = @instance
           AND fencing_token = @token
           AND expires_at > now()
        """;

    /// <summary>Reads the live lease, if there is one.</summary>
    private const string Read =
        """
        SELECT owner_node, fencing_token, expires_at
          FROM flow_lease
         WHERE instance_id = @instance
           AND expires_at > now()
        """;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates a lease store over a data source.</summary>
    /// <param name="dataSource">The data source; its connection string selects the schema.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is null.</exception>
    public PostgresLeaseStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public async ValueTask<Result<FlowLease>> AcquireAsync(
        Guid instanceId,
        string ownerNode,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownerNode);

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var closing = connection.ConfigureAwait(false);

        using var command = connection.CreateCommand();

        command.CommandText = Acquire;
        command.Parameters.Add(Db.Uuid("instance", instanceId));
        command.Parameters.Add(Db.Text("owner", ownerNode));
        command.Parameters.Add(Db.Interval("ttl", ttl));

        var lease = await ReadLeaseAsync(command, instanceId, cancellationToken).ConfigureAwait(false);

        if (lease is null)
        {
            return DurabilityErrors.LeaseHeld(
                instanceId,
                await OwnerAsync(instanceId, cancellationToken).ConfigureAwait(false));
        }

        return lease;
    }

    /// <inheritdoc />
    public async ValueTask<Result<FlowLease>> RenewAsync(
        FlowLease lease,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var closing = connection.ConfigureAwait(false);

        using var command = connection.CreateCommand();

        command.CommandText = Renew;
        command.Parameters.Add(Db.Uuid("instance", lease.InstanceId));
        command.Parameters.Add(Db.Long("token", lease.Token.Value));
        command.Parameters.Add(Db.Interval("ttl", ttl));

        var renewed = await ReadLeaseAsync(command, lease.InstanceId, cancellationToken)
            .ConfigureAwait(false);

        if (renewed is null)
        {
            // Expired, or another node has acquired since. The caller's own copy of its
            // expiry is exactly the thing that cannot be trusted here.
            return DurabilityErrors.LeaseLost(lease.InstanceId, lease.Token);
        }

        return renewed;
    }

    /// <inheritdoc />
    public async ValueTask<Result<bool>> ReleaseAsync(
        FlowLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var closing = connection.ConfigureAwait(false);

        using var command = connection.CreateCommand();

        command.CommandText = Release;
        command.Parameters.Add(Db.Uuid("instance", lease.InstanceId));
        command.Parameters.Add(Db.Long("token", lease.Token.Value));

        var released = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (released == 0)
        {
            return DurabilityErrors.LeaseLost(lease.InstanceId, lease.Token);
        }

        return true;
    }

    /// <inheritdoc />
    public async ValueTask<Result<FlowLease>> ReadAsync(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var closing = connection.ConfigureAwait(false);

        using var command = connection.CreateCommand();

        command.CommandText = Read;
        command.Parameters.Add(Db.Uuid("instance", instanceId));

        var lease = await ReadLeaseAsync(command, instanceId, cancellationToken).ConfigureAwait(false);

        if (lease is null)
        {
            return DurabilityErrors.LeaseNotHeld(instanceId);
        }

        return lease;
    }

    private static async ValueTask<FlowLease?> ReadLeaseAsync(
        NpgsqlCommand command,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadLease(reader, instanceId);
    }

    /// <summary>Reads the lease on the current row.</summary>
    private static FlowLease ReadLease(NpgsqlDataReader reader, Guid instanceId) => new(
        instanceId,
        reader.GetString(0),
        new FencingToken(reader.GetInt64(1)),
        Db.ReadTimestamp(reader, 2));

    /// <summary>
    /// Who holds the lease, for the refusal message.
    /// </summary>
    /// <remarks>
    /// A second read, and only on the path that is already returning an error. Folding it
    /// into the acquire statement would put a join on the success path to improve a
    /// sentence nobody reads when it succeeds.
    /// </remarks>
    private async ValueTask<string> OwnerAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var held = await ReadAsync(instanceId, cancellationToken).ConfigureAwait(false);

        return held.IsSuccess ? held.Value.OwnerNode : "another node";
    }
}
