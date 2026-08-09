using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// Saved reports and dashboards, and the four statements that run them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The dimension and the measure are bound values inside a <c>CASE</c>, never text spliced
/// into the statement.</strong> This is the whole reason the vocabulary is closed: with a fixed
/// list of names, "group by whatever the user picked" is a parameter comparison rather than an
/// identifier the application assembles. So the statement is a constant the build can see —
/// <c>SqlFitnessTests</c> is what holds that — and a report is a row rather than a string somebody
/// has to trust.
/// </para>
/// <para>
/// <strong>The aggregate is chosen the same way.</strong> Five aggregate expressions in one
/// <c>CASE</c> over a bound <c>@measure</c>, instead of five statements per source. PostgreSQL
/// evaluates only the branch it needs, and the alternative is twenty statements that drift.
/// </para>
/// </remarks>
public sealed class ReportStore
{
    // The head and tail every source shares: group the rows the source produced, reduce them by
    // the measure, and take the largest hundred. Largest first, so a bounded response cuts the
    // tail rather than an arbitrary slice.
    private const string GroupHead = """
        SELECT r.dimension,
               count(*) AS row_count,
               CASE @measure
                   WHEN 'Count'   THEN count(*)::numeric
                   WHEN 'Sum'     THEN coalesce(sum(r.measured), 0)
                   WHEN 'Average' THEN avg(r.measured)
                   WHEN 'Min'     THEN min(r.measured)
                   WHEN 'Max'     THEN max(r.measured)
               END AS measure_value
        FROM (
        """;

    private const string GroupTail = """

        ) AS r
        GROUP BY r.dimension
        ORDER BY measure_value DESC NULLS LAST, r.dimension
        LIMIT @limit
        """;

    private const string OpportunityRows = """
            SELECT CASE @dimension
                       WHEN 'Outcome'     THEN coalesce(outcome, '(none)')
                       WHEN 'Currency'    THEN currency
                       WHEN 'Probability' THEN probability::text
                   END AS dimension,
                   CASE @measureOf
                       WHEN 'Amount'      THEN amount
                       WHEN 'Probability' THEN probability::numeric
                   END AS measured
            FROM opportunity
        """;

    private const string LeadRows = """
            SELECT CASE @dimension
                       WHEN 'Source' THEN source
                       WHEN 'Status' THEN status
                   END AS dimension,
                   CASE @measureOf
                       WHEN 'Score' THEN score::numeric
                   END AS measured
            FROM lead
        """;

    private const string ActivityRows = """
            SELECT CASE @dimension
                       WHEN 'Kind'          THEN kind
                       WHEN 'Status'        THEN status
                       WHEN 'RelatesToKind' THEN relates_to_kind
                   END AS dimension,
                   CASE @measureOf
                       WHEN 'EscalationCount' THEN escalation_count::numeric
                   END AS measured
            FROM activity
        """;

    // A custom object's dimension is already a value — a jsonb key — so it needs no CASE. The cast
    // is defensive for the reason 0011 gives about ordering: a value that will not parse is a row
    // that does not contribute, rather than a report that throws and takes the whole dashboard
    // down with it.
    private const string CustomRecordRows = """
            SELECT coalesce(values->>@dimension, '(none)') AS dimension,
                   CASE WHEN values->>@measureOf ~ '^-?[0-9]+(\.[0-9]+)?$'
                        THEN (values->>@measureOf)::numeric
                   END AS measured
            FROM custom_record
            WHERE object_id = @object
        """;

    private const string OpportunityReport = GroupHead + OpportunityRows + GroupTail;
    private const string LeadReport = GroupHead + LeadRows + GroupTail;
    private const string ActivityReport = GroupHead + ActivityRows + GroupTail;
    private const string CustomRecordReport = GroupHead + CustomRecordRows + GroupTail;

    private const string InsertReport = """
        INSERT INTO custom_report (
            report_id, tenant_id, name, label, source, object_id,
            dimension, measure, measure_of, created_at)
        VALUES (@id, @tenant, @name, @label, @source, @object,
            @dimension, @measure, @measureOf, @now)
        ON CONFLICT (tenant_id, name) DO NOTHING
        RETURNING report_id
        """;

    private const string ReadReport = """
        SELECT report_id, label, source, object_id, dimension, measure, measure_of
        FROM custom_report WHERE name = @name
        """;

    private const string InsertDashboard = """
        INSERT INTO dashboard (dashboard_id, tenant_id, name, label, created_at)
        VALUES (@id, @tenant, @name, @label, @now)
        ON CONFLICT (tenant_id, name) DO NOTHING
        RETURNING dashboard_id
        """;

    private const string InsertTile = """
        INSERT INTO dashboard_tile (dashboard_id, tenant_id, ordinal, report_id)
        VALUES (@dashboard, @tenant, @ordinal, @report)
        """;

    private const string ReadDashboard = """
        SELECT dashboard_id, label FROM dashboard WHERE name = @name
        """;

    private const string ReadTiles = """
        SELECT r.name
        FROM dashboard_tile t
        JOIN custom_report r ON r.report_id = t.report_id
        WHERE t.dashboard_id = @dashboard
        ORDER BY t.ordinal
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public ReportStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Saves a report, unless the name is taken.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">What the report is to be called.</param>
    /// <param name="report">What was asked for.</param>
    /// <param name="now">When it was saved.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The id, or null when the name is already in use.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is null.</exception>
    public async ValueTask<Guid?> SaveReportAsync(
        string? tenantId,
        Guid id,
        DefineReport report,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertReport;

        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "name", NpgsqlDbType.Text, report.Name);
        Add(command, "label", NpgsqlDbType.Text, report.Label);
        Add(command, "source", NpgsqlDbType.Text, report.Source.ToString());
        Add(command, "dimension", NpgsqlDbType.Text, report.Dimension);
        Add(command, "measure", NpgsqlDbType.Text, report.Measure.ToString());
        Add(command, "now", NpgsqlDbType.TimestampTz, now);
        AddOrNull(command, "object", NpgsqlDbType.Uuid, report.Target);
        AddOrNull(command, "measureOf", NpgsqlDbType.Text, report.MeasureOf);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
    }

    /// <summary>Reads a saved report by name.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="name">Which report.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The report, or null when this tenant has no such report.</returns>
    public async ValueTask<StoredReport?> ReportAsync(
        string? tenantId,
        string name,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        return await ReportOnAsync(connection, name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a saved report.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="report">Which report.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The groups, largest first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is null.</exception>
    public async ValueTask<IReadOnlyList<ReportGroup>> RunAsync(
        string? tenantId,
        StoredReport report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        return await GroupsAsync(connection, report, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Saves a dashboard and its tiles, unless the name is taken.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">What the dashboard is to be called.</param>
    /// <param name="name">Its identifier.</param>
    /// <param name="label">What to show a person.</param>
    /// <param name="reports">The reports it shows, in order.</param>
    /// <param name="now">When it was saved.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The id, or null when the name is already in use.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reports"/> is null.</exception>
    public async ValueTask<Guid?> SaveDashboardAsync(
        string? tenantId,
        Guid id,
        string name,
        string label,
        IReadOnlyList<Guid> reports,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reports);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertDashboard;

        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "name", NpgsqlDbType.Text, name);
        Add(command, "label", NpgsqlDbType.Text, label);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            is not Guid saved)
        {
            return null;
        }

        for (var ordinal = 0; ordinal < reports.Count; ordinal++)
        {
            var tile = connection.CreateCommand();
            await using var closingTile = tile.ConfigureAwait(false);

            tile.CommandText = InsertTile;

            Add(tile, "dashboard", NpgsqlDbType.Uuid, saved);
            Add(tile, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
            Add(tile, "ordinal", NpgsqlDbType.Integer, ordinal);
            Add(tile, "report", NpgsqlDbType.Uuid, reports[ordinal]);

            await tile.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return saved;
    }

    /// <summary>Runs every report on a dashboard, in the order it puts them.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="name">Which dashboard.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The dashboard's label and its tiles' results, or null when there is no such dashboard.</returns>
    public async ValueTask<(string Label, IReadOnlyList<ReportResult> Tiles)?> RunDashboardAsync(
        string? tenantId,
        string name,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        string label;
        Guid dashboard;

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = ReadDashboard;
        Add(command, "name", NpgsqlDbType.Text, name);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            dashboard = reader.GetGuid(0);
            label = reader.GetString(1);
        }

        var names = new List<string>();

        var tiles = connection.CreateCommand();
        await using var closingTiles = tiles.ConfigureAwait(false);

        tiles.CommandText = ReadTiles;
        Add(tiles, "dashboard", NpgsqlDbType.Uuid, dashboard);

        var tileReader = await tiles.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (tileReader.ConfigureAwait(false))
        {
            while (await tileReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                names.Add(tileReader.GetString(0));
            }
        }

        var results = new List<ReportResult>(names.Count);

        foreach (var tile in names)
        {
            // A tile whose report vanished cannot happen — the foreign key is ON DELETE RESTRICT —
            // so a missing one here would be a bug rather than a state, and skipping it silently
            // is what would hide it.
            var report = await ReportOnAsync(connection, tile, cancellationToken).ConfigureAwait(false);

            if (report is null)
            {
                continue;
            }

            var groups = await GroupsAsync(connection, report, cancellationToken).ConfigureAwait(false);

            results.Add(new ReportResult(tile, report.Label, report.Measure, report.MeasureOf, groups));
        }

        return (label, results);
    }

    private static async ValueTask<StoredReport?> ReportOnAsync(
        NpgsqlConnection connection,
        string name,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        command.CommandText = ReadReport;
        Add(command, "name", NpgsqlDbType.Text, name);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new StoredReport(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false)
                ? null
                : reader.GetGuid(3),
            reader.GetString(4),
            reader.GetString(5),
            await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
                ? null
                : reader.GetString(6));
    }

    private static async ValueTask<IReadOnlyList<ReportGroup>> GroupsAsync(
        NpgsqlConnection connection,
        StoredReport report,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        // Four constants, chosen by a saved value. The dimension and the measure field go in as
        // parameters — they are compared inside the statement, never concatenated into it.
        command.CommandText = report.Source switch
        {
            nameof(ReportSource.Opportunity) => OpportunityReport,
            nameof(ReportSource.Lead) => LeadReport,
            nameof(ReportSource.Activity) => ActivityReport,
            _ => CustomRecordReport,
        };

        Add(command, "dimension", NpgsqlDbType.Text, report.Dimension);
        Add(command, "measure", NpgsqlDbType.Text, report.Measure);
        Add(command, "measureOf", NpgsqlDbType.Text, report.MeasureOf ?? string.Empty);
        Add(command, "limit", NpgsqlDbType.Integer, ReportLimits.MaxGroups);

        if (report.Target is { } target)
        {
            Add(command, "object", NpgsqlDbType.Uuid, target);
        }

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        var groups = new List<ReportGroup>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var dimension = await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false)
                ? ReportLimits.NoDimension
                : reader.GetString(0);

            var measured = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
                ? null
                : (decimal?)await reader
                    .GetFieldValueAsync<decimal>(2, cancellationToken)
                    .ConfigureAwait(false);

            groups.Add(new ReportGroup(
                dimension,

                // Invariant, and trailing zeros removed, so a total of 1000.0000 and one of 1000
                // are the same string — a chart legend that says both is a chart nobody trusts.
                measured is { } value
                    ? value.Normalize().ToString(CultureInfo.InvariantCulture)
                    : string.Empty,
                (int)reader.GetInt64(1)));
        }

        return groups;
    }

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });

    private static void AddOrNull(
        NpgsqlCommand command, string name, NpgsqlDbType type, object? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });

    private ValueTask<NpgsqlConnection> OpenAsync(string? tenantId, CancellationToken cancellationToken) =>
        CrmTenantScope.OpenAsync(_source, tenantId, cancellationToken);
}

/// <summary>A report as stored.</summary>
/// <param name="ReportId">Its id.</param>
/// <param name="Label">What to show a person.</param>
/// <param name="Source">What it is about.</param>
/// <param name="Target">The object, for a custom-object report.</param>
/// <param name="Dimension">What it groups by.</param>
/// <param name="Measure">How it reduces each group.</param>
/// <param name="MeasureOf">What it aggregates, or null for a count.</param>
public sealed record StoredReport(
    Guid ReportId,
    string Label,
    string Source,
    Guid? Target,
    string Dimension,
    string Measure,
    string? MeasureOf);

/// <summary>Trims a decimal so that two equal totals are the same string.</summary>
internal static class DecimalFormatting
{
    /// <summary>Removes trailing zeros without changing the value.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The same number, at its shortest scale.</returns>
    public static decimal Normalize(this decimal value) => value / 1.000000000000000000000000000000000m;
}
