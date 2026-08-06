using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// Reading records back: ad-hoc queries, saved views, and the read half of field-level security.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Until this, a caller could write a custom record and never see one again.</strong>
/// That is the gap these routes close, and it is also what makes read-side masking tractable:
/// migration <c>0007</c> declined to do it on the grounds that a rule not applied to every
/// projection leaks, and the answer is that there is exactly one projection.
/// </para>
/// <para>
/// <strong>A redacted field is present and says so.</strong> A caller who cannot tell a withheld
/// field from an unset one cannot tell a permissions problem from a data problem, and will chase
/// the wrong one — which is why the value is a marker and the page names what it withheld.
/// </para>
/// </remarks>
public sealed class QueryApiTests
{
    private const string Objects = "/api/v1/crm/custom/objects";
    private const string Fields = "/api/v1/crm/custom/fields";
    private const string Records = "/api/v1/crm/custom/records";
    private const string Views = "/api/v1/crm/custom/list-views";
    private const string Queries = "/api/v1/crm/custom/queries";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>An ad-hoc query returns the records it was asked for.</summary>
    [Fact]
    public async Task AQueryReturnsTheRecordsOfItsObject()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        await RecordAsync(app, site, "ldn", 100);
        await RecordAsync(app, site, "par", 250);

        var page = await QueryAsync(app, new QueryRecords(site, null, null, 10), CrmTokens.Northwind);

        page.Records.Count.ShouldBe(2);
        page.Records.Select(r => r.Values["label"]).ShouldBe(["ldn", "par"], ignoreOrder: true);
        page.Redacted.ShouldBeEmpty();
    }

    /// <summary>A filter narrows what comes back.</summary>
    [Fact]
    public async Task AFilterNarrowsWhatComesBack()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        await RecordAsync(app, site, "ldn", 100);
        await RecordAsync(app, site, "par", 250);

        var page = await QueryAsync(
            app,
            new QueryRecords(site, null, new RollupFilter("floor_area", GuardOperator.GreaterThan, "200"), 10),
            CrmTokens.Northwind);

        page.Records.ShouldHaveSingleItem().Values["label"].ShouldBe("par");
    }

    /// <summary>A saved view is the query, and the request's own filter is not consulted.</summary>
    /// <remarks>
    /// A request that named a view and a filter would have two answers about which wins;
    /// whichever this build picked would surprise half its callers, so the view decides and the
    /// ad-hoc arguments are documented as ignored.
    /// </remarks>
    [Fact]
    public async Task ASavedViewDecidesWhatComesBack()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        await RecordAsync(app, site, "ldn", 100);
        await RecordAsync(app, site, "par", 250);
        await RecordAsync(app, site, "ber", 900);

        (await app.PostAsync(
            Views,
            new DefineListView(
                site, "large_sites", "Large sites",
                new RollupFilter("floor_area", GuardOperator.GreaterThan, "200"),
                OrderBy: "label",
                Limit: 50),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var page = await QueryAsync(
            app,
            new QueryRecords(null, "large_sites", null, 1),
            CrmTokens.Northwind);

        page.Records.Select(r => r.Values["label"]).ShouldBe(
            ["ber", "par"],
            "the view's filter, ordering and limit are what decide — the request asked for one row.");
    }

    /// <summary>A view naming a field nobody declared is refused when it is saved.</summary>
    [Fact]
    public async Task AViewNamingAnUndeclaredFieldIsRefusedWhenItIsSaved()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        var response = await app.PostAsync(
            Views,
            new DefineListView(site, "typo", "Typo", null, OrderBy: "flooor_area", Limit: 10),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            "a view ordering by a field nobody declared would return the same order for ever " +
            "and whoever pressed the button would believe it.");

        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.custom_rule_field_not_declared");
    }

    /// <summary>A field with a read grant is redacted for a caller without it, and named.</summary>
    [Fact]
    public async Task AFieldWithAReadGrantIsRedactedAndTheCallerIsTold()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        await FieldAsync(app, new DefineField(
            null, site, "board_notes", "Board notes", CustomFieldType.Text, IsRequired: false,
            ReadPermission: "crm.admin"));

        (await app.PostAsync(
            Records,
            new CreateRecord(site, new Dictionary<string, string?>
            {
                ["label"] = "ldn",
                ["board_notes"] = "acquisition target",
            }),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var asRep = await QueryAsync(app, new QueryRecords(site, null, null, 10), CrmTokens.Northwind);

        var seen = asRep.Records.ShouldHaveSingleItem();

        seen.Values["board_notes"].ShouldBe(
            "[redacted]",
            "the field was omitted, so a caller cannot tell a permissions problem from a data one.");
        seen.Values["label"].ShouldBe("ldn", "everything else must still come back.");

        asRep.Redacted.ShouldBe(["board_notes"]);

        var asManager = await QueryAsync(
            app, new QueryRecords(site, null, null, 10), CrmTokens.NorthwindManager);

        asManager.Records.ShouldHaveSingleItem().Values["board_notes"].ShouldBe(
            "acquisition target", "the manager holds crm.admin and still could not read it.");
        asManager.Redacted.ShouldBeEmpty();
    }

    /// <summary>Read and write grants are separate, and a caller can hold one without the other.</summary>
    /// <remarks>
    /// The whole reason migration <c>0009</c> adds a second column rather than reusing
    /// <c>required_permission</c>: "may change it" and "may see it" are different questions.
    /// </remarks>
    [Fact]
    public async Task AFieldCanBeWritableAndUnreadableToTheSameCaller()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        await FieldAsync(app, new DefineField(
            null, site, "case_note", "Case note", CustomFieldType.Text, IsRequired: false,
            ReadPermission: "crm.admin"));

        // The representative writes it — no required_permission — and cannot read it back.
        (await app.PostAsync(
            Records,
            new CreateRecord(site, new Dictionary<string, string?>
            {
                ["label"] = "ldn",
                ["case_note"] = "written by the clerk",
            }),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.OK, "the write grant is not the read grant.");

        (await QueryAsync(app, new QueryRecords(site, null, null, 10), CrmTokens.Northwind))
            .Records.ShouldHaveSingleItem().Values["case_note"].ShouldBe("[redacted]");
    }

    /// <summary>Masking is what a caller sees, never what a rule decides on.</summary>
    /// <remarks>
    /// A validation rule that could be defeated by not holding a grant would be a rule anybody
    /// could switch off by using a weaker token.
    /// </remarks>
    [Fact]
    public async Task AValidationRuleStillReadsAFieldTheCallerCannotSee()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        await FieldAsync(app, new DefineField(
            null, site, "risk", "Risk", CustomFieldType.Text, IsRequired: false,
            ReadPermission: "crm.admin"));

        (await app.PostAsync(
            "/api/v1/crm/custom/validation-rules",
            new DefineValidationRule(
                null, site, "no_high_risk", "risk", GuardOperator.Equals, "high",
                "A high-risk site cannot be recorded."),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var refused = await app.PostAsync(
            Records,
            new CreateRecord(site, new Dictionary<string, string?>
            {
                ["label"] = "ldn",
                ["risk"] = "high",
            }),
            CrmTokens.Northwind);

        refused.StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            "the rule read a masked value, so a weaker token would defeat every rule over a " +
            "read-restricted field.");
    }

    /// <summary>A query naming both a view and an object is refused, and naming neither.</summary>
    [Fact]
    public async Task AQueryNamingBothOrNeitherIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        foreach (var ambiguous in new[]
                 {
                     new QueryRecords(site, "some_view", null, 10),
                     new QueryRecords(null, null, null, 10),
                 })
        {
            var response = await app.PostAsync(
                Queries, ambiguous, CrmTokens.Northwind, idempotencyKey: null);

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Problem(response)).GetProperty("code").GetString().ShouldBe("crm.query_ambiguous");
        }
    }

    /// <summary>A limit outside the range is an error rather than a silent clamp.</summary>
    /// <remarks>
    /// A caller who asked for ten thousand and silently got five hundred would page through the
    /// same five hundred for ever, believing they had them all.
    /// </remarks>
    [Fact]
    public async Task ALimitOutsideTheRangeIsRefusedRatherThanClamped()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        var response = await app.PostAsync(
            Queries, new QueryRecords(site, null, null, 10_000), CrmTokens.Northwind,
            idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var problem = await Problem(response);

        problem.GetProperty("code").GetString().ShouldBe("crm.query_limit_out_of_range");
        problem.GetProperty("max").GetInt32().ShouldBe(QueryLimits.Max);
    }

    /// <summary>One tenant's query returns none of another tenant's records.</summary>
    [Fact]
    public async Task AQueryReturnsNoneOfAnotherTenantsRecords()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        await RecordAsync(app, site, "ldn", 100);

        var response = await app.PostAsync(
            Queries, new QueryRecords(site, null, null, 10), CrmTokens.Contoso,
            idempotencyKey: null);

        // The object is Northwind's, so to Contoso it does not exist — the same answer as an id
        // that never did, which is the point.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.custom_object_not_found");
    }

    // ------------------------------------------------------------------------------- fixtures

    private static async Task<Guid> WorldAsync(CrmApplication app)
    {
        var site = await ObjectAsync(app, "site");

        await FieldAsync(app, new DefineField(
            null, site, "label", "Label", CustomFieldType.Text, IsRequired: false));
        await FieldAsync(app, new DefineField(
            null, site, "floor_area", "Floor area", CustomFieldType.Number, IsRequired: false));

        return site;
    }

    private static async Task<Guid> ObjectAsync(CrmApplication app, string name)
    {
        var response = await app.PostAsync(
            Objects, new DefineObject(name, name), CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "declaring '" + name + "' failed.");

        return (await CrmApplication.ReadAsync<ObjectDefined>(response)).ObjectId;
    }

    private static async Task FieldAsync(CrmApplication app, DefineField field)
    {
        var response = await app.PostAsync(Fields, field, CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "declaring '" + field.Name + "' failed.");
    }

    private static async Task RecordAsync(CrmApplication app, Guid target, string label, int area)
    {
        var response = await app.PostAsync(
            Records,
            new CreateRecord(target, new Dictionary<string, string?>
            {
                ["label"] = label,
                ["floor_area"] = area.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<RecordPage> QueryAsync(
        CrmApplication app, QueryRecords query, string token)
    {
        // No Idempotency-Key: the query route is the one flow here that does not declare
        // Idempotent, because a read has nothing to make idempotent.
        var response = await app.PostAsync(Queries, query, token, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<RecordPage>(response);
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
