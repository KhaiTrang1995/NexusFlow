using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// Reads a page of a built-in entity.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Four statements, one shape, and no SQL is built.</strong> A filter of unknown size is
/// the case that makes people concatenate, and it does not have to be: the three arrays are bound
/// as parameters, <c>unnest</c> turns them back into rows inside the statement, and the criteria
/// are evaluated against <c>to_jsonb(row)</c>. So a filter of one criterion and a filter of twelve
/// run the same statement. This is the same trick <c>QueryStore</c> uses for custom records; the
/// only difference is that a built-in entity's values come from its columns rather than from a
/// jsonb column, which <c>to_jsonb</c> closes.
/// </para>
/// <para>
/// <strong>Why the whole row and then a projection.</strong> The statement selects
/// <c>to_jsonb(row)</c> and the store keeps only the columns <see cref="EntityColumns"/> offers.
/// A column added to one of these tables therefore appears here when somebody adds it to that
/// list and not before — which is the difference between a surface that is decided and one that
/// is whatever the schema happens to contain.
/// </para>
/// <para>
/// <strong>Keyset, on the primary key.</strong> Stable under insertion, unlike an offset, and the
/// cursor is the last id of the page — so a caller cannot construct one that means anything else.
/// </para>
/// </remarks>
public sealed class EntityQueryStore
{
    // The predicate, shared by all four. `cardinality(...) = 0` is the no-filter case and has to
    // come first: bool_and over an empty set is NULL, not true, and a WHERE of NULL returns
    // nothing at all.
    private const string Predicate = """
          AND (cardinality(@fields::text[]) = 0 OR coalesce((
              SELECT CASE WHEN @matchAll THEN bool_and(held) ELSE bool_or(held) END
              FROM unnest(@fields::text[], @operators::text[], @values::text[])
                   AS criterion(f, o, v)
              CROSS JOIN LATERAL (SELECT CASE o
                  WHEN 'Equals'      THEN body->>f = v
                  WHEN 'NotEquals'   THEN body->>f IS DISTINCT FROM v
                  WHEN 'GreaterThan' THEN (body->>f)::numeric > v::numeric
                  WHEN 'LessThan'    THEN (body->>f)::numeric < v::numeric
                  WHEN 'IsSet'       THEN (body->>f IS NOT NULL) = (v = 'true')
                  ELSE false
              END AS held) AS evaluated), false))
        """;

    private const string LeadPage = """
        SELECT lead_id, to_jsonb(l) AS body
        FROM lead l, LATERAL (SELECT to_jsonb(l) AS body) AS projected
        WHERE (@after IS NULL OR lead_id > @after)
        """ + Predicate + """

        ORDER BY lead_id
        LIMIT @limit
        """;

    private const string AccountPage = """
        SELECT account_id, to_jsonb(a) AS body
        FROM account a, LATERAL (SELECT to_jsonb(a) AS body) AS projected
        WHERE (@after IS NULL OR account_id > @after)
        """ + Predicate + """

        ORDER BY account_id
        LIMIT @limit
        """;

    private const string ContactPage = """
        SELECT contact_id, to_jsonb(c) AS body
        FROM contact c, LATERAL (SELECT to_jsonb(c) AS body) AS projected
        WHERE (@after IS NULL OR contact_id > @after)
        """ + Predicate + """

        ORDER BY contact_id
        LIMIT @limit
        """;

    // The one entity whose row is not the whole answer. `stage_id` is a foreign key into the
    // configured process, and an identifier is not something a pipeline board can group by or a
    // person can read — so the stage's *name* is merged into the projection. It is a column of
    // the answer without being a column of the table, which is why the join is here and the name
    // is in EntityColumns.Readable.
    private const string OpportunityPage = """
        SELECT opportunity_id,
               to_jsonb(o) || jsonb_build_object('stage', s.name) AS body
        FROM opportunity o
        LEFT JOIN process_stage s ON s.stage_id = o.stage_id,
        LATERAL (SELECT to_jsonb(o) || jsonb_build_object('stage', s.name) AS body) AS projected
        WHERE (@after IS NULL OR opportunity_id > @after)
        """ + Predicate + """

        ORDER BY opportunity_id
        LIMIT @limit
        """;

    private const string QuotePage = """
        SELECT quote_id, to_jsonb(q) AS body
        FROM quote q, LATERAL (SELECT to_jsonb(q) AS body) AS projected
        WHERE (@after IS NULL OR quote_id > @after)
        """ + Predicate + """

        ORDER BY quote_id
        LIMIT @limit
        """;

    private const string QuoteLinePage = """
        SELECT quote_line_id, to_jsonb(l) AS body
        FROM quote_line l, LATERAL (SELECT to_jsonb(l) AS body) AS projected
        WHERE (@after IS NULL OR quote_line_id > @after)
        """ + Predicate + """

        ORDER BY quote_line_id
        LIMIT @limit
        """;

    private const string OrderPage = """
        SELECT order_id, to_jsonb(o) AS body
        FROM sales_order o, LATERAL (SELECT to_jsonb(o) AS body) AS projected
        WHERE (@after IS NULL OR order_id > @after)
        """ + Predicate + """

        ORDER BY order_id
        LIMIT @limit
        """;

    private const string ActivityPage = """
        SELECT activity_id, to_jsonb(a) AS body
        FROM activity a, LATERAL (SELECT to_jsonb(a) AS body) AS projected
        WHERE (@after IS NULL OR activity_id > @after)
        """ + Predicate + """

        ORDER BY activity_id
        LIMIT @limit
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public EntityQueryStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Reads a page.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="query">What was asked for.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The rows and the next cursor.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is null.</exception>
    public async ValueTask<RecordPage> PageAsync(
        string? tenantId,
        ReadEntityPage query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        // One constant per entity, chosen by it. A table name cannot be a parameter, and building
        // one from a caller's value is the shape SqlFitnessTests exists to refuse.
        command.CommandText = query.Entity switch
        {
            ReadableEntity.Lead => LeadPage,
            ReadableEntity.Account => AccountPage,
            ReadableEntity.Contact => ContactPage,
            ReadableEntity.Quote => QuotePage,
            ReadableEntity.QuoteLine => QuoteLinePage,
            ReadableEntity.Order => OrderPage,
            ReadableEntity.Activity => ActivityPage,
            _ => OpportunityPage,
        };

        var criteria = query.Filter?.Criteria ?? [];

        AddTextArray(command, "fields", [.. criteria.Select(criterion => criterion.Field)]);
        AddTextArray(
            command, "operators", [.. criteria.Select(criterion => criterion.Operator.ToString())]);
        AddTextArray(command, "values", [.. criteria.Select(criterion => criterion.Value)]);

        Add(command, "matchAll", NpgsqlDbType.Boolean,
            (query.Filter?.Match ?? FilterMatch.All) == FilterMatch.All);

        Add(command, "after", NpgsqlDbType.Uuid,
            Guid.TryParse(query.After, out var cursor) ? cursor : DBNull.Value);

        Add(command, "limit", NpgsqlDbType.Integer,
            Math.Clamp(query.Limit, 1, EntityQueryLimits.MaxPage));

        var offered = EntityColumns.Readable(query.Entity);
        var rows = new List<RecordView>();
        var last = Guid.Empty;

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            last = reader.GetGuid(0);

            var body = await reader.GetFieldValueAsync<System.Text.Json.JsonDocument>(1, cancellationToken)
                .ConfigureAwait(false);

            using (body)
            {
                var values = new Dictionary<string, string?>(StringComparer.Ordinal);

                foreach (var column in offered)
                {
                    values[column] = body.RootElement.TryGetProperty(column, out var value)
                        && value.ValueKind is not System.Text.Json.JsonValueKind.Null
                        ? value.ValueKind is System.Text.Json.JsonValueKind.String
                            ? value.GetString()
                            : value.ToString()
                        : null;
                }

                rows.Add(new RecordView(last, values));
            }
        }

        // Null when this page was short, so a caller never makes a request that returns nothing.
        var next = rows.Count >= Math.Clamp(query.Limit, 1, EntityQueryLimits.MaxPage)
            ? last.ToString()
            : null;

        return new RecordPage(rows, [], next);
    }

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });

    /// <summary>Binds one of the three criterion arrays.</summary>
    /// <remarks>
    /// A separate helper for the reason <c>QueryStore</c> gives: a text array is
    /// <c>NpgsqlDbType.Text</c> with <c>NpgsqlDbType.Array</c>, and combining them with <c>|</c>
    /// is a bitwise operation on an enumeration that is not <c>[Flags]</c> — which the analyser
    /// refuses, correctly. The element type is inferred from the value instead.
    /// </remarks>
    private static void AddTextArray(NpgsqlCommand command, string name, string[] values) =>
        command.Parameters.Add(new NpgsqlParameter<string[]>(name, values));

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
