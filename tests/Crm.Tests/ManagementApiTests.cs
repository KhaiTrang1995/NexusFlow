using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// Who sees whose plans, what an account plan actually contains, and the numbers a leadership team
/// reviews.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Role is a scope, not a permission.</strong> A director asking "how is my organisation
/// doing" and a manager asking the same question both may read; what differs is whose plans are in
/// the total. Without a reporting line the only two answers available are "mine" and "the whole
/// tenant", and a company with nine sales managers gets neither of the ones it wanted.
/// </para>
/// <para>
/// <strong>The line is walked recursively, and a loop in it is the interesting case.</strong> A
/// cycle cannot be prevented by a constraint — a three-person loop is three individually legal
/// rows — so the write refuses the loop it can see and the read is depth-capped against the ones
/// it cannot.
/// </para>
/// </remarks>
public sealed class ManagementApiTests
{
    private const string Periods = "/api/v1/crm/planning/periods";
    private const string Strategies = "/api/v1/crm/planning/strategies";
    private const string Plans = "/api/v1/crm/planning/plans";
    private const string RollUps = "/api/v1/crm/planning/roll-ups";
    private const string Members = "/api/v1/crm/org/members";
    private const string Chart = "/api/v1/crm/org/chart";
    private const string Objectives = "/api/v1/crm/planning/objectives";
    private const string Stakeholders = "/api/v1/crm/planning/stakeholders";
    private const string Risks = "/api/v1/crm/planning/risks";
    private const string Kpis = "/api/v1/crm/kpis";
    private const string Scorecards = "/api/v1/crm/kpis/scorecards";
    private const string Reviews = "/api/v1/crm/kpis/reviews";

    private const string Rep = "rep-northwind-1";
    private const string Manager = "manager-northwind-1";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A representative sees their own plans; a director sees the organisation's.</summary>
    [Fact]
    public async Task RoleDecidesWhosePlansAreInTheTotal()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await MemberAsync(app, new SetOrgMember(Manager, "Bea Vance", OrgRole.Director, null));
        await MemberAsync(app, new SetOrgMember(Rep, "Ada Rowe", OrgRole.Representative, Manager));

        await StrategyAsync(app, 1_000_000m);
        await PlanAsync(app, "reps_book", world.Account, 400_000m, Rep);
        await PlanAsync(app, "someone_elses", world.Account, 250_000m, "rep-northwind-2");

        (await RollUpAsync(app, CrmTokens.Northwind)).Committed.ShouldBe(
            400_000m, "a representative's roll-up is their own book.");

        (await RollUpAsync(app, CrmTokens.NorthwindManager)).Committed.ShouldBe(
            650_000m, "a director's is the organisation's.");
    }

    /// <summary>A manager's total includes everybody below them, at any depth.</summary>
    /// <remarks>
    /// Two levels rather than one, because a line walked with a join instead of a recursion looks
    /// right until the organisation grows a third layer.
    /// </remarks>
    [Fact]
    public async Task AManagersTotalReachesEveryLevelBelowThem()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await MemberAsync(app, new SetOrgMember(Manager, "Bea Vance", OrgRole.Manager, null));
        await MemberAsync(app, new SetOrgMember(Rep, "Ada Rowe", OrgRole.Manager, Manager));
        await MemberAsync(app, new SetOrgMember("rep-northwind-2", "Cy Okoro", OrgRole.Representative, Rep));

        await StrategyAsync(app, 1_000_000m);
        await PlanAsync(app, "beas_own", world.Account, 100_000m, Manager);
        await PlanAsync(app, "adas_book", world.Account, 200_000m, Rep);
        await PlanAsync(app, "cys_book", world.Account, 300_000m, "rep-northwind-2");

        (await RollUpAsync(app, CrmTokens.NorthwindManager)).Committed.ShouldBe(
            600_000m, "the third layer is what a join instead of a recursion would miss.");

        (await RollUpAsync(app, CrmTokens.Northwind)).Committed.ShouldBe(
            500_000m, "Ada is a manager of one, and sees herself and Cy.");
    }

    /// <summary>Placing somebody so the line loops is refused.</summary>
    [Fact]
    public async Task APlacementThatWouldLoopTheLineIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await WorldAsync(app);

        await MemberAsync(app, new SetOrgMember(Manager, "Bea Vance", OrgRole.Manager, null));
        await MemberAsync(app, new SetOrgMember(Rep, "Ada Rowe", OrgRole.Manager, Manager));

        var response = await app.PostAsync(
            Members,
            new SetOrgMember(Manager, "Bea Vance", OrgRole.Manager, Rep),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.org_line_would_loop");
    }

    /// <summary>A caller who was never placed is refused, not treated as a representative.</summary>
    /// <remarks>
    /// Defaulted, a director whose row was never written would see one plan and conclude their
    /// organisation had stopped selling.
    /// </remarks>
    [Fact]
    public async Task ACallerWhoWasNeverPlacedIsRefusedRatherThanDefaulted()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await WorldAsync(app);
        await StrategyAsync(app, 100_000m);

        var response = await app.PostAsync(
            RollUps, new ReadRollUp("fy26"), CrmTokens.Northwind, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.caller_not_in_organisation");
    }

    /// <summary>An account plan carries objectives, and reports how many are outstanding.</summary>
    [Fact]
    public async Task AnAccountPlanCarriesObjectives()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await DirectorWorldAsync(app);

        await PlanAsync(app, "north_region", world.Account, 400_000m, Rep);

        (await ObjectiveAsync(app, new SetObjective(
            "north_region", 0, "Displace the incumbent in logistics",
            ObjectiveMeasure.Revenue, 250_000m, ObjectiveStatus.InProgress)))
            .Outstanding.ShouldBe(1);

        (await ObjectiveAsync(app, new SetObjective(
            "north_region", 1, "Two executive briefings",
            ObjectiveMeasure.Meetings, 2m, ObjectiveStatus.Achieved)))
            .Outstanding.ShouldBe(1, "an achieved objective is not outstanding.");

        (await ObjectiveAsync(app, new SetObjective(
            "north_region", 0, "Displace the incumbent in logistics",
            ObjectiveMeasure.Revenue, 250_000m, ObjectiveStatus.Achieved)))
            .Outstanding.ShouldBe(0, "writing the same ordinal again replaces it.");
    }

    /// <summary>The relationship map reports how many people are not on side.</summary>
    /// <remarks>
    /// The question that loses large B2B deals is not "what do they need" but "who has not agreed
    /// yet", and a map that only counted names would answer the wrong one.
    /// </remarks>
    [Fact]
    public async Task TheRelationshipMapReportsWhoIsNotOnSide()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await DirectorWorldAsync(app);

        await PlanAsync(app, "north_region", world.Account, 400_000m, Rep);

        var second = await app.Crm.ContactAsync(
            CrmTokens.NorthwindTenant, world.Account, Cancellation);

        await StakeholderAsync(app, new SetStakeholder(
            "north_region", world.Contact, StakeholderRole.Champion, Sentiment.Advocate, 5));

        var mapped = await StakeholderAsync(app, new SetStakeholder(
            "north_region", second, StakeholderRole.Procurement, Sentiment.Opposed, 4));

        mapped.Mapped.ShouldBe(2);
        mapped.Opposed.ShouldBe(1);
    }

    /// <summary>A demand plan has no people to map, and is refused one.</summary>
    [Fact]
    public async Task ADemandPlanHasNoStakeholders()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await DirectorWorldAsync(app);

        (await app.PostAsync(
            Plans,
            new DefinePlan(
                PlanKind.MarketingLead, "fy26", "web_dach", "Web, DACH", Rep,
                Channel: "Web", Segment: "DACH", TargetLeads: 500),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await app.PostAsync(
            Stakeholders,
            new SetStakeholder(
                "web_dach", world.Contact, StakeholderRole.Champion, Sentiment.Advocate, 3),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString().ShouldBe("crm.plan_kind_has_no");
    }

    /// <summary>A closed risk stays on the register and stops counting as open.</summary>
    [Fact]
    public async Task AClosedRiskStaysOnTheRegisterAndStopsCounting()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await DirectorWorldAsync(app);

        await PlanAsync(app, "north_region", world.Account, 400_000m, Rep);

        (await RiskAsync(app, new SetRisk(
            "north_region", 0, "Incumbent has a three-year contract",
            RiskSeverity.High, "Find the break clause", true))).Open.ShouldBe(1);

        (await RiskAsync(app, new SetRisk(
            "north_region", 1, "Budget freeze rumoured",
            RiskSeverity.Critical, "Ask the CFO directly", true))).Open.ShouldBe(2);

        (await RiskAsync(app, new SetRisk(
            "north_region", 0, "Incumbent has a three-year contract",
            RiskSeverity.High, "Break clause found, 90 days", false))).Open.ShouldBe(1);
    }

    /// <summary>A KPI's actual is computed live, and the direction decides what good is.</summary>
    /// <remarks>
    /// Without a direction, a target of five overdue steps and one of five million in pipeline
    /// would be scored the same way — and one of the two answers would be exactly wrong.
    /// </remarks>
    [Fact]
    public async Task TheDirectionDecidesWhatOnTrackMeans()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await DirectorWorldAsync(app);

        await OpportunityAsync(app, world, 250_000m, outcome: null);

        await KpiAsync(app, new DefineKpi(
            "pipeline", "Open pipeline", KpiSource.OpenPipeline, 200_000m,
            KpiDirection.HigherIsBetter));

        await KpiAsync(app, new DefineKpi(
            "thin_pipeline", "Pipeline ceiling", KpiSource.OpenPipeline, 200_000m,
            KpiDirection.LowerIsBetter));

        var card = await ScorecardAsync(app);

        card.Kpis.Single(k => k.Name == "pipeline").Status.ShouldBe("OnTrack");
        card.Kpis.Single(k => k.Name == "pipeline").Actual.ShouldBe(250_000m);

        card.Kpis.Single(k => k.Name == "thin_pipeline").Status.ShouldBe(
            "OffTrack", "the same number against the same target, the other way round.");

        card.Kpis[0].Status.ShouldBe(
            "OffTrack", "a scorecard is walked by what is wrong with it.");
    }

    /// <summary>A review records what the number was when the commentary was written.</summary>
    /// <remarks>
    /// This is the one place an actual is stored, and the reason is what the figure claims: a
    /// plan's cached actual claims to be current and is wrong between refreshes; a minute claims
    /// only to be what was said, and a minute that silently updated itself would not be one.
    /// </remarks>
    [Fact]
    public async Task AReviewKeepsTheNumberAsItWasWhenItWasWritten()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await DirectorWorldAsync(app);

        await OpportunityAsync(app, world, 100_000m, outcome: null);

        await KpiAsync(app, new DefineKpi(
            "pipeline", "Open pipeline", KpiSource.OpenPipeline, 200_000m,
            KpiDirection.HigherIsBetter));

        var response = await app.PostAsync(
            Reviews,
            new ReviewKpi("pipeline", "fy26", "Thin. Two deals slipped out of the quarter."),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var reviewed = await CrmApplication.ReadAsync<KpiReviewed>(response);

        reviewed.Actual.ShouldBe(100_000m);
        reviewed.Status.ShouldBe("OffTrack");

        // The pipeline moves; the minute does not.
        await OpportunityAsync(app, world, 400_000m, outcome: null);

        var card = await ScorecardAsync(app);
        var pipeline = card.Kpis.Single(k => k.Name == "pipeline");

        pipeline.Actual.ShouldBe(500_000m, "the live number followed the data.");
        pipeline.Status.ShouldBe("OnTrack");
        pipeline.LastCommentary.ShouldBe(
            "Thin. Two deals slipped out of the quarter.",
            "what was said stays what was said.");
    }

    /// <summary>Leads captured inside the period, and nothing outside it.</summary>
    [Fact]
    public async Task ALeadKpiCountsOnlyThePeriodsLeads()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await DirectorWorldAsync(app);

        await LeadAsync(app, new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero));
        await LeadAsync(app, new DateTimeOffset(2026, 12, 31, 23, 59, 0, TimeSpan.Zero));
        await LeadAsync(app, new DateTimeOffset(2027, 1, 1, 0, 1, 0, TimeSpan.Zero));

        await KpiAsync(app, new DefineKpi(
            "leads", "Leads", KpiSource.LeadsCaptured, 3m, KpiDirection.HigherIsBetter));

        var leads = (await ScorecardAsync(app)).Kpis.Single(k => k.Name == "leads");

        leads.Actual.ShouldBe(
            2m, "the last day counts to midnight; the next morning does not count at all.");

        leads.Status.ShouldBe("OffTrack");
    }

    /// <summary>Placing people is administrative; committing a plan is not.</summary>
    [Fact]
    public async Task PlacingPeopleIsAdministrative()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await WorldAsync(app);

        (await app.PostAsync(
            Members,
            new SetOrgMember(Rep, "Ada Rowe", OrgRole.Director, null),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(
                HttpStatusCode.Forbidden, "a representative does not promote themselves.");
    }

    /// <summary>One organisation's line never reaches another's.</summary>
    [Fact]
    public async Task OneTenantsLineNeverReachesAnothers()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await DirectorWorldAsync(app);

        await PlanAsync(app, "north_region", world.Account, 400_000m, Rep);

        (await app.PostAsync(
            RollUps, new ReadRollUp("fy26"), CrmTokens.Contoso, idempotencyKey: null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------------------- fixtures

    private static async Task<PeriodRollUp> RollUpAsync(CrmApplication app, string token)
    {
        var response = await app.PostAsync(
            RollUps, new ReadRollUp("fy26"), token, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<PeriodRollUp>(response);
    }

    private static async Task<Scorecard> ScorecardAsync(CrmApplication app)
    {
        var response = await app.PostAsync(
            Scorecards, new ReadScorecard("fy26"), CrmTokens.Northwind, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<Scorecard>(response);
    }

    /// <summary>
    /// The reporting line comes back whole, managers before their reports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Every scoped read in this sample is scoped by this line, and nothing could show
    /// it.</strong> Somebody placed under the wrong manager silently sees the wrong pipeline, and
    /// the answer looks like an empty quarter rather than a misplaced person.
    /// </para>
    /// <para>
    /// <strong>The order is the claim, not a nicety.</strong> A client draws the tree in one pass;
    /// a row arriving before its parent has to be held aside, and a build that returned insertion
    /// order would work on the fixture that happened to be inserted top-down and nowhere else. It
    /// is asserted by placing the deepest person first.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheReportingLineComesBackManagersFirst()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        // Deliberately bottom-up: the answer must not depend on the order they were written in.
        await MemberAsync(app, new SetOrgMember(Manager, "Bea Vance", OrgRole.Director, null));
        await MemberAsync(app, new SetOrgMember("mgr-2", "Jo Okafor", OrgRole.Manager, Manager));
        await MemberAsync(app, new SetOrgMember(Rep, "Ada Rowe", OrgRole.Representative, "mgr-2"));

        var chart = await ChartAsync(app);

        chart.Members.Select(member => member.UserId).ShouldBe(
            [Manager, "mgr-2", Rep], "the director, then the manager, then the representative.");

        var director = chart.Members[0]!;

        director.ReportsTo.ShouldBeNull("nobody is above the top.");
        director.Role.ShouldBe(OrgRole.Director);
        director.Reports.ShouldBe(1, "one person directly below, not two at any depth.");

        chart.Members[2]!.ReportsTo.ShouldBe("mgr-2");
    }

    /// <summary>
    /// A quota cannot be carried by somebody who is in no line at all.
    /// </summary>
    /// <remarks>
    /// <strong>The schema is what makes it true, not a check somebody wrote.</strong>
    /// <c>quota</c> carries a foreign key into <c>org_member</c>, so "assigned a number to a name
    /// nobody placed" is refused at the write rather than discovered at the review. This is the
    /// test that stopped the chart shipping an <c>Unplaced</c> count that could only ever be zero.
    /// </remarks>
    [Fact]
    public async Task ANumberCannotBeCarriedBySomebodyInNoLine()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await MemberAsync(app, new SetOrgMember(Manager, "Bea Vance", OrgRole.Director, null));

        (await app.PostAsync(
            "/api/v1/crm/planning/periods",
            new DefinePeriod("fy26", "FY26", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), null),
            CrmTokens.NorthwindManager)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await app.PostAsync(
            "/api/v1/crm/quotas",
            new SetQuota("fy26", "nobody-placed-1", QuotaMeasure.Revenue, 250_000m, 1.0m),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldNotBe(HttpStatusCode.OK, "there is nobody of that name in the line.");

        (await ChartAsync(app)).Members.Select(member => member.UserId)
            .ShouldBe([Manager], "and so nothing is missing from the chart.");
    }

    private static async Task<OrgChart> ChartAsync(CrmApplication app)
    {
        var response = await app.PostAsync(
            Chart, new ReadOrgChart(), CrmTokens.Northwind, idempotencyKey: null);

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancellation));

        return await CrmApplication.ReadAsync<OrgChart>(response);
    }

    private static async Task MemberAsync(CrmApplication app, SetOrgMember member)
    {
        (await app.PostAsync(Members, member, CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK, "placing '" + member.UserId + "' failed.");
    }

    private static async Task KpiAsync(CrmApplication app, DefineKpi kpi)
    {
        (await app.PostAsync(Kpis, kpi, CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<ObjectiveSet> ObjectiveAsync(CrmApplication app, SetObjective set)
    {
        var response = await app.PostAsync(Objectives, set, CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<ObjectiveSet>(response);
    }

    private static async Task<StakeholderSet> StakeholderAsync(
        CrmApplication app, SetStakeholder set)
    {
        var response = await app.PostAsync(Stakeholders, set, CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<StakeholderSet>(response);
    }

    private static async Task<RiskSet> RiskAsync(CrmApplication app, SetRisk set)
    {
        var response = await app.PostAsync(Risks, set, CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<RiskSet>(response);
    }

    private static async Task StrategyAsync(CrmApplication app, decimal target)
    {
        (await app.PostAsync(
            Strategies, new SetStrategy("fy26", "Grow.", target, "EUR"),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task PlanAsync(
        CrmApplication app, string name, Guid account, decimal target, string owner)
    {
        (await app.PostAsync(
            Plans,
            new DefinePlan(
                PlanKind.Account, "fy26", name, name, owner,
                Account: account, TargetAmount: target, Currency: "EUR"),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.OK, "committing '" + name + "' failed.");
    }

    private static async Task<World> DirectorWorldAsync(CrmApplication app)
    {
        var world = await WorldAsync(app);

        await MemberAsync(app, new SetOrgMember(Rep, "Ada Rowe", OrgRole.Director, null));
        await MemberAsync(app, new SetOrgMember(Manager, "Bea Vance", OrgRole.Director, null));
        await StrategyAsync(app, 1_000_000m);

        return world;
    }

    private static async Task<World> WorldAsync(CrmApplication app)
    {
        (await app.PostAsync(
            Periods,
            new DefinePeriod(
                "fy26", "FY26", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), null),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var account = await app.Crm.AccountAsync(
            CrmTokens.NorthwindTenant, Lifecycle.Customer, Cancellation);

        var contact = await app.Crm.ContactAsync(CrmTokens.NorthwindTenant, account, Cancellation);
        var (_, stage) = await app.Crm.ProcessAsync(CrmTokens.NorthwindTenant, 1, true, Cancellation);

        return new World(account, contact, stage);
    }

    private static async Task OpportunityAsync(
        CrmApplication app, World world, decimal amount, string? outcome)
    {
        await app.Crm.AsTenantAsync(
            CrmTokens.NorthwindTenant,
            """
            INSERT INTO opportunity (opportunity_id, tenant_id, account_id, primary_contact_id,
                                     name, amount, currency, stage_id, probability, expected_close,
                                     owner_id, outcome, stage_entered_at)
            VALUES (gen_random_uuid(), @tenant, @account, @contact, 'Deal', @amount, 'EUR',
                    @stage, 50, current_date, gen_random_uuid(), @outcome, now())
            """,
            Cancellation,
            ("tenant", (object?)CrmTokens.NorthwindTenant),
            ("account", world.Account),
            ("contact", world.Contact),
            ("amount", amount),
            ("stage", world.Stage),
            ("outcome", outcome));
    }

    private static async Task LeadAsync(CrmApplication app, DateTimeOffset at)
    {
        await app.Crm.AsTenantAsync(
            CrmTokens.NorthwindTenant,
            """
            INSERT INTO lead (lead_id, tenant_id, company, contact_name, source, status,
                              score, captured_at)
            VALUES (gen_random_uuid(), @tenant, 'Northwind', 'Ada Rowe', 'Web', 'New', 40, @at)
            """,
            Cancellation,
            ("tenant", (object?)CrmTokens.NorthwindTenant),
            ("at", at));
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private sealed record World(Guid Account, Guid Contact, Guid Stage);
}
