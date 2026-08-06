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

/// <summary>Saves a named query over a custom object.</summary>
/// <param name="Target">Which object.</param>
/// <param name="Name">The identifier a caller asks for. Lower case, snake case.</param>
/// <param name="Label">What a person sees.</param>
/// <param name="Filter">Which records, or null for all of them.</param>
/// <param name="OrderBy">Which field to order by, or null for insertion order.</param>
/// <param name="Limit">How many rows at most.</param>
public sealed record DefineListView(
    Guid Target,
    string Name,
    string Label,
    RecordFilter? Filter,
    string? OrderBy,
    int Limit);

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
public sealed record QueryRecords(
    Guid? Target,
    string? View,
    RecordFilter? Filter,
    int Limit);

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
public sealed record RecordPage(
    IReadOnlyList<RecordView> Records,
    IReadOnlyList<string> Redacted);

// ------------------------------------------------------------------------------- what can go wrong

/// <summary>Refusals the query surface can produce.</summary>
public static class QueryErrors
{
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
