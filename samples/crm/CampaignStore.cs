using Npgsql;
using NpgsqlTypes;

namespace Crm;

/// <summary>
/// The campaigns, what they touched, what they cost, and the deals the credit is shared out over.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No credit is computed in SQL.</strong> The touches come back as rows and
/// <c>Attribution.Split</c> shares them out — a pure function that can be tested at every boundary
/// without a database, and the only place the cutoff and the rounding live. Four models written as
/// four statements would be four places for one of them to be written without the cutoff.
/// </para>
/// <para>
/// <strong>A deal is decided when it entered the stage it is in.</strong> There is no separate
/// decided-at column and inventing one would be a second thing to keep in step;
/// <c>stage_entered_at</c> on a deal whose outcome is set is exactly the moment somebody decided
/// it.
/// </para>
/// </remarks>
public sealed class CampaignStore
{
    private const string InsertCampaign = """
        INSERT INTO campaign (
            campaign_id, tenant_id, name, label, channel, starts_on, ends_on, budget,
            is_active, created_at)
        VALUES (@id, @tenant, @name, @label, @channel, @starts, @ends, @budget, true, @now)
        ON CONFLICT (tenant_id, name) DO NOTHING
        RETURNING campaign_id
        """;

    private const string CampaignByName = "SELECT campaign_id FROM campaign WHERE name = @name";

    private const string LeadExists = "SELECT 1 FROM lead WHERE lead_id = @id";

    private const string ContactExists = "SELECT 1 FROM contact WHERE contact_id = @id";

    // DO NOTHING and a returned id, so a pipeline replaying a batch is told the touch was already
    // known rather than doubling every open in a rate whose denominator it is.
    private const string InsertTouch = """
        INSERT INTO campaign_touch (
            touch_id, tenant_id, campaign_id, lead_id, contact_id, kind, touched_at)
        VALUES (@id, @tenant, @campaign, @lead, @contact, @kind, @at)
        ON CONFLICT DO NOTHING
        RETURNING touch_id
        """;

    private const string ExistingTouch = """
        SELECT touch_id FROM campaign_touch
        WHERE campaign_id = @campaign
          AND lead_id IS NOT DISTINCT FROM @lead
          AND contact_id IS NOT DISTINCT FROM @contact
          AND kind = @kind
          AND touched_at = @at
        """;

    // The place in the ledger is chosen inside the insert, for the reason a case comment's is:
    // two spends written at once would otherwise both read the same maximum and one would be lost.
    private const string InsertCost = """
        INSERT INTO campaign_cost (
            campaign_id, tenant_id, ordinal, incurred_on, amount, note, recorded_by, recorded_at)
        SELECT @campaign, @tenant, coalesce(max(ordinal), 0) + 1, @on, @amount, @note, @by, @now
        FROM campaign_cost WHERE campaign_id = @campaign
        RETURNING ordinal
        """;

    private const string SpendAndBudget = """
        SELECT c.budget,
               coalesce((SELECT sum(amount) FROM campaign_cost k
                         WHERE k.campaign_id = c.campaign_id), 0)
        FROM campaign c WHERE c.campaign_id = @campaign
        """;

    // What each campaign did, before any deal is shared out. A send is a thing the sender did, so
    // only the other kinds count towards a response — a rate whose numerator and denominator are
    // both sends is one, for every campaign, for ever.
    private const string CampaignTotals = """
        SELECT c.campaign_id, c.name, c.label, c.channel, c.budget,
               count(DISTINCT coalesce(t.lead_id, t.contact_id)) AS people,
               count(DISTINCT CASE WHEN t.kind <> 'Sent'
                                   THEN coalesce(t.lead_id, t.contact_id) END) AS responses,
               coalesce((SELECT sum(amount) FROM campaign_cost k
                         WHERE k.campaign_id = c.campaign_id), 0) AS spent
        FROM campaign c
        LEFT JOIN campaign_touch t ON t.campaign_id = c.campaign_id
        GROUP BY c.campaign_id, c.name, c.label, c.channel, c.budget
        ORDER BY c.name
        LIMIT @limit
        """;

    private const string DecidedDeals = """
        SELECT opportunity_id, amount, stage_entered_at
        FROM opportunity
        WHERE outcome IS NOT NULL
          AND stage_entered_at >= @from
          AND stage_entered_at < @to
        ORDER BY stage_entered_at, opportunity_id
        LIMIT @limit
        """;

    // Both paths to a deal, as one set. A person is a lead before they convert and a contact
    // afterwards, and a campaign that touched them in both lives touched them once — which the
    // UNION says and an OR in a join predicate would have to be told.
    private const string TouchesOfDecidedDeals = """
        SELECT o.opportunity_id, c.campaign_id, c.name, t.touched_at
        FROM opportunity o
        JOIN campaign_touch t ON t.contact_id = o.primary_contact_id
        JOIN campaign c ON c.campaign_id = t.campaign_id
        WHERE o.outcome IS NOT NULL
          AND o.stage_entered_at >= @from
          AND o.stage_entered_at < @to
        UNION
        SELECT o.opportunity_id, c.campaign_id, c.name, t.touched_at
        FROM lead l
        JOIN opportunity o ON o.opportunity_id = l.converted_opportunity_id
        JOIN campaign_touch t ON t.lead_id = l.lead_id
        JOIN campaign c ON c.campaign_id = t.campaign_id
        WHERE o.outcome IS NOT NULL
          AND o.stage_entered_at >= @from
          AND o.stage_entered_at < @to
        """;

    private const string OneDeal = """
        SELECT amount, outcome, stage_entered_at
        FROM opportunity WHERE opportunity_id = @id
        """;

    private const string TouchesOfOneDeal = """
        SELECT c.campaign_id, c.name, t.touched_at
        FROM opportunity o
        JOIN campaign_touch t ON t.contact_id = o.primary_contact_id
        JOIN campaign c ON c.campaign_id = t.campaign_id
        WHERE o.opportunity_id = @id
        UNION
        SELECT c.campaign_id, c.name, t.touched_at
        FROM lead l
        JOIN campaign_touch t ON t.lead_id = l.lead_id
        JOIN campaign c ON c.campaign_id = t.campaign_id
        WHERE l.converted_opportunity_id = @id
        """;

    private readonly NpgsqlDataSource _source;

    /// <summary>Builds the store over the application's data source.</summary>
    /// <param name="source">The pool <c>AddFlowXPostgres</c> built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public CampaignStore(NpgsqlDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>Declares a campaign.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">What the campaign is to be called.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="now">When.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The id, or null when the name is already in use.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<Guid?> SaveCampaignAsync(
        string? tenantId,
        Guid id,
        DefineCampaign request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = InsertCampaign;

        Add(command, "id", NpgsqlDbType.Uuid, id);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(command, "name", NpgsqlDbType.Text, request.Name);
        Add(command, "label", NpgsqlDbType.Text, request.Label);
        Add(command, "channel", NpgsqlDbType.Text, request.Channel.ToString());
        Add(command, "starts", NpgsqlDbType.Date, request.StartsOn);
        Add(command, "ends", NpgsqlDbType.Date, request.EndsOn);
        Add(command, "budget", NpgsqlDbType.Numeric, request.Budget);
        Add(command, "now", NpgsqlDbType.TimestampTz, now);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
    }

    /// <summary>Records that a campaign reached somebody.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">What the touch is to be called.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The touch and whether it was already on file, or null when the campaign or the person is
    /// not this tenant's.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<TouchRecorded?> SaveTouchAsync(
        string? tenantId,
        Guid id,
        RecordTouch request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        if (await NamedAsync(connection, request.Campaign, cancellationToken)
                .ConfigureAwait(false) is not { } campaign)
        {
            return null;
        }

        var person = request.LeadId is { } lead
            ? await ScalarAsync(connection, LeadExists, lead, cancellationToken).ConfigureAwait(false)
            : await ScalarAsync(connection, ContactExists, request.ContactId!.Value, cancellationToken)
                .ConfigureAwait(false);

        if (person is null)
        {
            return null;
        }

        var write = connection.CreateCommand();
        await using var closingWrite = write.ConfigureAwait(false);

        write.CommandText = InsertTouch;

        Add(write, "id", NpgsqlDbType.Uuid, id);
        Add(write, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
        Add(write, "campaign", NpgsqlDbType.Uuid, campaign);
        Add(write, "lead", NpgsqlDbType.Uuid, (object?)request.LeadId ?? DBNull.Value);
        Add(write, "contact", NpgsqlDbType.Uuid, (object?)request.ContactId ?? DBNull.Value);
        Add(write, "kind", NpgsqlDbType.Text, request.Kind.ToString());
        Add(write, "at", NpgsqlDbType.TimestampTz, request.TouchedAt);

        if (await write.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid saved)
        {
            return new TouchRecorded(saved, false);
        }

        // Already on file. The id of the row that is there is returned rather than the one that
        // was not written, so a caller can point at the touch either way.
        var existing = connection.CreateCommand();
        await using var closingExisting = existing.ConfigureAwait(false);

        existing.CommandText = ExistingTouch;

        Add(existing, "campaign", NpgsqlDbType.Uuid, campaign);
        Add(existing, "lead", NpgsqlDbType.Uuid, (object?)request.LeadId ?? DBNull.Value);
        Add(existing, "contact", NpgsqlDbType.Uuid, (object?)request.ContactId ?? DBNull.Value);
        Add(existing, "kind", NpgsqlDbType.Text, request.Kind.ToString());
        Add(existing, "at", NpgsqlDbType.TimestampTz, request.TouchedAt);

        return await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid found
            ? new TouchRecorded(found, true)
            : null;
    }

    /// <summary>Records what a campaign actually cost.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="by">Who recorded it.</param>
    /// <param name="now">When.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Where it sits and what has been spent, or null when the campaign is not found.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<CampaignCostRecorded?> SaveCostAsync(
        string? tenantId,
        RecordCampaignCost request,
        string by,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        if (await NamedAsync(connection, request.Campaign, cancellationToken)
                .ConfigureAwait(false) is not { } campaign)
        {
            return null;
        }

        var ordinal = 0;

        for (var attempt = 0; attempt < ServiceLimits.NumberAttempts; attempt++)
        {
            var write = connection.CreateCommand();
            await using var closingWrite = write.ConfigureAwait(false);

            write.CommandText = InsertCost;

            Add(write, "campaign", NpgsqlDbType.Uuid, campaign);
            Add(write, "tenant", NpgsqlDbType.Text, tenantId ?? string.Empty);
            Add(write, "on", NpgsqlDbType.Date, request.IncurredOn);
            Add(write, "amount", NpgsqlDbType.Numeric, request.Amount);
            Add(write, "note", NpgsqlDbType.Text, request.Note);
            Add(write, "by", NpgsqlDbType.Text, by);
            Add(write, "now", NpgsqlDbType.TimestampTz, now);

            try
            {
                ordinal = (int)(await write.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false))!;

                break;
            }
            catch (PostgresException failure)
                when (failure.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // Somebody else took the place. Ask for the next one rather than lose the spend.
            }
        }

        var totals = connection.CreateCommand();
        await using var closingTotals = totals.ConfigureAwait(false);

        totals.CommandText = SpendAndBudget;
        Add(totals, "campaign", NpgsqlDbType.Uuid, campaign);

        var reader = await totals.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var budget = reader.GetDecimal(0);
        var spent = reader.GetDecimal(1);

        return new CampaignCostRecorded(campaign, ordinal, spent, spent > budget);
    }

    /// <summary>What each campaign did, before any deal is shared out.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The campaigns, by name.</returns>
    public async ValueTask<IReadOnlyList<StoredCampaignTotals>> TotalsAsync(
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = CampaignTotals;
        Add(command, "limit", NpgsqlDbType.Integer, CampaignLimits.MaxCampaigns);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingReader = reader.ConfigureAwait(false);

        var totals = new List<StoredCampaignTotals>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            totals.Add(new StoredCampaignTotals(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetDecimal(4),
                (int)reader.GetInt64(5),
                (int)reader.GetInt64(6),
                reader.GetDecimal(7)));
        }

        return totals;
    }

    /// <summary>The decided deals in a window, and everything that touched them.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="from">The earliest decision to count.</param>
    /// <param name="to">The first decision not to.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The deals, each with its touches in no particular order.</returns>
    public async ValueTask<IReadOnlyList<DecidedDeal>> DecidedAsync(
        string? tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var deals = new List<(Guid Id, decimal Amount, DateTimeOffset DecidedAt)>();

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = DecidedDeals;

        Add(command, "from", NpgsqlDbType.TimestampTz, from);
        Add(command, "to", NpgsqlDbType.TimestampTz, to);
        Add(command, "limit", NpgsqlDbType.Integer, CampaignLimits.MaxDeals);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                deals.Add((
                    reader.GetGuid(0),
                    reader.GetDecimal(1),
                    await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken)
                        .ConfigureAwait(false)));
            }
        }

        var touches = new Dictionary<Guid, List<Touch>>();

        var second = connection.CreateCommand();
        await using var closingSecond = second.ConfigureAwait(false);

        second.CommandText = TouchesOfDecidedDeals;

        Add(second, "from", NpgsqlDbType.TimestampTz, from);
        Add(second, "to", NpgsqlDbType.TimestampTz, to);

        var touchReader = await second.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (touchReader.ConfigureAwait(false))
        {
            while (await touchReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var deal = touchReader.GetGuid(0);

                if (!touches.TryGetValue(deal, out var carried))
                {
                    carried = [];
                    touches[deal] = carried;
                }

                carried.Add(new Touch(
                    touchReader.GetGuid(1),
                    touchReader.GetString(2),
                    await touchReader.GetFieldValueAsync<DateTimeOffset>(3, cancellationToken)
                        .ConfigureAwait(false)));
            }
        }

        return
        [
            .. deals.Select(deal => new DecidedDeal(
                deal.Id,
                deal.Amount,
                deal.DecidedAt,
                touches.TryGetValue(deal.Id, out var found) ? found : [])),
        ];
    }

    /// <summary>One deal and everything that touched the people on it.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="id">Which deal.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The deal, or null when it is not this tenant's.</returns>
    public async ValueTask<StoredDeal?> DealAsync(
        string? tenantId,
        Guid id,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await using var closing = connection.ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var closingCommand = command.ConfigureAwait(false);

        command.CommandText = OneDeal;
        Add(command, "id", NpgsqlDbType.Uuid, id);

        decimal amount;
        string? outcome;
        DateTimeOffset decidedAt;

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            amount = reader.GetDecimal(0);

            outcome = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
                ? null
                : reader.GetString(1);

            decidedAt = await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken)
                .ConfigureAwait(false);
        }

        var second = connection.CreateCommand();
        await using var closingSecond = second.ConfigureAwait(false);

        second.CommandText = TouchesOfOneDeal;
        Add(second, "id", NpgsqlDbType.Uuid, id);

        var touchReader = await second.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var closingTouches = touchReader.ConfigureAwait(false);

        var touches = new List<Touch>();

        while (await touchReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            touches.Add(new Touch(
                touchReader.GetGuid(0),
                touchReader.GetString(1),
                await touchReader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken)
                    .ConfigureAwait(false)));
        }

        return new StoredDeal(id, amount, outcome, decidedAt, touches);
    }

    private static async ValueTask<Guid?> NamedAsync(
        NpgsqlConnection connection,
        string name,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        command.CommandText = CampaignByName;
        Add(command, "name", NpgsqlDbType.Text, name);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
    }

    private static async ValueTask<object?> ScalarAsync(
        NpgsqlConnection connection,
        string sql,
        Guid id,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var closing = command.ConfigureAwait(false);

        command.CommandText = sql;
        Add(command, "id", NpgsqlDbType.Uuid, id);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });

    private ValueTask<NpgsqlConnection> OpenAsync(string? tenantId, CancellationToken cancellationToken) =>
        CrmTenantScope.OpenAsync(_source, tenantId, cancellationToken);
}

/// <summary>What one campaign did, before any deal is shared out.</summary>
/// <param name="CampaignId">Its id.</param>
/// <param name="Name">Its identifier.</param>
/// <param name="Label">What to show.</param>
/// <param name="Channel">How it reaches people.</param>
/// <param name="Budget">What was approved.</param>
/// <param name="People">How many distinct people it touched.</param>
/// <param name="Responses">How many of them did something.</param>
/// <param name="Spent">What went.</param>
public sealed record StoredCampaignTotals(
    Guid CampaignId,
    string Name,
    string Label,
    string Channel,
    decimal Budget,
    int People,
    int Responses,
    decimal Spent);

/// <summary>A decided deal and everything that touched the people on it.</summary>
/// <param name="OpportunityId">Which deal.</param>
/// <param name="Amount">What it is worth.</param>
/// <param name="DecidedAt">When somebody decided it.</param>
/// <param name="Touches">What touched it, in no particular order.</param>
public sealed record DecidedDeal(
    Guid OpportunityId,
    decimal Amount,
    DateTimeOffset DecidedAt,
    IReadOnlyList<Touch> Touches);

/// <summary>One deal, decided or not.</summary>
/// <param name="OpportunityId">Which deal.</param>
/// <param name="Amount">What it is worth.</param>
/// <param name="Outcome">How it went, or null when nobody has decided.</param>
/// <param name="DecidedAt">When it entered the stage it is in.</param>
/// <param name="Touches">What touched the people on it.</param>
public sealed record StoredDeal(
    Guid OpportunityId,
    decimal Amount,
    string? Outcome,
    DateTimeOffset DecidedAt,
    IReadOnlyList<Touch> Touches);
