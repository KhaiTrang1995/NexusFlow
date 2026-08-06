using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// What this API's JSON actually looks like on the wire.
/// </summary>
/// <remarks>
/// <para>
/// <strong>An enum crosses as its name, and this is the test that says so.</strong> The default
/// writes <c>CasePriority.Urgent</c> as <c>3</c>, and the number is a promise the sample cannot
/// keep: <c>CaseStatus</c> already gained <c>Escalated</c>, and had that member been inserted
/// rather than appended, every in-flight <c>3</c> would have quietly changed meaning — in a
/// direction no compiler and no test would have noticed.
/// </para>
/// <para>
/// <strong>The rest of the suite cannot catch this.</strong> Every other test posts a C# record
/// through <c>JsonContent.Create</c> and reads one back, so both ends move together and the wire
/// format is never named. This one asserts against raw JSON on both sides, which is the only
/// place a client written in another language is represented at all.
/// </para>
/// </remarks>
public sealed class WireFormatTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>An enum sent as its name is accepted.</summary>
    [Fact]
    public async Task AnEnumIsAcceptedAsItsName()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await PostRawAsync(
            app,
            "/api/v1/crm/service/policies",
            """
            {"name":"urgent_by_name","label":"Urgent","priority":"Urgent",
             "firstResponseMinutes":60,"resolutionMinutes":240,"businessHoursOnly":false}
            """);

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            "a client that writes the enum's name is the ordinary case for every language but C#.");
    }

    /// <summary>An enum comes back as its name.</summary>
    /// <remarks>
    /// The board is the one read whose response carries a live enum rather than a string the
    /// record already flattened — <c>ViewerScope.Role</c>. A number here is a number a caller has
    /// to look up in a table this API does not publish.
    /// </remarks>
    [Fact]
    public async Task AnEnumComesBackAsItsName()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await app.Crm.AsTenantAsync(
            CrmTokens.NorthwindTenant,
            """
            INSERT INTO org_member (tenant_id, user_id, display_name, role, reports_to)
            VALUES (@tenant, @user, 'B. Vance', 'Director', NULL)
            """,
            Cancellation,
            ("tenant", (object?)CrmTokens.NorthwindTenant),
            ("user", "manager-northwind-1"));

        await app.Crm.AsTenantAsync(
            CrmTokens.NorthwindTenant,
            """
            INSERT INTO plan_period (period_id, tenant_id, name, label, starts_on, ends_on, created_at)
            VALUES ('44444444-4444-4444-4444-444444444444', @tenant, 'fy26_q3', 'Q3 FY26',
                    '2026-07-01', '2026-09-30', now())
            """,
            Cancellation,
            ("tenant", (object?)CrmTokens.NorthwindTenant));

        await app.Crm.AsTenantAsync(
            CrmTokens.NorthwindTenant,
            """
            INSERT INTO sales_strategy (strategy_id, tenant_id, period_id, target_amount, currency, vision, created_at)
            VALUES (gen_random_uuid(), @tenant, '44444444-4444-4444-4444-444444444444',
                    1600000, 'EUR', 'Win the segment.', now())
            """,
            Cancellation,
            ("tenant", (object?)CrmTokens.NorthwindTenant));

        var response = await PostRawAsync(app, "/api/v1/crm/board", """{"period":"fy26_q3"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));
        var role = body.RootElement.GetProperty("viewedAs").GetProperty("role");

        role.ValueKind.ShouldBe(
            JsonValueKind.String,
            "an ordinal is a value a caller has to look up in a table this API does not publish.");
        role.GetString().ShouldBe("Director");
    }

    /// <summary>An ordinal is still accepted on the way in, and that is worth writing down.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The converter reads both and writes one.</strong> `JsonStringEnumConverter` accepts
    /// a number as well as a name, so a caller that was written against the old wire format keeps
    /// working. That is the reason the change is safe to make at all.
    /// </para>
    /// <para>
    /// <strong>And it is the limit of what the change bought.</strong> Nothing this repository
    /// serialises depends on an ordinal any more, but a third-party caller sending <c>3</c> is
    /// still choosing an encoding that changes meaning the day a member is inserted rather than
    /// appended. Refusing the number would close that, at the cost of a hand-written converter and
    /// of every existing numeric caller; asserted here rather than left as a surprise.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnOrdinalIsStillAcceptedOnTheWayIn()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await PostRawAsync(
            app,
            "/api/v1/crm/service/policies",
            """
            {"name":"urgent_by_number","label":"Urgent","priority":3,
             "firstResponseMinutes":60,"resolutionMinutes":240,"businessHoursOnly":false}
            """);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static Task<HttpResponseMessage> PostRawAsync(CrmApplication app, string route, string json) =>
        app.PostRawAsync(route, json, CrmTokens.NorthwindManager);
}
