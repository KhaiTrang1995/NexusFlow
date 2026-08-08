using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// What a tenant changes about how their CRM looks, and what a client draws from it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The first assertion here is about a defect that had been shipping.</strong> A custom
/// object and a custom field have carried a label since migration <c>0005</c> and nothing ever
/// read it back — describe returned the identifier twice, so every client drew <c>floor_area</c>
/// where somebody had typed "Floor area". Nothing failed, which is why it lasted: an API that
/// answers with a plausible wrong value is the kind nobody reports.
/// </para>
/// <para>
/// <strong>The rest is about the view knowing how it is drawn.</strong> A client that receives a
/// query and nothing else chooses between a table, a board and a stack of cards by itself — so the
/// choice is made once per client, differently, and one saved view becomes three features.
/// </para>
/// </remarks>
public sealed class TenantUiSettingsApiTests
{
    private const string Objects = "/api/v1/crm/custom/objects";
    private const string Fields = "/api/v1/crm/custom/fields";
    private const string Records = "/api/v1/crm/custom/records";
    private const string Views = "/api/v1/crm/custom/list-views";
    private const string Labels = "/api/v1/crm/labels";
    private const string Describe = "/api/v1/crm/describe";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Describe returns the label an administrator typed, not the identifier.</summary>
    [Fact]
    public async Task DescribeReturnsTheLabelSomebodyTypedAndNotTheIdentifier()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        var described = (await DescribeAsync(app)).Objects.ShouldHaveSingleItem();

        described.Label.ShouldBe("Site");
        described.Fields.Single(field => field.Name == "floor_area").Label.ShouldBe(
            "Floor area", "the label has been stored since 0005 and read back by nothing.");

        described.Id.ShouldBe(site);
    }

    /// <summary>Renaming a field changes its label and never its identifier.</summary>
    /// <remarks>
    /// The identifier is in saved views, in guards, in roll-up filters, in jsonb keys and in
    /// whatever a client cached last week. A settings screen that moved it would break all of
    /// them at once, and silently.
    /// </remarks>
    [Fact]
    public async Task RenamingAFieldMovesItsLabelAndNotItsIdentifier()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        var field = (await DescribeAsync(app)).Objects[0].Fields
            .Single(described => described.Name == "floor_area");

        var id = await FieldIdAsync(app, site, "floor_area");

        await LabelAsync(app, new SetLabel(null, id, null, null, "Area (m²)"));

        var renamed = (await DescribeAsync(app)).Objects[0].Fields
            .Single(described => described.Name == "floor_area");

        renamed.Label.ShouldBe("Area (m²)");
        renamed.Name.ShouldBe(field.Name, "the identifier is what everything else refers to.");
    }

    /// <summary>A tenant who calls a Lead an Enquiry says so, and every screen follows.</summary>
    [Fact]
    public async Task ABuiltInEntityAndItsColumnsCanBeRenamedPerTenant()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await LabelAsync(app, new SetLabel(null, null, EntityKind.Lead, null, "Enquiry"));
        await LabelAsync(app, new SetLabel(null, null, EntityKind.Lead, "company", "Organisation"));

        var lead = (await DescribeAsync(app)).Entities.Single(entity => entity.Kind == "Lead");

        lead.Label.ShouldBe("Enquiry");
        lead.Kind.ShouldBe("Lead", "the kind is the identifier and never moves.");
        lead.Columns.Single(column => column.Name == "company").Label.ShouldBe("Organisation");

        // Untouched columns come back under their own names, so a client draws a whole form from
        // one answer rather than a form with holes in it.
        lead.Columns.Single(column => column.Name == "email").Label.ShouldBe("email");

        var contoso = await DescribeAsync(app, CrmTokens.Contoso);

        contoso.Entities.Single(entity => entity.Kind == "Lead").Label.ShouldBe(
            "Lead", "one tenant's vocabulary is not another's.");
    }

    /// <summary>A built-in column comes back with the values it may take.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The client was holding the second copy.</strong> Describe named a lead's
    /// <c>status</c> column and said nothing about what a status is, so every client transcribed
    /// <c>LeadStatus</c> into its own language to draw a chip — and the web client's copy offered
    /// a <c>Nurture</c> that filters to nothing and omitted the <c>Converted</c> a conversion
    /// produces. Nothing failed; the list was just quietly missing a filter.
    /// </para>
    /// <para>
    /// <strong>The expected values are written out here rather than read off the enum.</strong>
    /// Asserting <c>Enum.GetNames&lt;LeadStatus&gt;()</c> against a describe that returns
    /// <c>Enum.GetNames&lt;LeadStatus&gt;()</c> asserts that two calls agree, which they do
    /// whatever either says. A member added to the enum is meant to fail this line: what a lead
    /// may be is a wire contract, and a client that cached yesterday's answer is holding the list
    /// below.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ABuiltInColumnCarriesTheValuesItMayTake()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var lead = (await DescribeAsync(app)).Entities.Single(entity => entity.Kind == "Lead");

        lead.Columns.Single(column => column.Name == "status").Options.ShouldBe(
            ["New", "Working", "Qualified", "Disqualified", "Converted"],
            "a picklist column that answers with no picklist is why the client had its own.");

        lead.Columns.Single(column => column.Name == "company").Options.ShouldBeEmpty(
            "a company is free text. Empty says 'draw a text box' — a column that claimed a " +
            "vocabulary it has not got would be a picker over five wrong answers.");
    }

    /// <summary>A column the entity does not have is refused rather than stored.</summary>
    /// <remarks>
    /// Stored, it would be a row nothing reads and nobody knows to delete — a settings screen
    /// slowly filling with typos.
    /// </remarks>
    [Fact]
    public async Task RenamingAColumnTheEntityDoesNotHaveIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await app.PostAsync(
            Labels,
            new SetLabel(null, null, EntityKind.Lead, "amount", "Value"),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.label_column_unknown");
    }

    /// <summary>A board carries the lane field, the lane order and the limit.</summary>
    [Fact]
    public async Task ABoardCarriesEverythingNeededToDrawIt()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        (await app.PostAsync(
            Views,
            new DefineListView(
                site, "by_stage", "By stage", null, null, 50,
                new ViewLayout(
                    ViewKind.Kanban,
                    GroupBy: "region",
                    Lanes: ["north", "south"],
                    WipLimit: 5)),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var view = (await DescribeAsync(app)).Objects[0].Views.ShouldHaveSingleItem();

        view.Kind.ShouldBe("Kanban");
        view.GroupBy.ShouldBe("region");
        view.Lanes.ShouldBe(["north", "south"], "lane order is what makes a board of nine readable.");
        view.WipLimit.ShouldBe(5);
    }

    /// <summary>A table carries its columns, in the order somebody arranged them.</summary>
    [Fact]
    public async Task ATableCarriesItsColumnsInOrder()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        (await app.PostAsync(
            Views,
            new DefineListView(
                site, "compact", "Compact", null, null, 50,
                new ViewLayout(ViewKind.List, Columns: ["floor_area", "region"])),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var view = (await DescribeAsync(app)).Objects[0].Views.ShouldHaveSingleItem();

        view.Kind.ShouldBe("List");
        view.Columns.ShouldBe(["floor_area", "region"]);
    }

    /// <summary>A view saved with no layout is a plain table, as it always was.</summary>
    [Fact]
    public async Task AViewSavedWithNoLayoutIsStillAPlainTable()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        (await app.PostAsync(
            Views,
            new DefineListView(site, "everything", "Everything", null, null, 50),
            CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var view = (await DescribeAsync(app)).Objects[0].Views.ShouldHaveSingleItem();

        view.Kind.ShouldBe("List");
        view.Columns.ShouldBeEmpty("empty means every field, which is what leaving it out meant.");
        view.GroupBy.ShouldBeNull();
    }

    /// <summary>A shape without what it needs, and a setting for the wrong shape, are both refused.</summary>
    /// <remarks>
    /// The second matters more than it looks. A stored setting nothing reads is a setting somebody
    /// changes, saves, and watches do nothing — and the next person spends an afternoon on it.
    /// </remarks>
    [Fact]
    public async Task ALayoutIsRefusedWhenItsSettingsDoNotMatchItsShape()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        (await ViewCode(app, site, "a", new ViewLayout(ViewKind.Kanban)))
            .ShouldBe("crm.view_setting_missing");

        (await ViewCode(app, site, "b", new ViewLayout(ViewKind.List, GroupBy: "region")))
            .ShouldBe("crm.view_setting_not_of_kind");

        (await ViewCode(app, site, "c", new ViewLayout(ViewKind.Card, TitleField: "region", WipLimit: 3)))
            .ShouldBe("crm.view_setting_not_of_kind");
    }

    /// <summary>A layout naming a field nobody declared is refused when the view is saved.</summary>
    [Fact]
    public async Task ALayoutNamingAnUndeclaredFieldIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await WorldAsync(app);

        (await ViewCode(app, site, "board", new ViewLayout(ViewKind.Kanban, GroupBy: "no_such_field")))
            .ShouldBe("crm.custom_rule_field_not_declared");
    }

    /// <summary>A multi-select holds several declared options and refuses one that is not.</summary>
    [Fact]
    public async Task AMultiSelectHoldsSeveralOptionsAndRefusesAStrayOne()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await ObjectAsync(app, "site");

        await FieldAsync(app, new DefineField(
            null, site, "tags", "Tags", CustomFieldType.MultiPicklist, IsRequired: false,
            Options:
            [
                new CustomFieldOption("gold", "Gold"),
                new CustomFieldOption("silver", "Silver"),
                new CustomFieldOption("bronze", "Bronze"),
            ]));

        (await app.PostAsync(
            Records,
            new CreateRecord(site, new Dictionary<string, string?>
            {
                ["tags"] = """["gold","bronze"]""",
            }),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var stray = await app.PostAsync(
            Records,
            new CreateRecord(site, new Dictionary<string, string?>
            {
                ["tags"] = """["gold","platinum"]""",
            }),
            CrmTokens.Northwind);

        stray.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(stray)).GetProperty("code").GetString().ShouldBe("crm.custom_value_not_an_option");
    }

    /// <summary>A multi-select comes back as the same string it went in as.</summary>
    /// <remarks>
    /// It is stored as a real jsonb array — so the GIN index over <c>values</c> can answer which
    /// records chose an option — and read back as that array's JSON. A caller that had to send one
    /// shape and parse another would be a caller with two representations of one value.
    /// </remarks>
    [Fact]
    public async Task AMultiSelectComesBackAsTheStringItWentInAs()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await ObjectAsync(app, "site");

        await FieldAsync(app, new DefineField(
            null, site, "tags", "Tags", CustomFieldType.MultiPicklist, IsRequired: false,
            Options: [new CustomFieldOption("gold", "Gold"), new CustomFieldOption("silver", "Silver")]));

        (await app.PostAsync(
            Records,
            new CreateRecord(site, new Dictionary<string, string?>
            {
                ["tags"] = """["gold","silver"]""",
            }),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await app.PostAsync(
            "/api/v1/crm/custom/queries",
            new QueryRecords(site, null, null, 10),
            CrmTokens.Northwind,
            idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var record = (await CrmApplication.ReadAsync<RecordPage>(response)).Records
            .ShouldHaveSingleItem();

        record.Values["tags"].ShouldBe("""["gold","silver"]""");
    }

    /// <summary>A multi-select with no options is refused, like a picklist with none.</summary>
    [Fact]
    public async Task AMultiSelectWithNoOptionsIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var site = await ObjectAsync(app, "site");

        var response = await app.PostAsync(
            Fields,
            new DefineField(
                null, site, "tags", "Tags", CustomFieldType.MultiPicklist, IsRequired: false),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.custom_picklist_has_no_options");
    }

    /// <summary>Renaming what everybody sees is administrative.</summary>
    [Fact]
    public async Task RenamingIsAdministrative()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        (await app.PostAsync(
            Labels,
            new SetLabel(null, null, EntityKind.Lead, null, "Enquiry"),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(
                HttpStatusCode.Forbidden, "a representative does not rename the tenant's world.");
    }

    // ------------------------------------------------------------------------------- fixtures

    private static async Task<string?> ViewCode(
        CrmApplication app, Guid site, string name, ViewLayout layout)
    {
        var response = await app.PostAsync(
            Views,
            new DefineListView(site, name, name, null, null, 50, layout),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        return (await Problem(response)).GetProperty("code").GetString();
    }

    private static async Task LabelAsync(CrmApplication app, SetLabel label)
    {
        (await app.PostAsync(Labels, label, CrmTokens.NorthwindManager))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<SchemaDescription> DescribeAsync(
        CrmApplication app, string? token = null)
    {
        var response = await app.PostAsync(
            Describe, new DescribeSchema(null), token ?? CrmTokens.Northwind, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<SchemaDescription>(response);
    }

    private static async Task<Guid> WorldAsync(CrmApplication app)
    {
        var site = await ObjectAsync(app, "site");

        await FieldAsync(app, new DefineField(
            null, site, "region", "Region", CustomFieldType.Text, IsRequired: false));
        await FieldAsync(app, new DefineField(
            null, site, "floor_area", "Floor area", CustomFieldType.Number, IsRequired: false));

        return site;
    }

    private static async Task<Guid> ObjectAsync(CrmApplication app, string name)
    {
        var response = await app.PostAsync(
            Objects,
            new DefineObject(name, char.ToUpperInvariant(name[0]) + name[1..]),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return (await CrmApplication.ReadAsync<ObjectDefined>(response)).ObjectId;
    }

    private static async Task<Guid> FieldAsync(CrmApplication app, DefineField field)
    {
        var response = await app.PostAsync(Fields, field, CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "declaring '" + field.Name + "' failed.");

        return (await CrmApplication.ReadAsync<FieldDefined>(response)).FieldId;
    }

    private static async Task<Guid> FieldIdAsync(CrmApplication app, Guid site, string name) =>
        await app.Crm.ScalarAsTenantAsync<Guid>(
            CrmTokens.NorthwindTenant,
            "SELECT field_id FROM custom_field WHERE object_id = @object AND name = @name",
            Cancellation,
            ("object", site),
            ("name", name));

    private static async Task<JsonElement> Problem(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
