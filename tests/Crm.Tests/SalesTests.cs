using System.Security.Claims;
using Crm;
using FlowX;
using FlowX.Conformance.InMemory;
using FlowX.Hosting;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// Quotes, discount approval and orders, of <c>docs/26-CRM-Sample.md</c> §5.2 and §10 package 9.
/// </summary>
/// <remarks>
/// <strong>The test package 9 exists for is
/// <see cref="ARepresentativeIsRefusedTheDiscountAManagerIsAllowed"/>.</strong> The arithmetic
/// around it is <see cref="PricingTests"/>'s and needs no server; what needs one is the part
/// where a grant on a token decides whether a row moves.
/// </remarks>
public sealed class SalesTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------------------- issuing

    [Fact]
    public async Task ASmallDiscountIsIssuedWithNobodyElsesSayingSo()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var opportunity = await OpportunityAsync(crm);

        var issued = await IssueAsync(crm, opportunity, discount: 100m, Representative);

        issued.IsSuccess.ShouldBeTrue(Because(issued));
        issued.Value!.Status.ShouldBe(QuoteStatus.Issued);
        issued.Value.NeedsApproval.ShouldBeFalse("100 off 1 000 is a tenth.");
        issued.Value.Total.Amount.ShouldBe(900m);
        issued.Value.Total.Currency.ShouldBe("EUR");
    }

    [Fact]
    public async Task ALargeDiscountIsWrittenAsADraftRatherThanRefused()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var opportunity = await OpportunityAsync(crm);

        var issued = await IssueAsync(crm, opportunity, discount: 300m, Representative);

        issued.IsSuccess.ShouldBeTrue(
            "asking for a discount is a representative's to do; granting it is not.");
        issued.Value!.Status.ShouldBe(QuoteStatus.Draft);
        issued.Value.NeedsApproval.ShouldBeTrue();
    }

    [Fact]
    public async Task AQuotesLinesAreWrittenWithIt()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var opportunity = await OpportunityAsync(crm);

        var issued = await IssueAsync(crm, opportunity, discount: 0m, Representative);

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind,
            "SELECT count(*) FROM quote_line WHERE quote_id = @quote",
            Cancellation,
            ("quote", issued.Value!.QuoteId))).ShouldBe(2L);
    }

    [Fact]
    public async Task AQuoteAgainstAnotherTenantsOpportunityIsNotFound()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var opportunity = await OpportunityAsync(crm);

        var issued = await IssueAsync(
            crm, opportunity, discount: 0m, Representative, CrmTokens.ContosoTenant);

        issued.Error!.Code.ShouldBe("crm.opportunity_not_found");
    }

    [Fact]
    public async Task AQuoteMixingCurrenciesIsRefusedBeforeAnythingIsWritten()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var opportunity = await OpportunityAsync(crm);

        var issued = await RunAsync(
            IssueQuoteFlow.Plan,
            Dispatcher(crm),
            new IssueQuote(
                opportunity,
                [
                    new QuoteRequestLine("SEAT", 1, new Money(100m, "EUR")),
                    new QuoteRequestLine("SUPPORT", 1, new Money(90m, "USD")),
                ],
                Discount: 0m,
                ValidForDays: 30),
            IssueQuoteFlow.Projection,
            Representative,
            CrmTokens.NorthwindTenant);

        issued.Error!.Code.ShouldBe("crm.quote_mixes_currencies");

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind, "SELECT count(*) FROM quote", Cancellation))
            .ShouldBe(0L, "pricing refuses before the store is reached.");
    }

    // ----------------------------------------------------------------------------- approving

    /// <summary>
    /// §10 package 9's "done when", and the only test that can falsify it.
    /// </summary>
    [Fact]
    public async Task ARepresentativeIsRefusedTheDiscountAManagerIsAllowed()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var opportunity = await OpportunityAsync(crm);

        var quote = (await IssueAsync(crm, opportunity, discount: 300m, Representative)).Value!.QuoteId;

        var refused = await ApproveAsync(crm, quote, Representative);

        refused.IsSuccess.ShouldBeFalse(
            "crm.discount.approve is not a grant a representative's token carries.");
        refused.Error!.Category.ShouldBe(ErrorCategory.Forbidden);

        (await ApprovedByAsync(crm, quote)).ShouldBeNull("a refused step must not have written.");

        var allowed = await ApproveAsync(crm, quote, Manager);

        allowed.IsSuccess.ShouldBeTrue(Because(allowed));
        allowed.Value!.ApprovedBy.ShouldBe(Approvers.Of(Manager));

        (await ApprovedByAsync(crm, quote)).ShouldBe(Approvers.Of(Manager));
        (await StatusAsync(crm, quote)).ShouldBe(QuoteStatus.Issued);
    }

    /// <summary>
    /// The approver is the caller the token attested, and not anything the body could say.
    /// </summary>
    [Fact]
    public async Task TheApproverComesOffTheCallersClaims()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var opportunity = await OpportunityAsync(crm);

        var quote = (await IssueAsync(crm, opportunity, discount: 300m, Representative)).Value!.QuoteId;

        await ApproveAsync(crm, quote, Manager);

        (await ApprovedByAsync(crm, quote)).ShouldNotBe(
            Approvers.Of(Representative),
            "the request carries a quote id and nothing else.");
    }

    [Fact]
    public async Task ASecondApprovalIsTheFirstOnesAnswerAgain()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var opportunity = await OpportunityAsync(crm);

        var quote = (await IssueAsync(crm, opportunity, discount: 300m, Representative)).Value!.QuoteId;

        var first = await ApproveAsync(crm, quote, Manager);
        var second = await ApproveAsync(crm, quote, Manager);

        second.IsSuccess.ShouldBeTrue("a redelivered approval is not a conflict.");
        second.Value!.ApprovedBy.ShouldBe(first.Value!.ApprovedBy);
    }

    [Fact]
    public async Task ApprovingAQuoteThatNeededNoApprovalIsRefused()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var opportunity = await OpportunityAsync(crm);

        var quote = (await IssueAsync(crm, opportunity, discount: 100m, Representative)).Value!.QuoteId;

        (await ApproveAsync(crm, quote, Manager)).Error!.Code.ShouldBe("crm.quote_wrong_status");
    }

    // ------------------------------------------------------------------------------ ordering

    [Fact]
    public async Task AnIssuedQuoteBecomesAnOrderForItsAccountAndTotal()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var opportunity = await OpportunityAsync(crm);

        var quote = (await IssueAsync(crm, opportunity, discount: 100m, Representative)).Value!.QuoteId;

        var placed = await PlaceAsync(crm, quote, Representative);

        placed.IsSuccess.ShouldBeTrue(Because(placed));
        placed.Value!.Total.Amount.ShouldBe(900m);
        placed.Value.OrderId.ShouldBe(SalesIds.Order(quote));

        (await crm.ScalarAsTenantAsync<decimal>(
            CrmSchemaHarness.Northwind,
            "SELECT total FROM sales_order WHERE quote_id = @quote",
            Cancellation,
            ("quote", quote))).ShouldBe(900m);

        (await StatusAsync(crm, quote)).ShouldBe(QuoteStatus.Accepted);
    }

    [Fact]
    public async Task ADraftQuoteCannotBeOrdered()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var opportunity = await OpportunityAsync(crm);

        var quote = (await IssueAsync(crm, opportunity, discount: 300m, Representative)).Value!.QuoteId;

        (await PlaceAsync(crm, quote, Representative)).Error!.Code.ShouldBe("crm.quote_wrong_status");
    }

    /// <summary>
    /// A quote left <c>Issued</c> with a discount this build would have held back is refused at
    /// the order, which is the moment the money is committed.
    /// </summary>
    [Fact]
    public async Task AnIssuedQuoteWhoseDiscountNobodyApprovedIsRefusedAtTheOrder()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var opportunity = await OpportunityAsync(crm);

        var quote = (await IssueAsync(crm, opportunity, discount: 300m, Representative)).Value!.QuoteId;

        // What an older build, or a lower threshold, would have left behind.
        await crm.AsTenantAsync(
            CrmSchemaHarness.Northwind,
            "UPDATE quote SET status = 'Issued' WHERE quote_id = @quote",
            Cancellation,
            ("quote", quote));

        (await PlaceAsync(crm, quote, Representative)).Error!.Code
            .ShouldBe("crm.discount_not_approved");
    }

    [Fact]
    public async Task AnApprovedDiscountCanBeOrdered()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var opportunity = await OpportunityAsync(crm);

        var quote = (await IssueAsync(crm, opportunity, discount: 300m, Representative)).Value!.QuoteId;

        await ApproveAsync(crm, quote, Manager);

        (await PlaceAsync(crm, quote, Representative)).Value!.Total.Amount.ShouldBe(700m);
    }

    [Fact]
    public async Task AnExpiredQuoteCannotBeOrdered()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var opportunity = await OpportunityAsync(crm);

        var quote = (await IssueAsync(crm, opportunity, discount: 0m, Representative)).Value!.QuoteId;

        await crm.AsTenantAsync(
            CrmSchemaHarness.Northwind,
            "UPDATE quote SET valid_until = now() - interval '1 day' WHERE quote_id = @quote",
            Cancellation,
            ("quote", quote));

        (await PlaceAsync(crm, quote, Representative)).Error!.Code.ShouldBe("crm.quote_expired");
    }

    [Fact]
    public async Task OrderingTheSameQuoteTwiceLeavesOneOrder()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var opportunity = await OpportunityAsync(crm);

        var quote = (await IssueAsync(crm, opportunity, discount: 0m, Representative)).Value!.QuoteId;

        var first = await PlaceAsync(crm, quote, Representative);
        var second = await PlaceAsync(crm, quote, Representative);

        second.IsSuccess.ShouldBeTrue("§6 draws at most one order per quote, not at most one call.");
        second.Value!.OrderId.ShouldBe(first.Value!.OrderId);

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind, "SELECT count(*) FROM sales_order", Cancellation))
            .ShouldBe(1L);
    }

    // ----------------------------------------------------------------------------- advancing

    [Fact]
    public async Task AdvancingAnOpportunityAnnouncesTheTriggerAndMovesNothing()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var opportunity = await OpportunityAsync(crm);

        var before = await StageAsync(crm, opportunity);

        var advanced = await RunAsync(
            AdvanceOpportunityFlow.Plan,
            new AdvanceOpportunityFlow.Dispatcher(
                applyOpportunityTrigger: new ApplyOpportunityTrigger(
                    new SalesStore(crm.DataSource), new TriggerLogStore(crm.DataSource))),
            new AdvanceOpportunity(opportunity, "advance"),
            AdvanceOpportunityFlow.Projection,
            Representative,
            CrmTokens.NorthwindTenant);

        advanced.IsSuccess.ShouldBeTrue(Because(advanced));
        advanced.Value!.Trigger.ShouldBe("advance");

        (await StageAsync(crm, opportunity)).ShouldBe(
            before, "where it goes is the configured process's answer, not this flow's.");
    }

    [Fact]
    public async Task AdvancingAnotherTenantsOpportunityIsNotFound()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var opportunity = await OpportunityAsync(crm);

        var advanced = await RunAsync(
            AdvanceOpportunityFlow.Plan,
            new AdvanceOpportunityFlow.Dispatcher(
                applyOpportunityTrigger: new ApplyOpportunityTrigger(
                    new SalesStore(crm.DataSource), new TriggerLogStore(crm.DataSource))),
            new AdvanceOpportunity(opportunity, "advance"),
            AdvanceOpportunityFlow.Projection,
            Representative,
            CrmTokens.ContosoTenant);

        advanced.Error!.Code.ShouldBe("crm.opportunity_not_found");
    }

    // ------------------------------------------------------------------------------- fixtures

    /// <summary>The sample's own tokens, so what is tested is what the deployment grants.</summary>
    private static ClaimsPrincipal Representative => Principal(CrmTokens.Northwind);

    /// <summary>The one caller holding <c>crm.discount.approve</c>.</summary>
    private static ClaimsPrincipal Manager => Principal(CrmTokens.NorthwindManager);

    private static ClaimsPrincipal Principal(string token) =>
        new(new ClaimsIdentity(CrmTokens.Claims[token], "CrmTokenTest"));

    private static async Task<Guid> OpportunityAsync(CrmSchemaHarness crm)
    {
        var account = await crm.AccountAsync(CrmSchemaHarness.Northwind, Lifecycle.Prospect, Cancellation);
        var contact = await crm.ContactAsync(CrmSchemaHarness.Northwind, account, Cancellation);
        var (_, stage) = await crm.ProcessAsync(CrmSchemaHarness.Northwind, 1, true, Cancellation);

        return await crm.OpportunityAsync(CrmSchemaHarness.Northwind, account, contact, stage, Cancellation);
    }

    /// <summary>Two lines coming to 1 000 EUR, so a 150 discount is exactly the threshold.</summary>
    private static ValueTask<FlowExecutionResult<QuoteIssued>> IssueAsync(
        CrmSchemaHarness crm,
        Guid opportunity,
        decimal discount,
        ClaimsPrincipal principal,
        string? tenant = null) =>
        RunAsync(
            IssueQuoteFlow.Plan,
            Dispatcher(crm),
            new IssueQuote(
                opportunity,
                [
                    new QuoteRequestLine("SEAT", 8, new Money(100m, "EUR")),
                    new QuoteRequestLine("SUPPORT", 1, new Money(200m, "EUR")),
                ],
                discount,
                ValidForDays: 30),
            IssueQuoteFlow.Projection,
            principal,
            tenant ?? CrmTokens.NorthwindTenant);

    private static IssueQuoteFlow.Dispatcher Dispatcher(CrmSchemaHarness crm) =>
        new(issueQuoteForOpportunity: new IssueQuoteForOpportunity(new SalesStore(crm.DataSource)));

    private static ValueTask<FlowExecutionResult<DiscountApproved>> ApproveAsync(
        CrmSchemaHarness crm, Guid quote, ClaimsPrincipal principal) =>
        RunAsync(
            ApproveDiscountFlow.Plan,
            new ApproveDiscountFlow.Dispatcher(
                approveQuoteDiscountCapability:
                    new ApproveQuoteDiscountCapability(new SalesStore(crm.DataSource))),
            new ApproveDiscount(quote),
            ApproveDiscountFlow.Projection,
            principal,
            CrmTokens.NorthwindTenant);

    private static ValueTask<FlowExecutionResult<OrderPlaced>> PlaceAsync(
        CrmSchemaHarness crm, Guid quote, ClaimsPrincipal principal) =>
        RunAsync(
            PlaceOrderFlow.Plan,
            new PlaceOrderFlow.Dispatcher(
                placeOrderForQuote: new PlaceOrderForQuote(new SalesStore(crm.DataSource))),
            new PlaceOrder(quote),
            PlaceOrderFlow.Projection,
            principal,
            CrmTokens.NorthwindTenant);

    private static ValueTask<FlowExecutionResult<TOut>> RunAsync<TIn, TOut>(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        TIn input,
        Func<FlowContext, TOut> projection,
        ClaimsPrincipal principal,
        string tenant)
        where TIn : notnull
    {
        var host = new FlowHost(
            new FlowEngine(SystemClock.Instance),
            new FlowXOptions
            {
                ApplicationName = "Crm",
                NodeName = "test-node",
                TenantIsolation = TenantIsolation.Row,
            },
            new FlowDurability(new InMemoryFlowJournal(), new InMemoryLeaseStore()));

        return host.RunAsync(
            plan,
            dispatcher,
            new FlowInvocation(
                "corr-" + Guid.NewGuid(),
                tenant,
                tenant,
                Deadline: null,
                Principal: principal,
                IsContinuation: false,
                TenantAttested: true),
            input,
            projection,
            Cancellation);
    }

    private static async ValueTask<Guid?> ApprovedByAsync(CrmSchemaHarness crm, Guid quote) =>
        await crm.ScalarAsTenantAsync<Guid?>(
            CrmSchemaHarness.Northwind,
            "SELECT approved_by FROM quote WHERE quote_id = @quote",
            Cancellation,
            ("quote", quote)).ConfigureAwait(false);

    private static async ValueTask<QuoteStatus> StatusAsync(CrmSchemaHarness crm, Guid quote) =>
        Enum.Parse<QuoteStatus>(await crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Northwind,
            "SELECT status FROM quote WHERE quote_id = @quote",
            Cancellation,
            ("quote", quote)).ConfigureAwait(false) ?? string.Empty);

    private static async ValueTask<Guid> StageAsync(CrmSchemaHarness crm, Guid opportunity) =>
        await crm.ScalarAsTenantAsync<Guid>(
            CrmSchemaHarness.Northwind,
            "SELECT stage_id FROM opportunity WHERE opportunity_id = @id",
            Cancellation,
            ("id", opportunity)).ConfigureAwait(false);

    private static string Because<T>(FlowExecutionResult<T> run) =>
        run.Error is null ? "the run failed with no error" : run.Error.Code + ": " + run.Error.Message;
}
