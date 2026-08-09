using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// Reads the change log, and deletes a record so that the log records it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The watermark is read in the same statement as the rows.</strong> Reading it first and
/// filtering on it second would put a commit between the two: a transaction that finished in the
/// gap would raise the watermark the client is told about while its changes were excluded from
/// the rows the client is given, and the client would never come back for them.
/// </para>
/// <para>
/// <strong>Nothing here masks.</strong> The capability does that, on what it returns, for the same
/// reason <c>QueryStore</c> does not.
/// </para>
/// </remarks>
public sealed class SyncStore
{
    // `pg_snapshot_xmin(pg_current_snapshot())` is the id below which no transaction is still
    // running. Serving strictly below it is what makes the cursor safe; 0012's header says why.
    //
    // THE WATERMARK COMES BACK IN THE SAME STATEMENT, AND ON THE EMPTY PAGE TOO — which is what
    // the `LEFT JOIN served ON true` is for: the watermark row survives having no changes to join
    // to. Reading it in a second statement instead would put a commit between the two, so a
    // transaction that finished in the gap would raise the watermark the client is told about
    // while its changes were excluded from the rows the client was given. The client would move
    // its cursor past them and never come back.
    private const string ChangesHead = """
        WITH watermark AS (SELECT pg_snapshot_xmin(pg_current_snapshot()) AS mark),
        served AS (
            SELECT c.record_id, c.object_id, c.kind, c.changed_at, c.xact_id, c.seq
            FROM custom_change c
            WHERE c.xact_id < (SELECT mark FROM watermark)
              AND (c.xact_id, c.seq) > (@sinceXact::xid8, @sinceSeq)
        """;

    private const string ForOneObject = "\n              AND c.object_id = @object";

    private const string ChangesTail = """

            ORDER BY c.xact_id, c.seq
            LIMIT @limit
        )
        SELECT w.mark::text, s.record_id, s.object_id, s.kind, s.changed_at,
               s.xact_id::text, s.seq, r.values::text
        FROM watermark w
        LEFT JOIN served s ON true
        LEFT JOIN custom_record r ON r.record_id = s.record_id
        ORDER BY s.xact_id, s.seq
        """;

    private const string AllChanges = ChangesHead + ChangesTail;

    private const string ObjectChanges = ChangesHead + ForOneObject + ChangesTail;

    private const string DeleteRecordById = "DELETE FROM custom_record WHERE record_id = @id";

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public SyncStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Reads what changed after a cursor.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="target">One object, or null for all of them.</param>
    /// <param name="since">Where the client got to.</param>
    /// <param name="limit">How many changes at most.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The changes oldest first, and the watermark the read was taken against — which is the
    /// cursor to give back when no change was served.
    /// </returns>
    public async ValueTask<(IReadOnlyList<ChangeRow> Rows, ulong Watermark)> ChangesAsync(
        string? tenantId,
        Guid? target,
        (ulong Xact, long Seq) since,
        int limit,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = target is null ? AllChanges : ObjectChanges;

        // xid8 has no Npgsql parameter type in this version, so the pair travels as text and the
        // statement casts it back. The comparison is still on xid8 and not on the text, which is
        // what matters: '10' sorts before '9' as text and after it as a transaction id.
        command.Parameters.Add(new NpgsqlParameter("sinceXact", NpgsqlDbType.Text)
        {
            Value = since.Xact.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

        command.Parameters.Add(new NpgsqlParameter("sinceSeq", NpgsqlDbType.Bigint)
        {
            Value = since.Seq,
        });

        command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = limit });

        if (target is { } one)
        {
            command.Parameters.Add(new NpgsqlParameter("object", NpgsqlDbType.Uuid) { Value = one });
        }

        var rows = new List<ChangeRow>();
        ulong mark = 0;

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            mark = ulong.Parse(
                reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture);

            // The one row the watermark always produces, with nothing joined to it.
            if (await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var changedAt = await reader
                .GetFieldValueAsync<DateTimeOffset>(4, cancellationToken)
                .ConfigureAwait(false);

            rows.Add(new ChangeRow(
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3),
                changedAt,
                ulong.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetInt64(6),
                await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetString(7)));
        }

        return (rows, mark);
    }

    /// <summary>Deletes a record, which the log's trigger turns into a tombstone.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="recordId">Which record.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether a row was there to delete.</returns>
    public async ValueTask<bool> DeleteAsync(
        string? tenantId,
        Guid recordId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = DeleteRecordById;
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = recordId });

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private ValueTask<NpgsqlConnection> OpenAsync(string? tenantId, CancellationToken cancellationToken) =>
        CrmTenantScope.OpenAsync(_source, tenantId, cancellationToken);
}

/// <summary>One row of the change log, as stored.</summary>
/// <param name="RecordId">Which record.</param>
/// <param name="ObjectId">Which object it belonged to.</param>
/// <param name="Kind"><c>Upserted</c> or <c>Deleted</c>.</param>
/// <param name="ChangedAt">When.</param>
/// <param name="Xact">The transaction that wrote it.</param>
/// <param name="Seq">Its position within that transaction.</param>
/// <param name="Values">The record as it stands, or null when it is gone.</param>
public sealed record ChangeRow(
    Guid RecordId,
    Guid ObjectId,
    string Kind,
    DateTimeOffset ChangedAt,
    ulong Xact,
    long Seq,
    string? Values);
