using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// What this tenant calls things.
/// </summary>
/// <remarks>
/// <strong>Renaming is an update, not a new row.</strong> A name is one fact, and a table that
/// accumulated every name a thing has ever had would need a rule about which one wins — which is
/// a rule somebody eventually gets wrong in a settings screen at four o'clock. The custom object
/// and custom field labels have lived on their own rows since 0005; only the built-in entities
/// needed a table, and it upserts.
/// </remarks>
public sealed class LabelStore
{
    private const string UpsertEntityLabel = """
        INSERT INTO entity_label (tenant_id, kind, field, label)
        VALUES (@tenant, @kind, @field, @label)
        ON CONFLICT (tenant_id, kind, field) DO UPDATE SET label = excluded.label
        """;

    private const string RenameObject =
        "UPDATE custom_object SET label = @label WHERE object_id = @id";

    private const string RenameField =
        "UPDATE custom_field SET label = @label WHERE field_id = @id";

    private const string LabelsForTenant =
        "SELECT kind, field, label FROM entity_label";

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public LabelStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Names a built-in entity, or one of its columns, for this tenant.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="kind">Which entity.</param>
    /// <param name="column">Which column, or <see cref="LabelLimits.TheEntityItself"/>.</param>
    /// <param name="label">What it is to be called.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async ValueTask SetEntityLabelAsync(
        string? tenantId,
        EntityKind kind,
        string column,
        string label,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = UpsertEntityLabel;

        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "kind", NpgsqlDbType.Text, kind.ToString());
        Add(command, "field", NpgsqlDbType.Text, column);
        Add(command, "label", NpgsqlDbType.Text, label);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Renames a custom object or a custom field.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">Which one.</param>
    /// <param name="isObject">Whether the id is an object's, rather than a field's.</param>
    /// <param name="label">What it is to be called.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether there was anything by that id in this tenant.</returns>
    public async ValueTask<bool> RenameAsync(
        string? tenantId,
        Guid id,
        bool isObject,
        string label,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        // Two constants chosen by a boolean, rather than one statement with the table name bound.
        // A table name cannot be a parameter, and building it from a caller's value is the shape
        // SqlFitnessTests exists to refuse.
        command.CommandText = isObject ? RenameObject : RenameField;

        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "label", NpgsqlDbType.Text, label);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <summary>Every built-in label this tenant has set.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The labels, keyed on <c>Kind</c> and the column name — with
    /// <see cref="LabelLimits.TheEntityItself"/> for the entity's own.
    /// </returns>
    /// <remarks>
    /// One read for the whole tenant, because describe needs every one of them and a read per
    /// entity would be four round trips to build one screen.
    /// </remarks>
    public async ValueTask<IReadOnlyDictionary<(string Kind, string Field), string>> LabelsAsync(
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = LabelsForTenant;

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        var labels = new Dictionary<(string, string), string>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            labels[(reader.GetString(0), reader.GetString(1))] = reader.GetString(2);
        }

        return labels;
    }

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });

    private ValueTask<NpgsqlConnection> OpenAsync(string? tenantId, CancellationToken cancellationToken) =>
        CrmTenantScope.OpenAsync(_source, tenantId, cancellationToken);
}
