using FlowX;

namespace Crm;

// -------------------------------------------------------------------------------- the vocabulary

/// <summary>How a campaign reaches people.</summary>
public enum CampaignChannel
{
    /// <summary>An inbox.</summary>
    Email,

    /// <summary>A room.</summary>
    Event,

    /// <summary>A room nobody travelled to.</summary>
    Webinar,

    /// <summary>Bought attention.</summary>
    Paid,

    /// <summary>Something written that people came to.</summary>
    Content,

    /// <summary>Somebody telephoning strangers.</summary>
    Outbound,

    /// <summary>Somebody else's audience.</summary>
    Partner,
}

/// <summary>What happened between a campaign and a person.</summary>
/// <remarks>
/// Ordered by how much it means. <c>Sent</c> is a thing the sender did; everything below it is a
/// thing the recipient did, and only those are worth a rate.
/// </remarks>
public enum TouchKind
{
    /// <summary>It went out.</summary>
    Sent,

    /// <summary>They opened it.</summary>
    Opened,

    /// <summary>They followed it.</summary>
    Clicked,

    /// <summary>They turned up.</summary>
    Attended,

    /// <summary>They replied.</summary>
    Responded,
}

// -------------------------------------------------------------------------------- what is asked

/// <summary>Declares a campaign.</summary>
/// <param name="Name">What to ask for it by.</param>
/// <param name="Label">What to show a person.</param>
/// <param name="Channel">How it reaches people.</param>
/// <param name="StartsOn">The first day.</param>
/// <param name="EndsOn">The last.</param>
/// <param name="Budget">What was approved. What was spent is a separate ledger.</param>
public sealed record DefineCampaign(
    string Name,
    string Label,
    CampaignChannel Channel,
    DateOnly StartsOn,
    DateOnly EndsOn,
    decimal Budget);

/// <summary>The campaign was declared.</summary>
/// <param name="CampaignId">Its id.</param>
/// <param name="Days">How long it runs, inclusive of both ends.</param>
public sealed record CampaignDefined(Guid CampaignId, int Days);

/// <summary>Records that a campaign reached somebody.</summary>
/// <param name="Campaign">Which campaign, by name.</param>
/// <param name="LeadId">Who, before they converted.</param>
/// <param name="ContactId">Who, afterwards. Exactly one of the two.</param>
/// <param name="Kind">What happened.</param>
/// <param name="TouchedAt">When. Given rather than taken from the clock, because a batch is loaded late.</param>
public sealed record RecordTouch(
    string Campaign,
    Guid? LeadId,
    Guid? ContactId,
    TouchKind Kind,
    DateTimeOffset TouchedAt);

/// <summary>The touch was recorded.</summary>
/// <param name="TouchId">Its id.</param>
/// <param name="AlreadyKnown">
/// Whether this exact touch was already on file. <strong>Said rather than swallowed</strong> — a
/// pipeline replaying a batch is the ordinary case, and a caller that could not tell a replay from
/// a new touch would either double every open or stop loading.
/// </param>
public sealed record TouchRecorded(Guid TouchId, bool AlreadyKnown);

/// <summary>Records what a campaign actually cost.</summary>
/// <param name="Campaign">Which campaign, by name.</param>
/// <param name="IncurredOn">When the money went.</param>
/// <param name="Amount">How much.</param>
/// <param name="Note">What for.</param>
public sealed record RecordCampaignCost(
    string Campaign,
    DateOnly IncurredOn,
    decimal Amount,
    string Note);

/// <summary>The spend was recorded.</summary>
/// <param name="CampaignId">Which campaign.</param>
/// <param name="Ordinal">Where it sits in the ledger.</param>
/// <param name="SpentToDate">Everything spent so far.</param>
/// <param name="OverBudget">
/// Whether the spend has passed what was approved. Reported rather than refused: the money has
/// already gone, and a ledger that would not record it is one somebody keeps in a spreadsheet
/// instead.
/// </param>
public sealed record CampaignCostRecorded(
    Guid CampaignId,
    int Ordinal,
    decimal SpentToDate,
    bool OverBudget);

/// <summary>Asks how the campaigns did.</summary>
/// <param name="Model">Which model shares the credit out.</param>
/// <param name="From">The earliest deal to count, by when it was decided.</param>
/// <param name="To">The latest.</param>
public sealed record ReadCampaignPerformance(
    AttributionModel Model,
    DateOnly? From,
    DateOnly? To);

/// <summary>Asks who influenced one deal.</summary>
/// <param name="OpportunityId">Which deal.</param>
/// <param name="Model">Which model shares the credit out.</param>
public sealed record ReadDealAttribution(Guid OpportunityId, AttributionModel Model);

/// <summary>What the campaign capabilities are given, once the flow has read the caller.</summary>
/// <param name="Define">The campaign to declare, when that is what was asked.</param>
/// <param name="Touch">The touch, when that is what was asked.</param>
/// <param name="Cost">The spend, when that is what was asked.</param>
/// <param name="Performance">The report, when that is what was asked.</param>
/// <param name="Deal">The one deal, when that is what was asked.</param>
/// <param name="UserId">Who is asking, from their claims and never from the body.</param>
public sealed record CampaignBy(
    DefineCampaign? Define,
    RecordTouch? Touch,
    RecordCampaignCost? Cost,
    ReadCampaignPerformance? Performance,
    ReadDealAttribution? Deal,
    string UserId);

// ------------------------------------------------------------------------------- what comes back

/// <summary>How one campaign did.</summary>
/// <param name="CampaignId">Its id.</param>
/// <param name="Campaign">Its identifier.</param>
/// <param name="Label">What to show.</param>
/// <param name="Channel">How it reaches people.</param>
/// <param name="People">How many distinct people it touched.</param>
/// <param name="Responses">
/// How many of them did something. Only a recipient's action counts — a send is a thing the sender
/// did, and a response rate whose denominator and numerator are both sends is one.
/// </param>
/// <param name="ResponseRate">
/// Responses over people, or null when it touched nobody. <strong>Null and not zero</strong>: a
/// campaign that reached nobody has no rate, and reporting zero puts it below one that reached a
/// thousand people and converted one.
/// </param>
/// <param name="InfluencedDeals">How many decided deals it touched before they were decided.</param>
/// <param name="AttributedAmount">What it was given of them, under the model named in the report.</param>
/// <param name="Budget">What was approved.</param>
/// <param name="Spent">What went.</param>
/// <param name="CostPerResponse">Spend over responses, or null when there were none.</param>
/// <param name="Return">
/// Attributed amount over spend, or null when nothing was spent. Null rather than infinity: a
/// campaign that cost nothing has no return, and a very large number is one somebody screenshots.
/// </param>
public sealed record CampaignPerformance(
    Guid CampaignId,
    string Campaign,
    string Label,
    string Channel,
    int People,
    int Responses,
    double? ResponseRate,
    int InfluencedDeals,
    decimal AttributedAmount,
    decimal Budget,
    decimal Spent,
    decimal? CostPerResponse,
    double? Return);

/// <summary>How the campaigns did.</summary>
/// <param name="Model">
/// Which model shared the credit out. <strong>Carried in the answer, always</strong> — a number
/// quoted without it is one two departments can argue about for a quarter while both are right.
/// </param>
/// <param name="Campaigns">The campaigns, most credit first.</param>
/// <param name="DealsConsidered">How many decided deals were shared out.</param>
/// <param name="AmountConsidered">What those deals were worth in total.</param>
/// <param name="AmountAttributed">
/// What was actually shared out. <strong>Lower than the total is the ordinary case</strong> and is
/// said rather than hidden: a deal nothing touched is not attributable, and a report that made the
/// two agree would be inventing influence.
/// </param>
public sealed record CampaignReport(
    string Model,
    IReadOnlyList<CampaignPerformance> Campaigns,
    int DealsConsidered,
    decimal AmountConsidered,
    decimal AmountAttributed);

/// <summary>Who influenced one deal.</summary>
/// <param name="OpportunityId">Which deal.</param>
/// <param name="Model">Which model shared it out.</param>
/// <param name="Amount">What the deal is worth.</param>
/// <param name="DecidedAt">When it was decided, and so where the cutoff falls.</param>
/// <param name="Credits">What each campaign was given, largest first.</param>
/// <param name="TouchesAfterTheDecision">
/// How many touches landed after the deal was decided and were therefore dropped. Reported because
/// a campaign whose entire claim is post-sale email is one somebody should be told about.
/// </param>
public sealed record DealAttribution(
    Guid OpportunityId,
    string Model,
    decimal Amount,
    DateTimeOffset DecidedAt,
    IReadOnlyList<AttributedCredit> Credits,
    int TouchesAfterTheDecision);

// ------------------------------------------------------------------------------- what can go wrong

/// <summary>Refusals the campaign surface can produce.</summary>
public static class CampaignErrors
{
    /// <summary>The campaign is not one this tenant has.</summary>
    /// <param name="name">What was asked for.</param>
    /// <returns>The refusal.</returns>
    public static Error CampaignNotFound(string name) =>
        new(
            "crm.campaign_not_found",
            $"'{name}' is not a campaign of this tenant.",
            ErrorCategory.NotFound);

    /// <summary>The campaign ends before it starts.</summary>
    /// <returns>The refusal.</returns>
    public static Error EndsBeforeItStarts() =>
        new(
            "crm.campaign_ends_before_it_starts",
            "A campaign cannot end before it begins.",
            ErrorCategory.Validation);

    /// <summary>The touch reaches nobody, or two people.</summary>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// A touch with neither is a row that can never be attributed to a deal; a touch with both is
    /// one that would be counted twice, once down each path.
    /// </remarks>
    public static Error TouchReachesOneOrTheOther() =>
        new(
            "crm.campaign_touch_target",
            "A touch reaches a lead or a contact, never both and never neither.",
            ErrorCategory.Validation);

    /// <summary>The person the touch names is not one this tenant has.</summary>
    /// <returns>The refusal.</returns>
    public static Error PersonNotFound() =>
        new(
            "crm.campaign_person_not_found",
            "The lead or contact this touch names is not one of this tenant's.",
            ErrorCategory.NotFound);

    /// <summary>The spend is not a positive amount.</summary>
    /// <param name="amount">What was given.</param>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// A negative row would be a correction, and a ledger that accepts corrections is one where
    /// the spend on any given day depends on which rows somebody chose to net off.
    /// </remarks>
    public static Error SpendIsNotPositive(decimal amount) =>
        new(
            "crm.campaign_spend_not_positive",
            $"'{amount}' is not an amount that was spent. A correction is a conversation, not a row.",
            ErrorCategory.Validation);

    /// <summary>The deal is not one this tenant has.</summary>
    /// <param name="id">What was asked for.</param>
    /// <returns>The refusal.</returns>
    public static Error DealNotFound(Guid id) =>
        new(
            "crm.campaign_deal_not_found",
            $"'{id}' is not an opportunity of this tenant.",
            ErrorCategory.NotFound);

    /// <summary>The deal has not been decided, so there is nothing to share out.</summary>
    /// <param name="id">Which deal.</param>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// Attribution needs a cutoff, and an open deal has none. Sharing out a deal that has not
    /// happened yet gives credit for a decision nobody has taken.
    /// </remarks>
    public static Error DealIsStillOpen(Guid id) =>
        new(
            "crm.campaign_deal_open",
            $"'{id}' has not been decided. Credit for a deal nobody has won yet is credit for a " +
            "decision nobody has taken.",
            ErrorCategory.Conflict);
}

/// <summary>What the campaign surface accepts.</summary>
public static class CampaignLimits
{
    /// <summary>The most campaigns one report answers with.</summary>
    public const int MaxCampaigns = 200;

    /// <summary>The most decided deals one report shares out.</summary>
    /// <remarks>
    /// Bounded because every deal in the window is shared out in the application, over its own
    /// touches. A report over a decade would be a scan and a report nobody waited for.
    /// </remarks>
    public const int MaxDeals = 2_000;
}
