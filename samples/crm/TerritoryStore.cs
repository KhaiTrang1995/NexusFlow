using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// The territories and their rules, the quotas, and what each is actually meeting.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No rule is ever evaluated in SQL.</strong> The rules come back as rows and
/// <c>ProcessRules.Holds</c> decides, which is the same evaluator transition guards, validation
/// rules, roll-up filters and list-view criteria use. Evaluating them in the statement would mean
/// assembling a predicate from stored text — the shape <c>SqlFitnessTests</c> exists to refuse —
/// and a second implementation of a comparison that already exists once.
/// </para>
/// <para>
/// <strong>Coverage counts accounts against rules, not against a list.</strong> "Which accounts
/// fall in no territory" is the question a list-per-person model cannot ask, because an account
/// missing from every list looks exactly like an account nobody has got to yet.
/// </para>
/// </remarks>
public sealed class TerritoryStore
{
    private const string InsertTerritory = """
        INSERT INTO territory (
            territory_id, tenant_id, name, label, parent_territory_id, priority, created_at)
        VALUES (@id, @tenant, @name, @label, @parent, @priority, @now)
        ON CONFLICT (tenant_id, name) DO NOTHING
        RETURNING territory_id
        """;

    private const string InsertRule = """
        INSERT INTO territory_rule (
            territory_id, tenant_id, ordinal, subject, attribute, operator, value)
        VALUES (@territory, @tenant, @ordinal, @subject, @attribute, @operator, @value)
        """;

    private const string InsertOwner = """
        INSERT INTO territory_assignment (territory_id, tenant_id, user_id)
        VALUES (@territory, @tenant, @user)
        ON CONFLICT (territory_id, user_id) DO NOTHING
        """;

    private const string TerritoryByName =
        "SELECT territory_id FROM territory WHERE name = @name";

    // Every territory with its rules and its owners, in the order routing considers them. One
    // read: a routing that issued a statement per territory would be slower with every patch a
    // growing company adds, which is exactly when it matters.
    private const string TerritoriesInOrder = """
        SELECT t.territory_id, t.name, t.label, t.priority,
               r.subject, r.attribute, r.operator, r.value
        FROM territory t
        LEFT JOIN territory_rule r ON r.territory_id = t.territory_id
        ORDER BY t.priority, t.name, r.ordinal
        """;

    private const string OwnersOfTerritory =
        "SELECT user_id FROM territory_assignment WHERE territory_id = @territory ORDER BY user_id";

    private const string AccountById = """
        SELECT region, industry, lifecycle FROM account WHERE account_id = @id
        """;

    private const string LeadById = """
        SELECT source, status, score::text FROM lead WHERE lead_id = @id
        """;

    private const string AllAccounts = """
        SELECT account_id, region, industry, lifecycle FROM account
        """;

    private const string OwnerCounts = """
        SELECT t.territory_id, t.name, t.label,
               (SELECT count(*) FROM territory_assignment a WHERE a.territory_id = t.territory_id)
        FROM territory t
        ORDER BY t.priority, t.name
        """;

    private const string UpsertQuota = """
        INSERT INTO quota (
            quota_id, tenant_id, period_id, user_id, measure, target, ramp_factor, created_at)
        VALUES (@id, @tenant, @period, @user, @measure, @target, @ramp, @now)
        ON CONFLICT (tenant_id, period_id, user_id, measure) DO UPDATE
            SET target = excluded.target, ramp_factor = excluded.ramp_factor
        RETURNING target * ramp_factor
        """;

    // The three numbers side by side: assigned, committed, achieved. Read together because a
    // review compares them, and three statements could show three instants.
    private const string Attainment = """
        SELECT q.user_id, m.display_name, q.measure,
               q.target * q.ramp_factor,
               q.target, q.ramp_factor,
               -- Null, not zero, on a quota that is not measured in money: a plan commits an
               -- amount, so there is nothing to compare a leads target against, and the money
               -- figure beside forty leads is a gap of minus half a million.
               CASE q.measure WHEN 'Revenue' THEN coalesce((
                   SELECT sum(p.target_amount) FROM plan p
                   WHERE p.period_id = q.period_id AND p.owner_id = q.user_id
                     AND p.target_amount IS NOT NULL), 0) END,
               CASE q.measure
                   WHEN 'Revenue' THEN coalesce((
                       SELECT sum(o.amount) FROM opportunity o
                       WHERE o.outcome = 'Won'
                         AND o.stage_entered_at >= @from AND o.stage_entered_at < @to
                         AND o.account_id IN (SELECT p.account_id FROM plan p
                                              WHERE p.period_id = q.period_id
                                                AND p.owner_id = q.user_id
                                                AND p.account_id IS NOT NULL)), 0)
                   WHEN 'Leads' THEN (
                       SELECT count(*)::numeric FROM lead l
                       WHERE l.captured_at >= @from AND l.captured_at < @to)
                   ELSE (
                       SELECT count(*)::numeric FROM activity a
                       WHERE a.status = 'Completed'
                         AND a.completed_at >= @from AND a.completed_at < @to)
               END
        FROM quota q
        JOIN org_member m ON m.user_id = q.user_id
        WHERE q.period_id = @period AND (@unrestricted OR q.user_id = ANY(@scope))
        ORDER BY q.user_id, q.measure
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public TerritoryStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Declares a territory, its rules and its owners.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">What the territory is to be called.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="parent">The parent's id, or null.</param>
    /// <param name="now">When it was declared.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The id, or null when the name is already in use.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<Guid?> SaveAsync(
        string? tenantId,
        Guid id,
        DefineTerritory request,
        Guid? parent,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertTerritory;

        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "name", NpgsqlDbType.Text, request.Name);
        Add(command, "label", NpgsqlDbType.Text, request.Label);
        Add(command, "parent", NpgsqlDbType.Uuid, (object?)parent ?? DBNull.Value);
        Add(command, "priority", NpgsqlDbType.Integer, request.Priority);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            is not Guid saved)
        {
            return null;
        }

        for (var ordinal = 0; ordinal < request.Rules.Count; ordinal++)
        {
            var rule = request.Rules[ordinal];

            var write = connection.CreateCommand();
            await using var closingWrite = write.ConfigureAwait(false);

            write.CommandText = InsertRule;

            Add(write, "territory", NpgsqlDbType.Uuid, saved);
            Add(write, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
            Add(write, "ordinal", NpgsqlDbType.Integer, ordinal);
            Add(write, "subject", NpgsqlDbType.Text, rule.Subject.ToString());
            Add(write, "attribute", NpgsqlDbType.Text, rule.Attribute);
            Add(write, "operator", NpgsqlDbType.Text, rule.Operator.ToString());
            Add(write, "value", NpgsqlDbType.Text, rule.Value);

            await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var owner in request.Owners)
        {
            var write = connection.CreateCommand();
            await using var closingWrite = write.ConfigureAwait(false);

            write.CommandText = InsertOwner;

            Add(write, "territory", NpgsqlDbType.Uuid, saved);
            Add(write, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
            Add(write, "user", NpgsqlDbType.Text, owner);

            await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return saved;
    }

    /// <summary>Finds a territory's id by name.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="name">Which territory.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The id, or null.</returns>
    public async ValueTask<Guid?> TerritoryIdAsync(
        string? tenantId,
        string name,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = TerritoryByName;
        Add(command, "name", NpgsqlDbType.Text, name);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
    }

    /// <summary>Where one account or lead routes.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="subject">Which sort of thing.</param>
    /// <param name="id">Which one.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Where it routes, or null when there is no such account or lead.</returns>
    public async ValueTask<RoutedTo?> RouteAsync(
        string? tenantId,
        RoutingSubject subject,
        Guid id,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var facts = await FactsAsync(connection, subject, id, cancellationToken)
            .ConfigureAwait(false);

        if (facts is null)
        {
            return null;
        }

        var territories = await TerritoriesAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        var considered = 0;

        foreach (var territory in territories)
        {
            considered++;

            if (!Matches(territory, subject, facts))
            {
                continue;
            }

            var owners = await OwnersAsync(connection, territory.Id, cancellationToken)
                .ConfigureAwait(false);

            return new RoutedTo(territory.Name, territory.Label, owners, considered);
        }

        return new RoutedTo(null, null, [], considered);
    }

    /// <summary>What is covered and what is not.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The coverage.</returns>
    public async ValueTask<Coverage> CoverageAsync(
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var territories = await TerritoriesAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        var owners = new Dictionary<Guid, (string Name, string Label, int Owners)>();

        var counts = connection.CreateCommand();
        await using var closingCounts = counts.ConfigureAwait(false);

        counts.CommandText = OwnerCounts;

        var countReader = await counts.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (countReader.ConfigureAwait(false))
        {
            while (await countReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                owners[countReader.GetGuid(0)] =
                    (countReader.GetString(1), countReader.GetString(2), (int)countReader.GetInt64(3));
            }
        }

        var matched = territories.ToDictionary(static t => t.Id, static _ => 0);
        var unrouted = 0;

        var accounts = connection.CreateCommand();
        await using var closingAccounts = accounts.ConfigureAwait(false);

        accounts.CommandText = AllAccounts;

        var accountReader = await accounts
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (accountReader.ConfigureAwait(false))
        {
            while (await accountReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var facts = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["region"] = accountReader.GetString(1),
                    ["industry"] = accountReader.GetString(2),
                    ["lifecycle"] = accountReader.GetString(3),
                };

                // The first territory whose rules all hold, in priority order — the same walk the
                // routing does, so coverage cannot disagree with what a caller is told.
                var landed = territories
                    .FirstOrDefault(t => Matches(t, RoutingSubject.Account, facts));

                if (landed is null)
                {
                    unrouted++;
                }
                else
                {
                    matched[landed.Id]++;
                }
            }
        }

        return new Coverage(
            [
                .. territories.Select(t => new TerritoryCoverage(
                    t.Name,
                    owners.TryGetValue(t.Id, out var row) ? row.Label : t.Label,
                    owners.TryGetValue(t.Id, out var count) ? count.Owners : 0,
                    matched[t.Id])),
            ],
            unrouted,
            owners.Values.Count(static row => row.Owners == 0));
    }

    /// <summary>Assigns a person their number.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">What a new quota would be called.</param>
    /// <param name="period">Which period.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="now">When.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The number after ramp.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<decimal> SaveQuotaAsync(
        string? tenantId,
        Guid id,
        Guid period,
        SetQuota request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = UpsertQuota;

        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "period", NpgsqlDbType.Uuid, period);
        Add(command, "user", NpgsqlDbType.Text, request.UserId);
        Add(command, "measure", NpgsqlDbType.Text, request.Measure.ToString());
        Add(command, "target", NpgsqlDbType.Numeric, request.Target);
        Add(command, "ramp", NpgsqlDbType.Numeric, request.RampFactor);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        return (decimal)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>How the assigned numbers are being met.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="period">Which period.</param>
    /// <param name="scope">Whose rows to read. Empty means no restriction.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The rows, weakest attainment first.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public async ValueTask<IReadOnlyList<QuotaAttainment>> AttainmentAsync(
        string? tenantId,
        StoredPeriod period,
        IReadOnlyList<string> scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(scope);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = Attainment;

        Add(command, "period", NpgsqlDbType.Uuid, period.PeriodId);
        Add(command, "from", NpgsqlDbType.TimestampTz,
            new DateTimeOffset(period.StartsOn.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        Add(command, "to", NpgsqlDbType.TimestampTz,
            new DateTimeOffset(
                period.EndsOn.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

        command.Parameters.Add(
            new NpgsqlParameter("unrestricted", NpgsqlDbType.Boolean) { Value = scope.Count == 0 });

        command.Parameters.Add(new NpgsqlParameter<string[]>("scope", [.. scope]));

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        var rows = new List<QuotaAttainment>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var quota = reader.GetDecimal(3);
            var actual = reader.GetDecimal(7);

            decimal? committed = await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
                ? null
                : reader.GetDecimal(6);

            rows.Add(new QuotaAttainment(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                quota,
                reader.GetDecimal(4),
                reader.GetDecimal(5),
                committed,
                actual,
                quota == 0 ? null : Math.Round(actual / quota * 100m, 1),
                committed is { } offered ? quota - offered : null));
        }

        return
        [
            .. rows
                .OrderBy(static row => row.Attainment ?? decimal.MaxValue)
                .ThenBy(static row => row.UserId, StringComparer.Ordinal),
        ];
    }

    /// <summary>Whether every rule of a territory holds for a subject's facts.</summary>
    /// <remarks>
    /// <strong>All of them, and rules about the other subject are skipped rather than failed.</strong>
    /// A territory that routes both accounts and leads carries rules for each; asking an account to
    /// satisfy a lead's rule would make every such territory match nothing.
    /// </remarks>
    private static bool Matches(
        StoredTerritory territory,
        RoutingSubject subject,
        IReadOnlyDictionary<string, string?> facts)
    {
        var applicable = territory.Rules.Where(rule => rule.Subject == subject).ToList();

        return applicable.Count > 0
            && applicable.All(rule => ProcessRules.Holds(
                rule.Operator,
                rule.Value,
                facts.TryGetValue(rule.Attribute, out var value) ? value : null));
    }

    private static async ValueTask<IReadOnlyList<StoredTerritory>> TerritoriesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        command.CommandText = TerritoriesInOrder;

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        var territories = new List<StoredTerritory>();
        var rules = new Dictionary<Guid, List<RoutingRule>>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetGuid(0);

            if (!rules.TryGetValue(id, out var carried))
            {
                carried = [];
                rules[id] = carried;

                territories.Add(new StoredTerritory(
                    id, reader.GetString(1), reader.GetString(2), carried));
            }

            // The left join produces one null row for a territory with no rules — which the
            // capability refuses to create, but which a hand-written row could still be.
            if (!await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false))
            {
                carried.Add(new RoutingRule(
                    Enum.Parse<RoutingSubject>(reader.GetString(4)),
                    reader.GetString(5),
                    Enum.Parse<GuardOperator>(reader.GetString(6)),
                    reader.GetString(7)));
            }
        }

        return territories;
    }

    private static async ValueTask<IReadOnlyList<string>> OwnersAsync(
        NpgsqlConnection connection,
        Guid territory,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        command.CommandText = OwnersOfTerritory;
        Add(command, "territory", NpgsqlDbType.Uuid, territory);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        var owners = new List<string>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            owners.Add(reader.GetString(0));
        }

        return owners;
    }

    private static async ValueTask<IReadOnlyDictionary<string, string?>?> FactsAsync(
        NpgsqlConnection connection,
        RoutingSubject subject,
        Guid id,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        // Two constants chosen by the subject. A table name cannot be a parameter, and building
        // one from a caller's value is the shape SqlFitnessTests exists to refuse.
        command.CommandText = subject == RoutingSubject.Account ? AccountById : LeadById;
        Add(command, "id", NpgsqlDbType.Uuid, id);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var names = RoutingAttributes.Of(subject);
        var facts = new Dictionary<string, string?>(StringComparer.Ordinal);

        for (var column = 0; column < names.Count; column++)
        {
            facts[names[column]] =
                await reader.IsDBNullAsync(column, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetString(column);
        }

        return facts;
    }

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });

    private ValueTask<NpgsqlConnection> OpenAsync(string? tenantId, CancellationToken cancellationToken) =>
        CrmTenantScope.OpenAsync(_source, tenantId, cancellationToken);
}

/// <summary>A territory as stored, with the rules routing evaluates.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its identifier.</param>
/// <param name="Label">What to show.</param>
/// <param name="Rules">What must hold for it to match.</param>
public sealed record StoredTerritory(
    Guid Id,
    string Name,
    string Label,
    IReadOnlyList<RoutingRule> Rules);
