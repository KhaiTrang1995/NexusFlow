using System.Net;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// What one tenant cannot reach of another tenant's, asked over HTTP by a caller holding a real
/// token.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The gap this closes.</strong> <c>TenantIsolationTests</c> proves the policies of
/// migration <c>0002</c> hold, on connections it opens itself; <c>EntityPageApiTests</c> proves
/// two tenants' pages do not overlap. Neither asks the question an attacker asks. A record id is
/// a guessable handle that travels — it is in a URL somebody pasted, in an export, in a support
/// ticket — and the interesting request is not "list what I may see", it is "give me this one",
/// made with a valid token of the wrong tenant. Nothing asserted what the wire answers to that.
/// </para>
/// <para>
/// <strong>And the answer has to be uninformative, not merely empty.</strong> An empty page is
/// the right body; it is the right body only if the same body comes back for an id that belongs
/// to nobody. A surface that says "no such record" to one and "not yours" to the other — by
/// status, by code, or by any byte of the answer — is an oracle: a caller with a list of ids
/// learns which of them exist in a tenant they cannot read, which is most of what they wanted.
/// </para>
/// </remarks>
public sealed class EntityPageTenantIsolationTests
{
    private const string Entities = "/api/v1/crm/entities";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// An id of one tenant's account reads nothing under the other's token, both ways round.
    /// </summary>
    /// <remarks>
    /// Both directions and a control, because an isolation that holds for whichever tenant the
    /// test happened to arrange first holds by accident, and a zero next to no positive is a
    /// zero that a mistyped column name would also produce.
    /// </remarks>
    [Fact]
    public async Task AnAccountOfAnotherTenantIsNotReadableByItsId()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var northwind = await app.Crm.AccountAsync(
            CrmTokens.NorthwindTenant, Lifecycle.Customer, Cancellation);

        var contoso = await app.Crm.AccountAsync(
            CrmTokens.ContosoTenant, Lifecycle.Customer, Cancellation);

        (await ByIdAsync(app, northwind, CrmTokens.Northwind)).Records.ShouldHaveSingleItem()
            .RecordId.ShouldBe(
                northwind,
                "a tenant cannot reach its own account by id, so every zero below is vacuous.");

        (await ByIdAsync(app, northwind, CrmTokens.Contoso)).Records.ShouldBeEmpty(
            "Contoso read Northwind's account by its id. The id is a handle that travels, and " +
            "a token that authenticates is not a token that entitles.");

        (await ByIdAsync(app, contoso, CrmTokens.Northwind)).Records.ShouldBeEmpty(
            "Northwind read Contoso's account by its id. Asserted separately from the other " +
            "direction: one that holds one way holds by accident.");
    }

    /// <summary>
    /// The refusal is indistinguishable from an id that exists nowhere.
    /// </summary>
    /// <remarks>
    /// The whole response is compared, not the status: a code, a count, a cursor or a message
    /// that differed between the two would tell a caller which of the ids they hold are real in
    /// a tenant they cannot read. Existence is the thing being withheld, and it leaks through any
    /// byte that changes.
    /// </remarks>
    [Fact]
    public async Task AnotherTenantsRecordIsNotFoundRatherThanForbidden()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var northwind = await app.Crm.AccountAsync(
            CrmTokens.NorthwindTenant, Lifecycle.Customer, Cancellation);

        var real = await RawAsync(app, northwind, CrmTokens.Contoso);
        var invented = await RawAsync(app, Guid.NewGuid(), CrmTokens.Contoso);

        real.Status.ShouldBe(
            HttpStatusCode.OK,
            "a page of no rows is a page. A 403 here would confirm the record exists, which is " +
            "the fact the caller is being refused.");

        real.Body.ShouldBe(
            invented.Body,
            "the answer for a record that exists in another tenant differs from the answer for " +
            "one that exists nowhere, so the surface is an oracle for ids.");

        real.Status.ShouldBe(invented.Status);
    }

    // ------------------------------------------------------------------------------- fixtures

    private static ReadEntityPage OneAccount(Guid id) =>
        new(
            ReadableEntity.Account,
            new RecordFilter(
                FilterMatch.All,
                [new RollupFilter("account_id", GuardOperator.Equals, id.ToString())]),
            2);

    private static async Task<RecordPage> ByIdAsync(CrmApplication app, Guid id, string token)
    {
        var response = await app.PostAsync(Entities, OneAccount(id), token, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<RecordPage>(response);
    }

    private static async Task<(HttpStatusCode Status, string Body)> RawAsync(
        CrmApplication app, Guid id, string token)
    {
        var response = await app.PostAsync(Entities, OneAccount(id), token, idempotencyKey: null);

        return (response.StatusCode, await response.Content.ReadAsStringAsync(Cancellation));
    }
}
