using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// The statements behind the validation rules, the uniqueness claims and the field history.
/// </summary>
/// <remarks>
/// <strong>Separate from <see cref="CustomSchemaStore"/> because these are rules about data
/// rather than the shape of it</strong>, and they are read on a different path: the schema store
/// answers "what is declared", this one answers "may this write happen, and what did it change".
/// One class holding both would be the class every future rule is added to.
/// </remarks>
public sealed class FieldPolicyStore
{
    private const string InsertRule = """
        INSERT INTO custom_validation_rule (
            rule_id, tenant_id, applies_to, object_id, name, field, operator, value, message,
            is_active, created_at)
        VALUES (@id, @tenant, @appliesTo, @object, @name, @field, @operator, @value, @message,
            true, @now)
        ON CONFLICT (tenant_id, name) DO NOTHING
        RETURNING rule_id
        """;

    private const string RulesForEntity = """
        SELECT rule_id, name, field, operator, value, message
        FROM custom_validation_rule
        WHERE applies_to = @appliesTo AND is_active
        ORDER BY created_at
        """;

    private const string RulesForObject = """
        SELECT rule_id, name, field, operator, value, message
        FROM custom_validation_rule
        WHERE object_id = @object AND is_active
        ORDER BY created_at
        """;

    // ON CONFLICT DO NOTHING and a row count, rather than catching a unique violation: a value
    // already claimed is an ordinary answer to an ordinary request, and an exception for it would
    // travel up through a transaction that has other work in it.
    private const string ClaimValue = """
        INSERT INTO custom_unique_value (field_id, value, tenant_id, record_id)
        VALUES (@field, @value, @tenant, @record)
        ON CONFLICT (field_id, value) DO NOTHING
        """;

    private const string ReleaseRecord = "DELETE FROM custom_unique_value WHERE record_id = @record";

    private const string InsertHistory = """
        INSERT INTO custom_field_history (
            history_id, tenant_id, entity_kind, entity_id, field_name,
            old_value, new_value, changed_by, changed_at)
        VALUES (@id, @tenant, @kind, @entity, @field, @old, @new, @who, @at)
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public FieldPolicyStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Declares a validation rule, or reports that the name is taken.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">The id to give it.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="now">The invocation's instant, not the wall clock.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The id written, or null when the name was taken.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<Guid?> DeclareRuleAsync(
        string? tenantId,
        Guid id,
        DefineValidationRule request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertRule;
        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "appliesTo", NpgsqlDbType.Text, (object?)request.AppliesTo?.ToString() ?? DBNull.Value);
        Add(command, "object", NpgsqlDbType.Uuid, (object?)request.Target ?? DBNull.Value);
        Add(command, "name", NpgsqlDbType.Text, request.Name);
        Add(command, "field", NpgsqlDbType.Text, request.Field);
        Add(command, "operator", NpgsqlDbType.Text, request.Operator.ToString());
        Add(command, "value", NpgsqlDbType.Text, request.Value);
        Add(command, "message", NpgsqlDbType.Text, request.Message);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
    }

    /// <summary>The active rules for a built-in entity kind.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="kind">Which kind.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The rules, in the order they were declared.</returns>
    public ValueTask<IReadOnlyList<ValidationRuleRow>> RulesForAsync(
        string? tenantId,
        EntityKind kind,
        CancellationToken cancellationToken) =>
        ReadRulesAsync(
            tenantId, RulesForEntity, "appliesTo", NpgsqlDbType.Text, kind.ToString(), cancellationToken);

    /// <summary>The active rules for a custom object.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="objectId">Which object.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The rules, in the order they were declared.</returns>
    public ValueTask<IReadOnlyList<ValidationRuleRow>> RulesForAsync(
        string? tenantId,
        Guid objectId,
        CancellationToken cancellationToken) =>
        ReadRulesAsync(
            tenantId, RulesForObject, "object", NpgsqlDbType.Uuid, objectId, cancellationToken);

    /// <summary>Claims the unique values a record is taking, or reports the first that is gone.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="recordId">The record taking them.</param>
    /// <param name="declared">The fields, for which of them are unique.</param>
    /// <param name="values">What is being written.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The name of the first field whose value was taken, or null when all were free.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    /// <strong>Claims first, then the caller writes the record.</strong> The other order would
    /// let two writers both see a free value. This leaves a claim behind if the record write then
    /// fails, which is the safe direction — a value held by nothing refuses a later writer, where
    /// the reverse would let two records share one.
    /// </remarks>
    public async ValueTask<string?> ClaimAsync(
        string? tenantId,
        Guid recordId,
        IReadOnlyDictionary<string, CustomFieldRow> declared,
        IReadOnlyDictionary<string, string?> values,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(values);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        foreach (var (name, value) in values)
        {
            if (value is null || !declared.TryGetValue(name, out var field) || !field.IsUnique)
            {
                continue;
            }

            var command = connection.CreateCommand();
            await using var closingCommand = command.ConfigureAwait(false);

            command.CommandText = ClaimValue;
            Add(command, "field", NpgsqlDbType.Uuid, field.Id);
            Add(command, "value", NpgsqlDbType.Text, value);
            Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
            Add(command, "record", NpgsqlDbType.Uuid, recordId);

            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>Releases every claim a record holds.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="recordId">The record.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// Called when a write that claimed values then failed, so a value is not held by a record
    /// that does not exist.
    /// </remarks>
    public async ValueTask ReleaseAsync(
        string? tenantId,
        Guid recordId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = ReleaseRecord;
        Add(command, "record", NpgsqlDbType.Uuid, recordId);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records what a write changed, one row per field that actually moved.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="kind">Which kind of entity.</param>
    /// <param name="entityId">Which row.</param>
    /// <param name="before">What it held.</param>
    /// <param name="after">What it holds now.</param>
    /// <param name="changedBy">The caller, derived from their claims.</param>
    /// <param name="at">The invocation's instant.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    /// <strong>Only what moved.</strong> A merge that rewrote every field on every call would
    /// make the history a log of writes rather than a record of changes, and the question anybody
    /// asks it — when did this become that — would need the reader to diff it themselves.
    /// </remarks>
    public async ValueTask RecordAsync(
        string? tenantId,
        EntityKind kind,
        Guid entityId,
        IReadOnlyDictionary<string, string?> before,
        IReadOnlyDictionary<string, string?> after,
        Guid changedBy,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        foreach (var (name, value) in after)
        {
            var was = before.GetValueOrDefault(name);

            if (string.Equals(was, value, StringComparison.Ordinal))
            {
                continue;
            }

            var command = connection.CreateCommand();
            await using var closingCommand = command.ConfigureAwait(false);

            command.CommandText = InsertHistory;
            Add(command, "id", NpgsqlDbType.Uuid, Guid.NewGuid());
            Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
            Add(command, "kind", NpgsqlDbType.Text, kind.ToString());
            Add(command, "entity", NpgsqlDbType.Uuid, entityId);
            Add(command, "field", NpgsqlDbType.Text, name);
            Add(command, "old", NpgsqlDbType.Text, (object?)was ?? DBNull.Value);
            Add(command, "new", NpgsqlDbType.Text, (object?)value ?? DBNull.Value);
            Add(command, "who", NpgsqlDbType.Uuid, changedBy);
            Add(command, "at", NpgsqlDbType.TimestampTz, at);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });

    private async ValueTask<IReadOnlyList<ValidationRuleRow>> ReadRulesAsync(
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

        var rules = new List<ValidationRuleRow>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rules.Add(new ValidationRuleRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                Enum.Parse<GuardOperator>(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5)));
        }

        return rules;
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
