using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// Reading records of a custom object, and the saved views that name a query.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The order is chosen from a closed set of two, not built from the caller's
/// string.</strong> <c>ORDER BY</c> takes an identifier, and a field name is data — which is
/// exactly the shape that makes SQL injection. It is bound as a value into
/// <c>values-&gt;&gt;@orderBy</c> instead, so the ordering is by a jsonb key rather than by a
/// column, and the two statements differ only in whether that clause is present.
/// </para>
/// <para>
/// <strong>The ordering is textual, and that is a real limit rather than an oversight.</strong>
/// <c>-&gt;&gt;</c> gives text, so ordering a Number field puts <c>"9"</c> after <c>"42"</c>.
/// Ordering numerically needs the field's declared type at statement-build time, which is a third
/// statement and a decision about what to do when the declared type and the stored value
/// disagree. Saying so beats a sort that is wrong for one of the four types.
/// </para>
/// </remarks>
public sealed class QueryStore
{
    private const string InsertView = """
        INSERT INTO custom_list_view (
            view_id, tenant_id, object_id, name, label,
            filter_field, filter_operator, filter_value, order_by, row_limit, created_at)
        VALUES (@id, @tenant, @object, @name, @label,
            @filterField, @filterOperator, @filterValue, @orderBy, @limit, @now)
        ON CONFLICT (tenant_id, name) DO NOTHING
        RETURNING view_id
        """;

    private const string ReadView = """
        SELECT object_id, filter_field, filter_operator, filter_value, order_by, row_limit
        FROM custom_list_view
        WHERE name = @name
        """;

    private const string RecordsInOrder = """
        SELECT record_id, values::text
        FROM custom_record
        WHERE object_id = @object
          AND (@filterField IS NULL OR (
              CASE @filterOperator
                  WHEN 'Equals'      THEN values->>@filterField = @filterValue
                  WHEN 'NotEquals'   THEN values->>@filterField IS DISTINCT FROM @filterValue
                  WHEN 'GreaterThan' THEN (values->>@filterField)::numeric > @filterValue::numeric
                  WHEN 'LessThan'    THEN (values->>@filterField)::numeric < @filterValue::numeric
                  WHEN 'IsSet'       THEN (values->>@filterField IS NOT NULL) = (@filterValue = 'true')
                  ELSE false
              END))
        ORDER BY values->>@orderBy, created_at
        LIMIT @limit
        """;

    private const string RecordsInsertionOrder = """
        SELECT record_id, values::text
        FROM custom_record
        WHERE object_id = @object
          AND (@filterField IS NULL OR (
              CASE @filterOperator
                  WHEN 'Equals'      THEN values->>@filterField = @filterValue
                  WHEN 'NotEquals'   THEN values->>@filterField IS DISTINCT FROM @filterValue
                  WHEN 'GreaterThan' THEN (values->>@filterField)::numeric > @filterValue::numeric
                  WHEN 'LessThan'    THEN (values->>@filterField)::numeric < @filterValue::numeric
                  WHEN 'IsSet'       THEN (values->>@filterField IS NOT NULL) = (@filterValue = 'true')
                  ELSE false
              END))
        ORDER BY created_at
        LIMIT @limit
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public QueryStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Saves a view, or reports that the name is taken.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">The id to give it.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="now">The invocation's instant, not the wall clock.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The id written, or null when the name was taken.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<Guid?> SaveViewAsync(
        string? tenantId,
        Guid id,
        DefineListView request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertView;
        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "object", NpgsqlDbType.Uuid, request.Target);
        Add(command, "name", NpgsqlDbType.Text, request.Name);
        Add(command, "label", NpgsqlDbType.Text, request.Label);
        Add(command, "filterField", NpgsqlDbType.Text, (object?)request.Filter?.Field ?? DBNull.Value);
        Add(command, "filterOperator", NpgsqlDbType.Text,
            (object?)request.Filter?.Operator.ToString() ?? DBNull.Value);
        Add(command, "filterValue", NpgsqlDbType.Text, (object?)request.Filter?.Value ?? DBNull.Value);
        Add(command, "orderBy", NpgsqlDbType.Text, (object?)request.OrderBy ?? DBNull.Value);
        Add(command, "limit", NpgsqlDbType.Integer, request.Limit);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
    }

    /// <summary>What a saved view asks for.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="name">The view's name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The query it stands for, or null when this tenant has no such view.</returns>
    public async ValueTask<(Guid Target, RollupFilter? Filter, string? OrderBy, int Limit)?> ReadViewAsync(
        string? tenantId,
        string name,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = ReadView;
        Add(command, "name", NpgsqlDbType.Text, name);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var filterField = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
            ? null
            : reader.GetString(1);

        var orderBy = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false)
            ? null
            : reader.GetString(4);

        return (
            reader.GetGuid(0),
            filterField is null
                ? null
                : new RollupFilter(
                    filterField,
                    Enum.Parse<GuardOperator>(reader.GetString(2)),
                    reader.GetString(3)),
            orderBy,
            reader.GetInt32(5));
    }

    /// <summary>The records of an object that a filter admits.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="target">Which object.</param>
    /// <param name="filter">Which records, or null for all of them.</param>
    /// <param name="orderBy">Which field to order by, or null for insertion order.</param>
    /// <param name="limit">How many rows at most.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The rows, unmasked. Masking is the capability's, on what it returns.</returns>
    /// <remarks>
    /// <strong>Unmasked here on purpose.</strong> A store that masked would be a second place the
    /// rule lived, and the two would drift; it would also make the rows unusable to anything that
    /// has to decide on them rather than show them.
    /// </remarks>
    public async ValueTask<IReadOnlyList<(Guid Id, string Values)>> RecordsAsync(
        string? tenantId,
        Guid target,
        RollupFilter? filter,
        string? orderBy,
        int limit,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        // Two constants, chosen by whether an ordering was asked for. The field itself is bound
        // as a value into `values->>@orderBy`; putting it into the identifier position is the
        // shape SqlFitnessTests exists to refuse, and it would be right to.
        command.CommandText = orderBy is { Length: > 0 } ? RecordsInOrder : RecordsInsertionOrder;

        Add(command, "object", NpgsqlDbType.Uuid, target);
        Add(command, "filterField", NpgsqlDbType.Text, (object?)filter?.Field ?? DBNull.Value);
        Add(command, "filterOperator", NpgsqlDbType.Text,
            (object?)filter?.Operator.ToString() ?? DBNull.Value);
        Add(command, "filterValue", NpgsqlDbType.Text, (object?)filter?.Value ?? DBNull.Value);
        Add(command, "limit", NpgsqlDbType.Integer, limit);

        if (orderBy is { Length: > 0 })
        {
            Add(command, "orderBy", NpgsqlDbType.Text, orderBy);
        }

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        var rows = new List<(Guid, string)>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return rows;
    }

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });

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
