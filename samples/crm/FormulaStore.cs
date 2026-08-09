using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// Declaring a formula, and reading the ones an entity has.
/// </summary>
/// <remarks>
/// <strong>Nothing here evaluates anything.</strong> A filter is evaluated in SQL because it
/// decides which of many rows come back; a formula is computed once, at the write of the one row
/// it belongs to, by <see cref="Formulas"/> — which is a pure function and therefore testable
/// without a database. The split is deliberate and it is the same one <c>ProcessRules</c> makes.
/// </remarks>
public sealed class FormulaStore
{
    private const string InsertFormula = """
        INSERT INTO custom_formula (
            formula_id, tenant_id, field_id, operation, left_field, right_field, literal, created_at)
        VALUES (@id, @tenant, @field, @operation, @left, @right, @literal, @now)
        ON CONFLICT (field_id) DO NOTHING
        RETURNING formula_id
        """;

    private const string MarkComputed =
        "UPDATE custom_field SET is_computed = true WHERE field_id = @id";

    private const string FormulasForObject = """
        SELECT f.formula_id, c.name, f.operation, f.left_field, f.right_field, f.literal
        FROM custom_formula f
        JOIN custom_field c ON c.field_id = f.field_id
        WHERE c.object_id = @object
        """;

    private const string FormulasForEntity = """
        SELECT f.formula_id, c.name, f.operation, f.left_field, f.right_field, f.literal
        FROM custom_formula f
        JOIN custom_field c ON c.field_id = f.field_id
        WHERE c.applies_to = @appliesTo
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public FormulaStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Declares a formula and marks its field computed.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">The id to give it.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="now">The invocation's instant, not the wall clock.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The id written, or null when something already computes that field.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<Guid?> DeclareAsync(
        string? tenantId,
        Guid id,
        DefineFormula request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertFormula;
        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "field", NpgsqlDbType.Uuid, request.Field);
        Add(command, "operation", NpgsqlDbType.Text, request.Operation.ToString());
        Add(command, "left", NpgsqlDbType.Text, request.Left);
        Add(command, "right", NpgsqlDbType.Text, (object?)request.Right ?? DBNull.Value);
        Add(command, "literal", NpgsqlDbType.Text, (object?)request.Literal ?? DBNull.Value);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not Guid written)
        {
            return null;
        }

        var mark = connection.CreateCommand();
        await using var closingMark = mark.ConfigureAwait(false);

        mark.CommandText = MarkComputed;
        Add(mark, "id", NpgsqlDbType.Uuid, request.Field);

        await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return written;
    }

    /// <summary>Every formula declared for a custom object.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="objectId">Which object.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The declarations.</returns>
    public ValueTask<IReadOnlyList<FormulaRow>> FormulasForAsync(
        string? tenantId,
        Guid objectId,
        CancellationToken cancellationToken) =>
        ReadAsync(tenantId, FormulasForObject, "object", NpgsqlDbType.Uuid, objectId, cancellationToken);

    /// <summary>Every formula declared for a built-in entity kind.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="kind">Which kind.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The declarations.</returns>
    public ValueTask<IReadOnlyList<FormulaRow>> FormulasForAsync(
        string? tenantId,
        EntityKind kind,
        CancellationToken cancellationToken) =>
        ReadAsync(
            tenantId, FormulasForEntity, "appliesTo", NpgsqlDbType.Text, kind.ToString(), cancellationToken);

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });

    private async ValueTask<IReadOnlyList<FormulaRow>> ReadAsync(
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

        var formulas = new List<FormulaRow>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            formulas.Add(new FormulaRow(
                reader.GetGuid(0),
                reader.GetString(1),
                Enum.Parse<FormulaOperation>(reader.GetString(2)),
                reader.GetString(3),
                await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetString(4),
                await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetString(5)));
        }

        return formulas;
    }

    private ValueTask<NpgsqlConnection> OpenAsync(string? tenantId, CancellationToken cancellationToken) =>
        CrmTenantScope.OpenAsync(_source, tenantId, cancellationToken);
}
