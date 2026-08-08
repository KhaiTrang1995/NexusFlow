using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// What a large B2B organisation does before the quarter starts, and checks every week afterwards.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The number this whole feature exists for is the gap.</strong> A group sets a target for
/// the year; sales commits account by account and marketing commits leads by channel. The
/// interesting quantity is the difference between the target and the sum of what was committed
/// against it — and every planning spreadsheet eventually grows a formula that hides it: a
/// rounding, a "stretch" column, a child scaled so the parent adds up. The assertions here are
/// that the gap is reported as it is, in both directions, and that nothing closes it.
/// </para>
/// <para>
/// <strong>Actuals are read live and stored nowhere.</strong> Coverage comes from the
/// opportunities and attainment from the leads, at the moment the roll-up is asked for. There is
/// no <c>actual_amount</c> column, and one of these tests is that changing the underlying data
/// moves the roll-up with no plan being touched.
/// </para>
/// </remarks>
public sealed class PlanningApiTests
{
    private const string Periods = "/api/v1/crm/planning/periods";
    private const string PeriodList = "/api/v1/crm/planning/periods/list";
    private const string Strategies = "/api/v1/crm/planning/strategies";
    private const string Plans = "/api/v1/crm/planning/plans";
    private const string Qualifications = "/api/v1/crm/planning/qualifications";
    private const string Steps = "/api/v1/crm/planning/steps";
    private const string RollUps = "/api/v1/crm/planning/roll-ups";
    private const string Members = "/api/v1/crm/org/members";

    /// <summary>The subject the representative token carries.</summary>
    private const string Rep = "rep-northwind-1";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>The gap is the target minus what was committed, and nothing closes it.</summary>
    [Fact]
    public async Task TheGapIsReportedAndNothingClosesIt()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await StrategyAsync(app, "fy26", "Own the mid-market in DACH.", 1_000_000m);

        await PlanAsync(app, AccountPlan("north_region", world.Account, 400_000m));
        await PlanAsync(app, AccountPlan("south_region", world.Account, 250_000m));

        var run = await RollUpAsync(app, "fy26");

        run.Target.ShouldBe(1_000_000m);
        run.Committed.ShouldBe(650_000m);
        run.Gap.ShouldBe(
            350_000m, "the difference is what every planning tool eventually learns to hide.");

        run.Vision.ShouldBe("Own the mid-market in DACH.");
    }

    /// <summary>Committing more than the target is a negative gap, not a clamp.</summary>
    /// <remarks>
    /// Over-commitment is a real and useful state — it is what a leadership team asks for
    /// deliberately. Clamping at zero would make an organisation that had over-planned
    /// indistinguishable from one that had exactly planned.
    /// </remarks>
    [Fact]
    public async Task OverCommitmentIsANegativeGapAndNotAClamp()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await StrategyAsync(app, "fy26", "Grow.", 100_000m);
        await PlanAsync(app, AccountPlan("north_region", world.Account, 130_000m));

        (await RollUpAsync(app, "fy26")).Gap.ShouldBe(-30_000m);
    }

    /// <summary>Coverage is read from the live pipeline, not from anything on the plan.</summary>
    [Fact]
    public async Task CoverageFollowsThePipelineWithNoPlanBeingTouched()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await StrategyAsync(app, "fy26", "Grow.", 500_000m);
        await PlanAsync(app, AccountPlan("north_region", world.Account, 300_000m));

        (await RollUpAsync(app, "fy26")).Accounts.ShouldHaveSingleItem()
            .OpenPipeline.ShouldBe(0m);

        await OpportunityAsync(app, world, 120_000m, outcome: null);
        await OpportunityAsync(app, world, 80_000m, outcome: null);

        // Decided, so not coverage — whichever way it was decided.
        await OpportunityAsync(app, world, 500_000m, outcome: "Won");

        var coverage = (await RollUpAsync(app, "fy26")).Accounts.ShouldHaveSingleItem();

        coverage.OpenPipeline.ShouldBe(
            200_000m, "a decided opportunity is not coverage, and nothing was written to the plan.");

        coverage.Target.ShouldBe(300_000m);
        coverage.Account.ShouldBe("Northwind Traders");
    }

    /// <summary>A deal plan reports what is known about it and what is overdue.</summary>
    /// <remarks>
    /// An overdue step is the earliest honest signal that a deal has stopped — earlier than the
    /// stage, which a seller moves, and earlier than the close date, which a seller also moves.
    /// </remarks>
    [Fact]
    public async Task ADealPlanReportsWhatIsKnownAndWhatIsOverdue()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);
        var deal = await OpportunityAsync(app, world, 90_000m, outcome: null);

        await StrategyAsync(app, "fy26", "Grow.", 100_000m);
        await PlanAsync(app, new DefinePlan(
            PlanKind.Opportunity, "fy26", "acme_renewal", "Acme renewal", world.Owner,
            Opportunity: deal, TargetAmount: 90_000m, Currency: "EUR"));

        await QualifyAsync(app, "acme_renewal", QualificationElement.EconomicBuyer, true, "CFO, met twice.");
        await QualifyAsync(app, "acme_renewal", QualificationElement.Champion, true, "Head of ops.");
        await QualifyAsync(app, "acme_renewal", QualificationElement.PaperProcess, false, "Legal unknown.");

        await StepAsync(app, "acme_renewal", 0, "Security review", world.Owner, Days(-10), false);
        await StepAsync(app, "acme_renewal", 1, "Legal review", world.Owner, Days(30), false);
        await StepAsync(app, "acme_renewal", 2, "Kick-off", world.Owner, Days(-5), true);

        var readiness = (await RollUpAsync(app, "fy26")).Opportunities.ShouldHaveSingleItem();

        readiness.Answered.ShouldBe(2, "a recorded 'no' is not an answered element.");
        readiness.OutOf.ShouldBe(8);
        readiness.Steps.ShouldBe(3);
        readiness.OverdueSteps.ShouldBe(
            1, "a completed step is not overdue however late it was.");
    }

    /// <summary>Marketing attainment counts the leads that arrived in the period, by channel.</summary>
    [Fact]
    public async Task MarketingAttainmentCountsTheLeadsThatActuallyArrived()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await StrategyAsync(app, "fy26", "Grow.", 100_000m);
        await PlanAsync(app, new DefinePlan(
            PlanKind.MarketingLead, "fy26", "web_dach", "Web, DACH", world.Owner,
            Channel: "Web", Segment: "DACH mid-market", TargetLeads: 500));

        await LeadAsync(app, "Web", DateTimeOffset.UtcNow);
        await LeadAsync(app, "Web", DateTimeOffset.UtcNow);

        // Another channel, and one outside the period. Neither counts.
        await LeadAsync(app, "Referral", DateTimeOffset.UtcNow);
        await LeadAsync(app, "Web", DateTimeOffset.UtcNow.AddYears(-5));

        var attainment = (await RollUpAsync(app, "fy26")).Marketing.ShouldHaveSingleItem();

        attainment.TargetLeads.ShouldBe(500);
        attainment.ActualLeads.ShouldBe(2);
        attainment.Segment.ShouldBe("DACH mid-market");
    }

    /// <summary>A lead plan carries no money target, so it never inflates the commitment.</summary>
    /// <remarks>
    /// A marketing plan with a revenue target would roll into the number twice — once as a
    /// commitment and once as the pipeline it was meant to create.
    /// </remarks>
    [Fact]
    public async Task ALeadPlanNeverAddsToTheRevenueCommitment()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await StrategyAsync(app, "fy26", "Grow.", 100_000m);
        await PlanAsync(app, AccountPlan("north_region", world.Account, 60_000m));
        await PlanAsync(app, new DefinePlan(
            PlanKind.MarketingLead, "fy26", "web_dach", "Web, DACH", world.Owner,
            Channel: "Web", Segment: "DACH", TargetLeads: 500));

        (await RollUpAsync(app, "fy26")).Committed.ShouldBe(60_000m);
    }

    /// <summary>A tenant that has declared no periods says so, rather than refusing.</summary>
    /// <remarks>
    /// Every executive and planning screen is "for a period", and the client used to hold three
    /// period names of its own. On any tenant but the seeded one, twelve screens asked for a
    /// quarter nobody had declared and each showed a not-found for a quarter printed on its own
    /// selector. Empty has to be an answer for a screen to be able to explain itself.
    /// </remarks>
    [Fact]
    public async Task ATenantWithNoPeriodsIsAnsweredEmptilyRatherThanRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await app.PostAsync(
            PeriodList, new ReadPeriods(), CrmTokens.Contoso, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CrmApplication.ReadAsync<DeclaredPeriods>(response)).Periods.ShouldBeEmpty();
    }

    /// <summary>The declared periods come back most recent first, saying which one is now.</summary>
    /// <remarks>
    /// <strong>Which one is current is decided by the server.</strong> A browser deciding it does
    /// so in whatever timezone the machine is set to, so two offices would open the same screen on
    /// different quarters on the last day of one.
    /// </remarks>
    [Fact]
    public async Task TheDeclaredPeriodsComeBackMostRecentFirstAndSayWhichIsNow()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await WorldAsync(app);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        (await app.PostAsync(
            Periods,
            new DefinePeriod("fy26_now", "The one we are in", today.AddDays(-1), today.AddDays(1), null),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await app.PostAsync(
            Periods,
            new DefinePeriod(
                "fy20", "Long gone", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), null),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await app.PostAsync(
            PeriodList, new ReadPeriods(), CrmTokens.Northwind, idempotencyKey: null);

        var declared = await CrmApplication.ReadAsync<DeclaredPeriods>(response);

        declared.Periods
            .Select(static period => period.StartsOn)
            .ShouldBeInOrder(SortDirection.Descending, "a selector opens on the period nearest now.");

        // More than one period can contain today — a quarter and the year it sits in both do —
        // so this is a fact about each period rather than about which single one is "the" current.
        declared.Periods.Single(static period => period.Name == "fy26_now").IsCurrent.ShouldBeTrue();
        declared.Periods.Single(static period => period.Name == "fy20").IsCurrent.ShouldBeFalse();
    }

    /// <summary>Reading the periods needs no administrator, only a reader.</summary>
    /// <remarks>
    /// Declaring a period is an administrative act; knowing which quarter you are looking at is
    /// not. Behind <c>crm.admin</c> every seller's screen would have no period to ask for and no
    /// way to find one.
    /// </remarks>
    [Fact]
    public async Task AnyReaderCanSeeWhichPeriodsExist()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await WorldAsync(app);

        var response = await app.PostAsync(
            PeriodList, new ReadPeriods(), CrmTokens.Northwind, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CrmApplication.ReadAsync<DeclaredPeriods>(response)).Periods.ShouldNotBeEmpty();
    }

    /// <summary>A quarter that sticks out of its year is refused.</summary>
    /// <remarks>
    /// It would make a roll-up that counts something twice or loses it, and neither shows up as an
    /// error — only as a total nobody can reconcile, three weeks later, in a board pack.
    /// </remarks>
    [Fact]
    public async Task APeriodOutsideItsParentIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await WorldAsync(app);

        var response = await app.PostAsync(
            Periods,
            new DefinePeriod(
                "q5_fy26", "Q5 FY26",
                new DateOnly(2026, 12, 1), new DateOnly(2027, 3, 31), "fy26"),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.period_outside_parent");
    }

    /// <summary>A channel no lead can arrive from is refused when the plan is committed.</summary>
    /// <remarks>
    /// Stored, it would report zero for ever and read as a marketing failure rather than a typo.
    /// </remarks>
    [Fact]
    public async Task AChannelNoLeadCanArriveFromIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        var response = await app.PostAsync(
            Plans,
            new DefinePlan(
                PlanKind.MarketingLead, "fy26", "tiktok", "TikTok", world.Owner,
                Channel: "TikTok", Segment: "DACH", TargetLeads: 100),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.plan_channel_unknown");
    }

    /// <summary>A plan carrying what its kind has no use for is refused.</summary>
    [Fact]
    public async Task APlanWhoseFieldsDoNotMatchItsKindIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        var response = await app.PostAsync(
            Plans,
            new DefinePlan(
                PlanKind.MarketingLead, "fy26", "web_dach", "Web, DACH", world.Owner,
                Channel: "Web", Segment: "DACH", TargetLeads: 100,
                TargetAmount: 50_000m, Currency: "EUR"),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.plan_fields_disagree");
    }

    /// <summary>A roll-up against no strategy is refused, not answered with zero.</summary>
    /// <remarks>
    /// Answered with a target of zero, it would show every commitment covering nothing and a gap
    /// of minus everything — which reads as good news.
    /// </remarks>
    [Fact]
    public async Task ARollUpAgainstNoStrategyIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await WorldAsync(app);

        var response = await app.PostAsync(
            RollUps, new ReadRollUp("fy26"), CrmTokens.Northwind, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.strategy_not_set");
    }

    /// <summary>Setting the strategy again replaces it rather than adding a second number.</summary>
    [Fact]
    public async Task SettingTheStrategyAgainReplacesTheNumber()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await WorldAsync(app);

        await StrategyAsync(app, "fy26", "Grow.", 100_000m);
        await StrategyAsync(app, "fy26", "Grow faster.", 150_000m);

        var run = await RollUpAsync(app, "fy26");

        run.Target.ShouldBe(150_000m, "a second row would need a rule about which one is current.");
        run.Vision.ShouldBe("Grow faster.");
    }

    /// <summary>Setting the number is management's; committing a plan is a seller's.</summary>
    [Fact]
    public async Task SettingTheNumberIsAdministrativeAndCommittingIsNot()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        (await app.PostAsync(
            Strategies,
            new SetStrategy("fy26", "Grow.", 100_000m, "EUR"),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(
                HttpStatusCode.Forbidden, "a representative does not set the group's number.");

        await StrategyAsync(app, "fy26", "Grow.", 100_000m);

        (await app.PostAsync(
            Plans, AccountPlan("north_region", world.Account, 60_000m), CrmTokens.Northwind))
            .StatusCode.ShouldBe(
                HttpStatusCode.OK, "committing against it is exactly what a seller does.");
    }

    /// <summary>One group's plan is never visible to another.</summary>
    [Fact]
    public async Task OneTenantsPlanIsNeverVisibleToAnother()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await StrategyAsync(app, "fy26", "Grow.", 100_000m);
        await PlanAsync(app, AccountPlan("north_region", world.Account, 60_000m));

        (await app.PostAsync(
            RollUps, new ReadRollUp("fy26"), CrmTokens.Contoso, idempotencyKey: null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------------------- fixtures

    private static DateOnly Days(int offset) =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddDays(offset));

    private static DefinePlan AccountPlan(string name, Guid account, decimal target) =>
        new(PlanKind.Account, "fy26", name, name, Rep,
            Account: account, TargetAmount: target, Currency: "EUR");

    private static async Task<PeriodRollUp> RollUpAsync(CrmApplication app, string period)
    {
        var response = await app.PostAsync(
            RollUps, new ReadRollUp(period), CrmTokens.Northwind, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<PeriodRollUp>(response);
    }

    private static async Task StrategyAsync(
        CrmApplication app, string period, string vision, decimal target)
    {
        (await app.PostAsync(
            Strategies, new SetStrategy(period, vision, target, "EUR"), CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task PlanAsync(CrmApplication app, DefinePlan plan)
    {
        (await app.PostAsync(Plans, plan, CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.OK, "committing '" + plan.Name + "' failed.");
    }

    private static async Task QualifyAsync(
        CrmApplication app, string plan, QualificationElement element, bool answered, string note)
    {
        (await app.PostAsync(
            Qualifications, new AnswerQualification(plan, element, answered, note),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task StepAsync(
        CrmApplication app, string plan, int ordinal, string what, string owner,
        DateOnly due, bool complete)
    {
        (await app.PostAsync(
            Steps, new SetPlanStep(plan, ordinal, what, owner, due, complete), CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
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

        await app.Crm.AsTenantAsync(
            CrmTokens.NorthwindTenant,
            "UPDATE account SET name = 'Northwind Traders' WHERE account_id = @id",
            Cancellation,
            ("id", (object?)account));

        // The roll-up is scoped by who is asking, so the caller has to exist in the organisation.
        // A director here, so these tests are about the arithmetic rather than about the scope —
        // ManagementApiTests is where the scope is asserted.
        (await app.PostAsync(
            Members,
            new SetOrgMember(Rep, "Ada Rowe", OrgRole.Director, null),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        return new World(account, contact, stage, Rep);
    }

    private static async Task<Guid> OpportunityAsync(
        CrmApplication app, World world, decimal amount, string? outcome)
    {
        var id = Guid.NewGuid();

        await app.Crm.AsTenantAsync(
            CrmTokens.NorthwindTenant,
            """
            INSERT INTO opportunity (opportunity_id, tenant_id, account_id, primary_contact_id,
                                     name, amount, currency, stage_id, probability, expected_close,
                                     owner_id, outcome, stage_entered_at)
            VALUES (@id, @tenant, @account, @contact, 'Deal', @amount, 'EUR',
                    @stage, 50, current_date, @owner, @outcome, now())
            """,
            Cancellation,
            ("id", (object?)id),
            ("tenant", CrmTokens.NorthwindTenant),
            ("account", world.Account),
            ("contact", world.Contact),
            ("amount", amount),
            ("stage", world.Stage),
            ("owner", Guid.NewGuid()),
            ("outcome", outcome));

        return id;
    }

    private static async Task LeadAsync(CrmApplication app, string source, DateTimeOffset at)
    {
        await app.Crm.AsTenantAsync(
            CrmTokens.NorthwindTenant,
            """
            INSERT INTO lead (lead_id, tenant_id, company, contact_name, source, status,
                              score, captured_at)
            VALUES (gen_random_uuid(), @tenant, 'Northwind', 'Ada Rowe', @source, 'New', 40, @at)
            """,
            Cancellation,
            ("tenant", (object?)CrmTokens.NorthwindTenant),
            ("source", source),
            ("at", at));
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private sealed record World(Guid Account, Guid Contact, Guid Stage, string Owner);
}
