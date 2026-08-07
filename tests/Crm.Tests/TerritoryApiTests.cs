using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// Who owns which accounts, what number each person carries, and what a seller who joined in
/// February carries.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A territory is rules, not a list of accounts.</strong> A list is always one import
/// behind, and nobody can ask which accounts are in no territory — an account missing from every
/// list looks exactly like one nobody has got to yet. The coverage test here is the one that
/// cannot be written at all against a list model.
/// </para>
/// <para>
/// <strong>A quota is not a plan.</strong> A plan is committed upwards by whoever owns it; a quota
/// is assigned downwards by whoever is above them. The gap between the two is a number no roll-up
/// of commitments can show, because every commitment inside it is real.
/// </para>
/// </remarks>
public sealed class TerritoryApiTests
{
    private const string Territories = "/api/v1/crm/territories";
    private const string Routes = "/api/v1/crm/territories/routes";
    private const string CoverageRoute = "/api/v1/crm/territories/coverage";
    private const string Quotas = "/api/v1/crm/quotas";
    private const string Attainment = "/api/v1/crm/quotas/attainment";
    private const string Periods = "/api/v1/crm/planning/periods";
    private const string Members = "/api/v1/crm/org/members";
    private const string Plans = "/api/v1/crm/planning/plans";

    private const string Rep = "rep-northwind-1";
    private const string Manager = "manager-northwind-1";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>An account routes to the first territory whose rules all hold.</summary>
    [Fact]
    public async Task AnAccountRoutesToTheFirstTerritoryWhoseRulesAllHold()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await WorldAsync(app);

        // The narrower patch first, the broader one behind it.
        await TerritoryAsync(app, new DefineTerritory(
            "dach_manufacturing", "DACH manufacturing", null, 10,
            [
                Rule("region", GuardOperator.Equals, "DACH"),
                Rule("industry", GuardOperator.Equals, "Manufacturing"),
            ],
            [Rep]));

        await TerritoryAsync(app, new DefineTerritory(
            "dach", "DACH", null, 20,
            [Rule("region", GuardOperator.Equals, "DACH")],
            [Manager]));

        var manufacturing = await AccountAsync(app, "DACH", "Manufacturing");
        var retail = await AccountAsync(app, "DACH", "Retail");

        var first = await RouteAsync(app, manufacturing);

        first.Territory.ShouldBe("dach_manufacturing", "priority is what decides between two.");
        first.Owners.ShouldBe([Rep]);
        first.Considered.ShouldBe(1);

        var second = await RouteAsync(app, retail);

        second.Territory.ShouldBe("dach", "the narrower rules did not all hold.");
        second.Owners.ShouldBe([Manager]);
        second.Considered.ShouldBe(2, "how many were looked at is what makes a routing explicable.");
    }

    /// <summary>An account that matches nothing routes nowhere, and says so.</summary>
    [Fact]
    public async Task AnAccountThatMatchesNothingRoutesNowhere()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await WorldAsync(app);

        await TerritoryAsync(app, new DefineTerritory(
            "dach", "DACH", null, 10,
            [Rule("region", GuardOperator.Equals, "DACH")],
            [Rep]));

        var routed = await RouteAsync(app, await AccountAsync(app, "APAC", "Retail"));

        routed.Territory.ShouldBeNull();
        routed.Owners.ShouldBeEmpty();
        routed.Considered.ShouldBe(1);
    }

    /// <summary>Coverage counts what falls in no territory at all.</summary>
    /// <remarks>
    /// The question a list-per-person model cannot ask. These are the accounts nobody owns, and
    /// they are invisible in every other view in this sample.
    /// </remarks>
    [Fact]
    public async Task CoverageCountsTheAccountsNobodyOwns()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await WorldAsync(app);

        await TerritoryAsync(app, new DefineTerritory(
            "dach", "DACH", null, 10,
            [Rule("region", GuardOperator.Equals, "DACH")],
            [Rep]));

        // A patch with rules and nobody on it routes accounts to nobody, which is worse than not
        // having the patch.
        await TerritoryAsync(app, new DefineTerritory(
            "apac", "APAC", null, 20,
            [Rule("region", GuardOperator.Equals, "APAC")],
            []));

        await AccountAsync(app, "DACH", "Manufacturing");
        await AccountAsync(app, "DACH", "Retail");
        await AccountAsync(app, "APAC", "Retail");
        await AccountAsync(app, "LATAM", "Retail");

        var coverage = await CoverageAsync(app);

        coverage.Territories.Single(t => t.Territory == "dach").Accounts.ShouldBe(
            3, "two here and the fixture's, which WorldAsync pins to DACH.");
        coverage.Territories.Single(t => t.Territory == "apac").Accounts.ShouldBe(1);
        coverage.Unrouted.ShouldBe(1, "the LATAM account falls in no territory at all.");
        coverage.Unowned.ShouldBe(1, "and APAC has rules but nobody on it.");
    }

    /// <summary>A lead routes on its own attributes, not an account's.</summary>
    /// <remarks>
    /// A territory carrying rules for both subjects must not ask an account to satisfy a lead's
    /// rule — that would make every such territory match nothing.
    /// </remarks>
    [Fact]
    public async Task ALeadRoutesOnItsOwnAttributes()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await WorldAsync(app);

        await TerritoryAsync(app, new DefineTerritory(
            "inbound", "Inbound", null, 10,
            [
                Rule("region", GuardOperator.Equals, "DACH"),
                new RoutingRule(RoutingSubject.Lead, "source", GuardOperator.Equals, "Web"),
            ],
            [Rep]));

        var web = await LeadAsync(app, "Web");
        var referral = await LeadAsync(app, "Referral");

        (await RouteAsync(app, web, RoutingSubject.Lead)).Territory.ShouldBe(
            "inbound", "the account rule is not the lead's to satisfy.");

        (await RouteAsync(app, referral, RoutingSubject.Lead)).Territory.ShouldBeNull();

        (await RouteAsync(app, await AccountAsync(app, "DACH", "Retail"))).Territory.ShouldBe(
            "inbound", "and the lead rule is not the account's.");
    }

    /// <summary>A rule naming an attribute the subject does not have is refused when saved.</summary>
    [Fact]
    public async Task ARuleOnAnAttributeTheSubjectDoesNotHaveIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await WorldAsync(app);

        var response = await app.PostAsync(
            Territories,
            new DefineTerritory(
                "bad", "Bad", null, 10,
                [Rule("country", GuardOperator.Equals, "DE")],
                [Rep]),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.routing_attribute_unknown");
    }

    /// <summary>A territory with no rules is refused, because it would match everything.</summary>
    [Fact]
    public async Task ATerritoryWithNoRulesIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await WorldAsync(app);

        var response = await app.PostAsync(
            Territories,
            new DefineTerritory("catch_all", "Catch all", null, 1, [], [Rep]),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.territory_without_rules");
    }

    /// <summary>A part-year seller carries a fraction of the number.</summary>
    /// <remarks>
    /// Without it, every new hire is reported as failing for their first two reviews.
    /// </remarks>
    [Fact]
    public async Task APartYearSellerCarriesAFractionOfTheNumber()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await WorldAsync(app);

        var full = await QuotaAsync(app, Rep, 400_000m, 1.0m);
        var ramped = await QuotaAsync(app, Manager, 400_000m, 0.5m);

        full.Target.ShouldBe(400_000m);
        ramped.Target.ShouldBe(200_000m, "half a period is half a number.");
    }

    /// <summary>A ramp of nought is refused, because it is not a part-year seller.</summary>
    [Fact]
    public async Task ARampOfNoughtIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await WorldAsync(app);

        var response = await app.PostAsync(
            Quotas,
            new SetQuota("fy26", Rep, QuotaMeasure.Revenue, 400_000m, 0m),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.quota_ramp_out_of_range");
    }

    /// <summary>Assigned, committed and achieved come back side by side.</summary>
    /// <remarks>
    /// The middle column is the one no roll-up of commitments can show, because every commitment
    /// inside the hole is real.
    /// </remarks>
    [Fact]
    public async Task AssignedCommittedAndAchievedComeBackSideBySide()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await QuotaAsync(app, Rep, 500_000m, 1.0m);
        await PlanAsync(app, "acme", world.Account, 380_000m, Rep);
        await OpportunityAsync(app, world, 120_000m, "Won");

        var row = (await AttainmentAsync(app)).Rows.Single(r => r.UserId == Rep);

        row.Quota.ShouldBe(500_000m);
        row.Committed.ShouldBe(380_000m);
        row.Actual.ShouldBe(120_000m);
        row.Attainment.ShouldBe(24.0m);
        row.CommitmentGap.ShouldBe(
            120_000m, "the hole between what was given out and what was taken on.");
    }

    /// <summary>
    /// A quota that is not measured in money has no commitment, and says null rather than a
    /// number.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A plan commits an amount.</strong> There is nothing in it to compare a target of
    /// forty leads against, so the honest answer is that the question does not arise. Reporting
    /// the money figure beside the leads target gave a gap of −541,960 on a real screen —
    /// arithmetic between two different things, which reads as a catastrophic shortfall rather
    /// than as a column that does not apply.
    /// </para>
    /// <para>
    /// <strong>Zero would be no better.</strong> It is a claim that nothing was committed, and
    /// that is untrue of a seller who committed 380,000 against their revenue number in the same
    /// period — the row beside it proves it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AQuotaNotMeasuredInMoneyHasNoCommitment()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await QuotaAsync(app, Rep, 500_000m, 1.0m);
        await PlanAsync(app, "acme", world.Account, 380_000m, Rep);

        (await app.PostAsync(
            Quotas, new SetQuota("fy26", Rep, QuotaMeasure.Leads, 40m, 1.0m), CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var rows = (await AttainmentAsync(app, CrmTokens.NorthwindManager)).Rows
            .Where(row => row.UserId == Rep)
            .ToDictionary(row => row.Measure, StringComparer.Ordinal);

        rows["Revenue"].Committed.ShouldBe(380_000m, "a plan commits an amount.");
        rows["Revenue"].CommitmentGap.ShouldBe(120_000m);

        rows["Leads"].Committed.ShouldBeNull("there is no commitment measured in leads.");
        rows["Leads"].CommitmentGap.ShouldBeNull("and so no gap either.");
        rows["Leads"].Quota.ShouldBe(40m, "the target itself is still a number.");
    }

    /// <summary>Attainment is scoped by the reporting line, like every other read.</summary>
    [Fact]
    public async Task AttainmentIsScopedByWhoIsAsking()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await WorldAsync(app);

        await MemberAsync(app, new SetOrgMember(Rep, "Ada Rowe", OrgRole.Representative, Manager));

        await QuotaAsync(app, Rep, 400_000m, 1.0m);
        await QuotaAsync(app, Manager, 900_000m, 1.0m);

        (await AttainmentAsync(app, CrmTokens.Northwind)).Rows
            .ShouldHaveSingleItem().UserId.ShouldBe(Rep);

        (await AttainmentAsync(app, CrmTokens.NorthwindManager)).Rows.Count.ShouldBe(2);
    }

    /// <summary>Setting a number is administrative.</summary>
    [Fact]
    public async Task SettingANumberIsAdministrative()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await WorldAsync(app);

        (await app.PostAsync(
            Quotas,
            new SetQuota("fy26", Rep, QuotaMeasure.Revenue, 400_000m, 1.0m),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(
                HttpStatusCode.Forbidden, "a representative does not set their own number.");
    }

    /// <summary>One tenant's territories never route another's accounts.</summary>
    [Fact]
    public async Task OneTenantsTerritoriesNeverRouteAnothers()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await WorldAsync(app);

        await TerritoryAsync(app, new DefineTerritory(
            "dach", "DACH", null, 10,
            [Rule("region", GuardOperator.Equals, "DACH")],
            [Rep]));

        var account = await AccountAsync(app, "DACH", "Manufacturing");

        (await app.PostAsync(
            Routes, new RouteSubject(RoutingSubject.Account, account), CrmTokens.Contoso,
            idempotencyKey: null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------------------- fixtures

    private static RoutingRule Rule(string attribute, GuardOperator op, string value) =>
        new(RoutingSubject.Account, attribute, op, value);

    private static async Task<RoutedTo> RouteAsync(
        CrmApplication app, Guid id, RoutingSubject subject = RoutingSubject.Account)
    {
        var response = await app.PostAsync(
            Routes, new RouteSubject(subject, id), CrmTokens.Northwind, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<RoutedTo>(response);
    }

    private static async Task<Coverage> CoverageAsync(CrmApplication app)
    {
        var response = await app.PostAsync(
            CoverageRoute, new ReadCoverage(), CrmTokens.Northwind, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<Coverage>(response);
    }

    private static async Task<QuotaAttainmentReport> AttainmentAsync(
        CrmApplication app, string? token = null)
    {
        var response = await app.PostAsync(
            Attainment, new ReadQuotaAttainment("fy26"), token ?? CrmTokens.Northwind,
            idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<QuotaAttainmentReport>(response);
    }

    private static async Task<QuotaSet> QuotaAsync(
        CrmApplication app, string user, decimal target, decimal ramp)
    {
        var response = await app.PostAsync(
            Quotas,
            new SetQuota("fy26", user, QuotaMeasure.Revenue, target, ramp),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<QuotaSet>(response);
    }

    private static async Task TerritoryAsync(CrmApplication app, DefineTerritory territory)
    {
        (await app.PostAsync(Territories, territory, CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK, "declaring '" + territory.Name + "' failed.");
    }

    private static async Task MemberAsync(CrmApplication app, SetOrgMember member)
    {
        (await app.PostAsync(Members, member, CrmTokens.NorthwindManager))
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

        var account = await app.Crm.AccountAsync(
            CrmTokens.NorthwindTenant, Lifecycle.Customer, Cancellation);

        // The harness picks its own region, and coverage counts every account in the tenant — so
        // an unpinned one would make the unrouted count depend on a fixture nobody reading this
        // test can see.
        await app.Crm.AsTenantAsync(
            CrmTokens.NorthwindTenant,
            "UPDATE account SET region = 'DACH', industry = 'Manufacturing' WHERE account_id = @id",
            Cancellation,
            ("id", (object?)account));

        var contact = await app.Crm.ContactAsync(CrmTokens.NorthwindTenant, account, Cancellation);
        var (_, stage) = await app.Crm.ProcessAsync(CrmTokens.NorthwindTenant, 1, true, Cancellation);

        return new World(account, contact, stage);
    }

    private static async Task<Guid> AccountAsync(
        CrmApplication app, string region, string industry)
    {
        var id = Guid.NewGuid();

        await app.Crm.AsTenantAsync(
            CrmTokens.NorthwindTenant,
            """
            INSERT INTO account (account_id, tenant_id, name, industry, lifecycle, region, owner_id)
            VALUES (@id, @tenant, 'Northwind', @industry, 'Customer', @region, gen_random_uuid())
            """,
            Cancellation,
            ("id", (object?)id),
            ("tenant", CrmTokens.NorthwindTenant),
            ("industry", industry),
            ("region", region));

        return id;
    }

    private static async Task<Guid> LeadAsync(CrmApplication app, string source)
    {
        var id = Guid.NewGuid();

        await app.Crm.AsTenantAsync(
            CrmTokens.NorthwindTenant,
            """
            INSERT INTO lead (lead_id, tenant_id, company, contact_name, source, status,
                              score, captured_at)
            VALUES (@id, @tenant, 'Northwind', 'Ada Rowe', @source, 'New', 40, now())
            """,
            Cancellation,
            ("id", (object?)id),
            ("tenant", CrmTokens.NorthwindTenant),
            ("source", source));

        return id;
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

    private static async Task<JsonElement> Problem(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private sealed record World(Guid Account, Guid Contact, Guid Stage);
}
