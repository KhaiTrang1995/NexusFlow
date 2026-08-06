using FlowX;

namespace Crm;

/// <summary>How the criteria of one filter combine.</summary>
/// <remarks>
/// <strong>Two connectives and no nesting.</strong> <c>(a AND b) OR (c AND d)</c> is a tree, and a
/// tree is a parser, an evaluator and a precedence rule — the expression language
/// <see cref="ProcessRules"/> spends its remarks refusing. A flat list joined by one connective
/// covers what a list actually needs and stays checkable in one pass.
/// </remarks>
public enum FilterMatch
{
    /// <summary>Every criterion must hold.</summary>
    All = 0,

    /// <summary>At least one must.</summary>
    Any = 1,
}

/// <summary>Which records a query returns.</summary>
/// <param name="Match">How the criteria combine.</param>
/// <param name="Criteria">
/// The criteria. Each is the same (field, operator, value) triple a transition guard, a
/// validation rule and a roll-up filter carry, and the operators are the same five — which is
/// what keeps four features sharing one vocabulary rather than four dialects of one.
/// </param>
public sealed record RecordFilter(FilterMatch Match, IReadOnlyList<RollupFilter> Criteria);

/// <summary>How a query sorts.</summary>
/// <param name="Field">Which field to order by.</param>
/// <param name="Descending">Largest first.</param>
/// <param name="Numeric">
/// Whether to sort as numbers. Stored rather than derived from the field's declared type, because
/// a row whose value will not cast would otherwise throw and fail the whole page; the numeric
/// orderings strip what will not parse and sort those last.
/// </param>
public sealed record RecordOrder(string Field, bool Descending, bool Numeric);

/// <summary>Searches every entity this tenant has for a phrase.</summary>
/// <param name="Phrase">What to look for. Parsed by <c>websearch_to_tsquery</c>.</param>
/// <param name="Limit">How many hits at most.</param>
public sealed record SearchEverything(string Phrase, int Limit);

/// <summary>One thing the search found.</summary>
/// <param name="Kind">
/// What sort of thing it is: one of <see cref="EntityKind"/>, or <c>CustomRecord</c>.
/// </param>
/// <param name="Id">The row, for a caller to go and read properly.</param>
/// <param name="Title">
/// What to show in a result list. Built from the entity's own columns, never from a custom field.
/// </param>
public sealed record SearchHit(string Kind, Guid Id, string Title);

/// <summary>What the search found.</summary>
/// <param name="Hits">The matches, most relevant first.</param>
public sealed record SearchResults(IReadOnlyList<SearchHit> Hits);

// -------------------------------------------------------------------------------- what is asked

/// <summary>How a saved view is drawn.</summary>
/// <remarks>
/// <strong>The view says, rather than each client deciding.</strong> A client that receives a
/// query and nothing else has to choose between a table, a board and a stack of cards by itself —
/// so the choice gets made once per client, differently, and the same saved view looks like three
/// different features. Three shapes and no more: adding a fourth is a code change, which is what
/// stops the settings screen from growing a layout engine.
/// </remarks>
public enum ViewKind
{
    /// <summary>A table. The default, and what every view saved before this meant.</summary>
    List = 0,

    /// <summary>A board of lanes, grouped by one field's value.</summary>
    Kanban = 1,

    /// <summary>A stack of cards, each showing a title and optionally a second line.</summary>
    Card = 2,
}

/// <summary>How a view is drawn, and what that shape needs to know.</summary>
/// <param name="Kind">Which shape.</param>
/// <param name="GroupBy">
/// <see cref="ViewKind.Kanban"/>: the field whose value is the lane. Required for a board — a
/// board with nothing to group by cannot be drawn at all — and refused for the other two.
/// </param>
/// <param name="Lanes">
/// <see cref="ViewKind.Kanban"/>: the order the lanes appear in. Empty means whatever order the
/// values come back in, which is fine for a board of two and unreadable for a board of nine.
/// </param>
/// <param name="WipLimit">
/// <see cref="ViewKind.Kanban"/>: what counts as too many cards in one lane, or null for no limit.
/// Reported, never enforced: a limit that refused a write would make a layout setting a business
/// rule, and the person who set it was arranging a screen.
/// </param>
/// <param name="TitleField"><see cref="ViewKind.Card"/>: the card's first line. Required.</param>
/// <param name="SubtitleField"><see cref="ViewKind.Card"/>: its second line, or null.</param>
/// <param name="Columns">
/// <see cref="ViewKind.List"/>: which fields are columns, in order. Empty means every declared
/// field, which is what a view saved before this meant and still means.
/// </param>
public sealed record ViewLayout(
    ViewKind Kind,
    string? GroupBy = null,
    IReadOnlyList<string>? Lanes = null,
    int? WipLimit = null,
    string? TitleField = null,
    string? SubtitleField = null,
    IReadOnlyList<string>? Columns = null);

/// <summary>Saves a named query over a custom object.</summary>
/// <param name="Target">Which object.</param>
/// <param name="Name">The identifier a caller asks for. Lower case, snake case.</param>
/// <param name="Label">What a person sees.</param>
/// <param name="Filter">Which records, or null for all of them.</param>
/// <param name="Order">How to sort, or null for insertion order.</param>
/// <param name="Limit">How many rows at most.</param>
/// <param name="Layout">How it is drawn, or null for a plain table of every field.</param>
public sealed record DefineListView(
    Guid Target,
    string Name,
    string Label,
    RecordFilter? Filter,
    RecordOrder? Order,
    int Limit,
    ViewLayout? Layout = null);

/// <summary>The view that was saved.</summary>
/// <param name="ViewId">Its id.</param>
/// <param name="Name">Its name.</param>
public sealed record ListViewDefined(Guid ViewId, string Name);

/// <summary>Asks for records of a custom object.</summary>
/// <param name="Target">Which object, or null when <paramref name="View"/> names one.</param>
/// <param name="View">A saved view's name, or null for an ad-hoc query.</param>
/// <param name="Filter">Which records. Ignored when <paramref name="View"/> is given.</param>
/// <param name="Limit">How many rows at most. Ignored when <paramref name="View"/> is given.</param>
/// <remarks>
/// <strong>A view or an object, never both, and the capability refuses both.</strong> A request
/// that named a saved view and an ad-hoc filter would have two answers about which wins, and
/// whichever this build picked would surprise half its callers.
/// </remarks>
/// <param name="After">
/// The cursor a previous page returned, or null for the first page. Keyset rather than an offset:
/// an <c>OFFSET</c> re-reads and re-skips every row before it, so page 50 costs fifty times page
/// 1 and a row inserted meanwhile shifts every later page by one — which a scrolling list shows
/// as a duplicate.
/// </param>
public sealed record QueryRecords(
    Guid? Target,
    string? View,
    RecordFilter? Filter,
    int Limit,
    string? After = null);

/// <summary>What the query capability is given, once the flow has read the caller.</summary>
/// <param name="Query">What was asked for.</param>
/// <param name="Scopes">
/// The grants the caller holds, from their claims and never from the body. What
/// <see cref="CustomFieldRow.ReadPermission"/> is checked against.
/// </param>
public sealed record ReadObjectRecords(QueryRecords Query, IReadOnlyList<string> Scopes);

/// <summary>One record, as much of it as this caller may see.</summary>
/// <param name="RecordId">The record.</param>
/// <param name="Values">Its values, with anything they may not read redacted.</param>
public sealed record RecordView(Guid RecordId, IReadOnlyDictionary<string, string?> Values);

/// <summary>What the query found.</summary>
/// <param name="Records">The rows, in the order the view asked for.</param>
/// <param name="Redacted">
/// The fields that were withheld from at least one row, named. <strong>Named on purpose:</strong>
/// a caller who cannot tell a redacted field from an absent one cannot tell a permissions problem
/// from a data problem, and will open a ticket about the wrong one.
/// </param>
/// <param name="NextCursor">
/// What to send as <c>After</c> for the next page, or null when this was the last one. Null is
/// decided by having fetched fewer rows than were asked for, so a caller never makes a request
/// that returns nothing.
/// </param>
public sealed record RecordPage(
    IReadOnlyList<RecordView> Records,
    IReadOnlyList<string> Redacted,
    string? NextCursor = null);

// ------------------------------------------------------------------------------- what can go wrong

/// <summary>Refusals the query surface can produce.</summary>
public static class QueryErrors
{
    /// <summary>A layout setting was given for a shape that has no use for it.</summary>
    /// <param name="kind">Which shape.</param>
    /// <param name="setting">What was set.</param>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// Refused rather than ignored. A stored setting nothing reads is a setting somebody changes,
    /// saves and watches do nothing — and the next person spends an afternoon on it.
    /// </remarks>
    public static Error SettingIsNotOfKind(ViewKind kind, string setting) =>
        new(
            "crm.view_setting_not_of_kind",
            $"A {kind} view has no '{setting}'.",
            ErrorCategory.Validation);

    /// <summary>A shape was saved without what it cannot be drawn without.</summary>
    /// <param name="kind">Which shape.</param>
    /// <param name="setting">What is missing.</param>
    /// <returns>The refusal.</returns>
    public static Error KindNeedsItsSetting(ViewKind kind, string setting) =>
        new(
            "crm.view_setting_missing",
            $"A {kind} view cannot be drawn without '{setting}'.",
            ErrorCategory.Validation);

    /// <summary>The work-in-progress limit was zero or negative.</summary>
    /// <param name="limit">What was asked for.</param>
    /// <returns>The refusal.</returns>
    public static Error WipLimitIsNotUsable(int limit) =>
        new(
            "crm.view_wip_limit_not_usable",
            $"A lane limit is at least one, and {limit} was set. Leave it out for no limit.",
            ErrorCategory.Validation);

    /// <summary>The request named a saved view and an object, or neither.</summary>
    public static Error AskForOneOrTheOther() =>
        new(
            "crm.query_ambiguous",
            "A query names a saved view or an object, and this named neither or both.",
            ErrorCategory.Validation);

    /// <summary>That view is not in this tenant.</summary>
    /// <param name="name">What was named.</param>
    public static Error ViewNotFound(string name) =>
        new Error(
            "crm.list_view_not_found",
            "That view is not in this tenant.",
            ErrorCategory.NotFound)
            .With("view", name);

    /// <summary>The filter holds more criteria than this build will evaluate.</summary>
    /// <param name="count">How many were sent.</param>
    public static Error TooManyCriteria(int count) =>
        new Error(
            "crm.query_too_many_criteria",
            $"A filter holds at most {QueryLimits.MaxCriteria} criteria, and {count} were sent.",
            ErrorCategory.Validation)
            .With("criteria", count)
            .With("max", QueryLimits.MaxCriteria);

    /// <summary>A search was asked for with nothing to look for.</summary>
    public static Error PhraseIsEmpty() =>
        new(
            "crm.search_phrase_empty",
            "A search needs something to look for.",
            ErrorCategory.Validation);

    /// <summary>A cursor was sent with an ordering that cannot be resumed from one.</summary>
    /// <remarks>
    /// <strong>Refused rather than ignored.</strong> Keyset pagination resumes from the last row's
    /// sort key, and this build stores a cursor of <c>(created_at, record_id)</c> — which resumes
    /// insertion order exactly and any other order not at all. Ignoring the cursor would restart
    /// an ordered list at the top on every scroll; honouring it against the wrong key would skip
    /// and repeat rows. Ordered pagination needs the sort value in the cursor and a comparison per
    /// ordering, which is four more statements and is not built.
    /// </remarks>
    public static Error CursorNeedsInsertionOrder() =>
        new(
            "crm.query_cursor_needs_insertion_order",
            "A cursor resumes insertion order, and this query is sorted. Ask for the sorted page " +
            "without a cursor, or drop the ordering.",
            ErrorCategory.Validation);

    /// <summary>The cursor is not one this build issued.</summary>
    /// <param name="cursor">What was sent.</param>
    public static Error CursorIsNotUsable(string cursor) =>
        new Error(
            "crm.query_cursor_not_usable",
            "That cursor is not one this API issued.",
            ErrorCategory.Validation)
            .With("cursor", cursor);

    /// <summary>The limit is outside what this build will serve.</summary>
    /// <param name="limit">What was asked for.</param>
    /// <remarks>
    /// Bounded rather than clamped. A caller who asked for ten thousand rows and silently got
    /// five hundred would page through the same five hundred for ever, believing they had them
    /// all — which is the failure mode a clamp always has and an error never does.
    /// </remarks>
    public static Error LimitIsOutOfRange(int limit) =>
        new Error(
            "crm.query_limit_out_of_range",
            $"A query returns between 1 and {QueryLimits.Max} rows, and {limit} was asked for.",
            ErrorCategory.Validation)
            .With("limit", limit)
            .With("max", QueryLimits.Max);
}

/// <summary>How much one query may return.</summary>
public static class QueryLimits
{
    /// <summary>The most criteria one filter may hold.</summary>
    /// <remarks>
    /// Bounded because every criterion is a jsonb extraction per row, and an unbounded list is a
    /// caller's way of asking for an unbounded scan. Twelve is more than any list view anybody
    /// has built and small enough that the cost is a constant.
    /// </remarks>
    public const int MaxCriteria = 12;

    /// <summary>The most rows one query returns.</summary>
    /// <remarks>
    /// The same ceiling migration <c>0009</c> puts on a saved view's <c>row_limit</c>, so a view
    /// cannot be saved asking for more than a request may ask for. Keyset pagination is the right
    /// answer past this and it is not built; a bounded honest answer beats an unbounded one.
    /// </remarks>
    public const int Max = 500;
}


/// <summary>
/// The cursor a page hands back, and what it resumes from.
/// </summary>
/// <remarks>
/// <strong>Opaque to the caller and cheap to parse.</strong> A client that took the cursor apart
/// would depend on the keyset this build happens to use, which is exactly the thing that changes
/// when ordered pagination arrives. It is base64 so it survives a URL and reads as an opaque
/// token; it is not encrypted, and it carries nothing a caller could not already see.
/// </remarks>
public static class RecordCursor
{
    /// <summary>The cursor for a row.</summary>
    /// <param name="createdAt">When the row was written.</param>
    /// <param name="recordId">Which row.</param>
    /// <returns>The token.</returns>
    public static string For(DateTimeOffset createdAt, Guid recordId) =>
        Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(
                createdAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ":" + recordId.ToString("n")));

    /// <summary>Reads a cursor, or reports that it is not one of ours.</summary>
    /// <param name="cursor">The token.</param>
    /// <returns>What it resumes from, or null when it is unreadable.</returns>
    public static (DateTimeOffset CreatedAt, Guid RecordId)? Read(string cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        try
        {
            var parts = System.Text.Encoding.UTF8
                .GetString(Convert.FromBase64String(cursor))
                .Split(':');

            if (parts.Length != 2 ||
                !long.TryParse(parts[0], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var ticks) ||
                !Guid.TryParseExact(parts[1], "N", out var id))
            {
                return null;
            }

            return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }
        catch (FormatException)
        {
            // Not base64. A caller's mistake rather than a fault, and the capability turns it
            // into an error naming the cursor.
            return null;
        }
    }
}
