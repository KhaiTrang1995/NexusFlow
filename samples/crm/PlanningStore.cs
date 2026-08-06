using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// The periods, the number, the commitments — and the four statements that say how a period is
/// actually going.
/// </summary>
/// <remarks>
/// <strong>Every actual is read here and stored nowhere.</strong> Coverage comes from
/// <c>opportunity</c> and attainment from <c>lead</c>, at the moment the roll-up is asked for.
/// There is no <c>actual_amount</c> column on a plan, and adding one would be the change that
/// makes every number on the screen unfalsifiable — right after a refresh, wrong before the next,
/// and with nothing to say which.
/// </remarks>
public sealed class PlanningStore
{
    private const string InsertPeriod = """
        INSERT INTO plan_period (
            period_id, tenant_id, name, label, starts_on, ends_on, parent_period_id, created_at)
        VALUES (@id, @tenant, @name, @label, @starts, @ends, @parent, @now)
        ON CONFLICT (tenant_id, name) DO NOTHING
        RETURNING period_id
        """;

    private const string ReadPeriod = """
        SELECT period_id, label, starts_on, ends_on FROM plan_period WHERE name = @name
        """;

    private const string UpsertStrategy = """
        INSERT INTO sales_strategy (
            strategy_id, tenant_id, period_id, vision, target_amount, currency, created_at)
        VALUES (@id, @tenant, @period, @vision, @target, @currency, @now)
        ON CONFLICT (tenant_id, period_id) DO UPDATE
            SET vision = excluded.vision,
                target_amount = excluded.target_amount,
                currency = excluded.currency
        RETURNING strategy_id
        """;

    private const string InsertPlan = """
        INSERT INTO plan (
            plan_id, tenant_id, period_id, kind, name, label, owner_id,
            account_id, opportunity_id, channel, segment,
            target_amount, currency, target_leads, created_at)
        VALUES (@id, @tenant, @period, @kind, @name, @label, @owner,
            @account, @opportunity, @channel, @segment,
            @targetAmount, @currency, @targetLeads, @now)
        ON CONFLICT (tenant_id, name) DO NOTHING
        RETURNING plan_id
        """;

    private const string ReadPlan = "SELECT plan_id FROM plan WHERE name = @name";

    private const string UpsertQualification = """
        INSERT INTO plan_qualification (plan_id, tenant_id, element, is_answered, note)
        VALUES (@plan, @tenant, @element, @answered, @note)
        ON CONFLICT (plan_id, element) DO UPDATE
            SET is_answered = excluded.is_answered, note = excluded.note
        """;

    private const string CountAnswered =
        "SELECT count(*) FROM plan_qualification WHERE plan_id = @plan AND is_answered";

    private const string UpsertStep = """
        INSERT INTO plan_step (
            plan_id, tenant_id, ordinal, description, owner_id, due_on, completed_at)
        VALUES (@plan, @tenant, @ordinal, @description, @owner, @due, @completed)
        ON CONFLICT (plan_id, ordinal) DO UPDATE
            SET description = excluded.description,
                owner_id = excluded.owner_id,
                due_on = excluded.due_on,
                completed_at = excluded.completed_at
        """;

    private const string CountOutstanding =
        "SELECT count(*) FROM plan_step WHERE plan_id = @plan AND completed_at IS NULL";

    // The number and what was committed against it, in one statement. Two reads would leave a
    // commitment able to land between them, and a gap that never quite reconciles with the list
    // below it is a gap nobody believes.
    // WHOSE PLANS ARE IN THE TOTAL. A director reads without restriction, a manager reads their
    // line, a representative reads their own — and all three run this statement. The scope is an
    // array parameter and a flag rather than a clause the application assembles, so the statement
    // stays a constant and the difference between the three is a value.
    private const string InScope =
        "\n              AND (@unrestricted OR p.owner_id = ANY(@scope))";

    private const string StrategyAndCommitted = """
        SELECT s.vision, s.target_amount, s.currency,
               coalesce((SELECT sum(p.target_amount) FROM plan p
                         WHERE p.period_id = s.period_id AND p.target_amount IS NOT NULL
        """ + InScope + """
        ), 0)
        FROM sales_strategy s
        WHERE s.period_id = @period
        """;

    // The live half. `outcome IS NULL` is what "still open" means in 0001's schema — a decided
    // opportunity is not coverage, whichever way it was decided.
    private const string AccountCoverageForPeriod = """
        SELECT p.name, a.name, p.target_amount, p.currency,
               coalesce((SELECT sum(o.amount) FROM opportunity o
                         WHERE o.account_id = p.account_id AND o.outcome IS NULL), 0)
        FROM plan p
        JOIN account a ON a.account_id = p.account_id
        WHERE p.period_id = @period AND p.kind = 'Account'
        """ + InScope + """
        ORDER BY p.name
        """;

    private const string OpportunityReadinessForPeriod = """
        SELECT p.name, p.target_amount, p.currency,
               (SELECT count(*) FROM plan_qualification q
                WHERE q.plan_id = p.plan_id AND q.is_answered),
               (SELECT count(*) FROM plan_step s WHERE s.plan_id = p.plan_id),
               (SELECT count(*) FROM plan_step s
                WHERE s.plan_id = p.plan_id AND s.completed_at IS NULL AND s.due_on < @today)
        FROM plan p
        WHERE p.period_id = @period AND p.kind = 'Opportunity'
        """ + InScope + """
        ORDER BY p.name
        """;

    // Attainment counts the leads of the plan's channel that arrived inside the period. Half-open
    // on the upper bound and computed from the period's own dates, so a lead captured at
    // 23:59 on the last day counts and one captured the next morning does not.
    private const string LeadAttainmentForPeriod = """
        SELECT p.name, coalesce(p.segment, ''), p.channel, p.target_leads,
               (SELECT count(*) FROM lead l
                WHERE l.source = p.channel
                  AND l.captured_at >= @from
                  AND l.captured_at < @to)
        FROM plan p
        WHERE p.period_id = @period AND p.kind = 'MarketingLead'
        """ + InScope + """
        ORDER BY p.name
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public PlanningStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Declares a period, unless the name is taken.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">What the period is to be called.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="parent">The parent's id, or null.</param>
    /// <param name="now">When it was declared.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The id, or null when the name is already in use.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<Guid?> SavePeriodAsync(
        string? tenantId,
        Guid id,
        DefinePeriod request,
        Guid? parent,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertPeriod;

        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "name", NpgsqlDbType.Text, request.Name);
        Add(command, "label", NpgsqlDbType.Text, request.Label);
        Add(command, "starts", NpgsqlDbType.Date, request.StartsOn);
        Add(command, "ends", NpgsqlDbType.Date, request.EndsOn);
        Add(command, "parent", NpgsqlDbType.Uuid, (object?)parent ?? DBNull.Value);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
    }

    /// <summary>Reads a period by name.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="name">Which period.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The period, or null when this tenant has no such period.</returns>
    public async ValueTask<StoredPeriod?> PeriodAsync(
        string? tenantId,
        string name,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        return await PeriodOnAsync(connection, name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets, or replaces, the number and the words for a period.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">What a new strategy would be called.</param>
    /// <param name="period">Which period.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="now">When it was set.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The strategy's id.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<Guid> SaveStrategyAsync(
        string? tenantId,
        Guid id,
        Guid period,
        SetStrategy request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = UpsertStrategy;

        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "period", NpgsqlDbType.Uuid, period);
        Add(command, "vision", NpgsqlDbType.Text, request.Vision);
        Add(command, "target", NpgsqlDbType.Numeric, request.Target);
        Add(command, "currency", NpgsqlDbType.Text, request.Currency);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>Commits a plan, unless the name is taken.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">What the plan is to be called.</param>
    /// <param name="period">Which period.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="now">When it was committed.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The id, or null when the name is already in use.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<Guid?> SavePlanAsync(
        string? tenantId,
        Guid id,
        Guid period,
        DefinePlan request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertPlan;

        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "period", NpgsqlDbType.Uuid, period);
        Add(command, "kind", NpgsqlDbType.Text, request.Kind.ToString());
        Add(command, "name", NpgsqlDbType.Text, request.Name);
        Add(command, "label", NpgsqlDbType.Text, request.Label);
        Add(command, "owner", NpgsqlDbType.Text, request.Owner);
        Add(command, "account", NpgsqlDbType.Uuid, (object?)request.Account ?? DBNull.Value);
        Add(command, "opportunity", NpgsqlDbType.Uuid, (object?)request.Opportunity ?? DBNull.Value);
        Add(command, "channel", NpgsqlDbType.Text, (object?)request.Channel ?? DBNull.Value);
        Add(command, "segment", NpgsqlDbType.Text, (object?)request.Segment ?? DBNull.Value);
        Add(command, "targetAmount", NpgsqlDbType.Numeric,
            (object?)request.TargetAmount ?? DBNull.Value);
        Add(command, "currency", NpgsqlDbType.Text, (object?)request.Currency ?? DBNull.Value);
        Add(command, "targetLeads", NpgsqlDbType.Integer,
            (object?)request.TargetLeads ?? DBNull.Value);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
    }

    /// <summary>Records an answer, and says how many are now answered.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="plan">Which plan.</param>
    /// <param name="request">What was answered.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many elements are answered, or null when there is no such plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<int?> AnswerAsync(
        string? tenantId,
        string plan,
        AnswerQualification request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        if (await PlanIdAsync(connection, plan, cancellationToken).ConfigureAwait(false)
            is not { } planId)
        {
            return null;
        }

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = UpsertQualification;

        Add(command, "plan", NpgsqlDbType.Uuid, planId);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "element", NpgsqlDbType.Text, request.Element.ToString());
        Add(command, "answered", NpgsqlDbType.Boolean, request.IsAnswered);
        Add(command, "note", NpgsqlDbType.Text, request.Note);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return await CountAsync(connection, CountAnswered, planId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Writes a step, and says how many are still to do.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="now">When, for a step being completed.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many steps are outstanding, or null when there is no such plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<int?> SaveStepAsync(
        string? tenantId,
        SetPlanStep request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        if (await PlanIdAsync(connection, request.Plan, cancellationToken).ConfigureAwait(false)
            is not { } planId)
        {
            return null;
        }

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = UpsertStep;

        Add(command, "plan", NpgsqlDbType.Uuid, planId);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "ordinal", NpgsqlDbType.Integer, request.Ordinal);
        Add(command, "description", NpgsqlDbType.Text, request.Description);
        Add(command, "owner", NpgsqlDbType.Text, request.Owner);
        Add(command, "due", NpgsqlDbType.Date, request.DueOn);
        Add(command, "completed", NpgsqlDbType.TimestampTz,
            request.IsComplete ? now : (object)DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return await CountAsync(connection, CountOutstanding, planId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>How a period is looking.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="period">Which period.</param>
    /// <param name="today">What counts as overdue.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The roll-up, or null when no strategy has been set for the period.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="period"/> is null.</exception>
    public async ValueTask<PeriodRollUp?> RollUpAsync(
        string? tenantId,
        StoredPeriod period,
        DateOnly today,
        IReadOnlyList<string> scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(scope);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        string vision;
        decimal target;
        string currency;
        decimal committed;

        var head = connection.CreateCommand();
        await using var closingHead = head.ConfigureAwait(false);

        head.CommandText = StrategyAndCommitted;
        Add(head, "period", NpgsqlDbType.Uuid, period.PeriodId);
        AddScope(head, scope);

        var reader = await head.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            vision = reader.GetString(0);
            target = reader.GetDecimal(1);
            currency = reader.GetString(2);
            committed = reader.GetDecimal(3);
        }

        var accounts = new List<AccountCoverage>();
        var deals = new List<OpportunityReadiness>();
        var marketing = new List<LeadAttainment>();

        var coverage = connection.CreateCommand();
        await using var closingCoverage = coverage.ConfigureAwait(false);

        coverage.CommandText = AccountCoverageForPeriod;
        Add(coverage, "period", NpgsqlDbType.Uuid, period.PeriodId);
        AddScope(coverage, scope);

        var coverageReader = await coverage
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (coverageReader.ConfigureAwait(false))
        {
            while (await coverageReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                accounts.Add(new AccountCoverage(
                    coverageReader.GetString(0),
                    coverageReader.GetString(1),
                    coverageReader.GetDecimal(2),
                    coverageReader.GetString(3),
                    coverageReader.GetDecimal(4)));
            }
        }

        var readiness = connection.CreateCommand();
        await using var closingReadiness = readiness.ConfigureAwait(false);

        readiness.CommandText = OpportunityReadinessForPeriod;
        Add(readiness, "period", NpgsqlDbType.Uuid, period.PeriodId);
        AddScope(readiness, scope);
        Add(readiness, "today", NpgsqlDbType.Date, today);

        var readinessReader = await readiness
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (readinessReader.ConfigureAwait(false))
        {
            while (await readinessReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                deals.Add(new OpportunityReadiness(
                    readinessReader.GetString(0),
                    readinessReader.GetDecimal(1),
                    readinessReader.GetString(2),
                    (int)readinessReader.GetInt64(3),
                    PlanningLimits.Elements,
                    (int)readinessReader.GetInt64(4),
                    (int)readinessReader.GetInt64(5)));
            }
        }

        var attainment = connection.CreateCommand();
        await using var closingAttainment = attainment.ConfigureAwait(false);

        attainment.CommandText = LeadAttainmentForPeriod;
        Add(attainment, "period", NpgsqlDbType.Uuid, period.PeriodId);
        AddScope(attainment, scope);
        Add(attainment, "from", NpgsqlDbType.TimestampTz,
            new DateTimeOffset(period.StartsOn.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

        // Half-open on the upper bound: the day after the last day, exclusive. A `<=` on the last
        // day would drop everything captured after midnight on it.
        Add(attainment, "to", NpgsqlDbType.TimestampTz,
            new DateTimeOffset(
                period.EndsOn.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

        var attainmentReader = await attainment
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (attainmentReader.ConfigureAwait(false))
        {
            while (await attainmentReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                marketing.Add(new LeadAttainment(
                    attainmentReader.GetString(0),
                    attainmentReader.GetString(1),
                    attainmentReader.GetString(2),
                    attainmentReader.GetInt32(3),
                    (int)attainmentReader.GetInt64(4)));
            }
        }

        return new PeriodRollUp(
            period.Name, vision, target, currency, committed, target - committed,
            accounts, deals, marketing);
    }

    private static async ValueTask<StoredPeriod?> PeriodOnAsync(
        NpgsqlConnection connection,
        string name,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        command.CommandText = ReadPeriod;
        Add(command, "name", NpgsqlDbType.Text, name);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new StoredPeriod(
                reader.GetGuid(0),
                name,
                reader.GetString(1),
                await reader.GetFieldValueAsync<DateOnly>(2, cancellationToken).ConfigureAwait(false),
                await reader.GetFieldValueAsync<DateOnly>(3, cancellationToken).ConfigureAwait(false))
            : null;
    }

    private static async ValueTask<Guid?> PlanIdAsync(
        NpgsqlConnection connection,
        string name,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        command.CommandText = ReadPlan;
        Add(command, "name", NpgsqlDbType.Text, name);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
    }

    private static async ValueTask<int> CountAsync(
        NpgsqlConnection connection,
        string statement,
        Guid planId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        command.CommandText = statement;
        Add(command, "plan", NpgsqlDbType.Uuid, planId);

        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    // An empty scope means a director: no restriction, rather than nothing. Getting that the wrong
    // way round would show a director a roll-up of zero and read as an organisation that had
    // stopped selling.
    private static void AddScope(NpgsqlCommand command, IReadOnlyList<string> scope)
    {
        command.Parameters.Add(
            new NpgsqlParameter("unrestricted", NpgsqlDbType.Boolean) { Value = scope.Count == 0 });

        command.Parameters.Add(new NpgsqlParameter<string[]>("scope", [.. scope]));
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

/// <summary>A period as stored.</summary>
/// <param name="PeriodId">Its id.</param>
/// <param name="Name">Its identifier.</param>
/// <param name="Label">What to show a person.</param>
/// <param name="StartsOn">Its first day.</param>
/// <param name="EndsOn">Its last day.</param>
public sealed record StoredPeriod(
    Guid PeriodId,
    string Name,
    string Label,
    DateOnly StartsOn,
    DateOnly EndsOn);
