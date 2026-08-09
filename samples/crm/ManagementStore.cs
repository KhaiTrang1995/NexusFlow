using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// The reporting line, what a plan contains, and the numbers a leadership team reviews.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The line is walked with <c>WITH RECURSIVE</c> and a depth cap.</strong> A cycle cannot
/// be prevented by a constraint — a three-person loop is three individually legal rows — so the
/// read is bounded and the write refuses the loop it can see. Both, because they fail differently:
/// the check stops the loop being created through this API, the cap stops a loop created any other
/// way from hanging every roll-up in the organisation.
/// </para>
/// <para>
/// <strong>A KPI's actual is one of five constant statements, chosen by a stored value.</strong>
/// The same argument as the reports: what a leadership team reviews is five or six numbers that
/// never change, and the alternative is an expression language nobody asked for.
/// </para>
/// </remarks>
public sealed class ManagementStore
{
    private const string UpsertMember = """
        INSERT INTO org_member (tenant_id, user_id, display_name, role, reports_to)
        VALUES (@tenant, @user, @name, @role, @manager)
        ON CONFLICT (tenant_id, user_id) DO UPDATE
            SET display_name = excluded.display_name,
                role = excluded.role,
                reports_to = excluded.reports_to
        """;

    private const string ReadMember =
        "SELECT display_name, role, reports_to FROM org_member WHERE user_id = @user";

    // Managers before their reports, so a client can draw the tree in one pass: `reports_to` is
    // a foreign key into this same table, and a row whose parent has not been seen yet has to be
    // held aside. Ordering by depth removes the holding aside.
    private const string Chart = """
        WITH RECURSIVE line AS (
            SELECT user_id, display_name, role, reports_to, 0 AS depth
            FROM org_member WHERE reports_to IS NULL
            UNION ALL
            SELECT m.user_id, m.display_name, m.role, m.reports_to, line.depth + 1
            FROM org_member m
            JOIN line ON m.reports_to = line.user_id
            WHERE line.depth < @maxDepth
        )
        SELECT DISTINCT ON (l.user_id)
               l.user_id, l.display_name, l.role, l.reports_to, l.depth,
               (SELECT count(*) FROM org_member d WHERE d.reports_to = l.user_id)
        FROM line l
        ORDER BY l.user_id, l.depth
        """;

    // Downwards: this person and everybody below them, at any depth. DISTINCT because a cycle
    // created outside this API would otherwise repeat rows for ever inside the cap; the cap is
    // what stops it running for ever at all.
    private const string LineBelow = """
        WITH RECURSIVE line AS (
            SELECT user_id, 1 AS depth FROM org_member WHERE user_id = @user
            UNION ALL
            SELECT m.user_id, line.depth + 1
            FROM org_member m
            JOIN line ON m.reports_to = line.user_id
            WHERE line.depth < @maxDepth
        )
        SELECT DISTINCT user_id FROM line
        """;

    // Upwards from a proposed manager. If the person being placed appears in it, placing them
    // there closes a loop.
    private const string LineAbove = """
        WITH RECURSIVE up AS (
            SELECT user_id, reports_to, 1 AS depth FROM org_member WHERE user_id = @manager
            UNION ALL
            SELECT m.user_id, m.reports_to, up.depth + 1
            FROM org_member m
            JOIN up ON m.user_id = up.reports_to
            WHERE up.depth < @maxDepth
        )
        SELECT count(*) FROM up WHERE user_id = @user
        """;

    private const string UpsertObjective = """
        INSERT INTO plan_objective (
            plan_id, tenant_id, ordinal, description, measure, target, status)
        VALUES (@plan, @tenant, @ordinal, @description, @measure, @target, @status)
        ON CONFLICT (plan_id, ordinal) DO UPDATE
            SET description = excluded.description,
                measure = excluded.measure,
                target = excluded.target,
                status = excluded.status
        """;

    private const string CountUnachieved =
        "SELECT count(*) FROM plan_objective WHERE plan_id = @plan AND status <> 'Achieved'";

    private const string UpsertStakeholder = """
        INSERT INTO account_stakeholder (
            plan_id, tenant_id, contact_id, role, sentiment, influence)
        VALUES (@plan, @tenant, @contact, @role, @sentiment, @influence)
        ON CONFLICT (plan_id, contact_id) DO UPDATE
            SET role = excluded.role,
                sentiment = excluded.sentiment,
                influence = excluded.influence
        """;

    // The two numbers a coverage review actually asks for, in one read.
    private const string CountStakeholders = """
        SELECT count(*),
               count(*) FILTER (WHERE sentiment IN ('Sceptical', 'Opposed'))
        FROM account_stakeholder WHERE plan_id = @plan
        """;

    private const string UpsertRisk = """
        INSERT INTO plan_risk (
            plan_id, tenant_id, ordinal, description, severity, mitigation, is_open)
        VALUES (@plan, @tenant, @ordinal, @description, @severity, @mitigation, @open)
        ON CONFLICT (plan_id, ordinal) DO UPDATE
            SET description = excluded.description,
                severity = excluded.severity,
                mitigation = excluded.mitigation,
                is_open = excluded.is_open
        """;

    private const string CountOpenRisks =
        "SELECT count(*) FROM plan_risk WHERE plan_id = @plan AND is_open";

    private const string PlanKindAndId =
        "SELECT plan_id, kind FROM plan WHERE name = @name";

    private const string InsertKpi = """
        INSERT INTO kpi (kpi_id, tenant_id, name, label, source, target, direction, created_at)
        VALUES (@id, @tenant, @name, @label, @source, @target, @direction, @now)
        ON CONFLICT (tenant_id, name) DO UPDATE
            SET label = excluded.label,
                source = excluded.source,
                target = excluded.target,
                direction = excluded.direction
        RETURNING kpi_id
        """;

    private const string KpisForTenant = """
        SELECT k.kpi_id, k.name, k.label, k.source, k.target, k.direction,
               (SELECT r.commentary FROM kpi_review r
                WHERE r.kpi_id = k.kpi_id AND r.period_id = @period)
        FROM kpi k
        ORDER BY k.name
        """;

    private const string OneKpi = """
        SELECT kpi_id, name, label, source, target, direction, NULL
        FROM kpi WHERE name = @name
        """;

    private const string UpsertReview = """
        INSERT INTO kpi_review (
            kpi_id, tenant_id, period_id, reviewed_at, reviewed_by, actual, commentary)
        VALUES (@kpi, @tenant, @period, @now, @by, @actual, @commentary)
        ON CONFLICT (kpi_id, period_id) DO UPDATE
            SET reviewed_at = excluded.reviewed_at,
                reviewed_by = excluded.reviewed_by,
                actual = excluded.actual,
                commentary = excluded.commentary
        """;

    // ------------------------------------------------------------------ the five KPI sources

    private const string OpenPipeline =
        "SELECT coalesce(sum(amount), 0) FROM opportunity WHERE outcome IS NULL";

    private const string WonRevenue = """
        SELECT coalesce(sum(amount), 0) FROM opportunity
        WHERE outcome = 'Won' AND stage_entered_at >= @from AND stage_entered_at < @to
        """;

    private const string LeadsCaptured = """
        SELECT count(*)::numeric FROM lead
        WHERE captured_at >= @from AND captured_at < @to
        """;

    private const string OpenTasks =
        "SELECT count(*)::numeric FROM activity WHERE status = 'Open'";

    private const string OverduePlanSteps = """
        SELECT count(*)::numeric FROM plan_step
        WHERE completed_at IS NULL AND due_on < @today
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public ManagementStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Places a person in the organisation, or moves them.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="request">Who, and where.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many people end up below them, or null when the move would loop.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<int?> PlaceAsync(
        string? tenantId,
        SetOrgMember request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        if (request.ReportsTo is { Length: > 0 } manager
            && await WouldLoopAsync(connection, request.UserId, manager, cancellationToken)
                .ConfigureAwait(false))
        {
            return null;
        }

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = UpsertMember;

        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "user", NpgsqlDbType.Text, request.UserId);
        Add(command, "name", NpgsqlDbType.Text, request.DisplayName);
        Add(command, "role", NpgsqlDbType.Text, request.Role.ToString());
        Add(command, "manager", NpgsqlDbType.Text, (object?)request.ReportsTo ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var line = await BelowAsync(connection, request.UserId, cancellationToken)
            .ConfigureAwait(false);

        // Less the person themselves, who is the first row of the recursion.
        return line.Count - 1;
    }

    /// <summary>Whether a manager already reports, at any depth, to the person being placed.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="userId">Who is being placed.</param>
    /// <param name="manager">Where.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether the placement would close a loop.</returns>
    public async ValueTask<bool> WouldLoopAsync(
        string? tenantId,
        string userId,
        string manager,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        return await WouldLoopAsync(connection, userId, manager, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The whole reporting line, managers before their reports.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Everybody, managers before their reports.</returns>
    public async ValueTask<OrgChart> ChartAsync(
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = Chart;
        Add(command, "maxDepth", NpgsqlDbType.Integer, ManagementLimits.MaxDepth);

        var members = new List<(int Depth, OrgChartMember Member)>();

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                members.Add((
                    reader.GetInt32(4),
                    new OrgChartMember(
                        reader.GetString(0),
                        reader.GetString(1),
                        Enum.Parse<OrgRole>(reader.GetString(2)),
                        await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false)
                            ? null
                            : reader.GetString(3),
                        (int)reader.GetInt64(5))));
            }
        }

        // DISTINCT ON needed its own order, so the depth ordering is applied here rather than in
        // the statement. Ordered by name inside a depth so the tree does not shuffle between reads.
        return new OrgChart(
            [
                .. members
                    .OrderBy(static row => row.Depth)
                    .ThenBy(static row => row.Member.DisplayName, StringComparer.Ordinal)
                    .Select(static row => row.Member),
            ]);
    }

    /// <summary>Who a caller is, and whose plans they therefore see.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="userId">Their subject.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The scope, or null when the caller has not been placed. A director's scope is empty, which
    /// means no restriction rather than nothing.
    /// </returns>
    public async ValueTask<ViewerScope?> ScopeAsync(
        string? tenantId,
        string userId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        OrgRole role;

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = ReadMember;
        Add(command, "user", NpgsqlDbType.Text, userId);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            role = Enum.Parse<OrgRole>(reader.GetString(1));
        }

        return role switch
        {
            OrgRole.Director => new ViewerScope(userId, role, []),
            OrgRole.Manager => new ViewerScope(
                userId,
                role,
                await BelowAsync(connection, userId, cancellationToken).ConfigureAwait(false)),
            _ => new ViewerScope(userId, role, [userId]),
        };
    }

    /// <summary>Writes an objective, and says how many of the plan's are not yet achieved.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="plan">Which plan.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many are outstanding.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<int> SaveObjectiveAsync(
        string? tenantId,
        Guid plan,
        SetObjective request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = UpsertObjective;

        Add(command, "plan", NpgsqlDbType.Uuid, plan);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "ordinal", NpgsqlDbType.Integer, request.Ordinal);
        Add(command, "description", NpgsqlDbType.Text, request.Description);
        Add(command, "measure", NpgsqlDbType.Text, request.Measure.ToString());
        Add(command, "target", NpgsqlDbType.Numeric, request.Target);
        Add(command, "status", NpgsqlDbType.Text, request.Status.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return await CountAsync(connection, CountUnachieved, plan, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Writes the relationship map, and says who is not on side.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="plan">Which plan.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many are mapped, and how many are sceptical or opposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<(int Mapped, int Opposed)> SaveStakeholderAsync(
        string? tenantId,
        Guid plan,
        SetStakeholder request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = UpsertStakeholder;

        Add(command, "plan", NpgsqlDbType.Uuid, plan);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "contact", NpgsqlDbType.Uuid, request.Contact);
        Add(command, "role", NpgsqlDbType.Text, request.Role.ToString());
        Add(command, "sentiment", NpgsqlDbType.Text, request.Sentiment.ToString());
        Add(command, "influence", NpgsqlDbType.Integer, request.Influence);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var counts = connection.CreateCommand();
        await using var closingCounts = counts.ConfigureAwait(false);

        counts.CommandText = CountStakeholders;
        Add(counts, "plan", NpgsqlDbType.Uuid, plan);

        var reader = await counts.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        return ((int)reader.GetInt64(0), (int)reader.GetInt64(1));
    }

    /// <summary>Writes a risk, and says how many of the plan's are still open.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="plan">Which plan.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many are open.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<int> SaveRiskAsync(
        string? tenantId,
        Guid plan,
        SetRisk request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = UpsertRisk;

        Add(command, "plan", NpgsqlDbType.Uuid, plan);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "ordinal", NpgsqlDbType.Integer, request.Ordinal);
        Add(command, "description", NpgsqlDbType.Text, request.Description);
        Add(command, "severity", NpgsqlDbType.Text, request.Severity.ToString());
        Add(command, "mitigation", NpgsqlDbType.Text, request.Mitigation);
        Add(command, "open", NpgsqlDbType.Boolean, request.IsOpen);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return await CountAsync(connection, CountOpenRisks, plan, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Finds a plan by name, and says what kind it is.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="name">Which plan.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The id and kind, or null when this tenant has no such plan.</returns>
    public async ValueTask<(Guid Id, string Kind)?> PlanAsync(
        string? tenantId,
        string name,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = PlanKindAndId;
        Add(command, "name", NpgsqlDbType.Text, name);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetGuid(0), reader.GetString(1))
            : null;
    }

    /// <summary>Declares a KPI, or changes one.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">What a new KPI would be called.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="now">When.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Its id.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<Guid> SaveKpiAsync(
        string? tenantId,
        Guid id,
        DefineKpi request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertKpi;

        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "name", NpgsqlDbType.Text, request.Name);
        Add(command, "label", NpgsqlDbType.Text, request.Label);
        Add(command, "source", NpgsqlDbType.Text, request.Source.ToString());
        Add(command, "target", NpgsqlDbType.Numeric, request.Target);
        Add(command, "direction", NpgsqlDbType.Text, request.Direction.ToString());
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>Every KPI, computed for a period.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="period">Which period.</param>
    /// <param name="today">What counts as overdue.</param>
    /// <param name="one">One KPI's name, or null for all of them.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The KPIs, off-track first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="period"/> is null.</exception>
    public async ValueTask<IReadOnlyList<(Guid Id, KpiResult Result)>> KpisAsync(
        string? tenantId,
        StoredPeriod period,
        DateOnly today,
        string? one,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(period);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var declared = new List<(Guid Id, string Name, string Label, KpiSource Source,
            decimal Target, KpiDirection Direction, string? Commentary)>();

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = one is null ? KpisForTenant : OneKpi;

        if (one is null)
        {
            Add(command, "period", NpgsqlDbType.Uuid, period.PeriodId);
        }
        else
        {
            Add(command, "name", NpgsqlDbType.Text, one);
        }

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                declared.Add((
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    Enum.Parse<KpiSource>(reader.GetString(3)),
                    reader.GetDecimal(4),
                    Enum.Parse<KpiDirection>(reader.GetString(5)),
                    await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
                        ? null
                        : reader.GetString(6)));
            }
        }

        var results = new List<(Guid, KpiResult)>(declared.Count);

        foreach (var kpi in declared)
        {
            var actual = await ActualAsync(connection, kpi.Source, period, today, cancellationToken)
                .ConfigureAwait(false);

            results.Add((
                kpi.Id,
                new KpiResult(
                    kpi.Name,
                    kpi.Label,
                    kpi.Source.ToString(),
                    kpi.Target,
                    actual,
                    kpi.Direction.ToString(),
                    Meets(actual, kpi.Target, kpi.Direction) ? "OnTrack" : "OffTrack",
                    kpi.Commentary)));
        }

        // Off-track first: a scorecard is walked by what is wrong with it, and a review that opens
        // on the numbers that are fine spends its first ten minutes on them.
        return
        [
            .. results
                .OrderBy(static row => row.Item2.Status == "OnTrack")
                .ThenBy(static row => row.Item2.Name, StringComparer.Ordinal),
        ];
    }

    /// <summary>Records what was said about a number, and what it was when it was said.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="kpi">Which KPI.</param>
    /// <param name="period">Which period.</param>
    /// <param name="actual">What the number was.</param>
    /// <param name="commentary">What was said.</param>
    /// <param name="by">Who said it.</param>
    /// <param name="now">When.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async ValueTask ReviewAsync(
        string? tenantId,
        Guid kpi,
        Guid period,
        decimal actual,
        string commentary,
        string by,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = UpsertReview;

        Add(command, "kpi", NpgsqlDbType.Uuid, kpi);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "period", NpgsqlDbType.Uuid, period);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);
        Add(command, "by", NpgsqlDbType.Text, by);
        Add(command, "actual", NpgsqlDbType.Numeric, actual);
        Add(command, "commentary", NpgsqlDbType.Text, commentary);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether an actual meets its target, the direction deciding which way.</summary>
    /// <param name="actual">What it is.</param>
    /// <param name="target">What good looks like.</param>
    /// <param name="direction">Which way is good.</param>
    /// <returns>Whether it is on track.</returns>
    public static bool Meets(decimal actual, decimal target, KpiDirection direction) =>
        direction == KpiDirection.HigherIsBetter ? actual >= target : actual <= target;

    private static async ValueTask<decimal> ActualAsync(
        NpgsqlConnection connection,
        KpiSource source,
        StoredPeriod period,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        // Five constants chosen by a stored value. The period's bounds and today go in as
        // parameters, never as text spliced into the statement.
        command.CommandText = source switch
        {
            KpiSource.OpenPipeline => OpenPipeline,
            KpiSource.WonRevenue => WonRevenue,
            KpiSource.LeadsCaptured => LeadsCaptured,
            KpiSource.OpenTasks => OpenTasks,
            _ => OverduePlanSteps,
        };

        if (source is KpiSource.WonRevenue or KpiSource.LeadsCaptured)
        {
            Add(command, "from", NpgsqlDbType.TimestampTz,
                new DateTimeOffset(period.StartsOn.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

            Add(command, "to", NpgsqlDbType.TimestampTz,
                new DateTimeOffset(
                    period.EndsOn.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        }

        if (source is KpiSource.OverduePlanSteps)
        {
            Add(command, "today", NpgsqlDbType.Date, today);
        }

        return (decimal)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async ValueTask<bool> WouldLoopAsync(
        NpgsqlConnection connection,
        string userId,
        string manager,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        command.CommandText = LineAbove;

        Add(command, "manager", NpgsqlDbType.Text, manager);
        Add(command, "user", NpgsqlDbType.Text, userId);
        Add(command, "maxDepth", NpgsqlDbType.Integer, ManagementLimits.MaxDepth);

        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! > 0;
    }

    private static async ValueTask<IReadOnlyList<string>> BelowAsync(
        NpgsqlConnection connection,
        string userId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        command.CommandText = LineBelow;

        Add(command, "user", NpgsqlDbType.Text, userId);
        Add(command, "maxDepth", NpgsqlDbType.Integer, ManagementLimits.MaxDepth);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        var line = new List<string>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            line.Add(reader.GetString(0));
        }

        return line;
    }

    private static async ValueTask<int> CountAsync(
        NpgsqlConnection connection,
        string statement,
        Guid plan,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        command.CommandText = statement;
        Add(command, "plan", NpgsqlDbType.Uuid, plan);

        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });

    private ValueTask<NpgsqlConnection> OpenAsync(string? tenantId, CancellationToken cancellationToken) =>
        CrmTenantScope.OpenAsync(_source, tenantId, cancellationToken);
}
