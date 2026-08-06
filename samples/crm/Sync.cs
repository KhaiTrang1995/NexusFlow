using System.Globalization;
using FlowX;

namespace Crm;

// -------------------------------------------------------------------------------- what is asked

/// <summary>Asks what changed since a client last looked.</summary>
/// <param name="Target">One object, or null for every object this tenant has.</param>
/// <param name="Since">
/// The cursor from the previous call, or null for a first sync. <strong>A cursor and not a
/// timestamp:</strong> two writers take their clock at the start of a transaction and can commit
/// in the other order, so a client that read up to the later stamp never sees the earlier row
/// again. <c>0012_crm_change_feed.sql</c> says what is used instead.
/// </param>
/// <param name="Limit">How many changes at most. The client keeps calling until it is caught up.</param>
public sealed record SyncChanges(Guid? Target, string? Since, int Limit);

/// <summary>What the sync capability is given, once the flow has read the caller.</summary>
/// <param name="Query">What was asked for.</param>
/// <param name="Scopes">The grants the caller holds, from their claims and never from the body.</param>
public sealed record ReadChanges(SyncChanges Query, IReadOnlyList<string> Scopes);

/// <summary>Asks for a record to be forgotten.</summary>
/// <param name="RecordId">Which one.</param>
public sealed record DeleteRecord(Guid RecordId);

/// <summary>The record is gone, and every client will be told so.</summary>
/// <param name="RecordId">Which one.</param>
/// <param name="Deleted">
/// False when there was nothing to delete. Not an error: a client retrying a delete it already
/// made would otherwise have to treat success and "already done" differently.
/// </param>
public sealed record RecordDeleted(Guid RecordId, bool Deleted);

// ------------------------------------------------------------------------------- what comes back

/// <summary>One thing that happened to one record.</summary>
/// <param name="RecordId">Which record.</param>
/// <param name="ObjectId">Which object it belonged to. Still populated after a delete.</param>
/// <param name="Kind"><c>Upserted</c> or <c>Deleted</c>.</param>
/// <param name="Values">
/// The record as it stands now, redacted the same way a query would redact it — or null when the
/// kind is <c>Deleted</c>, because there is nothing left to read and a client that is told the
/// values are empty would write empty values over its copy.
/// </param>
/// <param name="ChangedAt">
/// When it happened, for display. <strong>Not for resuming</strong> — <see cref="ChangePage.Cursor"/>
/// is what does that, and mixing the two is how a sync loses a row.
/// </param>
public sealed record RecordChange(
    Guid RecordId,
    Guid ObjectId,
    string Kind,
    IReadOnlyDictionary<string, string?>? Values,
    DateTimeOffset ChangedAt);

/// <summary>What changed, and where to resume.</summary>
/// <param name="Changes">The changes, oldest first.</param>
/// <param name="Cursor">
/// Where to resume, <strong>always present</strong> — including when nothing changed. A feed that
/// returned no cursor on an empty page would leave a client that syncs before its first write with
/// nothing to send next time, and it would start again from the beginning of history.
/// </param>
/// <param name="HasMore">
/// Whether a further call would return more immediately. A client drains this before it stops,
/// rather than waiting for the next poll and staying a page behind for as long as it is running.
/// </param>
/// <param name="Redacted">The fields withheld from at least one change, named.</param>
public sealed record ChangePage(
    IReadOnlyList<RecordChange> Changes,
    string Cursor,
    bool HasMore,
    IReadOnlyList<string> Redacted);

// -------------------------------------------------------------------------------- the cursor

/// <summary>Where a sync got to, as one opaque string.</summary>
/// <remarks>
/// Opaque on purpose. The pair inside is a transaction id and a position within it, and a client
/// that parsed either would be depending on how this build orders its change log — which is the
/// thing most likely to change and the change least likely to be noticed.
/// </remarks>
public static class SyncCursor
{
    /// <summary>Writes the cursor a client sends back.</summary>
    /// <param name="xact">The transaction id read up to.</param>
    /// <param name="seq">The position within it, or 0 for "the whole of it".</param>
    /// <returns>The opaque cursor.</returns>
    public static string For(ulong xact, long seq) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{xact}:{seq}")));

    /// <summary>Reads a cursor this API issued, or refuses one it did not.</summary>
    /// <param name="cursor">What the client sent.</param>
    /// <returns>The pair, or null when the cursor is not one of ours.</returns>
    public static (ulong Xact, long Seq)? Read(string? cursor)
    {
        if (cursor is not { Length: > 0 })
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[64];

        if (!Convert.TryFromBase64String(cursor, buffer, out var written))
        {
            return null;
        }

        var text = System.Text.Encoding.UTF8.GetString(buffer[..written]);
        var split = text.IndexOf(':', StringComparison.Ordinal);

        if (split <= 0
            || !ulong.TryParse(text[..split], CultureInfo.InvariantCulture, out var xact)
            || !long.TryParse(text[(split + 1)..], CultureInfo.InvariantCulture, out var seq)
            || seq < 0)
        {
            return null;
        }

        return (xact, seq);
    }
}

// ------------------------------------------------------------------------------- what can go wrong

/// <summary>Refusals the sync surface can produce.</summary>
public static class SyncErrors
{
    /// <summary>The cursor was not one this API issued.</summary>
    /// <param name="cursor">What was sent.</param>
    /// <returns>The refusal.</returns>
    public static Error CursorIsNotUsable(string cursor) =>
        new(
            "crm.sync_cursor_unusable",
            $"'{cursor}' is not a cursor this API issued. Sync from the beginning by omitting it.",
            ErrorCategory.Validation);

    /// <summary>The page size was outside what this surface serves.</summary>
    /// <param name="limit">What was asked for.</param>
    /// <returns>The refusal.</returns>
    public static Error LimitIsOutOfRange(int limit) =>
        new(
            "crm.sync_limit_out_of_range",
            $"A sync page is between 1 and {SyncLimits.Max} changes, and {limit} was asked for.",
            ErrorCategory.Validation);
}

/// <summary>What the sync surface will serve at once.</summary>
public static class SyncLimits
{
    /// <summary>The most changes one call returns.</summary>
    /// <remarks>
    /// Higher than a list page, because a client catching up after a week is drawing nothing and
    /// paying per round trip, where a list page is drawing rows on a screen.
    /// </remarks>
    public const int Max = 500;
}
