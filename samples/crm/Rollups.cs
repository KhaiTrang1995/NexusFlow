using FlowX;

namespace Crm;

/// <summary>What a roll-up does to its children's values.</summary>
/// <remarks>
/// <strong>Five, and each is a SQL aggregate this build emits.</strong> Closed for the reason
/// <see cref="CustomFieldType"/> is: an open set would be a string nothing dispatches on, and a
/// deployment would discover at run time which ones actually worked.
/// </remarks>
public enum RollupAggregate
{
    /// <summary>How many children there are. The only one that reads no field.</summary>
    Count = 0,

    /// <summary>Their total.</summary>
    Sum = 1,

    /// <summary>The smallest.</summary>
    Min = 2,

    /// <summary>The largest.</summary>
    Max = 3,

    /// <summary>Their mean.</summary>
    Average = 4,
}

/// <summary>Declares a field whose value is an aggregate over a parent's children.</summary>
/// <param name="Field">The field on the parent that holds the answer. Marked computed by this.</param>
/// <param name="Relationship">
/// The edge to walk. Its <c>from</c> object must be the one the field belongs to — <c>from</c> is
/// the parent, because migration <c>0006</c>'s cardinality trigger is what constrains the
/// <c>to</c> end to one.
/// </param>
/// <param name="Aggregate">What to do to the children.</param>
/// <param name="SourceField">
/// Which field of the child to aggregate. Null for <see cref="RollupAggregate.Count"/>, which
/// counts rows, and required for the other four.
/// </param>
/// <param name="Filter">Which children to count, or null for all of them.</param>
public sealed record DefineRollup(
    Guid Field,
    Guid Relationship,
    RollupAggregate Aggregate,
    Guid? SourceField,
    RollupFilter? Filter);

/// <summary>Which children a roll-up counts.</summary>
/// <param name="Field">The child's field to read.</param>
/// <param name="Operator">How to compare. The same five a guard and a validation rule have.</param>
/// <param name="Value">What to compare against.</param>
/// <remarks>
/// Salesforce calls this a roll-up filter, and it is the difference between "how many sites" and
/// "how many open sites" — which is the one anybody actually configures.
/// </remarks>
public sealed record RollupFilter(string Field, GuardOperator Operator, string Value);

/// <summary>The roll-up that was declared.</summary>
/// <param name="RollupId">Its id.</param>
/// <param name="Field">The field it computes.</param>
public sealed record RollupDefined(Guid RollupId, Guid Field);

/// <summary>A declared roll-up, as much of it as recomputing one needs.</summary>
/// <param name="Id">The roll-up.</param>
/// <param name="FieldName">The parent field that holds the answer.</param>
/// <param name="Relationship">The edge to walk.</param>
/// <param name="Aggregate">What to do to the children.</param>
/// <param name="SourceFieldName">Which child field to aggregate, or null for a count.</param>
/// <param name="Filter">Which children to count, or null for all.</param>
public sealed record RollupRow(
    Guid Id,
    string FieldName,
    Guid Relationship,
    RollupAggregate Aggregate,
    string? SourceFieldName,
    RollupFilter? Filter);

/// <summary>Refusals the roll-up machinery can produce.</summary>
public static class RollupErrors
{
    /// <summary>That field is not one this tenant declared.</summary>
    /// <param name="fieldId">What was named.</param>
    public static Error FieldNotFound(Guid fieldId) =>
        new Error(
            "crm.rollup_field_not_found",
            "That field is not in this tenant.",
            ErrorCategory.NotFound)
            .With("fieldId", fieldId);

    /// <summary>The field being computed does not belong to the edge's parent object.</summary>
    /// <param name="fieldId">The field.</param>
    /// <remarks>
    /// Refused when the roll-up is declared, because the alternative is an aggregate that walks
    /// an edge no child of this parent is on and reports zero for ever — which reads exactly like
    /// a parent that genuinely has no children.
    /// </remarks>
    public static Error FieldIsNotOnTheParent(Guid fieldId) =>
        new Error(
            "crm.rollup_field_not_on_parent",
            "A roll-up's field must belong to the object at the `from` end of the relationship, " +
            "which is the parent.",
            ErrorCategory.Validation)
            .With("fieldId", fieldId);

    /// <summary>The source field does not belong to the edge's child object.</summary>
    /// <param name="fieldId">The field.</param>
    public static Error SourceIsNotOnTheChild(Guid fieldId) =>
        new Error(
            "crm.rollup_source_not_on_child",
            "A roll-up's source field must belong to the object at the `to` end of the " +
            "relationship, which is the child.",
            ErrorCategory.Validation)
            .With("fieldId", fieldId);

    /// <summary>A counting roll-up was given a field, or a summing one was not.</summary>
    /// <param name="aggregate">Which aggregate.</param>
    public static Error SourceDoesNotMatchTheAggregate(RollupAggregate aggregate) =>
        new Error(
            "crm.rollup_source_mismatch",
            aggregate == RollupAggregate.Count
                ? "Count counts rows and reads no field, so it takes no source field."
                : $"{aggregate} needs a source field to aggregate.",
            ErrorCategory.Validation)
            .With("aggregate", aggregate.ToString());

    /// <summary>Something already computes that field.</summary>
    /// <param name="fieldId">The field.</param>
    /// <remarks>
    /// Two roll-ups on one field would give two answers with no rule about which wins, which is a
    /// defect nobody finds until the numbers disagree.
    /// </remarks>
    public static Error FieldIsAlreadyComputed(Guid fieldId) =>
        new Error(
            "crm.rollup_field_already_computed",
            "Something already computes that field.",
            ErrorCategory.Conflict)
            .With("fieldId", fieldId);

    /// <summary>A caller tried to write a field this build computes.</summary>
    /// <param name="field">Which field.</param>
    /// <remarks>
    /// <strong>Refused rather than ignored.</strong> A computed field a caller may write is a
    /// lie: the next recompute overwrites it, so the write appears to succeed and silently does
    /// nothing.
    /// </remarks>
    public static Error FieldIsComputed(string field) =>
        new Error(
            "crm.custom_field_is_computed",
            $"'{field}' is computed by a roll-up and cannot be written directly.",
            ErrorCategory.Validation)
            .With("field", field);
}
