using System.Globalization;
using System.Text.Json;
using FlowX;

namespace Crm;

// ------------------------------------------------------------------------------ what a type is

/// <summary>What a custom field holds.</summary>
/// <remarks>
/// <strong>Four, and adding a fifth is a code change.</strong> The set is closed for the reason
/// <see cref="ProcessFields"/> gives about operators: a type is not a label, it is a parse and a
/// comparison, and a type nothing in this file can parse is a value nothing can validate or
/// guard on. An administrator picks from these; they do not invent one.
/// </remarks>
public enum CustomFieldType
{
    /// <summary>Any string.</summary>
    Text = 0,

    /// <summary>A decimal, parsed invariantly.</summary>
    Number = 1,

    /// <summary><c>true</c> or <c>false</c>.</summary>
    Boolean = 2,

    /// <summary>An ISO-8601 instant.</summary>
    Date = 3,

    /// <summary>One of a closed, ordered, labelled set of values.</summary>
    /// <remarks>
    /// <strong>The most-used custom field type there is, and not for fashion.</strong> A field
    /// whose values are closed is the only kind an administrator can build a guard or a report on
    /// and be sure of the answer; free text with a convention is the same field with the closure
    /// removed, and the first misspelling is a row nothing matches.
    /// </remarks>
    Picklist = 4,

    /// <summary>The id of a record of another custom object.</summary>
    /// <remarks>
    /// The other shape of a relationship. <c>custom_relationship</c> models an edge as a row of
    /// its own, which is what many-to-many needs and what an edge with its own attributes needs;
    /// this is one value on the record, resolved by reading it. Collapsing the two would mean a
    /// link table for every lookup or a jsonb key for every many-to-many.
    /// </remarks>
    Reference = 5,

    /// <summary>Several of a closed, ordered, labelled set of values.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Stored as a jsonb array, and carried as that array's JSON text.</strong> The value
    /// model is a flat map of text — which is what keeps it serialisable without reflection — so a
    /// multi-select's value is <c>["gold","silver"]</c>. That is what a caller sends, what comes
    /// back, and what a guard compares, and it is the same string in all three. An array in the
    /// column rather than a delimited string, because the GIN index over <c>values</c> then
    /// already answers which records chose an option.
    /// </para>
    /// <para>
    /// <strong>What this deliberately does not add is a "contains" operator.</strong> The five
    /// operators are shared by transition guards, validation rules, roll-up filters and list-view
    /// criteria; a sixth that means something for one field type is a sixth all four have to
    /// explain. A guard over a multi-select compares whole values. Asking "does it include gold"
    /// is a real gap, and saying so beats half an operator.
    /// </para>
    /// </remarks>
    MultiPicklist = 6,
}

/// <summary>How many rows may sit on each end of a relationship.</summary>
public enum CustomCardinality
{
    /// <summary>One row on each end.</summary>
    OneToOne = 0,

    /// <summary>Many rows may point at one.</summary>
    OneToMany = 1,

    /// <summary>Anything may point at anything, once.</summary>
    ManyToMany = 2,
}

// -------------------------------------------------------------------------------- what is asked

/// <summary>Declares a field on a built-in entity kind or on a custom object.</summary>
/// <param name="AppliesTo">The built-in kind, or null when <paramref name="Target"/> is given.</param>
/// <param name="Target">The custom object, or null when <paramref name="AppliesTo"/> is given.</param>
/// <param name="Name">The identifier a caller and a guard use. Lower case, snake case.</param>
/// <param name="Label">What a person sees.</param>
/// <param name="Type">What it holds.</param>
/// <param name="IsRequired">Whether a record without it is refused.</param>
/// <param name="Options">
/// The allowed values, for <see cref="CustomFieldType.Picklist"/>. Empty for every other type,
/// and a picklist with none is refused — a closed set of nothing accepts nothing.
/// </param>
/// <param name="References">
/// The object a <see cref="CustomFieldType.Reference"/> points at. Null for every other type.
/// </param>
/// <param name="RequiredPermission">
/// The scope a caller must hold to write this field, or null when <c>crm.write</c> is enough.
/// Field-level security, and it restricts the write rather than the read — see
/// <see cref="CustomFieldPolicy.FirstForbiddenField"/> for why.
/// </param>
/// <param name="IsUnique">
/// Whether two records of this owner may hold the same value. Enforced by a claimed-value row
/// rather than by an index, because an index per declared field is DDL at run time.
/// </param>
/// <param name="ReadPermission">
/// The scope a caller must hold to see this field, or null when <c>crm.read</c> is enough.
/// </param>
public sealed record DefineField(
    EntityKind? AppliesTo,
    Guid? Target,
    string Name,
    string Label,
    CustomFieldType Type,
    bool IsRequired,
    IReadOnlyList<CustomFieldOption>? Options = null,
    Guid? References = null,
    string? RequiredPermission = null,
    bool IsUnique = false,
    string? ReadPermission = null);

/// <summary>One allowed value of a picklist.</summary>
/// <param name="Value">What is stored. Named like a field, because a guard compares it as text.</param>
/// <param name="Label">What a person sees. Free text, because nothing compares it.</param>
public sealed record CustomFieldOption(string Value, string Label);

/// <summary>The field that was declared.</summary>
/// <param name="FieldId">Its id.</param>
/// <param name="Name">Its name, which is what everything downstream refers to it by.</param>
public sealed record FieldDefined(Guid FieldId, string Name);

/// <summary>Declares an entity this build has never heard of.</summary>
/// <param name="Name">The identifier. Lower case, snake case.</param>
/// <param name="Label">What a person sees.</param>
public sealed record DefineObject(string Name, string Label);

/// <summary>The object that was declared.</summary>
/// <param name="ObjectId">Its id.</param>
/// <param name="Name">Its name.</param>
public sealed record ObjectDefined(Guid ObjectId, string Name);

/// <summary>Declares a named edge between two custom objects.</summary>
/// <param name="Name">The identifier.</param>
/// <param name="From">The object an edge starts at.</param>
/// <param name="To">The object it ends at.</param>
/// <param name="Cardinality">What the administrator promises about how many.</param>
public sealed record DefineRelationship(
    string Name,
    Guid From,
    Guid To,
    CustomCardinality Cardinality);

/// <summary>The relationship that was declared.</summary>
/// <param name="RelationshipId">Its id.</param>
/// <param name="Name">Its name.</param>
public sealed record RelationshipDefined(Guid RelationshipId, string Name);

/// <summary>Writes a row of a custom object.</summary>
/// <param name="Target">Which object.</param>
/// <param name="Values">Its values, by field name.</param>
public sealed record CreateRecord(Guid Target, IReadOnlyDictionary<string, string?> Values);

/// <summary>The record that was written.</summary>
/// <param name="RecordId">Its id.</param>
public sealed record RecordCreated(Guid RecordId);

/// <summary>What the record-writing capability is given, once the flow has read the caller.</summary>
/// <param name="Target">Which object.</param>
/// <param name="Values">Its values, by field name.</param>
/// <param name="Scopes">
/// The grants the caller holds, from their claims and never from the body. What
/// <see cref="CustomFieldRow.RequiredPermission"/> is checked against.
/// </param>
/// <remarks>
/// <strong>A projection for the same reason <see cref="ApproveQuoteDiscount"/> is one.</strong> A
/// capability is handed what the flow read off the caller; a <c>scopes</c> field on the request
/// would let anybody claim any grant, which is the shape ADR-0046 makes the same argument about
/// for the tenant.
/// </remarks>
public sealed record WriteObjectRecord(
    Guid Target,
    IReadOnlyDictionary<string, string?> Values,
    IReadOnlyList<string> Scopes);

/// <summary>Joins two records along a declared relationship.</summary>
/// <param name="Relationship">Which edge.</param>
/// <param name="From">The record it starts at.</param>
/// <param name="To">The record it ends at.</param>
public sealed record LinkRecords(Guid Relationship, Guid From, Guid To);

/// <summary>The link that was written.</summary>
/// <param name="LinkId">Its id.</param>
public sealed record RecordsLinked(Guid LinkId);

/// <summary>Sets the custom values of a built-in entity.</summary>
/// <param name="Kind">Which kind of entity.</param>
/// <param name="Id">Which row.</param>
/// <param name="Values">The values to set, by field name. Absent keys are left alone.</param>
public sealed record SetCustomFields(
    EntityKind Kind,
    Guid Id,
    IReadOnlyDictionary<string, string?> Values);

/// <summary>What the entity's custom values are now.</summary>
/// <param name="Id">The row.</param>
/// <param name="Values">Every custom value it holds.</param>
public sealed record CustomFieldsSet(Guid Id, IReadOnlyDictionary<string, string?> Values);

/// <summary>What the field-setting capability is given, once the flow has read the caller.</summary>
/// <param name="Kind">Which kind of entity.</param>
/// <param name="Id">Which row.</param>
/// <param name="Values">The values to set. Absent keys are left alone.</param>
/// <param name="Scopes">The grants the caller holds, from their claims and never from the body.</param>
/// <param name="ChangedBy">
/// Who is writing, derived from the caller's subject exactly as an approver is. What lands in
/// <c>custom_field_history.changed_by</c>.
/// </param>
public sealed record WriteEntityFields(
    EntityKind Kind,
    Guid Id,
    IReadOnlyDictionary<string, string?> Values,
    IReadOnlyList<string> Scopes,
    Guid ChangedBy);

// ------------------------------------------------------------------------------- what was read

/// <summary>A declared field, as much of it as anything downstream needs.</summary>
/// <param name="Id">The field.</param>
/// <param name="Name">Its name.</param>
/// <param name="Type">What it holds.</param>
/// <param name="IsRequired">Whether a record without it is refused.</param>
/// <param name="Options">
/// The allowed values of a picklist, or empty. Carried on the row rather than fetched when a
/// value is checked, so one read of the declarations answers every question about them.
/// </param>
/// <param name="References">The object a reference points at, or null.</param>
/// <param name="RequiredPermission">
/// The scope a caller must hold to write it, or null when <c>crm.write</c> is enough.
/// </param>
/// <param name="IsUnique">Whether two records of this owner may hold the same value.</param>
/// <param name="IsComputed">Whether a roll-up writes it, in which case no caller may.</param>
/// <param name="ReadPermission">
/// The scope a caller must hold to see it, or null when <c>crm.read</c> is enough. Separate from
/// <paramref name="RequiredPermission"/> because "may change it" and "may see it" are different
/// questions with different answers.
/// </param>
public sealed record CustomFieldRow(
    Guid Id,
    string Name,
    string Label,
    CustomFieldType Type,
    bool IsRequired,
    IReadOnlyList<string>? Options = null,
    Guid? References = null,
    string? RequiredPermission = null,
    bool IsUnique = false,
    bool IsComputed = false,
    string? ReadPermission = null);

// ------------------------------------------------------------------------------- what can go wrong

/// <summary>Refusals the dynamic-schema capabilities can produce.</summary>
public static class CustomSchemaErrors
{
    /// <summary>A field was declared against neither a kind nor an object, or against both.</summary>
    public static Error FieldHasNoOwner() =>
        new(
            "crm.custom_field_has_no_owner",
            "A field belongs to a built-in entity kind or to a custom object, and this named " +
            "neither or both.",
            ErrorCategory.Validation);

    /// <summary>The name is not one this schema can hold.</summary>
    /// <param name="name">What was sent.</param>
    /// <remarks>
    /// The same rule as the CHECK constraint, asked here so a caller gets the rule rather than a
    /// constraint name. Both exist for 0003's reason.
    /// </remarks>
    public static Error NameIsNotUsable(string name) =>
        new Error(
            "crm.custom_name_not_usable",
            "A name must start with a letter and hold only lower-case letters, digits and " +
            "underscores, up to 63 characters.",
            ErrorCategory.Validation)
            .With("name", name);

    /// <summary>Something with that name is already declared.</summary>
    /// <param name="name">What was sent.</param>
    public static Error NameIsTaken(string name) =>
        new Error(
            "crm.custom_name_taken",
            "This tenant already has one of those with that name.",
            ErrorCategory.Conflict)
            .With("name", name);

    /// <summary>That object is not in this tenant.</summary>
    /// <param name="objectId">What was named.</param>
    public static Error ObjectNotFound(Guid objectId) =>
        new Error(
            "crm.custom_object_not_found",
            "That object is not in this tenant.",
            ErrorCategory.NotFound)
            .With("objectId", objectId);

    /// <summary>That relationship is not in this tenant.</summary>
    /// <param name="relationshipId">What was named.</param>
    public static Error RelationshipNotFound(Guid relationshipId) =>
        new Error(
            "crm.custom_relationship_not_found",
            "That relationship is not in this tenant.",
            ErrorCategory.NotFound)
            .With("relationshipId", relationshipId);

    /// <summary>That record is not in this tenant, or is not of the object the edge joins.</summary>
    /// <param name="recordId">What was named.</param>
    public static Error RecordNotFound(Guid recordId) =>
        new Error(
            "crm.custom_record_not_found",
            "That record is not in this tenant, or is not of the object this relationship joins.",
            ErrorCategory.NotFound)
            .With("recordId", recordId);

    /// <summary>That entity is not in this tenant.</summary>
    /// <param name="kind">Which kind was named.</param>
    /// <param name="id">Which row.</param>
    public static Error EntityNotFound(EntityKind kind, Guid id) =>
        new Error(
            "crm.custom_entity_not_found",
            $"There is no {kind} in this tenant with that id.",
            ErrorCategory.NotFound)
            .With("kind", kind.ToString())
            .With("id", id);

    /// <summary>A value was sent for a field nobody declared.</summary>
    /// <param name="name">The field named.</param>
    public static Error FieldNotDeclared(string name) =>
        new Error(
            "crm.custom_field_not_declared",
            "Nothing declared a field by that name for this entity.",
            ErrorCategory.Validation)
            .With("field", name);

    /// <summary>A value is not of the type the field was declared with.</summary>
    /// <param name="name">The field.</param>
    /// <param name="type">What it holds.</param>
    /// <param name="value">What was sent.</param>
    public static Error ValueIsNotOfType(string name, CustomFieldType type, string value) =>
        new Error(
            "crm.custom_value_wrong_type",
            $"'{name}' was declared as {type} and '{value}' is not one.",
            ErrorCategory.Validation)
            .With("field", name)
            .With("type", type.ToString())
            .With("value", value);

    /// <summary>A picklist was declared with no values, so nothing could ever be stored in it.</summary>
    /// <param name="name">The field.</param>
    public static Error PicklistHasNoOptions(string name) =>
        new Error(
            "crm.custom_picklist_has_no_options",
            $"'{name}' is a Picklist and no allowed values were given. A closed set of nothing " +
            "accepts nothing.",
            ErrorCategory.Validation)
            .With("field", name);

    /// <summary>A value is not one of the picklist's allowed values.</summary>
    /// <param name="name">The field.</param>
    /// <param name="value">What was sent.</param>
    /// <param name="allowed">What may be sent.</param>
    public static Error ValueIsNotAnOption(string name, string value, IEnumerable<string> allowed) =>
        new Error(
            "crm.custom_value_not_an_option",
            $"'{value}' is not one of the values '{name}' allows.",
            ErrorCategory.Validation)
            .With("field", name)
            .With("value", value)
            .With("allowed", string.Join(", ", allowed));

    /// <summary>A reference field points at nothing this tenant has.</summary>
    /// <param name="name">The field.</param>
    /// <param name="value">What was sent.</param>
    public static Error ReferenceIsNotResolvable(string name, string value) =>
        new Error(
            "crm.custom_reference_not_resolvable",
            $"'{name}' points at a record of another object, and nothing this tenant has is at " +
            "that id.",
            ErrorCategory.Validation)
            .With("field", name)
            .With("value", value);

    /// <summary>A required field has no value.</summary>
    /// <param name="name">The field.</param>
    public static Error ValueIsRequired(string name) =>
        new Error(
            "crm.custom_value_required",
            $"'{name}' was declared required and no value was given.",
            ErrorCategory.Validation)
            .With("field", name);

    /// <summary>The cardinality the administrator promised would be broken by this link.</summary>
    /// <param name="cardinality">What was promised.</param>
    public static Error CardinalityWouldBreak(CustomCardinality cardinality) =>
        new Error(
            "crm.custom_cardinality_broken",
            $"That relationship is {cardinality} and one of these records is already on the " +
            "end of a link.",
            ErrorCategory.Conflict)
            .With("cardinality", cardinality.ToString());
}

// ------------------------------------------------------------------------------ the pure rules

/// <summary>
/// Whether a set of values is one the declared fields accept — the whole of the dynamic
/// schema's validation, as a pure function.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing here reads a database, a clock or a random source</strong>, for the reason
/// <see cref="ProcessRules"/> gives: the declarations are read once and every value is checked
/// against the same snapshot, so two fields cannot disagree about what was declared, and the
/// whole of this is testable without standing anything up.
/// </para>
/// <para>
/// <strong>Text on the wire, whatever the declared type.</strong> A value arrives as a string
/// and is parsed against the declaration rather than deserialised into it, because the type is
/// data — no <c>JsonTypeInfo</c> could name it and a <c>JsonElement</c> would put the parse in
/// the serialiser, where a bad value is a deserialisation failure rather than an error naming
/// the field. What lands in <c>jsonb</c> is typed, which is the point of
/// <see cref="ToJson"/> being separate from this.
/// </para>
/// </remarks>
public static class CustomValues
{
    /// <summary>Every reason these values are not acceptable.</summary>
    /// <param name="declared">The fields declared for the entity, by name.</param>
    /// <param name="values">What was sent.</param>
    /// <param name="requireComplete">
    /// Whether a missing required field is a fault. True when writing a whole record, false when
    /// setting some of an entity's fields — a partial update leaves absent keys alone, and
    /// calling a field it did not mention missing would make every update total.
    /// </param>
    /// <returns>The faults, empty when the values may be written.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static IReadOnlyList<Error> Validate(
        IReadOnlyDictionary<string, CustomFieldRow> declared,
        IReadOnlyDictionary<string, string?> values,
        bool requireComplete)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(values);

        var faults = new List<Error>();

        foreach (var (name, value) in values)
        {
            if (!declared.TryGetValue(name, out var field))
            {
                faults.Add(CustomSchemaErrors.FieldNotDeclared(name));

                continue;
            }

            if (value is null)
            {
                if (field.IsRequired)
                {
                    faults.Add(CustomSchemaErrors.ValueIsRequired(name));
                }

                continue;
            }

            if (!Parses(field.Type, value))
            {
                faults.Add(CustomSchemaErrors.ValueIsNotOfType(name, field.Type, value));

                continue;
            }

            // The closure, which is the whole point of the type. Checked here and not by the
            // column, because the allowed set is a tenant's rows rather than a CHECK — so this
            // is the only thing between a misspelt option and a value no report will ever match.
            if (field.Type == CustomFieldType.Picklist &&
                field.Options is { Count: > 0 } options &&
                !options.Contains(value, StringComparer.Ordinal))
            {
                faults.Add(CustomSchemaErrors.ValueIsNotAnOption(name, value, options));
            }

            // Every element of a multi-select, for the same reason: one misspelt option in an
            // array of five is a value no report will ever match, and it is the four beside it
            // that make it look like it worked.
            if (field.Type == CustomFieldType.MultiPicklist &&
                field.Options is { Count: > 0 } allowed &&
                Chosen(value).FirstOrDefault(chosen => !allowed.Contains(chosen, StringComparer.Ordinal))
                    is { } stray)
            {
                faults.Add(CustomSchemaErrors.ValueIsNotAnOption(name, stray, allowed));
            }
        }

        if (requireComplete)
        {
            foreach (var field in declared.Values)
            {
                if (field.IsRequired && !values.ContainsKey(field.Name))
                {
                    faults.Add(CustomSchemaErrors.ValueIsRequired(field.Name));
                }
            }
        }

        return faults;
    }

    /// <summary>Whether a name is one this schema can hold.</summary>
    /// <param name="name">The candidate.</param>
    /// <returns>Whether it is usable.</returns>
    /// <remarks>
    /// The same rule as the <c>CHECK</c> constraints of migration <c>0005</c>, stated twice on
    /// purpose. A name reaches a <c>jsonb</c> key and an administrator's guard, and one that
    /// needed quoting in either place would behave differently in the other.
    /// </remarks>
    public static bool IsUsableName(string? name) =>
        name is { Length: > 0 and <= 63 } &&
        char.IsAsciiLetterLower(name[0]) &&
        name.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');

    /// <summary>Turns validated values into the <c>jsonb</c> document that is stored.</summary>
    /// <param name="declared">The fields, for the types to write with.</param>
    /// <param name="values">What to write. Assumed to have passed <see cref="Validate"/>.</param>
    /// <returns>The document.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    /// Typed rather than all-strings, because the column is <c>jsonb</c> and a number stored as
    /// <c>"42"</c> orders as text — <c>"9"</c> after <c>"42"</c> — which would make every
    /// numeric guard over a custom field silently wrong.
    /// </remarks>
    public static string ToJson(
        IReadOnlyDictionary<string, CustomFieldRow> declared,
        IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(values);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            foreach (var (name, value) in values)
            {
                if (!declared.TryGetValue(name, out var field))
                {
                    continue;
                }

                if (value is null)
                {
                    writer.WriteNull(name);

                    continue;
                }

                switch (field.Type)
                {
                    case CustomFieldType.Number:
                        writer.WriteNumber(
                            name, decimal.Parse(value, CultureInfo.InvariantCulture));
                        break;

                    case CustomFieldType.Boolean:
                        writer.WriteBoolean(name, bool.Parse(value));
                        break;

                    // A real jsonb array, not the text of one. Stored as text it would need
                    // parsing in every statement that touched it, and the GIN index over `values`
                    // would be no use for asking which records chose an option.
                    case CustomFieldType.MultiPicklist:
                        writer.WritePropertyName(name);
                        writer.WriteStartArray();

                        foreach (var chosen in Chosen(value))
                        {
                            writer.WriteStringValue(chosen);
                        }

                        writer.WriteEndArray();
                        break;

                    default:
                        writer.WriteString(name, value);
                        break;
                }
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Reads a stored document back as the text a guard and an API compare.</summary>
    /// <param name="json">The <c>jsonb</c> document, or null.</param>
    /// <returns>Every value it holds, by name.</returns>
    /// <remarks>
    /// Invariant formatting, because <see cref="ProcessFacts.Read"/> returns text and the two
    /// sides of a guard have to be comparable. A number formatted by the current culture would
    /// make a guard hold in one deployment and not another.
    /// </remarks>
    public static IReadOnlyDictionary<string, string?> FromJson(string? json)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);

        if (json is null or { Length: 0 })
        {
            return values;
        }

        using var document = JsonDocument.Parse(json);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            values[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => property.Value.GetDecimal().ToString(CultureInfo.InvariantCulture),

                // The array's JSON, re-written compactly. PostgreSQL renders jsonb with a space
                // after each comma, so the raw text would come back differing from what was sent
                // by whitespace alone — and a guard written `["gold","silver"]` would then never
                // match a stored `["gold", "silver"]`. One canonical form, so the comparison is
                // between two values rather than between two spellings.
                JsonValueKind.Array => Compact(property.Value),
                _ => property.Value.GetString(),
            };
        }

        return values;
    }

    private static bool Parses(CustomFieldType type, string value) => type switch
    {
        CustomFieldType.Number =>
            decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _),
        CustomFieldType.Boolean => bool.TryParse(value, out _),
        CustomFieldType.Date =>
            DateTimeOffset.TryParse(
                value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),

        // A reference is an id here and a resolvable record in the capability. Parsing is what
        // this pure function can answer; whether the row exists needs a read, and putting one
        // behind Validate would make the whole of it depend on when it was asked.
        CustomFieldType.Reference => Guid.TryParse(value, out _),
        CustomFieldType.MultiPicklist => IsArrayOfStrings(value),
        _ => true,
    };

    /// <summary>The options a multi-select value chose, or empty when it is not an array.</summary>
    private static IReadOnlyList<string> Chosen(string value)
    {
        if (!IsArrayOfStrings(value))
        {
            return [];
        }

        using var document = JsonDocument.Parse(value);

        return [.. document.RootElement.EnumerateArray().Select(static element => element.GetString()!)];
    }

    /// <summary>The canonical, space-free JSON of an array.</summary>
    private static string Compact(JsonElement array)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            array.WriteTo(writer);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static bool IsArrayOfStrings(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);

            return document.RootElement.ValueKind == JsonValueKind.Array
                && document.RootElement.EnumerateArray()
                    .All(static element => element.ValueKind == JsonValueKind.String);
        }
        catch (JsonException)
        {
            // Not JSON at all is not an array of strings, which is what the caller asked. A throw
            // here would make a bad value a 500 instead of the refusal Validate is there to give.
            return false;
        }
    }
}
