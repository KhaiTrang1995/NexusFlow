using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// A page of a built-in entity.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The gap this closes.</strong> This sample could page through records of an entity
/// nobody had ever heard of, and could not answer "give me a page of accounts". Search returns an
/// identity rather than a row; a report returns a group rather than a record. A list view had
/// nothing to read, which is why twenty screens of the web client read fixtures.
/// </para>
/// <para>
/// <strong>What is worth asserting is the filter, not the select.</strong> A field name in a
/// filter is a caller's value, and this is the surface where one is closest to a statement.
/// </para>
/// </remarks>
public sealed class EntityPageApiTests
{
    private const string Entities = "/api/v1/crm/entities";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A page comes back with the entity's own columns.</summary>
    [Fact]
    public async Task APageComesBackWithTheEntitysOwnColumns()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await app.Crm.AccountAsync(CrmTokens.NorthwindTenant, Lifecycle.Customer, Cancellation);

        var page = await PageAsync(app, new ReadEntityPage(ReadableEntity.Account, null, 50));

        page.Records.Count.ShouldBeGreaterThan(0);

        var first = page.Records[0]!;
        first.Values.Keys.ShouldBe(
            ["account_id", "name", "industry", "lifecycle", "region", "owner_id"],
            ignoreOrder: true);
    }

    /// <summary>A column the entity does not have is refused, with the list in the message.</summary>
    /// <remarks>
    /// <strong>The test this file exists for.</strong> A field name reaching a statement is the
    /// injection every other surface here is arranged to prevent, and this is the surface where
    /// one is a filter away from the database.
    /// </remarks>
    [Fact]
    public async Task AColumnTheEntityDoesNotHaveIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await app.PostAsync(
            Entities,
            new ReadEntityPage(
                ReadableEntity.Account,
                new RecordFilter(
                    FilterMatch.All,
                    [new RollupFilter("name; DROP TABLE account", GuardOperator.Equals, "x")]),
                25),
            CrmTokens.Northwind,
            idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var problem = await Problem(response);
        problem.GetProperty("code").GetString().ShouldBe("crm.entity_field_unknown");
        problem.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain(
            "account_id", Case.Sensitive, "the refusal names what the entity does have.");
    }

    /// <summary>A filter keeps what holds and drops what does not.</summary>
    [Fact]
    public async Task AFilterKeepsWhatHolds()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await app.Crm.AccountAsync(CrmTokens.NorthwindTenant, Lifecycle.Customer, Cancellation);
        await app.Crm.AccountAsync(CrmTokens.NorthwindTenant, Lifecycle.Churned, Cancellation);

        var customers = await PageAsync(
            app,
            new ReadEntityPage(
                ReadableEntity.Account,
                new RecordFilter(
                    FilterMatch.All,
                    [new RollupFilter("lifecycle", GuardOperator.Equals, "Customer")]),
                50));

        customers.Records.Count.ShouldBeGreaterThan(0);
        customers.Records.ShouldAllBe(row => row.Values["lifecycle"] == "Customer");
    }

    /// <summary>Two criteria are joined by the match the caller asked for.</summary>
    /// <remarks>
    /// <c>All</c> and <c>Any</c> are one bound boolean and one statement, which is what stops the
    /// number of criteria from deciding the number of predicates.
    /// </remarks>
    [Fact]
    public async Task TwoCriteriaAreJoinedByTheMatchAsked()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        await app.Crm.AccountAsync(CrmTokens.NorthwindTenant, Lifecycle.Customer, Cancellation);
        await app.Crm.AccountAsync(CrmTokens.NorthwindTenant, Lifecycle.Churned, Cancellation);

        var either = await PageAsync(
            app,
            new ReadEntityPage(
                ReadableEntity.Account,
                new RecordFilter(
                    FilterMatch.Any,
                    [
                        new RollupFilter("lifecycle", GuardOperator.Equals, "Customer"),
                        new RollupFilter("lifecycle", GuardOperator.Equals, "Churned"),
                    ]),
                50));

        var neither = await PageAsync(
            app,
            new ReadEntityPage(
                ReadableEntity.Account,
                new RecordFilter(
                    FilterMatch.All,
                    [
                        new RollupFilter("lifecycle", GuardOperator.Equals, "Customer"),
                        new RollupFilter("lifecycle", GuardOperator.Equals, "Churned"),
                    ]),
                50));

        either.Records.Count.ShouldBeGreaterThan(1);
        neither.Records.ShouldBeEmpty("no account is both at once.");
    }

    /// <summary>The page walks forward and stops, without repeating a row.</summary>
    /// <remarks>
    /// Keyset rather than an offset. The cursor is the last id of the page, so a row inserted
    /// between two requests cannot shift the second one — which an offset shows as a duplicate.
    /// </remarks>
    [Fact]
    public async Task ThePageWalksForwardWithoutRepeatingARow()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        for (var index = 0; index < 5; index++)
        {
            await app.Crm.AccountAsync(CrmTokens.NorthwindTenant, Lifecycle.Customer, Cancellation);
        }

        var seen = new List<Guid>();
        string? cursor = null;

        for (var page = 0; page < 10; page++)
        {
            var answer = await PageAsync(app, new ReadEntityPage(ReadableEntity.Account, null, 2, cursor));

            seen.AddRange(answer.Records.Select(record => record.RecordId));
            cursor = answer.NextCursor;

            if (cursor is null)
            {
                break;
            }
        }

        seen.Count.ShouldBeGreaterThanOrEqualTo(5);
        seen.Distinct().Count().ShouldBe(seen.Count, "a keyset page never hands back a row twice.");
        cursor.ShouldBeNull("the last page is short, so there is nothing to ask for next.");
    }

    /// <summary>A cursor that is not an id is refused rather than read as the beginning.</summary>
    /// <remarks>
    /// Treating it as "start again" would hand page one to somebody who asked for page nine, and
    /// they would scroll for a while before noticing.
    /// </remarks>
    [Fact]
    public async Task ACursorThatIsNotAnIdIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await app.PostAsync(
            Entities,
            new ReadEntityPage(ReadableEntity.Account, null, 25, "page-2"),
            CrmTokens.Northwind,
            idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// An opportunity's stage comes back as its name, and can be filtered on.
    /// </summary>
    /// <remarks>
    /// <strong>The one column of the answer that is not a column of the table.</strong>
    /// <c>stage_id</c> is a foreign key into the configured process, and a pipeline board cannot
    /// group by an identifier — so the store merges the stage's name into the projection. Read
    /// and filter are asserted together because they are the same expression: the filter
    /// evaluates <c>body-&gt;&gt;'stage'</c>, so a projection that dropped the name would leave
    /// the filter matching nothing rather than failing.
    /// </remarks>
    [Fact]
    public async Task AnOpportunityCarriesTheStagesName()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var process = await app.Crm.ProcessAsync(CrmTokens.NorthwindTenant, 1, true, Cancellation);
        var account = await app.Crm.AccountAsync(
            CrmTokens.NorthwindTenant, Lifecycle.Customer, Cancellation);
        var contact = await app.Crm.ContactAsync(CrmTokens.NorthwindTenant, account, Cancellation);

        await app.Crm.OpportunityAsync(
            CrmTokens.NorthwindTenant, account, contact, process.Stage, Cancellation);

        var named = await app.Crm.ScalarAsTenantAsync<string>(
            CrmTokens.NorthwindTenant,
            "SELECT name FROM process_stage WHERE stage_id = @stage",
            Cancellation,
            ("stage", process.Stage));

        var page = await PageAsync(app, new ReadEntityPage(ReadableEntity.Opportunity, null, 50));

        page.Records.Count.ShouldBeGreaterThan(0);
        page.Records[0]!.Values["stage"].ShouldBe(named);

        var filtered = await PageAsync(
            app,
            new ReadEntityPage(
                ReadableEntity.Opportunity,
                new RecordFilter(FilterMatch.All, [new RollupFilter("stage", GuardOperator.Equals, named!)]),
                50));

        filtered.Records.Count.ShouldBeGreaterThan(0, "the stage is filterable, not merely visible.");
    }

    /// <summary>
    /// Quotes, orders and activities are readable without being declarable.
    /// </summary>
    /// <remarks>
    /// <strong>Why a vocabulary of the read surface's own.</strong> <c>EntityKind</c> is what a
    /// validation rule, a custom field and a field policy are declared against, and each of those
    /// is a <c>CHECK (applies_to IN (…))</c> in a migration. Widening it so a list screen could
    /// show quotes would also let somebody declare a rule on an entity that has never had one.
    /// <c>ReadableEntity</c> says the two sets are different and names both.
    /// </remarks>
    [Fact]
    public async Task TheThreeReadOnlyKindsComeBackWithTheirOwnColumns()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var process = await app.Crm.ProcessAsync(CrmTokens.NorthwindTenant, 1, true, Cancellation);
        var account = await app.Crm.AccountAsync(
            CrmTokens.NorthwindTenant, Lifecycle.Customer, Cancellation);
        var contact = await app.Crm.ContactAsync(CrmTokens.NorthwindTenant, account, Cancellation);
        var opportunity = await app.Crm.OpportunityAsync(
            CrmTokens.NorthwindTenant, account, contact, process.Stage, Cancellation);

        await app.Crm.QuoteAsync(CrmTokens.NorthwindTenant, opportunity, Cancellation);

        var quotes = await PageAsync(app, new ReadEntityPage(ReadableEntity.Quote, null, 25));

        quotes.Records.Count.ShouldBeGreaterThan(0);
        quotes.Records[0]!.Values.Keys.ShouldContain("total");
        quotes.Records[0]!.Values.Keys.ShouldNotContain(
            "tenant_id", "the projection is the list, not the row.");

        // Empty is the right answer for these two here, and the assertion is that they answer at
        // all: an unmapped kind falls through the store's switch to the opportunity statement,
        // which would come back with an opportunity's columns rather than a refusal.
        (await PageAsync(app, new ReadEntityPage(ReadableEntity.Order, null, 25)))
            .Records.ShouldBeEmpty();

        (await PageAsync(app, new ReadEntityPage(ReadableEntity.Activity, null, 25)))
            .Records.ShouldBeEmpty();
    }

    /// <summary>A column of one read-only kind is not a column of another.</summary>
    [Fact]
    public async Task AQuotesColumnIsNotAnActivitys()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await app.PostAsync(
            Entities,
            new ReadEntityPage(
                ReadableEntity.Activity,
                new RecordFilter(FilterMatch.All, [new RollupFilter("total", GuardOperator.Equals, "1")]),
                25),
            CrmTokens.Northwind,
            idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString().ShouldBe("crm.entity_field_unknown");
    }

    /// <summary>One tenant's page never contains another tenant's rows.</summary>
    [Fact]
    public async Task OneTenantsPageIsOnlyItsOwn()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await app.Crm.AccountAsync(CrmTokens.NorthwindTenant, Lifecycle.Customer, Cancellation);
        await app.Crm.AccountAsync(CrmTokens.ContosoTenant, Lifecycle.Customer, Cancellation);

        var northwind = await PageAsync(app, new ReadEntityPage(ReadableEntity.Account, null, 100));
        var contoso = await PageAsync(
            app, new ReadEntityPage(ReadableEntity.Account, null, 100), CrmTokens.Contoso);

        var shared = northwind.Records
            .Select(record => record.RecordId)
            .Intersect(contoso.Records.Select(record => record.RecordId));

        shared.ShouldBeEmpty("row-level security is what makes this true, not the query.");
    }

    /// <summary>Reading is a read grant, and nothing more.</summary>
    [Fact]
    public async Task ReadingAPageNeedsOnlyTheReadGrant()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        (await app.PostAsync(
            Entities,
            new ReadEntityPage(ReadableEntity.Lead, null, 10),
            CrmTokens.Northwind,
            idempotencyKey: null))
            .StatusCode.ShouldBe(HttpStatusCode.OK, "a representative reads their own leads.");
    }

    // ------------------------------------------------------------------------------- fixtures

    /// <summary>
    /// A quote's lines are readable, and one tenant's are never another's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong><c>quote_line</c> carries no <c>tenant_id</c> of its own.</strong> Its isolation is
    /// a policy that hops through <c>quote</c> — migration <c>0002</c>'s "five that inherit" — so
    /// exposing it as a readable entity is the one addition here where getting the policy wrong
    /// would be invisible on every screen and catastrophic in exactly one way. The second half of
    /// this test is the whole reason for the first.
    /// </para>
    /// <para>
    /// The client needs it because a quote builder without its lines is four invented rows beside
    /// a real quote's total, which is what it was.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AQuotesLinesAreReadableAndOnlyByItsOwnTenant()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var account = await app.Crm.AccountAsync(
            CrmTokens.NorthwindTenant, Lifecycle.Customer, Cancellation);
        var contact = await app.Crm.ContactAsync(CrmTokens.NorthwindTenant, account, Cancellation);
        var process = await app.Crm.ProcessAsync(CrmTokens.NorthwindTenant, 1, true, Cancellation);
        var opportunity = await app.Crm.OpportunityAsync(
            CrmTokens.NorthwindTenant, account, contact, process.Stage, Cancellation);

        var (quote, _) = await app.Crm.QuoteAsync(
            CrmTokens.NorthwindTenant, opportunity, Cancellation);

        var lines = await PageAsync(
            app,
            new ReadEntityPage(
                ReadableEntity.QuoteLine,
                new RecordFilter(FilterMatch.All, [new RollupFilter("quote_id", GuardOperator.Equals, quote.ToString())]),
                50));

        lines.Records.Count.ShouldBe(1);

        var line = lines.Records[0]!;

        line.Values["sku"].ShouldBe("SKU-1");
        line.Values["quantity"].ShouldBe("2");

        // The whole point. Contoso holds crm.read and asks the same question of the same table.
        (await PageAsync(
            app,
            new ReadEntityPage(ReadableEntity.QuoteLine, null, 50),
            CrmTokens.Contoso))
            .Records.ShouldBeEmpty("the policy hops through the quote, and that quote is not theirs.");
    }

    private static async Task<RecordPage> PageAsync(
        CrmApplication app,
        ReadEntityPage query,
        string token = CrmTokens.Northwind)
    {
        var response = await app.PostAsync(Entities, query, token, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<RecordPage>(response);
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
