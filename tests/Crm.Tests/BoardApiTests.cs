using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// Planning at more than one level, how the people and the deals are doing, and the one request a
/// board screen makes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The gap arithmetic is the same at every level.</strong> 0016 asked "does the sum of the
/// commitments add up to the number" once, at the top. A group with divisions, regions and
/// named-account teams asks it at four levels, and a plan tree is what makes the same subtraction
/// answer it at each one.
/// </para>
/// <para>
/// <strong>Every percentage here is null rather than zero when its denominator is empty.</strong>
/// A seller with no number is not a seller at nought per cent; a win rate over no decisions is not
/// nought either. Rendering both as zero is what puts the wrong name at the bottom of a table, and
/// two of these tests exist only to hold that.
/// </para>
/// </remarks>
public sealed class BoardApiTests
{
    private const string Periods = "/api/v1/crm/planning/periods";
    private const string Strategies = "/api/v1/crm/planning/strategies";
    private const string Plans = "/api/v1/crm/planning/plans";
    private const string Members = "/api/v1/crm/org/members";
    private const string Kpis = "/api/v1/crm/kpis";
    private const string Tree = "/api/v1/crm/planning/tree";
    private const string SalesPerf = "/api/v1/crm/performance/sales";
    private const string DealPerf = "/api/v1/crm/performance/deals";
    private const string Board = "/api/v1/crm/board";

    private const string Rep = "rep-northwind-1";
    private const string Manager = "manager-northwind-1";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A portfolio's gap is its own number less its children's, at its own level.</summary>
    [Fact]
    public async Task EachLevelOfTheTreeCarriesItsOwnGap()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await PortfolioAsync(app, "group", 1_000_000m, null);
        await PortfolioAsync(app, "dach", 600_000m, "group");
        await AccountPlanAsync(app, "acme", world.Account, 250_000m, Rep, "dach");
        await AccountPlanAsync(app, "globex", world.Account, 200_000m, Rep, "dach");

        var nodes = (await TreeAsync(app)).Nodes;

        var group = nodes.Single(n => n.Name == "group");
        var dach = nodes.Single(n => n.Name == "dach");

        group.Committed.ShouldBe(600_000m, "a node totals its children, never its descendants.");
        group.Gap.ShouldBe(400_000m);
        group.Depth.ShouldBe(1);

        dach.Committed.ShouldBe(450_000m);
        dach.Gap.ShouldBe(150_000m, "the same subtraction, one level down.");
        dach.Depth.ShouldBe(2);
        dach.Parent.ShouldBe("group");
        dach.Children.ShouldBe(2);

        nodes.Single(n => n.Name == "acme").Gap.ShouldBe(
            250_000m, "a leaf has committed nothing below it, so its gap is its whole target.");
    }

    /// <summary>The tree comes back parents before children.</summary>
    /// <remarks>
    /// A client indenting a tree has to be able to draw it in one pass; a list in arbitrary order
    /// would have to be sorted into one first, by every client, differently.
    /// </remarks>
    [Fact]
    public async Task TheTreeComesBackParentsBeforeChildren()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await PortfolioAsync(app, "group", 1_000_000m, null);
        await PortfolioAsync(app, "dach", 600_000m, "group");
        await AccountPlanAsync(app, "acme", world.Account, 250_000m, Rep, "dach");

        (await TreeAsync(app)).Nodes.Select(n => n.Name).ShouldBe(["group", "dach", "acme"]);
    }

    /// <summary>Rolling a plan into its own descendant is refused.</summary>
    /// <remarks>
    /// Two plans that are each other's parent is two individually legal rows, so no constraint
    /// catches it — and a reorganisation is exactly where it gets typed in.
    /// </remarks>
    [Fact]
    public async Task APlanRolledIntoItsOwnDescendantIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await WorldAsync(app);

        await PortfolioAsync(app, "group", 1_000_000m, null);
        await PortfolioAsync(app, "dach", 600_000m, "group");

        var response = await app.PostAsync(
            Plans,
            new DefinePlan(
                PlanKind.Portfolio, "fy26", "group", "Group", Rep,
                TargetAmount: 1_000_000m, Currency: "EUR", Parent: "dach"),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.plan_tree_would_loop");
    }

    /// <summary>An operations plan is counted in one of the activity kinds the schema records.</summary>
    [Fact]
    public async Task AnOperationsPlanIsCountedInActivities()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await WorldAsync(app);

        (await app.PostAsync(
            Plans,
            new DefinePlan(
                PlanKind.Operation, "fy26", "onboardings", "Onboardings", Rep,
                ActivityKind: "Meeting", TargetActivities: 120),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var bad = await app.PostAsync(
            Plans,
            new DefinePlan(
                PlanKind.Operation, "fy26", "webinars", "Webinars", Rep,
                ActivityKind: "Webinar", TargetActivities: 5),
            CrmTokens.Northwind);

        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(bad)).GetProperty("code").GetString().ShouldBe("crm.plan_activity_unknown");
    }

    /// <summary>An operations plan carries no money and a portfolio does.</summary>
    [Fact]
    public async Task MoneyBelongsToTheRevenueKindsAndTheCountingKindsHaveNone()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await WorldAsync(app);

        var response = await app.PostAsync(
            Plans,
            new DefinePlan(
                PlanKind.Operation, "fy26", "onboardings", "Onboardings", Rep,
                ActivityKind: "Meeting", TargetActivities: 120,
                TargetAmount: 50_000m, Currency: "EUR"),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.plan_fields_disagree");
    }

    /// <summary>Attainment is null for somebody who committed nothing, not nought per cent.</summary>
    [Fact]
    public async Task SomebodyWithNoNumberHasNoAttainmentRatherThanNought()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await AccountPlanAsync(app, "acme", world.Account, 400_000m, Rep, null);
        await OpportunityAsync(app, world, 100_000m, "Won");

        var sellers = (await SalesAsync(app)).Sellers;

        var rep = sellers.Single(s => s.UserId == Rep);

        rep.Committed.ShouldBe(400_000m);
        rep.Won.ShouldBe(100_000m);
        rep.Attainment.ShouldBe(25.0m);

        sellers.Single(s => s.UserId == Manager).Attainment.ShouldBeNull(
            "a person with no number is not a person at nought per cent.");

        sellers[^1].UserId.ShouldBe(
            Manager, "and sorting them as nought would put the wrong name at the bottom.");
    }

    /// <summary>A win rate over no decisions is null, not nought.</summary>
    [Fact]
    public async Task AWinRateOverNoDecisionsIsNotNought()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await OpportunityAsync(app, world, 100_000m, outcome: null);

        var deals = await DealsAsync(app);

        deals.Open.ShouldBe(1);
        deals.OpenValue.ShouldBe(100_000m);
        deals.WinRate.ShouldBeNull("a quarter in which nothing closed did not lose anything.");
        deals.AverageWonValue.ShouldBeNull();
    }

    /// <summary>Won, lost and the rate between them.</summary>
    [Fact]
    public async Task TheDealPictureCountsWonAndLostAndTheRateBetweenThem()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await OpportunityAsync(app, world, 100_000m, "Won");
        await OpportunityAsync(app, world, 200_000m, "Won");
        await OpportunityAsync(app, world, 60_000m, "Lost");
        await OpportunityAsync(app, world, 40_000m, outcome: null);

        var deals = await DealsAsync(app);

        deals.Won.ShouldBe(2);
        deals.WonValue.ShouldBe(300_000m);
        deals.Lost.ShouldBe(1);
        deals.LostValue.ShouldBe(60_000m);
        deals.WinRate.ShouldBe(66.7m, "won over decided, and the open one is not a decision.");
        deals.AverageWonValue.ShouldBe(150_000m);
    }

    /// <summary>An open deal nobody has moved in sixty days is stalled.</summary>
    /// <remarks>
    /// The number a pipeline review is actually for. A deal nobody has touched is not a deal that
    /// is going slowly.
    /// </remarks>
    [Fact]
    public async Task AnOpenDealNobodyHasMovedIsStalled()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await OpportunityAsync(app, world, 100_000m, outcome: null);
        await OpportunityAsync(
            app, world, 90_000m, outcome: null, enteredAt: DateTimeOffset.UtcNow.AddDays(-90));

        (await DealsAsync(app)).Stalled.ShouldBe(1);
    }

    /// <summary>The board answers everything in one request, at the caller's level.</summary>
    [Fact]
    public async Task TheBoardAnswersEverythingInOneRequest()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await PortfolioAsync(app, "group", 1_000_000m, null);
        await AccountPlanAsync(app, "acme", world.Account, 400_000m, Rep, "group");
        await OpportunityAsync(app, world, 120_000m, outcome: null);

        (await app.PostAsync(
            Kpis,
            new DefineKpi(
                "pipeline", "Open pipeline", KpiSource.OpenPipeline, 500_000m,
                KpiDirection.HigherIsBetter),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await app.PostAsync(
            Board, new ReadBoard("fy26"), CrmTokens.Northwind, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var board = await CrmApplication.ReadAsync<ExecutiveBoard>(response);

        board.ViewedAs.Role.ShouldBe(OrgRole.Director);
        board.RollUp.Target.ShouldBe(1_000_000m);
        board.RollUp.Committed.ShouldBe(1_400_000m, "both the portfolio and the account plan.");
        board.Tree.Nodes.Count.ShouldBe(2);
        board.Sales.Sellers.ShouldContain(s => s.UserId == Rep);
        board.Deals.Open.ShouldBe(1);
        board.Scorecard.Kpis.ShouldHaveSingleItem().Status.ShouldBe("OffTrack");
    }

    /// <summary>The board is scoped by who is asking, like every other roll-up.</summary>
    [Fact]
    public async Task TheBoardIsScopedByWhoIsAsking()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await MemberAsync(app, new SetOrgMember(Rep, "Ada Rowe", OrgRole.Representative, Manager));

        await AccountPlanAsync(app, "acme", world.Account, 400_000m, Rep, null);
        await AccountPlanAsync(app, "globex", world.Account, 250_000m, "rep-northwind-2", null);

        var mine = await BoardAsync(app, CrmTokens.Northwind);
        var everything = await BoardAsync(app, CrmTokens.NorthwindManager);

        mine.RollUp.Committed.ShouldBe(400_000m);
        mine.Sales.Sellers.ShouldHaveSingleItem().UserId.ShouldBe(Rep);

        everything.RollUp.Committed.ShouldBe(650_000m);
        everything.Sales.Sellers.Count.ShouldBe(2);
    }

    /// <summary>A board against a period with no number is refused.</summary>
    [Fact]
    public async Task ABoardAgainstNoStrategyIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        (await app.PostAsync(
            Periods,
            new DefinePeriod(
                "fy27", "FY27", new DateOnly(2027, 1, 1), new DateOnly(2027, 12, 31), null),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        await MemberAsync(app, new SetOrgMember(Rep, "Ada Rowe", OrgRole.Director, null));

        var response = await app.PostAsync(
            Board, new ReadBoard("fy27"), CrmTokens.Northwind, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Problem(response)).GetProperty("code").GetString().ShouldBe("crm.strategy_not_set");
    }

    // ------------------------------------------------------------------------------- fixtures

    private static async Task<ExecutiveBoard> BoardAsync(CrmApplication app, string token)
    {
        var response = await app.PostAsync(
            Board, new ReadBoard("fy26"), token, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<ExecutiveBoard>(response);
    }

    private static async Task<PlanTree> TreeAsync(CrmApplication app)
    {
        var response = await app.PostAsync(
            Tree, new ReadPlanTree("fy26", null), CrmTokens.Northwind, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<PlanTree>(response);
    }

    private static async Task<SalesPerformance> SalesAsync(CrmApplication app)
    {
        var response = await app.PostAsync(
            SalesPerf, new ReadSalesPerformance("fy26"), CrmTokens.Northwind,
            idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<SalesPerformance>(response);
    }

    private static async Task<DealPerformance> DealsAsync(CrmApplication app)
    {
        var response = await app.PostAsync(
            DealPerf, new ReadDealPerformance("fy26"), CrmTokens.Northwind, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<DealPerformance>(response);
    }

    private static async Task PortfolioAsync(
        CrmApplication app, string name, decimal target, string? parent)
    {
        (await app.PostAsync(
            Plans,
            new DefinePlan(
                PlanKind.Portfolio, "fy26", name, name, Rep,
                TargetAmount: target, Currency: "EUR", Parent: parent),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.OK, "committing '" + name + "' failed.");
    }

    private static async Task AccountPlanAsync(
        CrmApplication app, string name, Guid account, decimal target, string owner, string? parent)
    {
        (await app.PostAsync(
            Plans,
            new DefinePlan(
                PlanKind.Account, "fy26", name, name, owner,
                Account: account, TargetAmount: target, Currency: "EUR", Parent: parent),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.OK, "committing '" + name + "' failed.");
    }

    private static async Task MemberAsync(CrmApplication app, SetOrgMember member)
    {
        (await app.PostAsync(Members, member, CrmTokens.NorthwindManager))
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

        await MemberAsync(app, new SetOrgMember(Manager, "Bea Vance", OrgRole.Director, null));
        await MemberAsync(app, new SetOrgMember(Rep, "Ada Rowe", OrgRole.Director, null));

        (await app.PostAsync(
            Strategies, new SetStrategy("fy26", "Grow.", 1_000_000m, "EUR"),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var account = await app.Crm.AccountAsync(
            CrmTokens.NorthwindTenant, Lifecycle.Customer, Cancellation);

        var contact = await app.Crm.ContactAsync(CrmTokens.NorthwindTenant, account, Cancellation);
        var (_, stage) = await app.Crm.ProcessAsync(CrmTokens.NorthwindTenant, 1, true, Cancellation);

        return new World(account, contact, stage);
    }

    private static async Task OpportunityAsync(
        CrmApplication app,
        World world,
        decimal amount,
        string? outcome,
        DateTimeOffset? enteredAt = null)
    {
        await app.Crm.AsTenantAsync(
            CrmTokens.NorthwindTenant,
            """
            INSERT INTO opportunity (opportunity_id, tenant_id, account_id, primary_contact_id,
                                     name, amount, currency, stage_id, probability, expected_close,
                                     owner_id, outcome, stage_entered_at)
            VALUES (gen_random_uuid(), @tenant, @account, @contact, 'Deal', @amount, 'EUR',
                    @stage, 50, current_date, gen_random_uuid(), @outcome, @entered)
            """,
            Cancellation,
            ("tenant", (object?)CrmTokens.NorthwindTenant),
            ("account", world.Account),
            ("contact", world.Contact),
            ("amount", amount),
            ("stage", world.Stage),
            ("outcome", outcome),
            // Now by default, not a fixed date inside the period: a hard-coded one drifts past
            // the staleness window as the calendar moves, and the stalled test would then pass for
            // the wrong reason on some days and fail on others.
            ("entered", enteredAt ?? DateTimeOffset.UtcNow));
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private sealed record World(Guid Account, Guid Contact, Guid Stage);
}
