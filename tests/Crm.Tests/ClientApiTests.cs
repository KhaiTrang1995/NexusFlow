using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// What a mobile or web client needs before it can render anything: a description of the schema,
/// and a page it can scroll.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A client cannot hard-code this schema, which is the whole point of the sample.</strong>
/// An administrator invented the objects, the fields, the picklist values and the views at run
/// time, so a client has nothing to draw a form or a list from until it asks. Compiling them into
/// the client would put the schema in two places and make every tenant's build different.
/// </para>
/// <para>
/// <strong>The permissions come back resolved, not as rules.</strong> A client that received
/// <c>readPermission: "crm.admin"</c> would have to know which grants its user holds and
/// reimplement the comparison — a second copy of an authorisation rule, in JavaScript. It gets
/// <c>canRead</c> and <c>canWrite</c>, computed from the same scopes the write path checks.
/// </para>
/// </remarks>
public sealed class ClientApiTests
{
    private const string Objects = "/api/v1/crm/custom/objects";
    private const string Fields = "/api/v1/crm/custom/fields";
    private const string Records = "/api/v1/crm/custom/records";
    private const string Views = "/api/v1/crm/custom/list-views";
    private const string Queries = "/api/v1/crm/custom/queries";
    private const string Describe = "/api/v1/crm/describe";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A description carries everything a form needs to be drawn.</summary>
    [Fact]
    public async Task ADescriptionCarriesWhatAFormNeedsToBeDrawn()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        await FieldAsync(app, new DefineField(
            null, site, "tier", "Tier", CustomFieldType.Picklist, IsRequired: true,
            Options: [new CustomFieldOption("gold", "Gold"), new CustomFieldOption("silver", "Silver")]));

        var description = await DescribeAsync(app, CrmTokens.Northwind);

        description.Version.ShouldBe(
            CrmMigrator.TargetVersion,
            "a client caches a description, and this is what tells it the cache is stale.");

        var described = description.Objects.ShouldHaveSingleItem();

        described.Name.ShouldBe("site");
        described.Id.ShouldBe(site, "the id is what a query is asked for.");

        var tier = described.Fields.Single(field => field.Name == "tier");

        tier.Type.ShouldBe(nameof(CustomFieldType.Picklist));
        tier.IsRequired.ShouldBeTrue();
        tier.Options.ShouldBe(
            ["gold", "silver"],
            "a picklist with no options in the description is a select a client cannot fill.");
    }

    /// <summary>The four built-in kinds are described even when nothing was added to them.</summary>
    /// <remarks>
    /// An absent key and an empty list are the same fact only if somebody remembers they are. A
    /// client rendering a lead form needs to know there are no custom fields as much as it needs
    /// to know there are three.
    /// </remarks>
    [Fact]
    public async Task TheBuiltInKindsAreDescribedEvenWhenEmpty()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var description = await DescribeAsync(app, CrmTokens.Northwind);

        description.Entities.Select(entity => entity.Kind).ShouldBe(
            ["Lead", "Account", "Contact", "Opportunity"]);

        description.Entities.ShouldAllBe(entity => entity.Fields.Count == 0);
    }

    /// <summary>A field's permissions come back resolved for the caller who asked.</summary>
    [Fact]
    public async Task PermissionsAreResolvedForTheCallerWhoAsked()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        await FieldAsync(app, new DefineField(
            null, site, "board_notes", "Board notes", CustomFieldType.Text, IsRequired: false,
            RequiredPermission: "crm.admin", ReadPermission: "crm.admin"));

        var asRep = Field(await DescribeAsync(app, CrmTokens.Northwind), "board_notes");

        asRep.CanRead.ShouldBeFalse();
        asRep.CanWrite.ShouldBeFalse(
            "a form that accepts a field and fails on submit is a form somebody retypes.");

        var asManager = Field(await DescribeAsync(app, CrmTokens.NorthwindManager), "board_notes");

        asManager.CanRead.ShouldBeTrue();
        asManager.CanWrite.ShouldBeTrue();
    }

    /// <summary>A computed field is writable by nobody, whatever grants they hold.</summary>
    [Fact]
    public async Task AComputedFieldIsWritableByNobody()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        var total = await FieldAsync(app, new DefineField(
            null, site, "total", "Total", CustomFieldType.Number, IsRequired: false));

        (await app.PostAsync(
            "/api/v1/crm/custom/formulas",
            new DefineFormula(total, FormulaOperation.Multiply, "floor_area", null, "2"),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var described = Field(await DescribeAsync(app, CrmTokens.NorthwindManager), "total");

        described.IsComputed.ShouldBeTrue();
        described.CanWrite.ShouldBeFalse(
            "a form that offers to edit a roll-up is a form whose next screen contradicts it.");
    }

    /// <summary>The saved views over an object are described with it.</summary>
    [Fact]
    public async Task TheSavedViewsAreDescribedWithTheirObject()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        (await app.PostAsync(
            Views,
            new DefineListView(site, "large_sites", "Large sites", null, null, Limit: 50),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var described = (await DescribeAsync(app, CrmTokens.Northwind)).Objects.ShouldHaveSingleItem();

        var view = described.Views.ShouldHaveSingleItem();

        view.Name.ShouldBe("large_sites");
        view.Label.ShouldBe("Large sites");
    }

    /// <summary>One tenant's description holds none of another tenant's objects.</summary>
    [Fact]
    public async Task ADescriptionHoldsNoneOfAnotherTenantsObjects()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await WorldAsync(app);

        (await DescribeAsync(app, CrmTokens.Contoso)).Objects.ShouldBeEmpty(
            "Contoso was told the shape of Northwind's data, which says what they sell.");
    }

    // ------------------------------------------------------------------------------- paging

    /// <summary>A cursor walks the whole list once, in order, with no repeats.</summary>
    [Fact]
    public async Task ACursorWalksTheWholeListExactlyOnce()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        for (var i = 0; i < 7; i++)
        {
            await RecordAsync(app, site, "site-" + i);
        }

        var seen = new List<string?>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var page = await PageAsync(app, site, limit: 3, cursor);

            seen.AddRange(page.Records.Select(record => record.Values["label"]));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);

        seen.ShouldBe(
            [.. Enumerable.Range(0, 7).Select(i => "site-" + i)],
            "the walk repeated or skipped a row, which is what an OFFSET does when a row is " +
            "inserted mid-scroll.");
    }

    /// <summary>The last page carries no cursor, so a client never asks for nothing.</summary>
    [Fact]
    public async Task TheLastPageCarriesNoCursor()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        await RecordAsync(app, site, "a");
        await RecordAsync(app, site, "b");

        (await PageAsync(app, site, limit: 5, null)).NextCursor.ShouldBeNull(
            "a partial page is the last one.");
    }

    /// <summary>A cursor against a sorted query is refused rather than ignored.</summary>
    /// <remarks>
    /// Ignoring it would restart an ordered list at the top on every scroll; honouring it against
    /// the wrong key would skip and repeat rows. Both are worse than an error a client can read.
    /// </remarks>
    [Fact]
    public async Task ACursorAgainstASortedQueryIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        await RecordAsync(app, site, "a");

        (await app.PostAsync(
            Views,
            new DefineListView(
                site, "by_label", "By label", null,
                new RecordOrder("label", false, false), Limit: 2),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await app.PostAsync(
            Queries,
            new QueryRecords(null, "by_label", null, 2, After: RecordCursor.For(DateTimeOffset.UtcNow, Guid.NewGuid())),
            CrmTokens.Northwind,
            idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.query_cursor_needs_insertion_order");
    }

    /// <summary>A cursor this API did not issue is refused.</summary>
    [Fact]
    public async Task ACursorThisApiDidNotIssueIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        var response = await app.PostAsync(
            Queries,
            new QueryRecords(site, null, null, 10, After: "not-a-cursor"),
            CrmTokens.Northwind,
            idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.query_cursor_not_usable");
    }

    // ------------------------------------------------------------------------------- fixtures

    private static DescribedField Field(SchemaDescription description, string name) =>
        description.Objects.ShouldHaveSingleItem().Fields.Single(field => field.Name == name);

    private static async Task<SchemaDescription> DescribeAsync(CrmApplication app, string token)
    {
        var response = await app.PostAsync(
            Describe, new DescribeSchema(null), token, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<SchemaDescription>(response);
    }

    private static async Task<RecordPage> PageAsync(
        CrmApplication app, Guid site, int limit, string? cursor)
    {
        var response = await app.PostAsync(
            Queries, new QueryRecords(site, null, null, limit, cursor), CrmTokens.Northwind,
            idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<RecordPage>(response);
    }

    private static async Task<Guid> WorldAsync(CrmApplication app)
    {
        var response = await app.PostAsync(
            Objects, new DefineObject("site", "Site"), CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var site = (await CrmApplication.ReadAsync<ObjectDefined>(response)).ObjectId;

        await FieldAsync(app, new DefineField(
            null, site, "label", "Label", CustomFieldType.Text, IsRequired: false));
        await FieldAsync(app, new DefineField(
            null, site, "floor_area", "Floor area", CustomFieldType.Number, IsRequired: false));

        return site;
    }

    private static async Task<Guid> FieldAsync(CrmApplication app, DefineField field)
    {
        var response = await app.PostAsync(Fields, field, CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "declaring '" + field.Name + "' failed.");

        return (await CrmApplication.ReadAsync<FieldDefined>(response)).FieldId;
    }

    private static async Task RecordAsync(CrmApplication app, Guid site, string label)
    {
        var response = await app.PostAsync(
            Records,
            new CreateRecord(site, new Dictionary<string, string?> { ["label"] = label }),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
