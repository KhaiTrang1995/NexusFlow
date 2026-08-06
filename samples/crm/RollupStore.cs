using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// Declaring a roll-up, and recomputing one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The aggregate is chosen from a closed set and the rest of the statement is
/// constant.</strong> This is the file where building SQL from configuration is most tempting —
/// the aggregate, the field and the comparison are all data. None of them is interpolated: the
/// aggregate is a <c>switch</c> over five constants at the sink, which is what
/// <c>SqlFitnessTests</c> can still resolve, and the field names and the bound are bound
/// parameters reaching <c>jsonb</c> as values.
/// </para>
/// <para>
/// <strong>The filter is evaluated in SQL rather than in the process.</strong> Reading every
/// child to filter them here would make a roll-up over ten thousand rows ten thousand rows on the
/// wire; the operators are the same five, expressed once as a <c>CASE</c> the planner can use.
/// </para>
/// </remarks>
public sealed class RollupStore
{
    private const string InsertRollup = """
        INSERT INTO custom_rollup (
            rollup_id, tenant_id, field_id, relationship_id, aggregate, source_field_id,
            filter_field, filter_operator, filter_value, created_at)
        VALUES (@id, @tenant, @field, @relationship, @aggregate, @source,
            @filterField, @filterOperator, @filterValue, @now)
        ON CONFLICT (field_id) DO NOTHING
        RETURNING rollup_id
        """;

    private const string MarkComputed =
        "UPDATE custom_field SET is_computed = true WHERE field_id = @id";

    private const string RollupsForRelationship = """
        SELECT r.rollup_id, f.name, r.relationship_id, r.aggregate, s.name,
               r.filter_field, r.filter_operator, r.filter_value
        FROM custom_rollup r
        JOIN custom_field f ON f.field_id = r.field_id
        LEFT JOIN custom_field s ON s.field_id = r.source_field_id
        WHERE r.relationship_id = @relationship
        """;

    private const string FieldOwner = """
        SELECT object_id, is_computed FROM custom_field WHERE field_id = @id
        """;

    // The five aggregates, over the children of one parent, with the filter applied in SQL.
    //
    // `values->>@source` reads the child's value as text and the cast makes the arithmetic
    // numeric; a Sum over text would concatenate, and a Max over text would order "9" after "42".
    // Count is the one that reads no field at all, which is why it is a separate statement rather
    // than a sixth arm.
    private const string CountChildren = """
        SELECT count(*)::text
        FROM custom_link l
        JOIN custom_record c ON c.record_id = l.to_record_id
        WHERE l.relationship_id = @relationship AND l.from_record_id = @parent
          AND (@filterField IS NULL OR (
              CASE @filterOperator
                  WHEN 'Equals'      THEN c.values->>@filterField = @filterValue
                  WHEN 'NotEquals'   THEN c.values->>@filterField IS DISTINCT FROM @filterValue
                  WHEN 'GreaterThan' THEN (c.values->>@filterField)::numeric > @filterValue::numeric
                  WHEN 'LessThan'    THEN (c.values->>@filterField)::numeric < @filterValue::numeric
                  WHEN 'IsSet'       THEN (c.values->>@filterField IS NOT NULL) = (@filterValue = 'true')
                  ELSE false
              END))
        """;

    private const string SumChildren = """
        SELECT sum((c.values->>@source)::numeric)::text
        FROM custom_link l
        JOIN custom_record c ON c.record_id = l.to_record_id
        WHERE l.relationship_id = @relationship AND l.from_record_id = @parent
          AND (@filterField IS NULL OR (
              CASE @filterOperator
                  WHEN 'Equals'      THEN c.values->>@filterField = @filterValue
                  WHEN 'NotEquals'   THEN c.values->>@filterField IS DISTINCT FROM @filterValue
                  WHEN 'GreaterThan' THEN (c.values->>@filterField)::numeric > @filterValue::numeric
                  WHEN 'LessThan'    THEN (c.values->>@filterField)::numeric < @filterValue::numeric
                  WHEN 'IsSet'       THEN (c.values->>@filterField IS NOT NULL) = (@filterValue = 'true')
                  ELSE false
              END))
        """;

    private const string MinChildren = """
        SELECT min((c.values->>@source)::numeric)::text
        FROM custom_link l
        JOIN custom_record c ON c.record_id = l.to_record_id
        WHERE l.relationship_id = @relationship AND l.from_record_id = @parent
          AND (@filterField IS NULL OR (
              CASE @filterOperator
                  WHEN 'Equals'      THEN c.values->>@filterField = @filterValue
                  WHEN 'NotEquals'   THEN c.values->>@filterField IS DISTINCT FROM @filterValue
                  WHEN 'GreaterThan' THEN (c.values->>@filterField)::numeric > @filterValue::numeric
                  WHEN 'LessThan'    THEN (c.values->>@filterField)::numeric < @filterValue::numeric
                  WHEN 'IsSet'       THEN (c.values->>@filterField IS NOT NULL) = (@filterValue = 'true')
                  ELSE false
              END))
        """;

    private const string MaxChildren = """
        SELECT max((c.values->>@source)::numeric)::text
        FROM custom_link l
        JOIN custom_record c ON c.record_id = l.to_record_id
        WHERE l.relationship_id = @relationship AND l.from_record_id = @parent
          AND (@filterField IS NULL OR (
              CASE @filterOperator
                  WHEN 'Equals'      THEN c.values->>@filterField = @filterValue
                  WHEN 'NotEquals'   THEN c.values->>@filterField IS DISTINCT FROM @filterValue
                  WHEN 'GreaterThan' THEN (c.values->>@filterField)::numeric > @filterValue::numeric
                  WHEN 'LessThan'    THEN (c.values->>@filterField)::numeric < @filterValue::numeric
                  WHEN 'IsSet'       THEN (c.values->>@filterField IS NOT NULL) = (@filterValue = 'true')
                  ELSE false
              END))
        """;

    private const string AverageChildren = """
        SELECT round(avg((c.values->>@source)::numeric), 6)::text
        FROM custom_link l
        JOIN custom_record c ON c.record_id = l.to_record_id
        WHERE l.relationship_id = @relationship AND l.from_record_id = @parent
          AND (@filterField IS NULL OR (
              CASE @filterOperator
                  WHEN 'Equals'      THEN c.values->>@filterField = @filterValue
                  WHEN 'NotEquals'   THEN c.values->>@filterField IS DISTINCT FROM @filterValue
                  WHEN 'GreaterThan' THEN (c.values->>@filterField)::numeric > @filterValue::numeric
                  WHEN 'LessThan'    THEN (c.values->>@filterField)::numeric < @filterValue::numeric
                  WHEN 'IsSet'       THEN (c.values->>@filterField IS NOT NULL) = (@filterValue = 'true')
                  ELSE false
              END))
        """;

    // jsonb_set rather than `||` with a rendered document, because the value is a number and
    // building the object as text here would be the one place in this file that assembled JSON
    // by hand. to_jsonb of a numeric keeps it a number, which is what a numeric guard needs.
    private const string WriteRollupValue = """
        UPDATE custom_record
        SET values = jsonb_set(values, ARRAY[@field], to_jsonb(@value::numeric), true)
        WHERE record_id = @parent
        """;

    private const string ClearRollupValue = """
        UPDATE custom_record
        SET values = jsonb_set(values, ARRAY[@field], 'null'::jsonb, true)
        WHERE record_id = @parent
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public RollupStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Declares a roll-up and marks its field computed.</summary>
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
        DefineRollup request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertRollup;
        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "field", NpgsqlDbType.Uuid, request.Field);
        Add(command, "relationship", NpgsqlDbType.Uuid, request.Relationship);
        Add(command, "aggregate", NpgsqlDbType.Text, request.Aggregate.ToString());
        Add(command, "source", NpgsqlDbType.Uuid, (object?)request.SourceField ?? DBNull.Value);
        Add(command, "filterField", NpgsqlDbType.Text, (object?)request.Filter?.Field ?? DBNull.Value);
        Add(command, "filterOperator", NpgsqlDbType.Text,
            (object?)request.Filter?.Operator.ToString() ?? DBNull.Value);
        Add(command, "filterValue", NpgsqlDbType.Text, (object?)request.Filter?.Value ?? DBNull.Value);
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

    /// <summary>Which object a field belongs to, and whether something already computes it.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="fieldId">The field.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The owner and its computed flag, or null when this tenant has no such field.</returns>
    public async ValueTask<(Guid? Object, bool IsComputed)?> FieldOwnerAsync(
        string? tenantId,
        Guid fieldId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = FieldOwner;
        Add(command, "id", NpgsqlDbType.Uuid, fieldId);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var owner = await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false)
            ? (Guid?)null
            : reader.GetGuid(0);

        return (owner, reader.GetBoolean(1));
    }

    /// <summary>Recomputes every roll-up that walks a relationship, for one parent.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="relationshipId">The edge that was just linked.</param>
    /// <param name="parentId">The parent whose children changed.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many roll-ups were written.</returns>
    /// <remarks>
    /// <strong>Every roll-up on the edge, not just one.</strong> An administrator may declare a
    /// count and a sum over the same relationship, and a recompute that updated whichever it
    /// found first would leave the other stale — which is the failure that makes people stop
    /// trusting the numbers.
    /// </remarks>
    public async ValueTask<int> RecomputeAsync(
        string? tenantId,
        Guid relationshipId,
        Guid parentId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var rollups = await ReadAsync(connection, relationshipId, cancellationToken)
            .ConfigureAwait(false);

        foreach (var rollup in rollups)
        {
            var value = await AggregateAsync(connection, rollup, parentId, cancellationToken)
                .ConfigureAwait(false);

            var write = connection.CreateCommand();
            await using var closingWrite = write.ConfigureAwait(false);

            // Null when a Min over no children has nothing to answer with. Written as JSON null
            // rather than left alone, so a roll-up whose last child was filtered out reads as
            // "nothing" instead of as whatever it was before.
            write.CommandText = value is null ? ClearRollupValue : WriteRollupValue;
            Add(write, "parent", NpgsqlDbType.Uuid, parentId);
            Add(write, "field", NpgsqlDbType.Text, rollup.FieldName);

            if (value is not null)
            {
                Add(write, "value", NpgsqlDbType.Text, value);
            }

            await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return rollups.Count;
    }

    private static async ValueTask<string?> AggregateAsync(
        NpgsqlConnection connection,
        RollupRow rollup,
        Guid parentId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        // A switch over five constants at the sink. A helper returning one would be a run-time
        // value as far as SqlFitnessTests is concerned, and it would be right — nothing stops a
        // helper interpolating.
        command.CommandText = rollup.Aggregate switch
        {
            RollupAggregate.Count => CountChildren,
            RollupAggregate.Sum => SumChildren,
            RollupAggregate.Min => MinChildren,
            RollupAggregate.Max => MaxChildren,
            RollupAggregate.Average => AverageChildren,
            _ => throw new ArgumentOutOfRangeException(nameof(rollup), rollup.Aggregate, "No such aggregate."),
        };

        Add(command, "relationship", NpgsqlDbType.Uuid, rollup.Relationship);
        Add(command, "parent", NpgsqlDbType.Uuid, parentId);
        Add(command, "filterField", NpgsqlDbType.Text, (object?)rollup.Filter?.Field ?? DBNull.Value);
        Add(command, "filterOperator", NpgsqlDbType.Text,
            (object?)rollup.Filter?.Operator.ToString() ?? DBNull.Value);
        Add(command, "filterValue", NpgsqlDbType.Text, (object?)rollup.Filter?.Value ?? DBNull.Value);

        if (rollup.Aggregate != RollupAggregate.Count)
        {
            Add(command, "source", NpgsqlDbType.Text, rollup.SourceFieldName ?? string.Empty);
        }

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static async ValueTask<List<RollupRow>> ReadAsync(
        NpgsqlConnection connection,
        Guid relationshipId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = RollupsForRelationship;
        Add(command, "relationship", NpgsqlDbType.Uuid, relationshipId);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        var rollups = new List<RollupRow>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var source = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false)
                ? null
                : reader.GetString(4);

            var filterField = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false)
                ? null
                : reader.GetString(5);

            rollups.Add(new RollupRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetGuid(2),
                Enum.Parse<RollupAggregate>(reader.GetString(3)),
                source,
                filterField is null
                    ? null
                    : new RollupFilter(
                        filterField,
                        Enum.Parse<GuardOperator>(reader.GetString(6)),
                        reader.GetString(7))));
        }

        return rollups;
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
