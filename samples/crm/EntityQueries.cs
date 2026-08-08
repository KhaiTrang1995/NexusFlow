using FlowX;

namespace Crm;

// -------------------------------------------------------------------------------- what is asked

/// <summary>What a page can be asked for.</summary>
/// <remarks>
/// <para>
/// <strong>A vocabulary of its own, and not <see cref="EntityKind"/> widened.</strong>
/// <c>EntityKind</c> is what a validation rule, a custom field and a field policy are declared
/// against, and every one of those is a <c>CHECK (applies_to IN (…))</c> in a migration. Adding
/// <c>Quote</c> to it would mean somebody could declare a rule on an entity that has never had
/// one, in three migrations, to make a list screen work.
/// </para>
/// <para>
/// So what may be <em>read</em> is a superset of what may be <em>declared on</em>, said once,
/// here. The four that overlap map straight across; the three that do not are read-only and
/// stay that way until somebody has a reason for them not to be.
/// </para>
/// </remarks>
public enum ReadableEntity
{
    /// <summary>A lead.</summary>
    Lead = 0,

    /// <summary>An account.</summary>
    Account = 1,

    /// <summary>A contact.</summary>
    Contact = 2,

    /// <summary>An opportunity.</summary>
    Opportunity = 3,

    /// <summary>A quote.</summary>
    Quote = 4,

    /// <summary>An order.</summary>
    Order = 5,

    /// <summary>A task, call, meeting or note.</summary>
    Activity = 6,

    /// <summary>
    /// One priced line of a quote.
    /// </summary>
    /// <remarks>
    /// <strong>Its tenant is its quote's, and the policy of migration <c>0002</c> is what says
    /// so.</strong> The table carries no <c>tenant_id</c> of its own — the row-level policy hops
    /// through <c>quote</c> — so this is readable for the same reason and by the same rule as
    /// everything else here, not by an exception made for it.
    /// </remarks>
    QuoteLine = 7,
}

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
/// <strong>Seven, through a vocabulary of the read surface's own.</strong> See
/// <see cref="ReadableEntity"/>: quotes, orders and activities are readable without being
/// declarable, which is what stops a list screen turning into three migrations.
/// </para>
/// </remarks>
public sealed record ReadEntityPage(
    ReadableEntity Entity,
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
    public static Error FieldIsNotOfEntity(ReadableEntity entity, string field) =>
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
