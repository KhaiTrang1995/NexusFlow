using FlowX;

namespace Crm;

/// <summary>
/// Declares a campaign.
/// </summary>
[Capability("crm.campaign.define", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.admin")]
public sealed class DefineCrmCampaign : ICapability<CampaignBy, CampaignDefined>
{
    private readonly CampaignStore _campaigns;

    /// <summary>Creates the capability.</summary>
    /// <param name="campaigns">Writes the campaign.</param>
    /// <exception cref="ArgumentNullException"><paramref name="campaigns"/> is null.</exception>
    public DefineCrmCampaign(CampaignStore campaigns)
    {
        ArgumentNullException.ThrowIfNull(campaigns);

        _campaigns = campaigns;
    }

    /// <inheritdoc />
    public async ValueTask<Result<CampaignDefined>> ExecuteAsync(
        CampaignBy input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (input.Define is not { } define)
        {
            return Result.Fail<CampaignDefined>(BulkErrors.AskForOneOrTheOther());
        }

        if (!CustomValues.IsUsableName(define.Name))
        {
            return Result.Fail<CampaignDefined>(CustomSchemaErrors.NameIsNotUsable(define.Name));
        }

        if (define.EndsOn < define.StartsOn)
        {
            return Result.Fail<CampaignDefined>(CampaignErrors.EndsBeforeItStarts());
        }

        return await _campaigns
                .SaveCampaignAsync(ctx.TenantId, ctx.NewId(), define, ctx.UtcNow, ct)
                .ConfigureAwait(false) is { } saved
            ? Result.Ok(new CampaignDefined(
                saved, define.EndsOn.DayNumber - define.StartsOn.DayNumber + 1))
            : Result.Fail<CampaignDefined>(CustomSchemaErrors.NameIsTaken(define.Name));
    }
}

/// <summary>
/// Records that a campaign reached somebody.
/// </summary>
/// <remarks>
/// <strong>A replay is told it is a replay.</strong> Loading a batch twice is the ordinary case for
/// a marketing pipeline, and a caller that could not tell a replay from a new touch would either
/// double every open — the denominator of every rate in the report — or stop loading.
/// </remarks>
[Capability("crm.campaign.touch", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.write")]
public sealed class RecordCrmCampaignTouch : ICapability<CampaignBy, TouchRecorded>
{
    private readonly CampaignStore _campaigns;

    /// <summary>Creates the capability.</summary>
    /// <param name="campaigns">Writes the touch.</param>
    /// <exception cref="ArgumentNullException"><paramref name="campaigns"/> is null.</exception>
    public RecordCrmCampaignTouch(CampaignStore campaigns)
    {
        ArgumentNullException.ThrowIfNull(campaigns);

        _campaigns = campaigns;
    }

    /// <inheritdoc />
    public async ValueTask<Result<TouchRecorded>> ExecuteAsync(
        CampaignBy input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (input.Touch is not { } touch)
        {
            return Result.Fail<TouchRecorded>(BulkErrors.AskForOneOrTheOther());
        }

        // Neither is a row that can never be attributed to a deal; both is a row that would be
        // counted twice, once down each path from the deal back to the person.
        if (touch.LeadId is null == (touch.ContactId is null))
        {
            return Result.Fail<TouchRecorded>(CampaignErrors.TouchReachesOneOrTheOther());
        }

        return await _campaigns.SaveTouchAsync(ctx.TenantId, ctx.NewId(), touch, ct)
                .ConfigureAwait(false) is { } saved
            ? Result.Ok(saved)
            : Result.Fail<TouchRecorded>(CampaignErrors.PersonNotFound());
    }
}

/// <summary>
/// Records what a campaign actually cost.
/// </summary>
/// <remarks>
/// <strong>Spend over budget is reported, not refused.</strong> The money has already gone. A
/// ledger that would not record it is one somebody keeps in a spreadsheet instead, and then the
/// return this build computes is a return on a number that was never true.
/// </remarks>
[Capability("crm.campaign.cost", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.admin")]
public sealed class RecordCrmCampaignCost : ICapability<CampaignBy, CampaignCostRecorded>
{
    private readonly CampaignStore _campaigns;

    /// <summary>Creates the capability.</summary>
    /// <param name="campaigns">Writes the spend.</param>
    /// <exception cref="ArgumentNullException"><paramref name="campaigns"/> is null.</exception>
    public RecordCrmCampaignCost(CampaignStore campaigns)
    {
        ArgumentNullException.ThrowIfNull(campaigns);

        _campaigns = campaigns;
    }

    /// <inheritdoc />
    public async ValueTask<Result<CampaignCostRecorded>> ExecuteAsync(
        CampaignBy input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (input.Cost is not { } cost)
        {
            return Result.Fail<CampaignCostRecorded>(BulkErrors.AskForOneOrTheOther());
        }

        if (cost.Amount <= 0)
        {
            return Result.Fail<CampaignCostRecorded>(
                CampaignErrors.SpendIsNotPositive(cost.Amount));
        }

        return await _campaigns.SaveCostAsync(ctx.TenantId, cost, input.UserId, ctx.UtcNow, ct)
                .ConfigureAwait(false) is { } saved
            ? Result.Ok(saved)
            : Result.Fail<CampaignCostRecorded>(CampaignErrors.CampaignNotFound(cost.Campaign));
    }
}

/// <summary>
/// How the campaigns did, under a model somebody named.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The model is carried in the answer.</strong> Credit is the output of a model, not a
/// fact about a deal, and a number quoted without the model that produced it is one two
/// departments can argue about for a quarter while both are right.
/// </para>
/// <para>
/// <strong>What was attributed is reported next to what was considered.</strong> They will not
/// agree, because a deal nothing touched is not attributable — and a report that made them agree
/// would be inventing influence to close a gap.
/// </para>
/// </remarks>
[Capability("crm.campaign.performance", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.read",
    Idempotent = true)]
public sealed class ReadCrmCampaignPerformance : ICapability<CampaignBy, CampaignReport>
{
    private readonly CampaignStore _campaigns;

    /// <summary>Creates the capability.</summary>
    /// <param name="campaigns">Reads the campaigns, the touches and the deals.</param>
    /// <exception cref="ArgumentNullException"><paramref name="campaigns"/> is null.</exception>
    public ReadCrmCampaignPerformance(CampaignStore campaigns)
    {
        ArgumentNullException.ThrowIfNull(campaigns);

        _campaigns = campaigns;
    }

    /// <inheritdoc />
    public async ValueTask<Result<CampaignReport>> ExecuteAsync(
        CampaignBy input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (input.Performance is not { } ask)
        {
            return Result.Fail<CampaignReport>(BulkErrors.AskForOneOrTheOther());
        }

        var from = ask.From is { } start
            ? new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : DateTimeOffset.MinValue;

        // The day after the last one asked for. A window written as a closed interval on a
        // timestamp drops everything decided after midnight on its final day, which is most of it.
        var to = ask.To is { } end
            ? new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : DateTimeOffset.MaxValue;

        var totals = await _campaigns.TotalsAsync(ctx.TenantId, ct).ConfigureAwait(false);
        var deals = await _campaigns.DecidedAsync(ctx.TenantId, from, to, ct).ConfigureAwait(false);

        var attributed = new Dictionary<Guid, decimal>();
        var influenced = new Dictionary<Guid, int>();
        var shared = 0m;

        foreach (var deal in deals)
        {
            var credits = Attribution.Split(ask.Model, deal.Touches, deal.Amount, deal.DecidedAt);

            foreach (var credit in credits)
            {
                attributed[credit.CampaignId] =
                    attributed.GetValueOrDefault(credit.CampaignId) + credit.Amount;

                influenced[credit.CampaignId] = influenced.GetValueOrDefault(credit.CampaignId) + 1;
                shared += credit.Amount;
            }
        }

        var performance = totals
            .Select(row => new CampaignPerformance(
                row.CampaignId,
                row.Name,
                row.Label,
                row.Channel,
                row.People,
                row.Responses,

                // Null and not zero. A campaign that reached nobody has no rate, and reporting a
                // zero sorts it below one that reached a thousand people and converted one.
                row.People == 0 ? null : (double)row.Responses / row.People,
                influenced.GetValueOrDefault(row.CampaignId),
                attributed.GetValueOrDefault(row.CampaignId),
                row.Budget,
                row.Spent,
                row.Responses == 0 ? null : decimal.Round(row.Spent / row.Responses, 4),
                row.Spent == 0 ? null : (double)(attributed.GetValueOrDefault(row.CampaignId) / row.Spent)))
            .OrderByDescending(row => row.AttributedAmount)
            .ThenBy(row => row.Campaign, StringComparer.Ordinal)
            .ToList();

        return Result.Ok(new CampaignReport(
            ask.Model.ToString(),
            performance,
            deals.Count,
            deals.Sum(deal => deal.Amount),
            shared));
    }
}

/// <summary>
/// Who influenced one deal.
/// </summary>
/// <remarks>
/// <strong>Touches after the decision are counted and reported, not silently dropped.</strong> A
/// campaign whose entire claim on a deal is post-sale email is claiming credit for a decision
/// already taken, and the number of touches that fell outside the cutoff is how somebody notices.
/// </remarks>
[Capability("crm.campaign.attribution", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "crm.read",
    Idempotent = true)]
public sealed class ReadCrmDealAttribution : ICapability<CampaignBy, DealAttribution>
{
    private readonly CampaignStore _campaigns;

    /// <summary>Creates the capability.</summary>
    /// <param name="campaigns">Reads the deal and its touches.</param>
    /// <exception cref="ArgumentNullException"><paramref name="campaigns"/> is null.</exception>
    public ReadCrmDealAttribution(CampaignStore campaigns)
    {
        ArgumentNullException.ThrowIfNull(campaigns);

        _campaigns = campaigns;
    }

    /// <inheritdoc />
    public async ValueTask<Result<DealAttribution>> ExecuteAsync(
        CampaignBy input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        if (input.Deal is not { } ask)
        {
            return Result.Fail<DealAttribution>(BulkErrors.AskForOneOrTheOther());
        }

        if (await _campaigns.DealAsync(ctx.TenantId, ask.OpportunityId, ct).ConfigureAwait(false)
            is not { } deal)
        {
            return Result.Fail<DealAttribution>(
                CampaignErrors.DealNotFound(ask.OpportunityId));
        }

        // Attribution needs a cutoff and an open deal has none.
        if (deal.Outcome is null)
        {
            return Result.Fail<DealAttribution>(
                CampaignErrors.DealIsStillOpen(ask.OpportunityId));
        }

        var credits = Attribution.Split(ask.Model, deal.Touches, deal.Amount, deal.DecidedAt);

        return Result.Ok(new DealAttribution(
            deal.OpportunityId,
            ask.Model.ToString(),
            deal.Amount,
            deal.DecidedAt,
            credits,
            deal.Touches.Count(touch => touch.TouchedAt > deal.DecidedAt)));
    }
}
