using System.Net;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// The same screens, from three chairs.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The gap this closes.</strong> This sample minted three tokens and two of them were a
/// representative. A director's scope resolves to the empty list — every seller, rather than a
/// named few — and nothing could reach that path, because the client's director borrowed the
/// manager's credentials and therefore saw a manager's two reports. A role whose entire
/// behaviour is "sees more than a manager" cannot be demonstrated with a manager's token.
/// </para>
/// <para>
/// <strong>Scope is resolved from the reporting line, never from the token's scopes.</strong> A
/// grant says what somebody may do; the org chart says whose rows they may do it to. Conflating
/// them is how a manager ends up seeing another manager's team by holding the same permission.
/// </para>
/// </remarks>
public sealed class RolePerspectiveTests
{
    private const string Board = "/api/v1/crm/board";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Each role sees the set of sellers its place in the org chart gives it.</summary>
    [Fact]
    public async Task EachRoleSeesWhatItsPlaceInTheChartAllows()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await ChartAsync(app);

        var seller = await ViewedAsAsync(app, CrmTokens.Northwind);
        var manager = await ViewedAsAsync(app, CrmTokens.NorthwindManager);
        var director = await ViewedAsAsync(app, CrmTokens.NorthwindDirector);

        seller.Role.ShouldBe(OrgRole.Representative);
        seller.Scope.ShouldBe(["rep-northwind-1"], "a seller sees their own rows and no others.");

        manager.Role.ShouldBe(OrgRole.Manager);
        manager.Scope.ShouldBe(
            ["rep-northwind-1", "rep-northwind-2", "manager-northwind-1"],
            ignoreOrder: true,
            "a manager sees their reports and themselves.");

        director.Role.ShouldBe(OrgRole.Director);

        // Empty is not "nobody". It is the absence of a filter — every seller in the tenant —
        // and a reader who took it for "no rows" would have the meaning exactly backwards.
        director.Scope.ShouldBeEmpty("a director's scope is unfiltered, not narrow.");
    }

    /// <summary>
    /// A manager does not see another manager's team by holding the same permission.
    /// </summary>
    /// <remarks>
    /// The distinction the whole file exists for: <c>crm.read</c> says a manager may read, and
    /// the reporting line says whose.
    /// </remarks>
    [Fact]
    public async Task AManagerSeesTheirOwnReportsAndNotEverybody()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await ChartAsync(app);

        // A second manager with a report of their own, under the same director.
        await app.PostAsync(
            "/api/v1/crm/org/members",
            new SetOrgMember("manager-northwind-2", "R. Silva", OrgRole.Manager, "director-northwind-1"),
            CrmTokens.NorthwindManager);

        await app.PostAsync(
            "/api/v1/crm/org/members",
            new SetOrgMember("rep-northwind-3", "K. Osei", OrgRole.Representative, "manager-northwind-2"),
            CrmTokens.NorthwindManager);

        var manager = await ViewedAsAsync(app, CrmTokens.NorthwindManager);

        manager.Scope.ShouldNotContain(
            "rep-northwind-3", "the other manager's report is not this manager's business.");

        (await ViewedAsAsync(app, CrmTokens.NorthwindDirector)).Scope.ShouldBeEmpty(
            "the director above both still sees everybody.");
    }

    /// <summary>
    /// A director is not in the discount chain, and the token says so.
    /// </summary>
    /// <remarks>
    /// <strong>Seniority is not the same thing as a grant.</strong> The manager approves a
    /// discount because that is the control; a token that gave the director every permission
    /// would make the three personas indistinguishable, which is the opposite of what they are
    /// for.
    /// </remarks>
    [Fact]
    public async Task ADirectorIsNotInTheDiscountChain()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var quote = await QuoteNeedingApprovalAsync(app);

        (await app.PostAsync(
            "/api/v1/crm/quotes/approvals", new ApproveDiscount(quote), CrmTokens.NorthwindDirector))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a director holds no discount grant.");

        (await app.PostAsync(
            "/api/v1/crm/quotes/approvals", new ApproveDiscount(quote), CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK, "the manager is the control.");
    }

    /// <summary>
    /// A seller's whole journey works, and the order is refused until the discount is approved.
    /// </summary>
    /// <remarks>
    /// The one path a seller walks every day: capture, convert, advance, quote, and — only once
    /// somebody senior has agreed the discount — commit the money.
    /// </remarks>
    [Fact]
    public async Task ASellerWalksTheWholeJourneyAndTheOrderWaitsForApproval()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var quote = await QuoteNeedingApprovalAsync(app);

        (await app.PostAsync("/api/v1/crm/orders", new PlaceOrder(quote), CrmTokens.Northwind))
            .StatusCode.ShouldBe(
                HttpStatusCode.Conflict, "a draft quote is not something to take money against.");

        await app.PostAsync(
            "/api/v1/crm/quotes/approvals", new ApproveDiscount(quote), CrmTokens.NorthwindManager);

        (await app.PostAsync("/api/v1/crm/orders", new PlaceOrder(quote), CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.OK, "and once it is approved, it is.");
    }

    // ------------------------------------------------------------------------------- fixtures

    private static async Task<ViewerScope> ViewedAsAsync(CrmApplication app, string token)
    {
        var response = await app.PostAsync(
            Board, new ReadBoard("fy26_q3"), token, idempotencyKey: null);

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Cancellation));

        return (await CrmApplication.ReadAsync<ExecutiveBoard>(response)).ViewedAs;
    }

    /// <summary>A period, and the reporting line the three tokens sit in.</summary>
    private static async Task ChartAsync(CrmApplication app)
    {
        (await app.PostAsync(
            "/api/v1/crm/planning/periods",
            new DefinePeriod("fy26_q3", "FY26 Q3", new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30), null),
            CrmTokens.NorthwindManager)).StatusCode.ShouldBe(HttpStatusCode.OK, "the period");

        // The board rolls up against a number, and refuses when there is none — so a period
        // without a strategy is a 404 rather than an empty board.
        (await app.PostAsync(
            "/api/v1/crm/planning/strategies",
            new SetStrategy("fy26_q3", "Two reference accounts before the segment push.", 900_000m, "EUR"),
            CrmTokens.NorthwindManager)).StatusCode.ShouldBe(HttpStatusCode.OK, "the strategy");

        // Top down: `reports_to` is a foreign key into this same table.
        foreach (var member in (SetOrgMember[])
                 [
                     new("director-northwind-1", "P. Almeida", OrgRole.Director, null),
                     new("manager-northwind-1", "J. Okafor", OrgRole.Manager, "director-northwind-1"),
                     new("rep-northwind-1", "A. Ruiz", OrgRole.Representative, "manager-northwind-1"),
                     new("rep-northwind-2", "M. Lindqvist", OrgRole.Representative, "manager-northwind-1"),
                 ])
        {
            (await app.PostAsync("/api/v1/crm/org/members", member, CrmTokens.NorthwindManager))
                .StatusCode.ShouldBe(HttpStatusCode.OK, member.UserId);
        }
    }

    /// <summary>A quote whose discount crossed the threshold, so it is Draft.</summary>
    private static async Task<Guid> QuoteNeedingApprovalAsync(CrmApplication app)
    {
        var process = await app.Crm.ProcessAsync(CrmTokens.NorthwindTenant, 1, true, Cancellation);
        var account = await app.Crm.AccountAsync(
            CrmTokens.NorthwindTenant, Lifecycle.Customer, Cancellation);
        var contact = await app.Crm.ContactAsync(CrmTokens.NorthwindTenant, account, Cancellation);
        var opportunity = await app.Crm.OpportunityAsync(
            CrmTokens.NorthwindTenant, account, contact, process.Stage, Cancellation);

        var response = await app.PostAsync(
            "/api/v1/crm/quotes",
            new IssueQuote(
                opportunity,
                [new QuoteRequestLine("PLAT", 2, new Money(50_000m, "EUR"))],
                Discount: 30_000m,
                ValidForDays: 21),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var issued = await CrmApplication.ReadAsync<QuoteIssued>(response);

        issued.Status.ShouldBe(QuoteStatus.Draft, "the discount crossed the threshold.");
        issued.NeedsApproval.ShouldBeTrue();

        return issued.QuoteId;
    }
}
