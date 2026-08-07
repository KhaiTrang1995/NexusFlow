using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// Writes a seed's rows, and writes none of them twice.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every statement is <c>ON CONFLICT DO NOTHING</c> on the primary key, and that is the
/// whole idempotency design.</strong> The id came from the alias, so the second run's insert
/// collides with the first run's row and reports nothing written. No ledger, no checksum, no
/// "has this file been applied" table — those all answer the question at the granularity of the
/// file, which is the wrong granularity: a run that fails on item nine has applied eight, and a
/// file-level ledger either forgets them or refuses to continue.
/// </para>
/// <para>
/// <strong>Rows are written under the tenant's own connection scope.</strong> Same as every
/// other store here: <c>flowx_tenant</c> cannot bypass row-level security, so a mistake in the
/// document's tenant is refused by the database and not merely by the code that read the file.
/// The check in <see cref="SeedErrors.TenantNotServed"/> is the first of those two, not the only
/// one.
/// </para>
/// </remarks>
public sealed class SeedStore
{
    private const string InsertAccount = """
        INSERT INTO account (account_id, tenant_id, name, industry, lifecycle, region, owner_id)
        VALUES (@id, @tenant, @name, @industry, @lifecycle, @region, @owner)
        ON CONFLICT (account_id) DO NOTHING
        """;

    private const string InsertContact = """
        INSERT INTO contact (contact_id, tenant_id, account_id, full_name, email, phone, is_primary)
        VALUES (@id, @tenant, @account, @name, @email, @phone, @primary)
        ON CONFLICT (contact_id) DO NOTHING
        """;

    private const string InsertOpportunity = """
        INSERT INTO opportunity (
            opportunity_id, tenant_id, account_id, primary_contact_id, name, amount, currency,
            stage_id, probability, expected_close, owner_id, stage_entered_at)
        VALUES (@id, @tenant, @account, @contact, @name, @amount, @currency, @stage, @probability,
            @close, @owner, @now)
        ON CONFLICT (opportunity_id) DO NOTHING
        """;

    private const string InsertLead = """
        INSERT INTO lead (
            lead_id, tenant_id, company, contact_name, email, source, status, score, owner_id,
            captured_at)
        VALUES (@id, @tenant, @company, @name, @email, @source, @status, @score, @owner, @now)
        ON CONFLICT (lead_id) DO NOTHING
        """;

    private const string ReadActiveProcess = """
        SELECT process_id, version
        FROM process_definition
        WHERE applies_to = @kind AND is_active
        LIMIT 1
        """;

    private const string InsertProcess = """
        INSERT INTO process_definition (
            process_id, tenant_id, applies_to, version, is_active, published_at)
        VALUES (@id, @tenant, @kind, @version, true, @now)
        ON CONFLICT (process_id) DO NOTHING
        """;

    private const string InsertStage = """
        INSERT INTO process_stage (stage_id, process_id, name, ordinal, is_terminal)
        VALUES (@id, @process, @name, @ordinal, @terminal)
        ON CONFLICT (stage_id) DO NOTHING
        """;

    private const string InsertTransition = """
        INSERT INTO process_transition (transition_id, from_stage_id, to_stage_id, trigger, ordinal)
        VALUES (@id, @from, @to, @trigger, @ordinal)
        ON CONFLICT (transition_id) DO NOTHING
        """;

    private const string InsertPeriod = """
        INSERT INTO plan_period (
            period_id, tenant_id, name, label, starts_on, ends_on, parent_period_id, created_at)
        VALUES (@id, @tenant, @name, @label, @starts, @ends, @parent, @now)
        ON CONFLICT (period_id) DO NOTHING
        """;

    private const string InsertStrategy = """
        INSERT INTO sales_strategy (
            strategy_id, tenant_id, period_id, vision, target_amount, currency, created_at)
        VALUES (@id, @tenant, @period, @vision, @target, @currency, @now)
        ON CONFLICT (strategy_id) DO NOTHING
        """;

    private const string InsertOrgMember = """
        INSERT INTO org_member (tenant_id, user_id, display_name, role, reports_to)
        VALUES (@tenant, @user, @name, @role, @reports)
        ON CONFLICT (tenant_id, user_id) DO NOTHING
        """;

    private const string InsertKpi = """
        INSERT INTO kpi (kpi_id, tenant_id, name, label, source, target, direction, created_at)
        VALUES (@id, @tenant, @name, @label, @source, @target, @direction, @now)
        ON CONFLICT (kpi_id) DO NOTHING
        """;

    private const string InsertTerritory = """
        INSERT INTO territory (
            territory_id, tenant_id, name, label, parent_territory_id, priority, created_at)
        VALUES (@id, @tenant, @name, @label, NULL, @priority, @now)
        ON CONFLICT (territory_id) DO NOTHING
        """;

    private const string InsertQuota = """
        INSERT INTO quota (
            quota_id, tenant_id, period_id, user_id, measure, target, ramp_factor, created_at)
        VALUES (@id, @tenant, @period, @user, @measure, @target, @ramp, @now)
        ON CONFLICT (quota_id) DO NOTHING
        """;

    private const string InsertBusinessHours = """
        INSERT INTO business_hours (tenant_id, day_of_week, opens_at, closes_at)
        VALUES (@tenant, @day, @opens, @closes)
        ON CONFLICT (tenant_id, day_of_week) DO NOTHING
        """;

    private const string InsertSlaPolicy = """
        INSERT INTO sla_policy (
            policy_id, tenant_id, name, label, priority, first_response_minutes,
            resolution_minutes, business_hours_only, is_active)
        VALUES (@id, @tenant, @name, @label, @priority, @first, @resolution, @hours, true)
        ON CONFLICT (policy_id) DO NOTHING
        """;

    private const string InsertCampaign = """
        INSERT INTO campaign (
            campaign_id, tenant_id, name, label, channel, starts_on, ends_on, budget,
            is_active, created_at)
        VALUES (@id, @tenant, @name, @label, @channel, @starts, @ends, @budget, true, @now)
        ON CONFLICT (campaign_id) DO NOTHING
        """;

    private const string InsertActivity = """
        INSERT INTO activity (
            activity_id, tenant_id, kind, subject, relates_to_kind, relates_to_id, owner_id,
            due_at, status, completed_at, escalation_count)
        VALUES (@id, @tenant, @kind, @subject, @parentKind, @parent, @owner, @due, @status, NULL, 0)
        ON CONFLICT (activity_id) DO NOTHING
        """;

    private const string InsertQuote = """
        INSERT INTO quote (
            quote_id, tenant_id, opportunity_id, status, subtotal, discount, total, currency,
            valid_until, approved_by)
        VALUES (@id, @tenant, @opportunity, @status, @subtotal, @discount, @total, @currency,
            @valid, NULL)
        ON CONFLICT (quote_id) DO NOTHING
        """;

    private const string InsertOrder = """
        INSERT INTO sales_order (
            order_id, tenant_id, quote_id, account_id, status, total, currency, placed_at)
        SELECT @id, @tenant, q.quote_id, @account, @status, q.total, q.currency, @now
        FROM quote q WHERE q.quote_id = @quote
        ON CONFLICT (order_id) DO NOTHING
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public SeedStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Writes an account.</summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="id">The id derived from its alias.</param>
    /// <param name="account">What to write.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Whether a row was inserted rather than already there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="account"/> is null.</exception>
    public async ValueTask<bool> WriteAccountAsync(
        string tenant,
        Guid id,
        SeedAccount account,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);

        var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertAccount;
        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenant);
        Add(command, "name", NpgsqlDbType.Text, account.Name);
        Add(command, "industry", NpgsqlDbType.Text, account.Industry);
        Add(command, "lifecycle", NpgsqlDbType.Text, account.Lifecycle.ToString());
        Add(command, "region", NpgsqlDbType.Text, account.Region);
        Add(command, "owner", NpgsqlDbType.Uuid, account.Owner);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>Writes a contact.</summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="id">The id derived from its alias.</param>
    /// <param name="accountId">The account it belongs to.</param>
    /// <param name="contact">What to write.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Whether a row was inserted rather than already there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contact"/> is null.</exception>
    public async ValueTask<bool> WriteContactAsync(
        string tenant,
        Guid id,
        Guid accountId,
        SeedContact contact,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contact);

        var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertContact;
        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenant);
        Add(command, "account", NpgsqlDbType.Uuid, accountId);
        Add(command, "name", NpgsqlDbType.Text, contact.FullName);
        Add(command, "email", NpgsqlDbType.Text, (object?)contact.Email ?? DBNull.Value);
        Add(command, "phone", NpgsqlDbType.Text, (object?)contact.Phone ?? DBNull.Value);
        Add(command, "primary", NpgsqlDbType.Boolean, contact.Primary);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>Writes an opportunity.</summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="id">The id derived from its alias.</param>
    /// <param name="accountId">The account.</param>
    /// <param name="contactId">The primary contact.</param>
    /// <param name="stageId">The stage it sits in.</param>
    /// <param name="opportunity">What to write.</param>
    /// <param name="now">The instant it entered that stage.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Whether a row was inserted rather than already there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="opportunity"/> is null.</exception>
    public async ValueTask<bool> WriteOpportunityAsync(
        string tenant,
        Guid id,
        Guid accountId,
        Guid contactId,
        Guid stageId,
        SeedOpportunity opportunity,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(opportunity);

        var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertOpportunity;
        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenant);
        Add(command, "account", NpgsqlDbType.Uuid, accountId);
        Add(command, "contact", NpgsqlDbType.Uuid, contactId);
        Add(command, "name", NpgsqlDbType.Text, opportunity.Name);
        Add(command, "amount", NpgsqlDbType.Numeric, opportunity.Amount);
        Add(command, "currency", NpgsqlDbType.Text, opportunity.Currency);
        Add(command, "stage", NpgsqlDbType.Uuid, stageId);
        Add(command, "probability", NpgsqlDbType.Integer, opportunity.Probability);
        Add(command, "close", NpgsqlDbType.Date, opportunity.ExpectedClose);
        Add(command, "owner", NpgsqlDbType.Uuid, opportunity.Owner);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>Writes a lead.</summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="id">The id derived from its alias.</param>
    /// <param name="lead">What to write.</param>
    /// <param name="now">When it was captured.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Whether a row was inserted rather than already there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lead"/> is null.</exception>
    public async ValueTask<bool> WriteLeadAsync(
        string tenant,
        Guid id,
        SeedLead lead,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lead);

        var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertLead;
        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenant);
        Add(command, "company", NpgsqlDbType.Text, lead.Company);
        Add(command, "name", NpgsqlDbType.Text, lead.ContactName);
        Add(command, "email", NpgsqlDbType.Text, (object?)lead.Email ?? DBNull.Value);
        Add(command, "source", NpgsqlDbType.Text, lead.Source.ToString());
        Add(command, "status", NpgsqlDbType.Text, lead.Status.ToString());
        Add(command, "score", NpgsqlDbType.Integer, lead.Score);
        Add(command, "owner", NpgsqlDbType.Uuid, (object?)lead.Owner ?? DBNull.Value);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>Which definition drives an entity kind for this tenant, if any is active.</summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="kind">Which kind.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The active definition's id and version, or null when there is none.</returns>
    public async ValueTask<(Guid Id, int Version)?> ActiveProcessAsync(
        string tenant,
        EntityKind kind,
        CancellationToken ct)
    {
        var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = ReadActiveProcess;
        Add(command, "kind", NpgsqlDbType.Text, kind.ToString());

        var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? (reader.GetGuid(0), reader.GetInt32(1))
            : null;
    }

    /// <summary>
    /// Publishes a definition, its stages and its transitions, or none of them.
    /// </summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="process">What to publish.</param>
    /// <param name="now">The instant it was published.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Whether it was written rather than already there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="process"/> is null.</exception>
    /// <remarks>
    /// <strong>One transaction, unlike everything else here.</strong> A definition is not a row,
    /// it is a graph: a definition with half its stages is a pipeline an opportunity can enter
    /// and not leave, and <c>process_transition</c>'s foreign keys would let exactly that be
    /// committed one statement at a time.
    /// </remarks>
    public async ValueTask<bool> WriteProcessAsync(
        string tenant,
        SeedProcess process,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(process);

        var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var closingTransaction = transaction.ConfigureAwait(false);

        var processId = SeedIds.For(tenant, "process", process.Alias);
        var written = await ExecuteAsync(
            connection,
            InsertProcess,
            command =>
            {
                Add(command, "id", NpgsqlDbType.Uuid, processId);
                Add(command, "tenant", NpgsqlDbType.Text, tenant);
                Add(command, "kind", NpgsqlDbType.Text, process.AppliesTo.ToString());
                Add(command, "version", NpgsqlDbType.Integer, process.Version);
                Add(command, "now", NpgsqlDbType.TimestampTz, now);
            },
            ct).ConfigureAwait(false);

        for (var ordinal = 0; ordinal < process.Stages.Count; ordinal++)
        {
            var stage = process.Stages[ordinal]!;

            await ExecuteAsync(
                connection,
                InsertStage,
                command =>
                {
                    Add(command, "id", NpgsqlDbType.Uuid, StageId(tenant, process.Alias, stage.Name));
                    Add(command, "process", NpgsqlDbType.Uuid, processId);
                    Add(command, "name", NpgsqlDbType.Text, stage.Name);
                    Add(command, "ordinal", NpgsqlDbType.Integer, ordinal);
                    Add(command, "terminal", NpgsqlDbType.Boolean, stage.Terminal);
                },
                ct).ConfigureAwait(false);
        }

        for (var ordinal = 0; ordinal < process.Transitions.Count; ordinal++)
        {
            var transition = process.Transitions[ordinal]!;

            await ExecuteAsync(
                connection,
                InsertTransition,
                command =>
                {
                    Add(
                        command,
                        "id",
                        NpgsqlDbType.Uuid,
                        SeedIds.For(
                            tenant,
                            "transition",
                            $"{process.Alias}:{transition.From}>{transition.To}:{transition.Trigger}"));
                    Add(command, "from", NpgsqlDbType.Uuid, StageId(tenant, process.Alias, transition.From));
                    Add(command, "to", NpgsqlDbType.Uuid, StageId(tenant, process.Alias, transition.To));
                    Add(command, "trigger", NpgsqlDbType.Text, transition.Trigger);
                    Add(command, "ordinal", NpgsqlDbType.Integer, ordinal);
                },
                ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return written;
    }


    /// <summary>Writes a period of the fiscal calendar.</summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="period">What to write.</param>
    /// <param name="now">The instant it was declared.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Whether a row was inserted rather than already there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="period"/> is null.</exception>
    public ValueTask<bool> WritePeriodAsync(
        string tenant, SeedPeriod period, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(period);

        return WriteAsync(tenant, InsertPeriod, command =>
        {
            Add(command, "id", NpgsqlDbType.Uuid, SeedIds.For(tenant, "period", period.Alias));
            Add(command, "tenant", NpgsqlDbType.Text, tenant);
            Add(command, "name", NpgsqlDbType.Text, period.Name);
            Add(command, "label", NpgsqlDbType.Text, period.Label);
            Add(command, "starts", NpgsqlDbType.Date, period.StartsOn);
            Add(command, "ends", NpgsqlDbType.Date, period.EndsOn);
            Add(command, "parent", NpgsqlDbType.Uuid, period.Parent is { } parent
                ? SeedIds.For(tenant, "period", parent)
                : DBNull.Value);
            Add(command, "now", NpgsqlDbType.TimestampTz, now);
        }, ct);
    }

    /// <summary>Writes the number for a period.</summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="strategy">What to write.</param>
    /// <param name="now">The instant it was set.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Whether a row was inserted rather than already there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="strategy"/> is null.</exception>
    public ValueTask<bool> WriteStrategyAsync(
        string tenant, SeedStrategy strategy, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        return WriteAsync(tenant, InsertStrategy, command =>
        {
            Add(command, "id", NpgsqlDbType.Uuid, SeedIds.For(tenant, "strategy", strategy.Period));
            Add(command, "tenant", NpgsqlDbType.Text, tenant);
            Add(command, "period", NpgsqlDbType.Uuid, SeedIds.For(tenant, "period", strategy.Period));
            Add(command, "vision", NpgsqlDbType.Text, strategy.Vision);
            Add(command, "target", NpgsqlDbType.Numeric, strategy.TargetAmount);
            Add(command, "currency", NpgsqlDbType.Text, strategy.Currency);
            Add(command, "now", NpgsqlDbType.TimestampTz, now);
        }, ct);
    }

    /// <summary>Writes one person into the reporting line.</summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="member">What to write.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Whether a row was inserted rather than already there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="member"/> is null.</exception>
    public ValueTask<bool> WriteOrgMemberAsync(
        string tenant, SeedOrgMember member, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(member);

        return WriteAsync(tenant, InsertOrgMember, command =>
        {
            Add(command, "tenant", NpgsqlDbType.Text, tenant);
            Add(command, "user", NpgsqlDbType.Text, member.UserId);
            Add(command, "name", NpgsqlDbType.Text, member.DisplayName);
            Add(command, "role", NpgsqlDbType.Text, member.Role.ToString());
            Add(command, "reports", NpgsqlDbType.Text, (object?)member.ReportsTo ?? DBNull.Value);
        }, ct);
    }

    /// <summary>Writes a measured number.</summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="kpi">What to write.</param>
    /// <param name="now">The instant it was declared.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Whether a row was inserted rather than already there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="kpi"/> is null.</exception>
    public ValueTask<bool> WriteKpiAsync(
        string tenant, SeedKpi kpi, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(kpi);

        return WriteAsync(tenant, InsertKpi, command =>
        {
            Add(command, "id", NpgsqlDbType.Uuid, SeedIds.For(tenant, "kpi", kpi.Alias));
            Add(command, "tenant", NpgsqlDbType.Text, tenant);
            Add(command, "name", NpgsqlDbType.Text, kpi.Name);
            Add(command, "label", NpgsqlDbType.Text, kpi.Label);
            Add(command, "source", NpgsqlDbType.Text, kpi.Source.ToString());
            Add(command, "target", NpgsqlDbType.Numeric, kpi.Target);
            Add(command, "direction", NpgsqlDbType.Text, kpi.Direction.ToString());
            Add(command, "now", NpgsqlDbType.TimestampTz, now);
        }, ct);
    }

    /// <summary>Writes a territory.</summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="territory">What to write.</param>
    /// <param name="now">The instant it was declared.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Whether a row was inserted rather than already there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="territory"/> is null.</exception>
    public ValueTask<bool> WriteTerritoryAsync(
        string tenant, SeedTerritory territory, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(territory);

        return WriteAsync(tenant, InsertTerritory, command =>
        {
            Add(command, "id", NpgsqlDbType.Uuid, SeedIds.For(tenant, "territory", territory.Alias));
            Add(command, "tenant", NpgsqlDbType.Text, tenant);
            Add(command, "name", NpgsqlDbType.Text, territory.Name);
            Add(command, "label", NpgsqlDbType.Text, territory.Label);
            Add(command, "priority", NpgsqlDbType.Integer, territory.Priority);
            Add(command, "now", NpgsqlDbType.TimestampTz, now);
        }, ct);
    }

    /// <summary>Writes somebody's number.</summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="quota">What to write.</param>
    /// <param name="now">The instant it was set.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Whether a row was inserted rather than already there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="quota"/> is null.</exception>
    public ValueTask<bool> WriteQuotaAsync(
        string tenant, SeedQuota quota, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(quota);

        return WriteAsync(tenant, InsertQuota, command =>
        {
            Add(command, "id", NpgsqlDbType.Uuid, SeedIds.For(
                tenant, "quota", $"{quota.Period}:{quota.UserId}:{quota.Measure}"));
            Add(command, "tenant", NpgsqlDbType.Text, tenant);
            Add(command, "period", NpgsqlDbType.Uuid, SeedIds.For(tenant, "period", quota.Period));
            Add(command, "user", NpgsqlDbType.Text, quota.UserId);
            Add(command, "measure", NpgsqlDbType.Text, quota.Measure.ToString());
            Add(command, "target", NpgsqlDbType.Numeric, quota.Target);
            Add(command, "ramp", NpgsqlDbType.Numeric, quota.RampFactor);
            Add(command, "now", NpgsqlDbType.TimestampTz, now);
        }, ct);
    }

    /// <summary>Writes one day of the desk's opening hours.</summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="hours">What to write.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Whether a row was inserted rather than already there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hours"/> is null.</exception>
    public ValueTask<bool> WriteBusinessHoursAsync(
        string tenant, SeedBusinessHours hours, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hours);

        return WriteAsync(tenant, InsertBusinessHours, command =>
        {
            Add(command, "tenant", NpgsqlDbType.Text, tenant);
            Add(command, "day", NpgsqlDbType.Smallint, (short)hours.DayOfWeek);
            Add(command, "opens", NpgsqlDbType.Time, hours.OpensAt);
            Add(command, "closes", NpgsqlDbType.Time, hours.ClosesAt);
        }, ct);
    }

    /// <summary>Writes what a case of one priority is promised.</summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="policy">What to write.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Whether a row was inserted rather than already there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is null.</exception>
    public ValueTask<bool> WriteSlaPolicyAsync(
        string tenant, SeedSlaPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return WriteAsync(tenant, InsertSlaPolicy, command =>
        {
            Add(command, "id", NpgsqlDbType.Uuid, SeedIds.For(tenant, "sla", policy.Alias));
            Add(command, "tenant", NpgsqlDbType.Text, tenant);
            Add(command, "name", NpgsqlDbType.Text, policy.Name);
            Add(command, "label", NpgsqlDbType.Text, policy.Label);
            Add(command, "priority", NpgsqlDbType.Text, policy.Priority.ToString());
            Add(command, "first", NpgsqlDbType.Integer, policy.FirstResponseMinutes);
            Add(command, "resolution", NpgsqlDbType.Integer, policy.ResolutionMinutes);
            Add(command, "hours", NpgsqlDbType.Boolean, policy.BusinessHoursOnly);
        }, ct);
    }

    /// <summary>Writes a campaign.</summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="campaign">What to write.</param>
    /// <param name="now">The instant it was declared.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Whether a row was inserted rather than already there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="campaign"/> is null.</exception>
    public ValueTask<bool> WriteCampaignAsync(
        string tenant, SeedCampaign campaign, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        return WriteAsync(tenant, InsertCampaign, command =>
        {
            Add(command, "id", NpgsqlDbType.Uuid, SeedIds.For(tenant, "campaign", campaign.Alias));
            Add(command, "tenant", NpgsqlDbType.Text, tenant);
            Add(command, "name", NpgsqlDbType.Text, campaign.Name);
            Add(command, "label", NpgsqlDbType.Text, campaign.Label);
            Add(command, "channel", NpgsqlDbType.Text, campaign.Channel.ToString());
            Add(command, "starts", NpgsqlDbType.Date, campaign.StartsOn);
            Add(command, "ends", NpgsqlDbType.Date, campaign.EndsOn);
            Add(command, "budget", NpgsqlDbType.Numeric, campaign.Budget);
            Add(command, "now", NpgsqlDbType.TimestampTz, now);
        }, ct);
    }

    /// <summary>Writes a task, call, meeting or note.</summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="activity">What to write.</param>
    /// <param name="parentId">The row it hangs off.</param>
    /// <param name="now">The instant the seed was applied; the due date is counted from it.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Whether a row was inserted rather than already there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="activity"/> is null.</exception>
    public ValueTask<bool> WriteActivityAsync(
        string tenant, SeedActivity activity, Guid parentId, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(activity);

        return WriteAsync(tenant, InsertActivity, command =>
        {
            Add(command, "id", NpgsqlDbType.Uuid, SeedIds.For(tenant, "activity", activity.Alias));
            Add(command, "tenant", NpgsqlDbType.Text, tenant);
            Add(command, "kind", NpgsqlDbType.Text, activity.Kind.ToString());
            Add(command, "subject", NpgsqlDbType.Text, activity.Subject);
            Add(command, "parentKind", NpgsqlDbType.Text, activity.RelatesToKind.ToString());
            Add(command, "parent", NpgsqlDbType.Uuid, parentId);
            Add(command, "owner", NpgsqlDbType.Uuid, activity.Owner);
            Add(command, "due", NpgsqlDbType.TimestampTz, now.AddDays(activity.DueInDays));
            Add(command, "status", NpgsqlDbType.Text, activity.Status.ToString());
        }, ct);
    }

    /// <summary>Writes a quote.</summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="quote">What to write.</param>
    /// <param name="now">The instant it was priced; the validity runs from it.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Whether a row was inserted rather than already there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="quote"/> is null.</exception>
    public ValueTask<bool> WriteQuoteAsync(
        string tenant, SeedQuote quote, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(quote);

        return WriteAsync(tenant, InsertQuote, command =>
        {
            Add(command, "id", NpgsqlDbType.Uuid, SeedIds.For(tenant, "quote", quote.Alias));
            Add(command, "tenant", NpgsqlDbType.Text, tenant);
            Add(command, "opportunity", NpgsqlDbType.Uuid,
                SeedIds.For(tenant, "opportunity", quote.Opportunity));
            Add(command, "status", NpgsqlDbType.Text, quote.Status.ToString());
            Add(command, "subtotal", NpgsqlDbType.Numeric, quote.Subtotal);
            Add(command, "discount", NpgsqlDbType.Numeric, quote.Discount);

            // Computed rather than carried. A file that stated all three could state a total that
            // is not the subtotal less the discount, and nothing downstream would ever say so.
            Add(command, "total", NpgsqlDbType.Numeric, quote.Subtotal - quote.Discount);
            Add(command, "currency", NpgsqlDbType.Text, quote.Currency);
            Add(command, "valid", NpgsqlDbType.TimestampTz, now.AddDays(quote.ValidForDays));
        }, ct);
    }

    /// <summary>Writes an order, taking its money from the quote it was placed from.</summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="order">What to write.</param>
    /// <param name="now">When it was placed.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>Whether a row was inserted rather than already there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="order"/> is null.</exception>
    /// <remarks>
    /// The total is selected from the quote rather than restated. An order whose money disagrees
    /// with the quote it came from is the one inconsistency in this sample that would be believed.
    /// </remarks>
    public ValueTask<bool> WriteOrderAsync(
        string tenant, SeedOrder order, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(order);

        return WriteAsync(tenant, InsertOrder, command =>
        {
            Add(command, "id", NpgsqlDbType.Uuid, SeedIds.For(tenant, "order", order.Alias));
            Add(command, "tenant", NpgsqlDbType.Text, tenant);
            Add(command, "quote", NpgsqlDbType.Uuid, SeedIds.For(tenant, "quote", order.Quote));
            Add(command, "account", NpgsqlDbType.Uuid, SeedIds.For(tenant, "account", order.Account));
            Add(command, "status", NpgsqlDbType.Text, order.Status.ToString());
            Add(command, "now", NpgsqlDbType.TimestampTz, now);
        }, ct);
    }

    /// <summary>Opens a scoped connection, runs one statement, and reports whether it wrote.</summary>
    /// <remarks>
    /// The ten writes above are one statement each against one tenant-scoped connection, and
    /// spelling that out ten times is ten places for the scope to be forgotten in.
    /// </remarks>
    private async ValueTask<bool> WriteAsync(
        string tenant,
        string sql,
        Action<NpgsqlCommand> bind,
        CancellationToken ct)
    {
        var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        return await ExecuteAsync(connection, sql, bind, ct).ConfigureAwait(false);
    }

    /// <summary>The id a stage of a seeded process always has.</summary>
    /// <param name="tenant">Whose.</param>
    /// <param name="processAlias">The process's alias in the file.</param>
    /// <param name="stage">The stage's name.</param>
    /// <returns>The identifier.</returns>
    public static Guid StageId(string tenant, string processAlias, string stage) =>
        SeedIds.For(tenant, "stage", processAlias + ":" + stage);

    private static async ValueTask<bool> ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        Action<NpgsqlCommand> bind,
        CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        command.CommandText = sql;
        bind(command);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });

    private async ValueTask<NpgsqlConnection> OpenAsync(string tenant, CancellationToken ct)
    {
        var connection = await _source.OpenConnectionAsync(ct).ConfigureAwait(false);

        try
        {
            await CrmTenantScope.ApplyAsync(connection, tenant, ct).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return connection;
    }
}
