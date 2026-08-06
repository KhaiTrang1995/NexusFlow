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
            filter_field, filter_operator, filter_value, order_by, row_limit, match_mode,
            order_descending, order_numeric, created_at,
            kind, group_by, lanes, wip_limit, title_field, subtitle_field, display_columns)
        VALUES (@id, @tenant, @object, @name, @label,
            @filterField, @filterOperator, @filterValue, @orderBy, @limit, @match,
            @descending, @numeric, @now,
            @kind, @groupBy, @lanes, @wipLimit, @titleField, @subtitleField, @columns)
        ON CONFLICT (tenant_id, name) DO NOTHING
        RETURNING view_id
        """;

    private const string ReadView = """
        SELECT view_id, object_id, filter_field, filter_operator, filter_value,
               order_by, row_limit, match_mode, order_descending, order_numeric
        FROM custom_list_view
        WHERE name = @name
        """;

    private const string InsertCriterion = """
        INSERT INTO custom_filter_criterion (
            criterion_id, tenant_id, view_id, ordinal, field, operator, value)
        VALUES (@id, @tenant, @view, @ordinal, @field, @operator, @value)
        """;

    private const string ViewsForObject = """
        SELECT name, label, kind, group_by, lanes, wip_limit,
               title_field, subtitle_field, display_columns
        FROM custom_list_view WHERE object_id = @object ORDER BY name
        """;

    private const string CriteriaForView = """
        SELECT field, operator, value
        FROM custom_filter_criterion
        WHERE view_id = @view
        ORDER BY ordinal
        """;

    // HOW N CRITERIA ARE EVALUATED WITHOUT BUILDING N PREDICATES.
    //
    // A filter with an unknown number of criteria is the case that makes people concatenate SQL,
    // and it does not have to be. The three arrays are bound as parameters and `unnest` turns
    // them back into rows inside the statement; the correlated subquery evaluates each against
    // the outer record and folds them with bool_and or bool_or. So the statement is a constant,
    // the criteria are values, and a filter of one criterion and a filter of twelve run the same
    // plan shape.
    //
    // `cardinality(...) = 0` is the no-filter case, which has to come first: bool_and over an
    // empty set is NULL, not true, and a WHERE of NULL returns nothing.
    private const string Predicate = """
              AND (cardinality(@fields::text[]) = 0 OR coalesce((
                  SELECT CASE WHEN @matchAll THEN bool_and(held) ELSE bool_or(held) END
                  FROM unnest(@fields::text[], @operators::text[], @values::text[])
                       AS criterion(f, o, v)
                  CROSS JOIN LATERAL (SELECT CASE o
                      WHEN 'Equals'      THEN r.values->>f = v
                      WHEN 'NotEquals'   THEN r.values->>f IS DISTINCT FROM v
                      WHEN 'GreaterThan' THEN (r.values->>f)::numeric > v::numeric
                      WHEN 'LessThan'    THEN (r.values->>f)::numeric < v::numeric
                      WHEN 'IsSet'       THEN (r.values->>f IS NOT NULL) = (v = 'true')
                      ELSE false
                  END AS held) AS evaluated), false))
        """;

    // Four orderings and not a built clause. `ORDER BY` takes an expression, and the two axes an
    // administrator picks — text or numeric, ascending or descending — are two bits, so they are
    // four constants rather than a string somebody assembles. The field itself is still a bound
    // value inside `values->>@orderBy`.
    //
    // The numeric ones cast defensively. A row whose value will not parse would otherwise throw
    // and fail the whole page; NULLS LAST puts it at the end instead, which is what a blank cell
    // means in a sorted list.
    private const string OrderText = "\n        ORDER BY r.values->>@orderBy, r.created_at\n        LIMIT @limit";

    private const string OrderTextDescending =
        "\n        ORDER BY r.values->>@orderBy DESC NULLS LAST, r.created_at\n        LIMIT @limit";

    private const string OrderNumeric =
        "\n        ORDER BY nullif(regexp_replace(r.values->>@orderBy, '[^0-9.eE+-]', '', 'g'), '')::numeric\n" +
        "                 NULLS LAST, r.created_at\n        LIMIT @limit";

    private const string OrderNumericDescending =
        "\n        ORDER BY nullif(regexp_replace(r.values->>@orderBy, '[^0-9.eE+-]', '', 'g'), '')::numeric\n" +
        "                 DESC NULLS LAST, r.created_at\n        LIMIT @limit";

    private const string RecordsBody = """
        SELECT r.record_id, r.values::text, r.created_at
        FROM custom_record r
        WHERE r.object_id = @object
        """ + Predicate;

    // The keyset. (created_at, record_id) is unique and is exactly the insertion ordering, so a
    // row-value comparison resumes from the last row of the previous page — no OFFSET, no
    // re-reading, and a row inserted meanwhile cannot shift a later page.
    private const string AfterCursor =
        "\n          AND (r.created_at, r.record_id) > (@afterAt, @afterId)";

    private const string RecordsInOrder = RecordsBody + OrderText;

    private const string RecordsInOrderDescending = RecordsBody + OrderTextDescending;

    private const string RecordsInNumericOrder = RecordsBody + OrderNumeric;

    private const string RecordsInNumericOrderDescending = RecordsBody + OrderNumericDescending;

    private const string RecordsInsertionOrder =
        RecordsBody + "\n        ORDER BY r.created_at, r.record_id\n        LIMIT @limit";

    private const string RecordsAfterCursor =
        RecordsBody + AfterCursor + "\n        ORDER BY r.created_at, r.record_id\n        LIMIT @limit";

    // Every entity a person would type a name into a box to find, and the custom objects an
    // administrator invented, in one statement.
    //
    // WHAT A HIT CARRIES, AND WHY IT IS NOT THE ROW. A title from the entity's own columns and an
    // id — never a custom field. A custom record's values are read-secured per field, and a search
    // that returned them would be a second projection of custom values with no masking, which is
    // exactly the leak the masking function exists to prevent. A hit says what to go and read,
    // and the query surface is what decides how much of it may be seen.
    //
    // The tenant does not appear in the predicate. Row-level security is what scopes this, on all
    // five tables at once, which is the whole argument for the scope being a connection setting
    // rather than a WHERE clause somebody has to remember to write in a sixth place.
    private const string SearchEverything = """
        SELECT kind, id, title, rank FROM (
            SELECT 'Lead' AS kind, lead_id AS id, company AS title,
                   ts_rank(search_document, websearch_to_tsquery('simple', @phrase)) AS rank
            FROM lead WHERE search_document @@ websearch_to_tsquery('simple', @phrase)
            UNION ALL
            SELECT 'Account', account_id, name,
                   ts_rank(search_document, websearch_to_tsquery('simple', @phrase))
            FROM account WHERE search_document @@ websearch_to_tsquery('simple', @phrase)
            UNION ALL
            SELECT 'Contact', contact_id, full_name,
                   ts_rank(search_document, websearch_to_tsquery('simple', @phrase))
            FROM contact WHERE search_document @@ websearch_to_tsquery('simple', @phrase)
            UNION ALL
            SELECT 'Opportunity', opportunity_id, name,
                   ts_rank(search_document, websearch_to_tsquery('simple', @phrase))
            FROM opportunity WHERE search_document @@ websearch_to_tsquery('simple', @phrase)
            UNION ALL
            SELECT 'CustomRecord', r.record_id, o.label,
                   ts_rank(r.search_document, websearch_to_tsquery('simple', @phrase))
            FROM custom_record r
            JOIN custom_object o ON o.object_id = r.object_id
            WHERE r.search_document @@ websearch_to_tsquery('simple', @phrase)
        ) AS hits
        ORDER BY rank DESC, title
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
        Add(command, "filterField", NpgsqlDbType.Text, DBNull.Value);
        Add(command, "filterOperator", NpgsqlDbType.Text, DBNull.Value);
        Add(command, "filterValue", NpgsqlDbType.Text, DBNull.Value);
        Add(command, "match", NpgsqlDbType.Text,
            (request.Filter?.Match ?? FilterMatch.All).ToString());
        Add(command, "orderBy", NpgsqlDbType.Text, (object?)request.Order?.Field ?? DBNull.Value);
        Add(command, "descending", NpgsqlDbType.Boolean, request.Order?.Descending ?? false);
        Add(command, "numeric", NpgsqlDbType.Boolean, request.Order?.Numeric ?? false);
        Add(command, "limit", NpgsqlDbType.Integer, request.Limit);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        var layout = request.Layout ?? new ViewLayout(ViewKind.List);

        Add(command, "kind", NpgsqlDbType.Text, layout.Kind.ToString());
        Add(command, "groupBy", NpgsqlDbType.Text, (object?)layout.GroupBy ?? DBNull.Value);
        Add(command, "titleField", NpgsqlDbType.Text, (object?)layout.TitleField ?? DBNull.Value);
        Add(command, "subtitleField", NpgsqlDbType.Text,
            (object?)layout.SubtitleField ?? DBNull.Value);
        Add(command, "wipLimit", NpgsqlDbType.Integer, (object?)layout.WipLimit ?? DBNull.Value);

        // Null and not an empty array, because the column's meaning of "unset" is null: an empty
        // array of columns is a table with no columns, which is not what leaving it out means.
        AddTextArrayOrNull(command, "lanes", layout.Lanes);
        AddTextArrayOrNull(command, "columns", layout.Columns);

        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not Guid written)
        {
            return null;
        }

        var ordinal = 0;

        foreach (var criterion in request.Filter?.Criteria ?? [])
        {
            var write = connection.CreateCommand();
            await using var closingWrite = write.ConfigureAwait(false);

            write.CommandText = InsertCriterion;
            Add(write, "id", NpgsqlDbType.Uuid, Guid.NewGuid());
            Add(write, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
            Add(write, "view", NpgsqlDbType.Uuid, written);
            Add(write, "ordinal", NpgsqlDbType.Integer, ordinal++);
            Add(write, "field", NpgsqlDbType.Text, criterion.Field);
            Add(write, "operator", NpgsqlDbType.Text, criterion.Operator.ToString());
            Add(write, "value", NpgsqlDbType.Text, criterion.Value);

            await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return written;
    }

    /// <summary>What a saved view asks for.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="name">The view's name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The query it stands for, or null when this tenant has no such view.</returns>
    public async ValueTask<(Guid Target, RecordFilter? Filter, RecordOrder? Order, int Limit)?> ReadViewAsync(
        string? tenantId,
        string name,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        Guid viewId;
        Guid target;
        RollupFilter? legacy;
        string? orderBy;
        int limit;
        FilterMatch match;
        bool descending;
        bool numeric;

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = ReadView;
        Add(command, "name", NpgsqlDbType.Text, name);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        await using (reader.ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            viewId = reader.GetGuid(0);
            target = reader.GetGuid(1);

            legacy = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
                ? null
                : new RollupFilter(
                    reader.GetString(2),
                    Enum.Parse<GuardOperator>(reader.GetString(3)),
                    reader.GetString(4));

            orderBy = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false)
                ? null
                : reader.GetString(5);

            limit = reader.GetInt32(6);
            match = Enum.Parse<FilterMatch>(reader.GetString(7));
            descending = reader.GetBoolean(8);
            numeric = reader.GetBoolean(9);
        }

        // The criteria table first, and the three columns of migration 0009 as the fallback. A
        // view saved before that migration keeps working; one saved after it has no legacy row,
        // so the two cannot both answer and there is no rule needed about which wins.
        var criteria = await CriteriaAsync(connection, viewId, cancellationToken).ConfigureAwait(false);

        var filter = criteria.Count > 0
            ? new RecordFilter(match, criteria)
            : legacy is null
                ? null
                : new RecordFilter(FilterMatch.All, [legacy]);

        return (
            target,
            filter,
            orderBy is { Length: > 0 } ? new RecordOrder(orderBy, descending, numeric) : null,
            limit);
    }

    private static async ValueTask<List<RollupFilter>> CriteriaAsync(
        NpgsqlConnection connection,
        Guid viewId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = CriteriaForView;
        Add(command, "view", NpgsqlDbType.Uuid, viewId);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        var criteria = new List<RollupFilter>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            criteria.Add(new RollupFilter(
                reader.GetString(0),
                Enum.Parse<GuardOperator>(reader.GetString(1)),
                reader.GetString(2)));
        }

        return criteria;
    }

    /// <summary>Every saved view over one object.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="objectId">Which object.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The views, by name.</returns>
    public async ValueTask<IReadOnlyList<DescribedView>> ViewsForAsync(
        string? tenantId,
        Guid objectId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = ViewsForObject;
        Add(command, "object", NpgsqlDbType.Uuid, objectId);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        var views = new List<DescribedView>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            views.Add(new DescribedView(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                await TextOrNullAsync(reader, 3, cancellationToken).ConfigureAwait(false),
                await ArrayOrEmptyAsync(reader, 4, cancellationToken).ConfigureAwait(false),
                await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetInt32(5),
                await TextOrNullAsync(reader, 6, cancellationToken).ConfigureAwait(false),
                await TextOrNullAsync(reader, 7, cancellationToken).ConfigureAwait(false),
                await ArrayOrEmptyAsync(reader, 8, cancellationToken).ConfigureAwait(false)));
        }

        return views;
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
    public async ValueTask<IReadOnlyList<(Guid Id, string Values, DateTimeOffset CreatedAt)>> RecordsAsync(
        string? tenantId,
        Guid target,
        RecordFilter? filter,
        RecordOrder? order,
        int limit,
        (DateTimeOffset CreatedAt, Guid RecordId)? after,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        // Two constants, chosen by whether an ordering was asked for. The field itself is bound
        // as a value into `values->>@orderBy`; putting it into the identifier position is the
        // shape SqlFitnessTests exists to refuse, and it would be right to.
        command.CommandText = (order, after) switch
        {
            (null, not null) => RecordsAfterCursor,
            (null, null) => RecordsInsertionOrder,
            ({ Numeric: true, Descending: true }, _) => RecordsInNumericOrderDescending,
            ({ Numeric: true }, _) => RecordsInNumericOrder,
            ({ Descending: true }, _) => RecordsInOrderDescending,
            _ => RecordsInOrder,
        };

        var criteria = filter?.Criteria ?? [];

        Add(command, "object", NpgsqlDbType.Uuid, target);
        AddTextArray(command, "fields", [.. criteria.Select(static c => c.Field)]);
        AddTextArray(command, "operators", [.. criteria.Select(static c => c.Operator.ToString())]);
        AddTextArray(command, "values", [.. criteria.Select(static c => c.Value)]);
        Add(command, "matchAll", NpgsqlDbType.Boolean,
            filter is null || filter.Match == FilterMatch.All);
        Add(command, "limit", NpgsqlDbType.Integer, limit);

        if (order is not null)
        {
            Add(command, "orderBy", NpgsqlDbType.Text, order.Field);
        }

        if (after is { } resume)
        {
            Add(command, "afterAt", NpgsqlDbType.TimestampTz, resume.CreatedAt);
            Add(command, "afterId", NpgsqlDbType.Uuid, resume.RecordId);
        }

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        var rows = new List<(Guid, string, DateTimeOffset)>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add((
                reader.GetGuid(0),
                reader.GetString(1),
                await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken)
                    .ConfigureAwait(false)));
        }

        return rows;
    }

    /// <summary>Every entity this tenant has whose search document matches a phrase.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="phrase">What to look for.</param>
    /// <param name="limit">How many hits at most.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The hits, most relevant first.</returns>
    /// <remarks>
    /// <strong>A hit carries a title and an id, and never a custom field.</strong> A search that
    /// returned values would be a second projection of them with no masking, which is the leak
    /// <see cref="CustomFieldPolicy.Mask"/> exists to prevent. This says what to go and read; the
    /// query surface decides what may be seen of it.
    /// </remarks>
    public async ValueTask<IReadOnlyList<SearchHit>> SearchAsync(
        string? tenantId,
        string phrase,
        int limit,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = SearchEverything;
        Add(command, "phrase", NpgsqlDbType.Text, phrase);
        Add(command, "limit", NpgsqlDbType.Integer, limit);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        var hits = new List<SearchHit>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            hits.Add(new SearchHit(reader.GetString(0), reader.GetGuid(1), reader.GetString(2)));
        }

        return hits;
    }

    /// <summary>Binds one of the three criterion arrays.</summary>
    /// <remarks>
    /// A separate helper because a text array is <c>NpgsqlDbType.Text</c> with
    /// <c>NpgsqlDbType.Array</c>, and combining them with <c>|</c> is a bitwise operation on an
    /// enumeration that is not <c>[Flags]</c> — which the analyser refuses, correctly. The
    /// parameter's element type is inferred from the value instead.
    /// </remarks>
    private static void AddTextArray(NpgsqlCommand command, string name, string[] values) =>
        command.Parameters.Add(new NpgsqlParameter<string[]>(name, values));

    private static void AddTextArrayOrNull(
        NpgsqlCommand command, string name, IReadOnlyList<string>? values) =>
        command.Parameters.Add(new NpgsqlParameter<string[]?>(
            name, values is { Count: > 0 } ? [.. values] : null));

    private static async ValueTask<string?> TextOrNullAsync(
        NpgsqlDataReader reader, int ordinal, CancellationToken cancellationToken) =>
        await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false)
            ? null
            : reader.GetString(ordinal);

    private static async ValueTask<IReadOnlyList<string>> ArrayOrEmptyAsync(
        NpgsqlDataReader reader, int ordinal, CancellationToken cancellationToken) =>
        await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false)
            ? []
            : await reader
                .GetFieldValueAsync<string[]>(ordinal, cancellationToken)
                .ConfigureAwait(false);

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
