using FlowX;

namespace Crm;

// -------------------------------------------------------------------------------- what is asked

/// <summary>Reads a page of a built-in entity.</summary>
/// <param name="Entity">Which one.</param>
/// <param name="Filter">
/// What to keep, in the same five operators as everywhere else in this sample. Null means every
/// row this caller may see.
/// </param>
/// <param name="Limit">How many rows at most.</param>
/// <param name="After">
/// The cursor a previous page returned, or null for the first. Keyset rather than an offset, for
/// the reason <see cref="QueryRecords"/> gives at length: an <c>OFFSET</c> re-reads every row
/// before it, and a row inserted meanwhile shifts every later page by one — which a scrolling
/// list shows as a duplicate.
/// </param>
/// <remarks>
/// <para>
/// <strong>The gap this closes.</strong> This sample could declare an entity nobody had heard of
/// and page through its records, search five tables at once, and roll a quarter up — and could
/// not answer "give me a page of accounts". Everything a client did with a built-in entity went
/// through <c>search</c>, which answers with an identity and not a row, or through a report,
/// which answers with a group and not a record. A list view had nothing to read.
/// </para>
/// <para>
/// <strong>Four entities and not seven, deliberately.</strong> <see cref="EntityKind"/> is the
/// closed vocabulary the validation rules and the field policy already speak, and it names four.
/// Quotes, orders and activities would need it widened — which means the <c>applies_to</c> check
/// constraints in three migrations, and a rule written against an entity that had no rules
/// yesterday. That is a change worth making on its own, not as a side effect of adding a list.
/// </para>
/// </remarks>
public sealed record ReadEntityPage(
    EntityKind Entity,
    RecordFilter? Filter,
    int Limit,
    string? After = null);

/// <summary>What the entity read is given, once the flow has read the caller.</summary>
/// <param name="Query">What was asked for.</param>
/// <param name="Scopes">
/// The grants the caller holds, from their claims and never from the body. What a field's read
/// permission is checked against.
/// </param>
public sealed record ReadEntityRecords(ReadEntityPage Query, IReadOnlyList<string> Scopes);

// ------------------------------------------------------------------------------- what can go wrong

/// <summary>Refusals the built-in entity read can produce.</summary>
public static class EntityQueryErrors
{
    /// <summary>The filter names a column the entity does not have.</summary>
    /// <param name="entity">Which entity.</param>
    /// <param name="field">What was asked for.</param>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// <strong>The refusal that keeps this surface out of SQL.</strong> A field name reaching a
    /// statement is the injection this whole codebase is arranged to prevent; checking it against
    /// the entity's own closed column list means the name is either one of a handful of constants
    /// or it never reaches the database at all.
    /// </remarks>
    public static Error FieldIsNotOfEntity(EntityKind entity, string field) =>
        new(
            "crm.entity_field_unknown",
            $"'{field}' is not a field of {entity}. It has: " +
            string.Join(", ", EntityColumns.Readable(entity)) + ".",
            ErrorCategory.Validation);
}

/// <summary>What the built-in entity read accepts.</summary>
public static class EntityQueryLimits
{
    /// <summary>The most rows one page answers with.</summary>
    public const int MaxPage = 200;

    /// <summary>The most criteria one filter carries.</summary>
    public const int MaxCriteria = 10;
}
