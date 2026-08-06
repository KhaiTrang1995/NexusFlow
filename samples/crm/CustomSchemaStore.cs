using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// The statements behind the run-time schema: what an administrator declared, and the rows
/// written against it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every statement is a constant and every value is bound.</strong> A dynamic schema is
/// the one place where building SQL from a caller's string is tempting — the field is data, so
/// why not the column? Because then the identifier is the caller's, and
/// <c>SqlFitnessTests</c> would be right to fail this file. The names live in <c>jsonb</c> keys
/// and in <c>custom_field.name</c>, which are values; nothing here interpolates.
/// </para>
/// <para>
/// <strong>The tenant reaches the database as a scope, never as a predicate.</strong> Same as
/// every other store here: <see cref="CrmTenantScope"/> sets <c>flowx.tenant_id</c> and the
/// policies of migrations <c>0002</c> and <c>0005</c> decide. A <c>WHERE tenant_id = </c> in
/// these statements would be a second, weaker copy of that rule.
/// </para>
/// </remarks>
public sealed class CustomSchemaStore
{
    private const string InsertObject = """
        INSERT INTO custom_object (object_id, tenant_id, name, label, created_at)
        VALUES (@id, @tenant, @name, @label, @now)
        ON CONFLICT (tenant_id, name) DO NOTHING
        RETURNING object_id
        """;

    private const string InsertField = """
        INSERT INTO custom_field (
            field_id, tenant_id, applies_to, object_id, name, label, data_type, is_required,
            references_object_id, created_at)
        VALUES (@id, @tenant, @appliesTo, @object, @name, @label, @type, @required, @references, @now)
        ON CONFLICT DO NOTHING
        RETURNING field_id
        """;

    private const string InsertOption = """
        INSERT INTO custom_field_option (option_id, tenant_id, field_id, value, label, ordinal)
        VALUES (@id, @tenant, @field, @value, @label, @ordinal)
        ON CONFLICT (field_id, value) DO NOTHING
        """;

    private const string InsertRelationship = """
        INSERT INTO custom_relationship (
            relationship_id, tenant_id, name, from_object_id, to_object_id, cardinality)
        VALUES (@id, @tenant, @name, @from, @to, @cardinality)
        ON CONFLICT (tenant_id, name) DO NOTHING
        RETURNING relationship_id
        """;

    private const string InsertRecord = """
        INSERT INTO custom_record (record_id, tenant_id, object_id, values, created_at)
        VALUES (@id, @tenant, @object, @values::jsonb, @now)
        ON CONFLICT (record_id) DO NOTHING
        """;

    private const string InsertLink = """
        INSERT INTO custom_link (
            link_id, tenant_id, relationship_id, from_record_id, to_record_id, created_at)
        VALUES (@id, @tenant, @relationship, @from, @to, @now)
        ON CONFLICT (relationship_id, from_record_id, to_record_id) DO NOTHING
        """;

    // The options come back as an aggregate rather than as a second query, so one read answers
    // every question about a field. A left join and array_agg keeps a field with no options a
    // row rather than dropping it, which an inner join would.
    private const string FieldsForEntity = """
        SELECT f.field_id, f.name, f.data_type, f.is_required, f.references_object_id,
               array_remove(array_agg(o.value ORDER BY o.ordinal), NULL)
        FROM custom_field f
        LEFT JOIN custom_field_option o ON o.field_id = f.field_id
        WHERE f.applies_to = @appliesTo
        GROUP BY f.field_id
        """;

    private const string FieldsForObject = """
        SELECT f.field_id, f.name, f.data_type, f.is_required, f.references_object_id,
               array_remove(array_agg(o.value ORDER BY o.ordinal), NULL)
        FROM custom_field f
        LEFT JOIN custom_field_option o ON o.field_id = f.field_id
        WHERE f.object_id = @object
        GROUP BY f.field_id
        """;

    private const string ObjectExists = "SELECT count(*) FROM custom_object WHERE object_id = @id";

    private const string RelationshipEnds = """
        SELECT cardinality, from_object_id, to_object_id
        FROM custom_relationship
        WHERE relationship_id = @id
        """;

    private const string RecordObject = "SELECT object_id FROM custom_record WHERE record_id = @id";

    // ----------------------------------------------------------------- the dynamic columns
    //
    // One statement per kind rather than one with the table name bound, because a table name
    // cannot be a parameter and building it from `kind` would put a caller's string into the
    // identifier position. Four constants is the price of that rule holding here too.

    private const string ReadLeadFields =
        "SELECT custom_fields::text FROM lead WHERE lead_id = @id";

    private const string ReadAccountFields =
        "SELECT custom_fields::text FROM account WHERE account_id = @id";

    private const string ReadContactFields =
        "SELECT custom_fields::text FROM contact WHERE contact_id = @id";

    private const string ReadOpportunityFields =
        "SELECT custom_fields::text FROM opportunity WHERE opportunity_id = @id";

    // `||` rather than an assignment: setting some of an entity's fields must leave the rest
    // alone, and jsonb concatenation is right-biased, which is exactly that.
    private const string MergeLeadFields = """
        UPDATE lead SET custom_fields = custom_fields || @values::jsonb WHERE lead_id = @id
        RETURNING custom_fields::text
        """;

    private const string MergeAccountFields = """
        UPDATE account SET custom_fields = custom_fields || @values::jsonb WHERE account_id = @id
        RETURNING custom_fields::text
        """;

    private const string MergeContactFields = """
        UPDATE contact SET custom_fields = custom_fields || @values::jsonb WHERE contact_id = @id
        RETURNING custom_fields::text
        """;

    private const string MergeOpportunityFields = """
        UPDATE opportunity SET custom_fields = custom_fields || @values::jsonb
        WHERE opportunity_id = @id
        RETURNING custom_fields::text
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public CustomSchemaStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Declares an object, or reports that the name is taken.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">The id to give it.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="now">The invocation's instant, not the wall clock.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The id written, or null when something already had that name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<Guid?> DeclareObjectAsync(
        string? tenantId,
        Guid id,
        DefineObject request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertObject;
        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "name", NpgsqlDbType.Text, request.Name);
        Add(command, "label", NpgsqlDbType.Text, request.Label);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
    }

    /// <summary>Declares a field, or reports that the name is taken on that owner.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">The id to give it.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="now">The invocation's instant.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The id written, or null when the name was taken.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<Guid?> DeclareFieldAsync(
        string? tenantId,
        Guid id,
        DefineField request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertField;
        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "appliesTo", NpgsqlDbType.Text, (object?)request.AppliesTo?.ToString() ?? DBNull.Value);
        Add(command, "object", NpgsqlDbType.Uuid, (object?)request.Target ?? DBNull.Value);
        Add(command, "name", NpgsqlDbType.Text, request.Name);
        Add(command, "label", NpgsqlDbType.Text, request.Label);
        Add(command, "type", NpgsqlDbType.Text, request.Type.ToString());
        Add(command, "required", NpgsqlDbType.Boolean, request.IsRequired);
        Add(command, "references", NpgsqlDbType.Uuid, (object?)request.References ?? DBNull.Value);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not Guid written)
        {
            return null;
        }

        // The options, on the same connection. Not the same transaction: a field written without
        // its options is a picklist that accepts nothing, which CustomValues.Validate reports as
        // an error naming the field — visible and fixable, where a half-open transaction would
        // not be. Widening this to a transaction is right and is a change to every writer here.
        var ordinal = 0;

        foreach (var option in request.Options ?? [])
        {
            var write = connection.CreateCommand();
            await using var closingWrite = write.ConfigureAwait(false);

            write.CommandText = InsertOption;
            Add(write, "id", NpgsqlDbType.Uuid, Guid.NewGuid());
            Add(write, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
            Add(write, "field", NpgsqlDbType.Uuid, written);
            Add(write, "value", NpgsqlDbType.Text, option.Value);
            Add(write, "label", NpgsqlDbType.Text, option.Label);
            Add(write, "ordinal", NpgsqlDbType.Integer, ordinal++);

            await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return written;
    }

    /// <summary>Declares a relationship, or reports that the name is taken.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">The id to give it.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The id written, or null when the name was taken.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<Guid?> DeclareRelationshipAsync(
        string? tenantId,
        Guid id,
        DefineRelationship request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertRelationship;
        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "name", NpgsqlDbType.Text, request.Name);
        Add(command, "from", NpgsqlDbType.Uuid, request.From);
        Add(command, "to", NpgsqlDbType.Uuid, request.To);
        Add(command, "cardinality", NpgsqlDbType.Text, request.Cardinality.ToString());

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
    }

    /// <summary>Writes a record of a custom object.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">The id to give it.</param>
    /// <param name="objectId">Which object.</param>
    /// <param name="json">The validated document.</param>
    /// <param name="now">The invocation's instant.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async ValueTask WriteRecordAsync(
        string? tenantId,
        Guid id,
        Guid objectId,
        string json,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertRecord;
        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "object", NpgsqlDbType.Uuid, objectId);
        Add(command, "values", NpgsqlDbType.Text, json);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Joins two records, or reports that the cardinality refused it.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">The id to give the link.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="now">The invocation's instant.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether the link stood.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <remarks>
    /// The refusal comes back as <c>false</c> rather than an exception, because
    /// <c>custom_link_cardinality</c> raising is the expected answer to a caller asking for a
    /// second parent on a <c>OneToMany</c> — a business condition, not a fault.
    /// </remarks>
    public async ValueTask<bool> LinkAsync(
        string? tenantId,
        Guid id,
        LinkRecords request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertLink;
        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "relationship", NpgsqlDbType.Uuid, request.Relationship);
        Add(command, "from", NpgsqlDbType.Uuid, request.From);
        Add(command, "to", NpgsqlDbType.Uuid, request.To);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (PostgresException refusal) when (refusal.SqlState == "23000")
        {
            return false;
        }
    }

    /// <summary>Every field declared for a built-in entity kind, by name.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="kind">Which kind.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The declarations.</returns>
    public ValueTask<IReadOnlyDictionary<string, CustomFieldRow>> FieldsForAsync(
        string? tenantId,
        EntityKind kind,
        CancellationToken cancellationToken) =>
        ReadFieldsAsync(
            tenantId, FieldsForEntity, "appliesTo", NpgsqlDbType.Text, kind.ToString(), cancellationToken);

    /// <summary>Every field declared for a custom object, by name.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="objectId">Which object.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The declarations.</returns>
    public ValueTask<IReadOnlyDictionary<string, CustomFieldRow>> FieldsForAsync(
        string? tenantId,
        Guid objectId,
        CancellationToken cancellationToken) =>
        ReadFieldsAsync(
            tenantId, FieldsForObject, "object", NpgsqlDbType.Uuid, objectId, cancellationToken);

    /// <summary>Whether this tenant has that object.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="objectId">Which object.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether it is there.</returns>
    public async ValueTask<bool> HasObjectAsync(
        string? tenantId,
        Guid objectId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = ObjectExists;
        Add(command, "id", NpgsqlDbType.Uuid, objectId);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long and > 0;
    }

    /// <summary>What a relationship joins, and how many of each it allows.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="relationshipId">Which relationship.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The cardinality and both ends, or null when this tenant has no such edge.</returns>
    public async ValueTask<(CustomCardinality Cardinality, Guid From, Guid To)?> ReadRelationshipAsync(
        string? tenantId,
        Guid relationshipId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = RelationshipEnds;
        Add(command, "id", NpgsqlDbType.Uuid, relationshipId);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return (
            Enum.Parse<CustomCardinality>(reader.GetString(0)),
            reader.GetGuid(1),
            reader.GetGuid(2));
    }

    /// <summary>Which object a record belongs to.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="recordId">Which record.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The object, or null when this tenant has no such record.</returns>
    public async ValueTask<Guid?> ObjectOfAsync(
        string? tenantId,
        Guid recordId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = RecordObject;
        Add(command, "id", NpgsqlDbType.Uuid, recordId);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
    }

    /// <summary>The custom values a built-in entity holds.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="kind">Which kind.</param>
    /// <param name="id">Which row.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The values, or null when this tenant has no such row.</returns>
    public async ValueTask<IReadOnlyDictionary<string, string?>?> ReadCustomFieldsAsync(
        string? tenantId,
        EntityKind kind,
        Guid id,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        // The switch is here rather than behind a helper because SqlFitnessTests resolves the
        // expression that reaches CommandText, and a call it cannot see through is a run-time
        // value as far as that gate is concerned — correctly, since nothing stops a helper
        // interpolating. Four constants in a switch is the shape that stays checkable.
        command.CommandText = kind switch
        {
            EntityKind.Lead => ReadLeadFields,
            EntityKind.Account => ReadAccountFields,
            EntityKind.Contact => ReadContactFields,
            EntityKind.Opportunity => ReadOpportunityFields,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No such entity kind."),
        };
        Add(command, "id", NpgsqlDbType.Uuid, id);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json
            ? CustomValues.FromJson(json)
            : null;
    }

    /// <summary>Merges values into a built-in entity's custom values.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="kind">Which kind.</param>
    /// <param name="id">Which row.</param>
    /// <param name="json">The validated document to merge in.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Everything the row holds afterwards, or null when there was no such row.</returns>
    public async ValueTask<IReadOnlyDictionary<string, string?>?> MergeCustomFieldsAsync(
        string? tenantId,
        EntityKind kind,
        Guid id,
        string json,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = kind switch
        {
            EntityKind.Lead => MergeLeadFields,
            EntityKind.Account => MergeAccountFields,
            EntityKind.Contact => MergeContactFields,
            EntityKind.Opportunity => MergeOpportunityFields,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No such entity kind."),
        };
        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "values", NpgsqlDbType.Text, json);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string merged
            ? CustomValues.FromJson(merged)
            : null;
    }

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });

    private async ValueTask<IReadOnlyDictionary<string, CustomFieldRow>> ReadFieldsAsync(
        string? tenantId,
        string sql,
        string parameter,
        NpgsqlDbType type,
        object value,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = sql;
        Add(command, parameter, type, value);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        var fields = new Dictionary<string, CustomFieldRow>(StringComparer.Ordinal);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(1);

            var references = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false)
                ? (Guid?)null
                : reader.GetGuid(4);

            var options = await reader.GetFieldValueAsync<string[]>(5, cancellationToken)
                .ConfigureAwait(false);

            fields[name] = new CustomFieldRow(
                reader.GetGuid(0),
                name,
                Enum.Parse<CustomFieldType>(reader.GetString(2)),
                reader.GetBoolean(3),
                options,
                references);
        }

        return fields;
    }

    private async ValueTask<NpgsqlConnection> OpenAsync(
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await CrmTenantScope.ApplyAsync(connection, tenantId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return connection;
    }
}
