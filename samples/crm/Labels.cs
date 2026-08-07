using FlowX;

namespace Crm;

/// <summary>Which built-in columns a tenant may rename.</summary>
/// <remarks>
/// <para>
/// <strong>A closed list, for the same reason the report vocabulary is closed.</strong> A tenant
/// renaming <c>lead.company</c> is renaming a column this build has; letting them name any string
/// would store labels for columns that do not exist, and a settings screen would show a growing
/// list of typos nobody can delete because nothing knows they are wrong.
/// </para>
/// <para>
/// <strong>Not every column is here.</strong> The keys, the foreign keys and the timestamps are
/// not things a person sees on a form, and offering to rename <c>tenant_id</c> would be offering
/// to rename something that never appears.
/// </para>
/// </remarks>
public static class EntityColumns
{
    /// <summary>The columns of a built-in entity that a person sees, and may rename.</summary>
    /// <param name="kind">Which entity.</param>
    /// <returns>The column names.</returns>
    public static IReadOnlyList<string> Of(EntityKind kind) => kind switch
    {
        EntityKind.Lead => ["company", "contact_name", "email", "source", "status", "score"],
        EntityKind.Account => ["name", "industry", "lifecycle"],
        EntityKind.Contact => ["full_name", "email", "phone", "title"],
        EntityKind.Opportunity =>
            ["name", "amount", "currency", "probability", "expected_close", "outcome"],
        _ => [],
    };

    /// <summary>The columns a read of this entity answers with, and may be filtered on.</summary>
    /// <param name="kind">Which entity.</param>
    /// <returns>The column names, in the order a list shows them.</returns>
    /// <remarks>
    /// <para>
    /// <strong>A different list from <see cref="Of"/>, and deliberately so.</strong> That one is
    /// what a person may rename — no keys, no foreign keys, no timestamps, because offering to
    /// rename <c>tenant_id</c> is offering to rename something nobody sees. This one is what a
    /// list view reads, and a list without the identifier it links by, the owner it filters on
    /// and the date it sorts by is not a list anybody can use.
    /// </para>
    /// <para>
    /// <strong>Both are closed, for the same reason.</strong> A field name in a filter is a
    /// caller's value; membership of this array is the whole check that keeps it out of a
    /// statement. A name that came back from <c>information_schema</c> would be a name that
    /// exists, which is not the same as a name this surface offers.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Readable(ReadableEntity kind) => kind switch
    {
        ReadableEntity.Lead =>
            ["lead_id", "company", "contact_name", "email", "source", "status", "score",
             "owner_id", "captured_at"],
        ReadableEntity.Account =>
            ["account_id", "name", "industry", "lifecycle", "region", "owner_id"],
        ReadableEntity.Contact =>
            ["contact_id", "account_id", "full_name", "email", "phone", "is_primary"],
        ReadableEntity.Quote =>
            ["quote_id", "opportunity_id", "status", "subtotal", "discount", "total", "currency",
             "valid_until", "approved_by"],
        ReadableEntity.Order =>
            ["order_id", "quote_id", "account_id", "status", "total", "currency", "placed_at"],
        ReadableEntity.Activity =>
            ["activity_id", "kind", "subject", "relates_to_kind", "relates_to_id", "owner_id",
             "due_at", "status", "completed_at", "escalation_count"],
        _ =>
            ["opportunity_id", "account_id", "primary_contact_id", "name", "amount", "currency",
             "probability", "expected_close", "outcome", "owner_id", "stage_entered_at",

             // Not a column of `opportunity`. The store merges the configured process's stage
             // name into the projection, because an identifier is not something a board can
             // group by — see EntityQueryStore.OpportunityPage.
             "stage"],
    };

    /// <summary>Which column identifies a row, and therefore orders the keyset.</summary>
    /// <param name="kind">Which entity.</param>
    /// <returns>The primary key's name.</returns>
    public static string KeyOf(ReadableEntity kind) => Readable(kind)[0]!;

    /// <summary>Whether a read of this entity offers a column by that name.</summary>
    /// <param name="kind">Which entity.</param>
    /// <param name="field">What was asked for.</param>
    /// <returns>Whether it is offered.</returns>
    public static bool HasReadable(ReadableEntity kind, string field) =>
        Readable(kind).Contains(field, StringComparer.Ordinal);
}

// -------------------------------------------------------------------------------- what is asked

/// <summary>Renames one thing, in this tenant only.</summary>
/// <param name="Target">A custom object to rename, or null.</param>
/// <param name="Field">A custom field to rename, or null.</param>
/// <param name="Entity">A built-in entity to rename, or null.</param>
/// <param name="Column">
/// With <paramref name="Entity"/>: which of its columns, or null for the entity itself.
/// </param>
/// <param name="Label">What it is to be called here.</param>
/// <remarks>
/// <strong>One route and not three, because a settings screen has one "rename" button.</strong>
/// Exactly one of the three subjects is given, and the capability refuses the rest — a request
/// naming two subjects would have two answers about which was renamed.
/// </remarks>
public sealed record SetLabel(
    Guid? Target,
    Guid? Field,
    EntityKind? Entity,
    string? Column,
    string Label);

/// <summary>The thing was renamed.</summary>
/// <param name="Label">What it is now called.</param>
public sealed record LabelSet(string Label);

// ------------------------------------------------------------------------------- what can go wrong

/// <summary>Refusals the labelling surface can produce.</summary>
public static class LabelErrors
{
    /// <summary>The request named more than one subject, or none.</summary>
    public static Error NameOneSubject() =>
        new(
            "crm.label_ambiguous",
            "A rename names an object, a field or a built-in entity — one of the three.",
            ErrorCategory.Validation);

    /// <summary>A column was named without an entity to look it up on.</summary>
    public static Error ColumnNeedsItsEntity() =>
        new(
            "crm.label_column_without_entity",
            "A column belongs to an entity, and no entity was named.",
            ErrorCategory.Validation);

    /// <summary>The column is not one this entity has.</summary>
    /// <param name="kind">Which entity.</param>
    /// <param name="column">What was asked for.</param>
    /// <returns>The refusal.</returns>
    public static Error ColumnIsNotOfEntity(EntityKind kind, string column) =>
        new(
            "crm.label_column_unknown",
            $"'{column}' is not a column of {kind} that a person sees. It has: " +
            string.Join(", ", EntityColumns.Of(kind)) + ".",
            ErrorCategory.Validation);

    /// <summary>The label was empty or longer than the column holds.</summary>
    /// <param name="label">What was sent.</param>
    /// <returns>The refusal.</returns>
    public static Error LabelIsNotUsable(string label) =>
        new(
            "crm.label_not_usable",
            $"A label is between 1 and {LabelLimits.MaxLength} characters, and '{label}' is not.",
            ErrorCategory.Validation);

    /// <summary>There was nothing by that id to rename.</summary>
    public static Error SubjectNotFound() =>
        new(
            "crm.label_subject_not_found",
            "There is nothing by that id in this tenant to rename.",
            ErrorCategory.NotFound);
}

/// <summary>What a label may be.</summary>
public static class LabelLimits
{
    /// <summary>The longest label the column holds.</summary>
    public const int MaxLength = 128;

    /// <summary>What an entity's own label is stored under, the columns having names.</summary>
    /// <remarks>
    /// Empty and not null: a null cannot be part of a primary key, and a partial unique index over
    /// a nullable column would let one tenant give one entity two names.
    /// </remarks>
    public const string TheEntityItself = "";
}
