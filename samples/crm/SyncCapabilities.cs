using FlowX;

namespace Crm;

/// <summary>
/// Tells a client what changed since it last looked, deletes included.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is not a query with a date filter.</strong> Two things a filter cannot do. It
/// cannot report a deletion, because there is no row left to match it, so a client that saw a
/// record once keeps it forever. And a timestamp is not a safe place to resume from: transactions
/// take their clock at the start and can commit in the other order, so a client that read up to
/// the later stamp will never see the earlier row. <c>0012_crm_change_feed.sql</c> says what is
/// used instead of a clock.
/// </para>
/// <para>
/// <strong>The redaction is the query surface's, applied again here.</strong> A change feed that
/// returned unmasked values would be a way around the read policy that happens to be spelled
/// differently, and it is the second projection of custom values — <c>CustomFieldPolicy.Mask</c>
/// is why that is one rule rather than two.
/// </para>
/// </remarks>
[Capability("crm.custom.sync", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.read",
    Idempotent = true)]
public sealed class ReadRecordChanges : ICapability<ReadChanges, ChangePage>
{
    private readonly CustomSchemaStore _schema;
    private readonly SyncStore _sync;

    /// <summary>Creates the capability.</summary>
    /// <param name="schema">Reads the declarations, for what may be masked.</param>
    /// <param name="sync">Reads the change log.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public ReadRecordChanges(CustomSchemaStore schema, SyncStore sync)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(sync);

        _schema = schema;
        _sync = sync;
    }

    /// <inheritdoc />
    public async ValueTask<Result<ChangePage>> ExecuteAsync(
        ReadChanges input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var query = input.Query;

        if (query.Limit is < 1 or > SyncLimits.Max)
        {
            return Result.Fail<ChangePage>(SyncErrors.LimitIsOutOfRange(query.Limit));
        }

        // A cursor this API did not issue is refused rather than treated as "from the beginning".
        // Starting over silently is how a client that corrupts its stored cursor re-downloads the
        // whole tenant on every launch and nobody finds out.
        (ulong Xact, long Seq) since = (0, 0);

        if (query.Since is { Length: > 0 } cursor)
        {
            if (SyncCursor.Read(cursor) is not { } read)
            {
                return Result.Fail<ChangePage>(SyncErrors.CursorIsNotUsable(cursor));
            }

            since = read;
        }

        if (query.Target is { } target
            && !await _schema.HasObjectAsync(ctx.TenantId, target, ct).ConfigureAwait(false))
        {
            return Result.Fail<ChangePage>(CustomSchemaErrors.ObjectNotFound(target));
        }

        var (rows, watermark) = await _sync
            .ChangesAsync(ctx.TenantId, query.Target, since, query.Limit, ct)
            .ConfigureAwait(false);

        var redacted = new SortedSet<string>(StringComparer.Ordinal);
        var declarations = new Dictionary<Guid, IReadOnlyDictionary<string, CustomFieldRow>>();
        var changes = new List<RecordChange>(rows.Count);

        foreach (var row in rows)
        {
            IReadOnlyDictionary<string, string?>? values = null;

            // A deleted record carries no values, and does not get an empty map either: a client
            // told the values are empty writes empty values over its copy, which is worse than
            // being told nothing at all.
            if (row.Values is { } stored)
            {
                if (!declarations.TryGetValue(row.ObjectId, out var declared))
                {
                    declared = await _schema
                        .FieldsForAsync(ctx.TenantId, row.ObjectId, ct)
                        .ConfigureAwait(false);

                    declarations[row.ObjectId] = declared;
                }

                values = CustomFieldPolicy.Mask(
                    declared, CustomValues.FromJson(stored), input.Scopes, redacted);
            }

            changes.Add(new RecordChange(
                row.RecordId, row.ObjectId, row.Kind, values, row.ChangedAt));
        }

        // The cursor is the last change served, or the watermark the read was taken against when
        // nothing was. Always one or the other, so a first sync that finds nothing still comes
        // back knowing where "now" was.
        var next = rows.Count > 0
            ? SyncCursor.For(rows[^1].Xact, rows[^1].Seq)
            : SyncCursor.For(watermark, 0);

        return Result.Ok(new ChangePage(changes, next, rows.Count == query.Limit, [.. redacted]));
    }
}

/// <summary>
/// Deletes a record, and leaves a tombstone behind so every client learns it is gone.
/// </summary>
/// <remarks>
/// <strong><c>crm.write</c>, the same grant as writing one.</strong> A separate delete grant was
/// considered and is not what this sample argues for: a caller who can overwrite every field of a
/// record can already destroy it, and a permission that stops the honest half of that is a
/// permission whose name overstates it.
/// </remarks>
[Capability("crm.custom.record.delete", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.write",
    Idempotent = true)]
public sealed class DeleteCustomRecord : ICapability<DeleteRecord, RecordDeleted>
{
    private readonly SyncStore _sync;

    /// <summary>Creates the capability.</summary>
    /// <param name="sync">Performs the delete the log's trigger observes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sync"/> is null.</exception>
    public DeleteCustomRecord(SyncStore sync)
    {
        ArgumentNullException.ThrowIfNull(sync);

        _sync = sync;
    }

    /// <inheritdoc />
    public async ValueTask<Result<RecordDeleted>> ExecuteAsync(
        DeleteRecord input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        // Nothing to delete is success and not a 404. A client retrying a delete whose response it
        // lost would otherwise have to tell "already done" from "never existed", and it cannot.
        var deleted = await _sync
            .DeleteAsync(ctx.TenantId, input.RecordId, ct)
            .ConfigureAwait(false);

        return Result.Ok(new RecordDeleted(input.RecordId, deleted));
    }
}
