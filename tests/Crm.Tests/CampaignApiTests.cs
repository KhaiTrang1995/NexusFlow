using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// Campaigns, what they touched, what they cost, and who gets the credit.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The arithmetic is proved in <c>AttributionTests</c>, without a database.</strong> What
/// is proved here is the part only a schema can show: that a person touched as a lead and a person
/// touched as a contact are the same person, that the model a caller named comes back with the
/// number, and that a report says how much of what it considered it could not attribute.
/// </para>
/// <para>
/// <strong>The gap between considered and attributed is the honest part.</strong> Most deals in
/// most organisations are not influenced by a campaign. A report that closed that gap would be
/// inventing influence, so it is reported instead.
/// </para>
/// </remarks>
public sealed class CampaignApiTests
{
    private const string Campaigns = "/api/v1/crm/campaigns";
    private const string Touches = "/api/v1/crm/campaigns/touches";
    private const string Costs = "/api/v1/crm/campaigns/costs";
    private const string Performance = "/api/v1/crm/campaigns/performance";
    private const string Attributions = "/api/v1/crm/campaigns/attribution";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A campaign says how long it runs, so a typo in the dates is visible.</summary>
    [Fact]
    public async Task ACampaignSaysHowLongItRuns()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var defined = await CampaignAsync(app, "spring_webinar", CampaignChannel.Webinar);

        defined.Days.ShouldBe(31, "a month inclusive of both ends.");
    }

    /// <summary>A campaign that ends before it starts is refused.</summary>
    [Fact]
    public async Task ACampaignThatEndsBeforeItStartsIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await app.PostAsync(
            Campaigns,
            new DefineCampaign(
                "backwards", "Backwards", CampaignChannel.Email,
                new DateOnly(2026, 6, 30), new DateOnly(2026, 6, 1), 1_000m),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.campaign_ends_before_it_starts");
    }

    /// <summary>A replayed batch is told it is a replay rather than doubling the numbers.</summary>
    /// <remarks>
    /// Loading a batch twice is the ordinary case for a marketing pipeline, and an open is the
    /// denominator of every rate in the report.
    /// </remarks>
    [Fact]
    public async Task AReplayedTouchIsRecognisedRatherThanCountedTwice()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var lead = await app.Crm.LeadAsync(CrmTokens.NorthwindTenant, Cancellation);

        await CampaignAsync(app, "spring_webinar", CampaignChannel.Webinar);

        var at = new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero);

        var first = await TouchAsync(
            app, new RecordTouch("spring_webinar", lead, null, TouchKind.Opened, at));

        first.AlreadyKnown.ShouldBeFalse();

        var second = await TouchAsync(
            app, new RecordTouch("spring_webinar", lead, null, TouchKind.Opened, at));

        second.AlreadyKnown.ShouldBeTrue();
        second.TouchId.ShouldBe(first.TouchId, "the row on file, not the one that was not written.");

        var rows = await app.Crm.ScalarAsTenantAsync<long>(
            CrmTokens.NorthwindTenant,
            "SELECT count(*) FROM campaign_touch WHERE lead_id = @id",
            Cancellation,
            ("id", (object?)lead));

        rows.ShouldBe(1, "a nullable column in a unique constraint is where duplicates hide.");
    }

    /// <summary>A touch reaching nobody, or two people, is refused.</summary>
    [Fact]
    public async Task ATouchReachesOneOrTheOther()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var lead = await app.Crm.LeadAsync(CrmTokens.NorthwindTenant, Cancellation);

        await CampaignAsync(app, "spring_webinar", CampaignChannel.Webinar);

        var neither = await app.PostAsync(
            Touches,
            new RecordTouch("spring_webinar", null, null, TouchKind.Sent, DateTimeOffset.UtcNow),
            CrmTokens.Northwind,
            idempotencyKey: Guid.NewGuid().ToString("N"));

        neither.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(neither)).GetProperty("code").GetString()
            .ShouldBe("crm.campaign_touch_target");

        var both = await app.PostAsync(
            Touches,
            new RecordTouch(
                "spring_webinar", lead, Guid.NewGuid(), TouchKind.Sent, DateTimeOffset.UtcNow),
            CrmTokens.Northwind,
            idempotencyKey: Guid.NewGuid().ToString("N"));

        both.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>Spend accrues as a ledger and says when it has passed the budget.</summary>
    /// <remarks>
    /// Reported rather than refused: the money has already gone, and a ledger that would not
    /// record it is one somebody keeps in a spreadsheet instead.
    /// </remarks>
    [Fact]
    public async Task SpendAccruesAndSaysWhenItHasPassedTheBudget()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await CampaignAsync(app, "spring_webinar", CampaignChannel.Webinar, budget: 1_000m);

        var first = await CostAsync(app, "spring_webinar", 600m);

        first.SpentToDate.ShouldBe(600m);
        first.OverBudget.ShouldBeFalse();

        var second = await CostAsync(app, "spring_webinar", 700m);

        second.Ordinal.ShouldBe(first.Ordinal + 1);
        second.SpentToDate.ShouldBe(1_300m);
        second.OverBudget.ShouldBeTrue();
    }

    /// <summary>A negative spend is refused; a correction is a conversation.</summary>
    [Fact]
    public async Task ANegativeSpendIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await CampaignAsync(app, "spring_webinar", CampaignChannel.Webinar);

        var response = await app.PostAsync(
            Costs,
            new RecordCampaignCost(
                "spring_webinar", new DateOnly(2026, 6, 10), -50m, "Refund"),
            CrmTokens.NorthwindManager,
            idempotencyKey: Guid.NewGuid().ToString("N"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.campaign_spend_not_positive");
    }

    /// <summary>
    /// A person touched as a lead and again as a contact is one person on one deal.
    /// </summary>
    /// <remarks>
    /// <strong>The thing only a schema can show.</strong> Somebody is a lead before they convert
    /// and a contact afterwards, and a campaign that reached them in the first life influenced the
    /// deal that came out of the second. A build that only followed the contact would report the
    /// entire top of the funnel as having influenced nothing.
    /// </remarks>
    [Fact]
    public async Task ACampaignThatTouchedThemAsALeadInfluencedTheDealTheyBecame()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await CampaignAsync(app, "spring_webinar", CampaignChannel.Webinar);
        await CampaignAsync(app, "autumn_event", CampaignChannel.Event);

        // Reached as a lead, ninety days before the deal was decided.
        await TouchAsync(
            app,
            new RecordTouch(
                "spring_webinar", world.Lead, null, TouchKind.Attended, world.Decided.AddDays(-90)));

        // Reached again as a contact, two days before.
        await TouchAsync(
            app,
            new RecordTouch(
                "autumn_event", null, world.Contact, TouchKind.Responded, world.Decided.AddDays(-2)));

        var attribution = await AttributionAsync(
            app, world.Opportunity, AttributionModel.FirstTouch);

        attribution.Model.ShouldBe("FirstTouch");
        attribution.Credits.Count.ShouldBe(2, "both lives are the same person.");
        attribution.Credits[0].Campaign.ShouldBe(
            "spring_webinar", "the lead touch came first and first touch gives it everything.");
        attribution.Credits[0].Amount.ShouldBe(attribution.Amount);
    }

    /// <summary>A touch after the deal was decided is counted out and said out loud.</summary>
    [Fact]
    public async Task ATouchAfterTheDecisionIsExcludedAndReported()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await CampaignAsync(app, "spring_webinar", CampaignChannel.Webinar);
        await CampaignAsync(app, "retention", CampaignChannel.Email);

        await TouchAsync(
            app,
            new RecordTouch(
                "spring_webinar", null, world.Contact, TouchKind.Attended, world.Decided.AddDays(-30)));

        await TouchAsync(
            app,
            new RecordTouch(
                "retention", null, world.Contact, TouchKind.Sent, world.Decided.AddDays(30)));

        var attribution = await AttributionAsync(
            app, world.Opportunity, AttributionModel.LastTouch);

        attribution.TouchesAfterTheDecision.ShouldBe(1);
        attribution.Credits.ShouldNotContain(credit => credit.Campaign == "retention");
        attribution.Credits[0].Campaign.ShouldBe("spring_webinar");
    }

    /// <summary>A deal nobody has decided cannot be shared out.</summary>
    [Fact]
    public async Task ADealNobodyHasDecidedCannotBeSharedOut()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app, decided: false);

        var response = await app.PostAsync(
            Attributions,
            new ReadDealAttribution(world.Opportunity, AttributionModel.Linear),
            CrmTokens.Northwind,
            idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.campaign_deal_open");
    }

    /// <summary>The report names the model and shows what it could not attribute.</summary>
    /// <remarks>
    /// Two deals are decided; one was touched and one was not. A report whose attributed total
    /// equalled its considered total would be inventing influence for the second.
    /// </remarks>
    [Fact]
    public async Task TheReportNamesTheModelAndShowsWhatItCouldNotAttribute()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var touched = await WorldAsync(app);
        await WorldAsync(app, version: 2);

        await CampaignAsync(app, "spring_webinar", CampaignChannel.Webinar);
        await CostAsync(app, "spring_webinar", 5_000m);

        await TouchAsync(
            app,
            new RecordTouch(
                "spring_webinar", null, touched.Contact, TouchKind.Attended,
                touched.Decided.AddDays(-30)));

        var report = await PerformanceAsync(
            app, new ReadCampaignPerformance(AttributionModel.Linear, null, null));

        report.Model.ShouldBe("Linear");
        report.DealsConsidered.ShouldBe(2);
        report.AmountAttributed.ShouldBeLessThan(
            report.AmountConsidered, "a deal nothing touched is not attributable.");

        var webinar = report.Campaigns.Single(row => row.Campaign == "spring_webinar");

        webinar.InfluencedDeals.ShouldBe(1);
        webinar.AttributedAmount.ShouldBe(touched.Amount);
        webinar.People.ShouldBe(1);
        webinar.Responses.ShouldBe(1, "attending is something the recipient did.");
        webinar.Spent.ShouldBe(5_000m);
        webinar.Return.ShouldNotBeNull();
    }

    /// <summary>A send is not a response, and a campaign that reached nobody has no rate.</summary>
    /// <remarks>
    /// A rate whose numerator and denominator are both sends is one, for every campaign, for ever
    /// — and a campaign that reached nobody reporting zero sorts below one that reached a thousand
    /// people and converted one.
    /// </remarks>
    [Fact]
    public async Task ASendIsNotAResponseAndAnUntouchedCampaignHasNoRate()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var lead = await app.Crm.LeadAsync(CrmTokens.NorthwindTenant, Cancellation);

        await CampaignAsync(app, "sent_only", CampaignChannel.Email);
        await CampaignAsync(app, "reached_nobody", CampaignChannel.Paid);

        await TouchAsync(
            app,
            new RecordTouch(
                "sent_only", lead, null, TouchKind.Sent,
                new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero)));

        var report = await PerformanceAsync(
            app, new ReadCampaignPerformance(AttributionModel.Linear, null, null));

        var sent = report.Campaigns.Single(row => row.Campaign == "sent_only");

        sent.People.ShouldBe(1);
        sent.Responses.ShouldBe(0, "a send is a thing the sender did.");
        sent.ResponseRate.ShouldBe(0);
        sent.CostPerResponse.ShouldBeNull("nothing was spent and nobody responded.");

        var nobody = report.Campaigns.Single(row => row.Campaign == "reached_nobody");

        nobody.People.ShouldBe(0);
        nobody.ResponseRate.ShouldBeNull("no rate, rather than a zero that sorts it below a bad one.");
    }

    /// <summary>The window is inclusive of its last day.</summary>
    /// <remarks>
    /// A window written as a closed interval on a timestamp drops everything decided after
    /// midnight on its final day, which is most of it.
    /// </remarks>
    [Fact]
    public async Task TheWindowIncludesItsLastDay()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        var day = DateOnly.FromDateTime(world.Decided.UtcDateTime);

        var report = await PerformanceAsync(
            app, new ReadCampaignPerformance(AttributionModel.Linear, day, day));

        report.DealsConsidered.ShouldBe(1, "the deal was decided during the day asked for.");
    }

    /// <summary>Declaring a campaign is administrative.</summary>
    [Fact]
    public async Task DeclaringACampaignIsAdministrative()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        (await app.PostAsync(
            Campaigns,
            new DefineCampaign(
                "rep_wrote_this", "Mine", CampaignChannel.Email,
                new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), 0m),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>A touch naming another tenant's campaign is not found.</summary>
    [Fact]
    public async Task ATouchNamingACampaignThisTenantDoesNotHaveIsNotFound()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var lead = await app.Crm.LeadAsync(CrmTokens.NorthwindTenant, Cancellation);

        var response = await app.PostAsync(
            Touches,
            new RecordTouch("never_declared", lead, null, TouchKind.Sent, DateTimeOffset.UtcNow),
            CrmTokens.Northwind,
            idempotencyKey: Guid.NewGuid().ToString("N"));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.campaign_person_not_found");
    }

    // ------------------------------------------------------------------------------- fixtures

    private sealed record World(
        Guid Lead, Guid Contact, Guid Opportunity, decimal Amount, DateTimeOffset Decided);

    private static async Task<CampaignDefined> CampaignAsync(
        CrmApplication app,
        string name,
        CampaignChannel channel,
        decimal budget = 0m)
    {
        var response = await app.PostAsync(
            Campaigns,
            new DefineCampaign(
                name, name, channel,
                new DateOnly(2026, 6, 1), new DateOnly(2026, 7, 1), budget),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "declaring '" + name + "' failed.");

        return await CrmApplication.ReadAsync<CampaignDefined>(response);
    }

    private static async Task<TouchRecorded> TouchAsync(CrmApplication app, RecordTouch touch)
    {
        var response = await app.PostAsync(
            Touches, touch, CrmTokens.Northwind, idempotencyKey: Guid.NewGuid().ToString("N"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<TouchRecorded>(response);
    }

    private static async Task<CampaignCostRecorded> CostAsync(
        CrmApplication app, string campaign, decimal amount)
    {
        var response = await app.PostAsync(
            Costs,
            new RecordCampaignCost(campaign, new DateOnly(2026, 6, 10), amount, "Venue"),
            CrmTokens.NorthwindManager,
            idempotencyKey: Guid.NewGuid().ToString("N"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<CampaignCostRecorded>(response);
    }

    private static async Task<CampaignReport> PerformanceAsync(
        CrmApplication app, ReadCampaignPerformance ask)
    {
        var response = await app.PostAsync(
            Performance, ask, CrmTokens.Northwind, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<CampaignReport>(response);
    }

    private static async Task<DealAttribution> AttributionAsync(
        CrmApplication app, Guid opportunity, AttributionModel model)
    {
        var response = await app.PostAsync(
            Attributions,
            new ReadDealAttribution(opportunity, model),
            CrmTokens.Northwind,
            idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<DealAttribution>(response);
    }

    /// <summary>A lead who converted into a contact and a decided deal.</summary>
    private static async Task<World> WorldAsync(
        CrmApplication app, bool decided = true, int version = 1)
    {
        var account = await app.Crm.AccountAsync(
            CrmTokens.NorthwindTenant, Lifecycle.Customer, Cancellation);

        var contact = await app.Crm.ContactAsync(CrmTokens.NorthwindTenant, account, Cancellation);
        // A version per call. Two worlds in one test would otherwise collide on the process
        // definition's uniqueness, which is a fixture problem and not the one under test.
        var (_, stage) = await app.Crm.ProcessAsync(
            CrmTokens.NorthwindTenant, version, version == 1, Cancellation);

        var opportunity = await app.Crm.OpportunityAsync(
            CrmTokens.NorthwindTenant, account, contact, stage, Cancellation);

        var lead = await app.Crm.LeadAsync(CrmTokens.NorthwindTenant, Cancellation);

        await app.Crm.AsTenantAsync(
            CrmTokens.NorthwindTenant,
            """
            UPDATE lead
            SET status = 'Converted',
                converted_account_id = @account,
                converted_contact_id = @contact,
                converted_opportunity_id = @deal,
                converted_at = now()
            WHERE lead_id = @id
            """,
            Cancellation,
            ("account", (object?)account),
            ("contact", contact),
            ("deal", opportunity),
            ("id", lead));

        // Decided a week ago rather than now, so a touch "two days before the decision" is still
        // in the past and a test does not depend on the clock crossing a boundary mid-run.
        var at = DateTimeOffset.UtcNow.AddDays(-7);

        if (decided)
        {
            await app.Crm.AsTenantAsync(
                CrmTokens.NorthwindTenant,
                "UPDATE opportunity SET outcome = 'Won', stage_entered_at = @at WHERE opportunity_id = @id",
                Cancellation,
                ("at", (object?)at),
                ("id", opportunity));
        }

        return new World(lead, contact, opportunity, 50_000m, at);
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
