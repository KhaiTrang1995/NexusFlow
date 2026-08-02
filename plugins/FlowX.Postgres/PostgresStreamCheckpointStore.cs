using Npgsql;

namespace FlowX.Postgres;

/// <summary>
/// Keeps one stream subscription's position in <c>stream_checkpoint</c>, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deliberately the smallest store in this adapter.</strong> One row, one opaque string,
/// two statements. Everything a stream engine could have persisted instead — open windows, partial
/// aggregates, a watermark — is reconstructed by re-reading the source from this position, which
/// is the decision
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0055-a-window-names-the-instance-it-starts.md">ADR-0055</a>
/// records and the reason it costs a table rather than a subsystem.
/// </para>
/// <para>
/// <strong>The position is never interpreted.</strong> It is whatever the <see cref="IStreamSource"/>
/// rendered — a Redis stream id, a Kafka offset, a file byte count — and this class stores and
/// returns it verbatim. Ordering is not enforced here and could not be; see the migration's own
/// comment for why, and for what enforces it instead.
/// </para>
/// <para>
/// <strong>No transaction and no fence.</strong> A checkpoint is not part of a journal commit:
/// it is written after the windows' flows have already committed, and writing the two together
/// would make the checkpoint a hostage to the last window's transaction. A node that dies between
/// the two re-reads and deduplicates, which is the arrangement every other trigger in this
/// platform already uses (ADR-0035, ADR-0048, ADR-0049).
/// </para>
/// <para>
/// <strong>A store that is unreachable throws</strong>, matching <see cref="PostgresChangeFeed"/>
/// rather than the <c>Result</c> the interface allows for. <c>FlowStreamService</c> catches and
/// passes again in a second, which is the same answer a returned error would produce and one
/// fewer classification to keep in step with the driver's exception hierarchy.
/// </para>
/// </remarks>
public sealed class PostgresStreamCheckpointStore : IStreamCheckpointStore
{
    private const string Read =
        "SELECT position FROM stream_checkpoint WHERE subscription_id = $1";

    /// <remarks>
    /// <c>IS DISTINCT FROM</c> in the <c>WHERE</c> of the update makes a repeated commit of the
    /// same position report <c>false</c> without writing a row — which is what
    /// <see cref="IStreamCheckpointStore.CommitAsync"/> promises, and it also keeps
    /// <c>updated_at</c> honest for the operator watching a subscription that has stopped moving.
    /// </remarks>
    private const string Commit =
        """
        INSERT INTO stream_checkpoint (subscription_id, position)
        VALUES ($1, $2)
        ON CONFLICT (subscription_id) DO UPDATE
            SET position = excluded.position, updated_at = now()
            WHERE stream_checkpoint.position IS DISTINCT FROM excluded.position
        """;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates a checkpoint store over one data source.</summary>
    /// <param name="dataSource">The data source. Its connection string must select the schema the migrations ran in.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is null.</exception>
    public PostgresStreamCheckpointStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public async ValueTask<Result<StreamPosition?>> ReadAsync(
        StreamSubscription subscription, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var closing = connection.ConfigureAwait(false);

        var command = new NpgsqlCommand(Read, connection);

        await using var disposing = command.ConfigureAwait(false);

        command.Parameters.AddWithValue(StreamCheckpointId.For(subscription));

        var stored = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return Result.Ok(stored is string position ? new StreamPosition(position) : (StreamPosition?)null);
    }

    /// <inheritdoc />
    public async ValueTask<Result<bool>> CommitAsync(
        StreamSubscription subscription, StreamPosition position, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var closing = connection.ConfigureAwait(false);

        var command = new NpgsqlCommand(Commit, connection);

        await using var disposing = command.ConfigureAwait(false);

        command.Parameters.AddWithValue(StreamCheckpointId.For(subscription));
        command.Parameters.AddWithValue(position.Value);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }
}

/// <summary>The key one stream subscription's checkpoint row is held under.</summary>
/// <remarks>
/// The same four terms the window's instance id is derived from, under a scope of their own so
/// that a checkpoint key can never collide with an instance id. Derived here rather than taken
/// from <c>FlowX.Runtime</c> because this package references only <c>FlowX.Abstractions</c>. The
/// derivation is <c>DerivedIdentity</c>'s, byte for byte — SHA-256 over NUL-separated terms, laid
/// out as a UUID version 8 — and nothing depends on the two agreeing: this key is read and
/// written only here, so a divergence would be invisible rather than wrong. It is the same
/// construction because an operator reading two tables should be able to recompute both the same
/// way.
/// </remarks>
internal static class StreamCheckpointId
{
    /// <summary>The scope term, which keeps these ids out of every other id space.</summary>
    public const string Scope = "flowx\0stream\0checkpoint";

    /// <summary>The id one subscription's row is keyed on.</summary>
    public static Guid For(StreamSubscription subscription)
    {
        var material =
            Scope + '\0' +
            subscription.FlowId + '\0' +
            subscription.FlowVersion + '\0' +
            subscription.Source + '\0' +
            subscription.Group + '\0';

        return AsVersion8(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>Lays the first sixteen bytes of a digest out as a UUID version 8, per RFC 9562.</summary>
    private static Guid AsVersion8(ReadOnlySpan<byte> digest)
    {
        Span<byte> bytes = stackalloc byte[16];

        digest[..16].CopyTo(bytes);

        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes, bigEndian: true);
    }
}
