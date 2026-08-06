using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// The three sales routes, in the order a deal goes through them: quote, approval, order.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What only shows up over HTTP.</strong> <c>PricingTests</c> already proves the
/// arithmetic and <c>SalesTests</c> proves what the statements write. Neither can reach the two
/// things this file exists for: that <c>crm.discount.approve</c> — a permission declared on a
/// step, not on a route — refuses a representative's token and admits a manager's, and that a
/// refusal arrives as <c>application/problem+json</c> carrying the error code the capability
/// raised, which is the only part of a failure a client can branch on.
/// </para>
/// <para>
/// <strong>The threshold is asked twice and both askings are asserted.</strong>
/// <c>DiscountPolicy.Threshold</c> decides whether a quote is <c>Draft</c> or <c>Issued</c>, and
/// again — at the moment the money is committed — whether an order may be placed on it. A
/// deployment that only checked the first would let an unapproved quote be ordered, so a test
/// that only checked the first would not notice.
/// </para>
/// </remarks>
public sealed class SalesApiTests
{
    private const string Quotes = "/api/v1/crm/quotes";
    private const string Approvals = "/api/v1/crm/quotes/approvals";
    private const string Orders = "/api/v1/crm/orders";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A quote inside the threshold is issued, and its lines are written.</summary>
    [Fact]
    public async Task AQuoteInsideTheThresholdIsIssuedAndItsLinesAreWritten()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var opportunity = await SeedOpportunityAsync(app);

        var response = await app.PostAsync(
            Quotes,
            new IssueQuote(opportunity, [Line(2, 500m)], Discount: 100m, ValidForDays: 30),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var issued = await CrmApplication.ReadAsync<QuoteIssued>(response);

        issued.NeedsApproval.ShouldBeFalse("100 off 1000 is inside the 15 % threshold.");
        issued.Status.ShouldBe(QuoteStatus.Issued);
        issued.Total.Amount.ShouldBe(900m);
        issued.Total.Currency.ShouldBe("EUR");

        (await app.Crm.ScalarAsTenantAsync<decimal>(
            CrmSchemaHarness.Northwind,
            "SELECT total FROM quote WHERE quote_id = @id",
            Cancellation,
            ("id", issued.QuoteId)))
            .ShouldBe(900m, "the response said 900 and the row says something else.");

        (await app.Crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind,
            "SELECT count(*) FROM quote_line WHERE quote_id = @id",
            Cancellation,
            ("id", issued.QuoteId)))
            .ShouldBe(1, "a quote with no lines is a total nobody can check.");
    }

    /// <summary>A quote past the threshold stays a draft and says so.</summary>
    [Fact]
    public async Task AQuotePastTheThresholdIsHeldAsADraft()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var opportunity = await SeedOpportunityAsync(app);

        var issued = await CrmApplication.ReadAsync<QuoteIssued>(
            await app.PostAsync(
                Quotes,
                new IssueQuote(opportunity, [Line(2, 500m)], Discount: 400m, ValidForDays: 30),
                CrmTokens.Northwind));

        issued.NeedsApproval.ShouldBeTrue("400 off 1000 is past the 15 % threshold.");
        issued.Status.ShouldBe(QuoteStatus.Draft);

        (await app.Crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Northwind,
            "SELECT status FROM quote WHERE quote_id = @id",
            Cancellation,
            ("id", issued.QuoteId)))
            .ShouldBe(nameof(QuoteStatus.Draft));
    }

    /// <summary>
    /// A representative cannot approve a discount, and the refusal is the step's rather than the
    /// route's.
    /// </summary>
    /// <remarks>
    /// Both tokens carry <c>crm.write</c> and both reach the same route; only the manager's
    /// carries <c>crm.discount.approve</c>. So a pass here cannot come from the endpoint being
    /// authenticated — it can only come from the permission on
    /// <c>crm.quote.approve_discount</c>, which is the claim the sample makes.
    /// </remarks>
    [Fact]
    public async Task ARepresentativeCannotApproveADiscountAndAManagerCan()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var opportunity = await SeedOpportunityAsync(app);

        var issued = await CrmApplication.ReadAsync<QuoteIssued>(
            await app.PostAsync(
                Quotes,
                new IssueQuote(opportunity, [Line(2, 500m)], Discount: 400m, ValidForDays: 30),
                CrmTokens.Northwind));

        var refused = await app.PostAsync(
            Approvals, new ApproveDiscount(issued.QuoteId), CrmTokens.Northwind);

        refused.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden,
            "a representative approved their own discount.");

        (await app.Crm.ScalarAsTenantAsync<object>(
            CrmSchemaHarness.Northwind,
            "SELECT approved_by FROM quote WHERE quote_id = @id",
            Cancellation,
            ("id", issued.QuoteId)))
            .ShouldBeNull("the request was refused and the quote was approved anyway.");

        var allowed = await app.PostAsync(
            Approvals, new ApproveDiscount(issued.QuoteId), CrmTokens.NorthwindManager);

        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);

        var approved = await CrmApplication.ReadAsync<DiscountApproved>(allowed);

        approved.QuoteId.ShouldBe(issued.QuoteId);
        approved.ApprovedBy.ShouldNotBe(
            Guid.Empty, "the approver is derived from the caller's claims and nobody was read.");

        (await app.Crm.ScalarAsTenantAsync<Guid>(
            CrmSchemaHarness.Northwind,
            "SELECT approved_by FROM quote WHERE quote_id = @id",
            Cancellation,
            ("id", issued.QuoteId)))
            .ShouldBe(approved.ApprovedBy, "the approver in the response is not the one stored.");
    }

    /// <summary>
    /// A quote nobody approved cannot be ordered, and the refusal names why in problem+json.
    /// </summary>
    /// <remarks>
    /// <strong>The code is <c>crm.quote_wrong_status</c> and not
    /// <c>crm.discount_not_approved</c>, and the difference is worth writing down.</strong> An
    /// over-threshold quote is held at <c>Draft</c>, and <see cref="PlaceOrderForQuote"/> asks
    /// about the status before it asks about the approval — so on this path the discount check
    /// is never reached. It is not dead code: <see cref="AnIssuedQuoteWithAnUnapprovedDiscountCannotBeOrdered"/>
    /// reaches it by the route its own remarks describe, a quote issued before the threshold
    /// moved.
    /// </remarks>
    [Fact]
    public async Task AnUnapprovedDraftCannotBeOrdered()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var opportunity = await SeedOpportunityAsync(app);

        var issued = await CrmApplication.ReadAsync<QuoteIssued>(
            await app.PostAsync(
                Quotes,
                new IssueQuote(opportunity, [Line(2, 500m)], Discount: 400m, ValidForDays: 30),
                CrmTokens.Northwind));

        var response = await app.PostAsync(
            Orders, new PlaceOrder(issued.QuoteId), CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        (await CodeOf(response)).ShouldBe(
            "crm.quote_wrong_status",
            "the status alone does not tell a client which of the four conflicts this is.");

        (await app.Crm.ScalarAsOwnerAsync<long>("SELECT count(*) FROM sales_order", Cancellation))
            .ShouldBe(0, "the order was refused and written anyway.");
    }

    /// <summary>
    /// The threshold is asked again where the money is committed, and refuses an issued quote
    /// nobody signed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Seeded rather than reached through <c>POST /quotes</c>, because that route cannot produce
    /// this row: a discount past the threshold is issued as a <c>Draft</c>. The state does occur
    /// — it is the one <see cref="SalesErrors.DiscountIsNotApproved"/>'s own remarks name, a
    /// quote issued while the threshold was higher and ordered after it moved — and it is the
    /// whole reason the check exists twice.
    /// </para>
    /// <para>
    /// Which makes this the only assertion in the file that could not be written against the
    /// HTTP surface alone. The refusal is still read off the wire.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnIssuedQuoteWithAnUnapprovedDiscountCannotBeOrdered()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var opportunity = await SeedOpportunityAsync(app);
        var quote = Guid.NewGuid();

        await app.Crm.AsTenantAsync(
            CrmSchemaHarness.Northwind,
            """
            INSERT INTO quote (quote_id, tenant_id, opportunity_id, status, subtotal, discount,
                total, currency, valid_until)
            VALUES (@quote, @tenant, @opportunity, 'Issued', 1000.0000, 400.0000, 600.0000,
                'EUR', now() + interval '30 days')
            """,
            Cancellation,
            ("quote", quote),
            ("tenant", CrmSchemaHarness.Northwind),
            ("opportunity", opportunity));

        var response = await app.PostAsync(Orders, new PlaceOrder(quote), CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await CodeOf(response)).ShouldBe(
            "crm.discount_not_approved",
            "an issued quote 40 % off was ordered with nobody's signature on it.");

        (await app.Crm.ScalarAsOwnerAsync<long>("SELECT count(*) FROM sales_order", Cancellation))
            .ShouldBe(0, "the order was refused and written anyway.");
    }

    /// <summary>Once a manager has approved, the same order goes through and is written.</summary>
    /// <remarks>
    /// The other half of the previous test, and the reason it is a separate one: a threshold that
    /// refused everything would pass that assertion and fail this.
    /// </remarks>
    [Fact]
    public async Task AnApprovedDiscountCanBeOrdered()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var opportunity = await SeedOpportunityAsync(app);

        var issued = await CrmApplication.ReadAsync<QuoteIssued>(
            await app.PostAsync(
                Quotes,
                new IssueQuote(opportunity, [Line(2, 500m)], Discount: 400m, ValidForDays: 30),
                CrmTokens.Northwind));

        (await app.PostAsync(Approvals, new ApproveDiscount(issued.QuoteId), CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await app.PostAsync(
            Orders, new PlaceOrder(issued.QuoteId), CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var placed = await CrmApplication.ReadAsync<OrderPlaced>(response);

        placed.QuoteId.ShouldBe(issued.QuoteId);
        placed.Total.Amount.ShouldBe(600m);

        (await app.Crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind,
            "SELECT count(*) FROM sales_order WHERE order_id = @id AND quote_id = @quote",
            Cancellation,
            ("id", placed.OrderId),
            ("quote", issued.QuoteId)))
            .ShouldBe(1, "the caller was told there was an order and there is no row.");
    }

    /// <summary>A quote against another tenant's opportunity is a 404, not somebody else's quote.</summary>
    /// <remarks>
    /// The id is real and the caller is authenticated — the only thing wrong with the request is
    /// whose opportunity it names. <c>NotFound</c> rather than <c>Forbidden</c> on purpose: a
    /// refusal that distinguished "not yours" from "does not exist" would confirm the id.
    /// </remarks>
    [Fact]
    public async Task AQuoteAgainstAnotherTenantsOpportunityIsNotFound()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var opportunity = await SeedOpportunityAsync(app);

        var response = await app.PostAsync(
            Quotes,
            new IssueQuote(opportunity, [Line(1, 100m)], Discount: 0m, ValidForDays: 30),
            CrmTokens.Contoso);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(response)).ShouldBe("crm.opportunity_not_found");

        (await app.Crm.ScalarAsOwnerAsync<long>("SELECT count(*) FROM quote", Cancellation))
            .ShouldBe(0, "a quote was written against an opportunity in another tenant.");
    }

    private static QuoteRequestLine Line(int quantity, decimal unitPrice) =>
        new("SKU-1", quantity, new Money(unitPrice, "EUR"));

    /// <summary>The <c>code</c> extension of a problem+json body.</summary>
    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        using var document = JsonDocument.Parse(body);

        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>An opportunity of Northwind's, with the account and contact it hangs off.</summary>
    private static async Task<Guid> SeedOpportunityAsync(CrmApplication app)
    {
        var crm = app.Crm;
        var account = await crm.AccountAsync(CrmSchemaHarness.Northwind, Lifecycle.Prospect, Cancellation);
        var contact = await crm.ContactAsync(CrmSchemaHarness.Northwind, account, Cancellation);
        var (_, stage) = await crm.ProcessAsync(CrmSchemaHarness.Northwind, 1, true, Cancellation);

        return await crm.OpportunityAsync(
            CrmSchemaHarness.Northwind, account, contact, stage, Cancellation);
    }
}
