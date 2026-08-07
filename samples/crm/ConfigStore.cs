using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// Lists what a tenant has declared, one statement per kind.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Twelve constants and one switch.</strong> A table name cannot be a parameter, and
/// building one from a caller's value is the shape <c>SqlFitnessTests</c> exists to refuse — so
/// the kind chooses a statement rather than composing one. Each returns the same four columns,
/// which is what lets one contract serve twelve tables.
/// </para>
/// <para>
/// <strong>The sentence is composed in SQL, deliberately.</strong> A summary built on the client
/// would need every column the sentence mentions, so the response would carry the whole row for
/// every kind — and a rule's operator and value would then be on the wire for the sake of a
/// string. Composing here means the read hands back what a screen draws and nothing more.
/// </para>
/// <para>
/// The tenant is nowhere in these predicates. Row-level security scopes all twelve, which is the
/// argument for the scope being a connection setting rather than a clause somebody remembers.
/// </para>
/// </remarks>
public sealed class ConfigStore
{
    private const string ListViews = """
        SELECT v.view_id, v.name, v.label,
               'over ' || o.label ||
               coalesce(', where ' || v.filter_field || ' ' || v.filter_operator || ' ' || v.filter_value, '') ||
               coalesce(', ordered by ' || v.order_by, ''),
               true
        FROM custom_list_view v
        JOIN custom_object o ON o.object_id = v.object_id
        ORDER BY v.label
        LIMIT @limit
        """;

    private const string ValidationRules = """
        SELECT r.rule_id, r.name, r.name,
               'refuses ' || coalesce(r.applies_to, o.label, 'a record') ||
               ' when ' || r.field || ' ' || r.operator || ' ' || r.value,
               true
        FROM custom_validation_rule r
        LEFT JOIN custom_object o ON o.object_id = r.object_id
        ORDER BY r.name
        LIMIT @limit
        """;

    private const string RollUps = """
        SELECT u.rollup_id, f.name, f.label,
               lower(u.aggregate) || ' of the children joined by ' || r.name,
               true
        FROM custom_rollup u
        JOIN custom_field f ON f.field_id = u.field_id
        JOIN custom_relationship r ON r.relationship_id = u.relationship_id
        ORDER BY f.label
        LIMIT @limit
        """;

    private const string Formulas = """
        SELECT m.formula_id, f.name, f.label,
               lower(m.operation) || ' of ' || m.left_field ||
               coalesce(' and ' || m.right_field, '') || coalesce(' and ' || m.literal, ''),
               true
        FROM custom_formula m
        JOIN custom_field f ON f.field_id = m.field_id
        ORDER BY f.label
        LIMIT @limit
        """;

    private const string Reports = """
        SELECT report_id, name, label,
               'groups ' || source || ' by ' || dimension,
               true
        FROM custom_report
        ORDER BY label
        LIMIT @limit
        """;

    private const string Dashboards = """
        SELECT d.dashboard_id, d.name, d.label,
               count(t.dashboard_id)::text || ' tile(s)',
               true
        FROM dashboard d
        LEFT JOIN dashboard_tile t ON t.dashboard_id = d.dashboard_id
        GROUP BY d.dashboard_id, d.name, d.label
        ORDER BY d.label
        LIMIT @limit
        """;

    // The endpoint is the address this server sends to, and it is the one column here that could
    // carry a credential in a query string. Reduced to its host: an administrator needs to know
    // where it points, and a setup screen is not where a token belongs.
    private const string Connectors = """
        SELECT connector_id, name, name,
               kind || ' to ' || split_part(split_part(endpoint, '://', 2), '/', 1),
               is_enabled
        FROM connector
        ORDER BY name
        LIMIT @limit
        """;

    private const string Labels = """
        SELECT NULL::uuid, kind || coalesce('.' || nullif(field, ''), ''), label,
               'this tenant calls ' ||
               CASE WHEN field = '' THEN 'the ' || kind ELSE kind || '.' || field END ||
               ' "' || label || '"',
               true
        FROM entity_label
        ORDER BY kind, field
        LIMIT @limit
        """;

    private const string ApprovalProcesses = """
        SELECT p.process_id, p.name, p.label,
               'on a ' || p.subject || ', ' || count(s.process_id)::text || ' step(s)' ||
               CASE WHEN p.priority = 100 THEN '' ELSE ', priority ' || p.priority::text END,
               p.is_active
        FROM approval_process p
        LEFT JOIN approval_step s ON s.process_id = p.process_id
        GROUP BY p.process_id, p.name, p.label, p.subject, p.priority, p.is_active
        ORDER BY p.priority, p.label
        LIMIT @limit
        """;

    private const string SlaPolicies = """
        SELECT policy_id, name, label,
               'answers a ' || priority || ' case within ' || first_response_minutes::text ||
               'm and resolves it within ' || resolution_minutes::text || 'm' ||
               CASE WHEN business_hours_only THEN ' (business hours)' ELSE ' (around the clock)' END,
               is_active
        FROM sla_policy
        ORDER BY label
        LIMIT @limit
        """;

    // The day is named by a CASE and not by to_char. `to_char(to_timestamp(n, 'ID'), 'Day')`
    // looks like it names a weekday and does not: with no date to anchor it, every value formats
    // as the same day — which is a label that is wrong six times in seven and looks deliberate.
    private const string BusinessHours = """
        SELECT NULL::uuid, day_of_week::text,
               CASE day_of_week
                   WHEN 0 THEN 'Sunday'
                   WHEN 1 THEN 'Monday'
                   WHEN 2 THEN 'Tuesday'
                   WHEN 3 THEN 'Wednesday'
                   WHEN 4 THEN 'Thursday'
                   WHEN 5 THEN 'Friday'
                   ELSE 'Saturday'
               END,
               'open ' || to_char(opens_at, 'HH24:MI') || ' to ' || to_char(closes_at, 'HH24:MI'),
               true
        FROM business_hours
        ORDER BY day_of_week
        LIMIT @limit
        """;

    private const string Territories = """
        SELECT t.territory_id, t.name, t.label,
               count(r.territory_id)::text || ' rule(s), priority ' || t.priority::text,
               true
        FROM territory t
        LEFT JOIN territory_rule r ON r.territory_id = t.territory_id
        GROUP BY t.territory_id, t.name, t.label, t.priority
        ORDER BY t.priority, t.label
        LIMIT @limit
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public ConfigStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Lists what this tenant has declared of one kind.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="query">Which kind, and how many.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What there is.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is null.</exception>
    public async ValueTask<IReadOnlyList<ConfigItem>> ListAsync(
        string? tenantId,
        ReadConfig query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = query.Kind switch
        {
            ConfigKind.ListView => ListViews,
            ConfigKind.ValidationRule => ValidationRules,
            ConfigKind.RollUp => RollUps,
            ConfigKind.Formula => Formulas,
            ConfigKind.Report => Reports,
            ConfigKind.Dashboard => Dashboards,
            ConfigKind.Connector => Connectors,
            ConfigKind.Label => Labels,
            ConfigKind.ApprovalProcess => ApprovalProcesses,
            ConfigKind.SlaPolicy => SlaPolicies,
            ConfigKind.BusinessHours => BusinessHours,
            _ => Territories,
        };

        command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer)
        {
            Value = Math.Clamp(query.Limit, 1, ConfigLimits.MaxItems),
        });

        var items = new List<ConfigItem>();

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new ConfigItem(
                await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2).Trim(),
                reader.GetString(3),
                reader.GetBoolean(4)));
        }

        return items;
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
