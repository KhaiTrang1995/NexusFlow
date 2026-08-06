using System.Net;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// <c>POST /api/v1/crm/leads</c>: the response a caller sees, and the rows it left behind.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The claim under test is the one <c>CaptureLeadFlow</c> makes in prose:</strong> one
/// write and one event, staged in the same transaction. Neither half is observable from the
/// response — a lead id comes back whether or not <c>lead.created</c> was staged, and an
/// endpoint that wrote nothing at all could return the same body. So both are read back out of
/// the database afterwards, the lead under the caller's tenant scope and the outbox row as the
/// schema's owner.
/// </para>
/// <para>
/// <strong>The tenant is asserted as a negative.</strong> That Northwind's lead exists proves
/// only that an insert happened; what has to hold is that Contoso cannot see it, because the
/// tenant on the row came off the token's <c>tid</c> claim and off nothing the caller could
/// influence (ADR-0046).
/// </para>
/// </remarks>
public sealed class LeadCaptureApiTests
{
    private const string Route = "/api/v1/crm/leads";

    private const string CountLead = "SELECT count(*) FROM lead WHERE lead_id = @id";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A captured lead is a row, and the event that announces it is staged with it.</summary>
    [Fact]
    public async Task ACapturedLeadIsWrittenAndItsEventIsStagedWithIt()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await app.PostAsync(
            Route,
            new CaptureLead("Northwind Traders", "A Person", "a.person@example.test", LeadSource.Web),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var captured = await CrmApplication.ReadAsync<LeadCaptured>(response);

        captured.LeadId.ShouldNotBe(Guid.Empty);

        (await app.Crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind, CountLead, Cancellation, ("id", captured.LeadId)))
            .ShouldBe(
                1,
                "the endpoint answered with a lead id and no lead was written. A 200 is not " +
                "evidence of a write.");

        (await app.Crm.ScalarAsOwnerAsync<long>(
            "SELECT count(*) FROM outbox_event WHERE type = 'lead.created' AND published_at IS NULL",
            Cancellation))
            .ShouldBe(
                1,
                "the lead exists and nothing announced it. A consumer cannot tell that from a " +
                "lead that was never captured, which is what staging the event in the write's " +
                "own transaction exists to prevent.");
    }

    /// <summary>The company on the row is the company that was sent.</summary>
    /// <remarks>
    /// Separate from the count above, and not folded into it: a capability that inserted a
    /// placeholder row would satisfy the count. This is the cheapest assertion that the payload
    /// reached the statement.
    /// </remarks>
    [Fact]
    public async Task TheRowCarriesWhatTheRequestSent()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await app.PostAsync(
            Route,
            new CaptureLead("Contoso Ltd", "Another Person", "another@example.test", LeadSource.Referral),
            CrmTokens.Contoso);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var captured = await CrmApplication.ReadAsync<LeadCaptured>(response);

        (await app.Crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Contoso,
            "SELECT company FROM lead WHERE lead_id = @id",
            Cancellation,
            ("id", captured.LeadId)))
            .ShouldBe("Contoso Ltd");

        (await app.Crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Contoso,
            "SELECT source FROM lead WHERE lead_id = @id",
            Cancellation,
            ("id", captured.LeadId)))
            .ShouldBe(nameof(LeadSource.Referral));
    }

    /// <summary>
    /// The lead belongs to the tenant the token resolved to, and the other tenant cannot reach it.
    /// </summary>
    [Fact]
    public async Task ALeadCapturedByOneTenantIsInvisibleToTheOther()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var captured = await CrmApplication.ReadAsync<LeadCaptured>(
            await app.PostAsync(
                Route,
                new CaptureLead("Northwind Traders", "A Person", null, LeadSource.Web),
                CrmTokens.Northwind));

        (await app.Crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Contoso, CountLead, Cancellation, ("id", captured.LeadId)))
            .ShouldBe(
                0,
                "Contoso reached a lead Northwind's token captured. The tenant on the row is " +
                "not the one the caller's tid claim resolved to.");

        (await app.Crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind, CountLead, Cancellation, ("id", captured.LeadId)))
            .ShouldBe(1, "and the tenant that captured it still reaches it.");
    }

    /// <summary>The route declares <c>Idempotent</c>, so a request without a key is refused.</summary>
    /// <remarks>
    /// <para>
    /// Asserted because it is the part of the contract a client meets first and no other test
    /// covers: <c>[HttpTrigger(Idempotent = true)]</c> makes <c>Idempotency-Key</c> mandatory,
    /// and a caller that omits it gets a <c>400</c> rather than a lead.
    /// </para>
    /// <para>
    /// The same key twice is deliberately not asserted here. What a replay does is the
    /// platform's contract rather than this sample's, and <c>FlowX.Http.Tests</c> is where it is
    /// held; a second copy of that assertion over a CRM route would fail for two different
    /// reasons and name neither.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ACaptureWithNoIdempotencyKeyIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await app.PostAsync(
            Route,
            new CaptureLead("Northwind Traders", "A Person", null, LeadSource.Web),
            CrmTokens.Northwind,
            idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await app.Crm.ScalarAsOwnerAsync<long>("SELECT count(*) FROM lead", Cancellation))
            .ShouldBe(0, "the request was refused and a row was written anyway.");
    }

    /// <summary>An anonymous caller writes nothing.</summary>
    /// <remarks>
    /// <c>crm.lead.capture</c> is <c>crm.write</c> on the step, so the refusal holds however the
    /// capability is reached. Asserted over HTTP because that is the transport where a caller
    /// with no token is a routine event rather than a programming error.
    /// </remarks>
    [Fact]
    public async Task AnAnonymousCaptureIsRefusedAndWritesNothing()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await app.PostAsync(
            Route,
            new CaptureLead("Nobody", "Nobody", null, LeadSource.Web),
            token: null);

        response.IsSuccessStatusCode.ShouldBeFalse("an anonymous caller wrote a lead.");
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);

        (await app.Crm.ScalarAsOwnerAsync<long>("SELECT count(*) FROM lead", Cancellation))
            .ShouldBe(0, "the request was refused and a row was written anyway.");
    }
}
