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

        var page = await PageAsync(app, new ReadEntityPage(EntityKind.Account, null, 50));

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
                EntityKind.Account,
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
                EntityKind.Account,
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
                EntityKind.Account,
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
                EntityKind.Account,
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
            var answer = await PageAsync(app, new ReadEntityPage(EntityKind.Account, null, 2, cursor));

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
            new ReadEntityPage(EntityKind.Account, null, 25, "page-2"),
            CrmTokens.Northwind,
            idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>One tenant's page never contains another tenant's rows.</summary>
    [Fact]
    public async Task OneTenantsPageIsOnlyItsOwn()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await app.Crm.AccountAsync(CrmTokens.NorthwindTenant, Lifecycle.Customer, Cancellation);
        await app.Crm.AccountAsync(CrmTokens.ContosoTenant, Lifecycle.Customer, Cancellation);

        var northwind = await PageAsync(app, new ReadEntityPage(EntityKind.Account, null, 100));
        var contoso = await PageAsync(
            app, new ReadEntityPage(EntityKind.Account, null, 100), CrmTokens.Contoso);

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
            new ReadEntityPage(EntityKind.Lead, null, 10),
            CrmTokens.Northwind,
            idempotencyKey: null))
            .StatusCode.ShouldBe(HttpStatusCode.OK, "a representative reads their own leads.");
    }

    // ------------------------------------------------------------------------------- fixtures

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
