using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// Roll-up summary fields: a parent's field whose value is an aggregate over its children.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Which end is the parent is not a naming choice.</strong> Migration <c>0006</c>'s
/// cardinality trigger constrains the <c>to</c> end of a <c>OneToMany</c> to one link, so a
/// <c>from</c> record may have many children and a <c>to</c> record has one parent. A roll-up
/// that got this backwards would aggregate one row for ever, and the declaration check is what
/// stops it.
/// </para>
/// <para>
/// <strong>Materialised, not computed on read.</strong> The value is written into the parent's
/// <c>custom_fields</c> so that everything already reading them — a transition guard, a report, a
/// projection — sees it without a second path. That is asserted by reading the row rather than
/// the response.
/// </para>
/// </remarks>
public sealed class RollupApiTests
{
    private const string Objects = "/api/v1/crm/custom/objects";
    private const string Fields = "/api/v1/crm/custom/fields";
    private const string Relationships = "/api/v1/crm/custom/relationships";
    private const string Records = "/api/v1/crm/custom/records";
    private const string Links = "/api/v1/crm/custom/links";
    private const string Rollups = "/api/v1/crm/custom/rollups";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A count roll-up follows its children as they are linked.</summary>
    [Fact]
    public async Task ACountRollupFollowsTheChildrenAsTheyAreLinked()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await RollupAsync(app, new DefineRollup(
            world.SiteCount, world.Edge, RollupAggregate.Count, null, null));

        var account = await RecordAsync(app, world.Account, []);

        await LinkAsync(app, world.Edge, account, await RecordAsync(app, world.Site, Site("l1", 100)));

        (await ValueAsync(app, account, "site_count")).ShouldBe("1");

        await LinkAsync(app, world.Edge, account, await RecordAsync(app, world.Site, Site("l2", 250)));

        (await ValueAsync(app, account, "site_count")).ShouldBe(
            "2", "the roll-up was written once and never recomputed.");
    }

    /// <summary>A sum roll-up totals the children's values, as a number.</summary>
    /// <remarks>
    /// <c>jsonb_typeof</c> rather than the value: a total stored as text would order <c>"9"</c>
    /// after <c>"42"</c>, so every numeric guard over the roll-up would quietly be wrong.
    /// </remarks>
    [Fact]
    public async Task ASumRollupTotalsItsChildrenAndStoresANumber()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await RollupAsync(app, new DefineRollup(
            world.TotalArea, world.Edge, RollupAggregate.Sum, world.FloorArea, null));

        var account = await RecordAsync(app, world.Account, []);

        await LinkAsync(app, world.Edge, account, await RecordAsync(app, world.Site, Site("l1", 100)));
        await LinkAsync(app, world.Edge, account, await RecordAsync(app, world.Site, Site("l2", 250)));

        (await ValueAsync(app, account, "total_area")).ShouldBe("350");

        (await app.Crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Northwind,
            "SELECT jsonb_typeof(values->'total_area') FROM custom_record WHERE record_id = @id",
            Cancellation,
            ("id", account)))
            .ShouldBe("number", "a total stored as text orders '9' after '42'.");
    }

    /// <summary>A filtered roll-up counts only the children the filter admits.</summary>
    /// <remarks>
    /// The difference between "how many sites" and "how many large sites", which is the one
    /// anybody actually configures.
    /// </remarks>
    [Fact]
    public async Task AFilteredRollupCountsOnlyWhatTheFilterAdmits()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await RollupAsync(app, new DefineRollup(
            world.SiteCount, world.Edge, RollupAggregate.Count, null,
            new RollupFilter("floor_area", GuardOperator.GreaterThan, "200")));

        var account = await RecordAsync(app, world.Account, []);

        await LinkAsync(app, world.Edge, account, await RecordAsync(app, world.Site, Site("l1", 100)));

        (await ValueAsync(app, account, "site_count")).ShouldBe(
            "0", "a site under the bound was counted.");

        await LinkAsync(app, world.Edge, account, await RecordAsync(app, world.Site, Site("l2", 250)));

        (await ValueAsync(app, account, "site_count")).ShouldBe("1");
    }

    /// <summary>Two roll-ups over one edge are both recomputed.</summary>
    /// <remarks>
    /// A recompute that updated whichever it found first would leave the other stale, which is
    /// the failure that makes people stop trusting the numbers.
    /// </remarks>
    [Fact]
    public async Task TwoRollupsOverOneEdgeAreBothRecomputed()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await RollupAsync(app, new DefineRollup(
            world.SiteCount, world.Edge, RollupAggregate.Count, null, null));
        await RollupAsync(app, new DefineRollup(
            world.TotalArea, world.Edge, RollupAggregate.Max, world.FloorArea, null));

        var account = await RecordAsync(app, world.Account, []);

        await LinkAsync(app, world.Edge, account, await RecordAsync(app, world.Site, Site("l1", 100)));
        await LinkAsync(app, world.Edge, account, await RecordAsync(app, world.Site, Site("l2", 250)));

        (await ValueAsync(app, account, "site_count")).ShouldBe("2");
        (await ValueAsync(app, account, "total_area")).ShouldBe("250");
    }

    /// <summary>A roll-up's field is read-only afterwards, and the refusal says why.</summary>
    [Fact]
    public async Task ARollupFieldCannotBeWrittenByACaller()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await RollupAsync(app, new DefineRollup(
            world.SiteCount, world.Edge, RollupAggregate.Count, null, null));

        var response = await app.PostAsync(
            Records,
            new CreateRecord(world.Account, new Dictionary<string, string?> { ["site_count"] = "99" }),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            "a caller wrote a computed field, and the next recompute would silently discard it.");

        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.custom_field_is_computed");
    }

    /// <summary>A roll-up whose field is on the wrong object is refused at declaration.</summary>
    /// <remarks>
    /// It would walk an edge no child of that parent is on and report zero for ever, which reads
    /// exactly like a parent that genuinely has no children — the worst kind of wrong number,
    /// because nobody investigates a zero.
    /// </remarks>
    [Fact]
    public async Task ARollupWhoseFieldIsOnTheChildIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        var response = await app.PostAsync(
            Rollups,
            new DefineRollup(world.FloorArea, world.Edge, RollupAggregate.Count, null, null),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.rollup_field_not_on_parent");
    }

    /// <summary>A summing roll-up with no source field is refused, and a counting one with one.</summary>
    [Theory]
    [InlineData(RollupAggregate.Sum, false)]
    [InlineData(RollupAggregate.Count, true)]
    public async Task ASourceFieldThatDoesNotMatchTheAggregateIsRefused(
        RollupAggregate aggregate, bool withSource)
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        var response = await app.PostAsync(
            Rollups,
            new DefineRollup(
                world.SiteCount, world.Edge, aggregate, withSource ? world.FloorArea : null, null),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.rollup_source_mismatch");
    }

    /// <summary>Two roll-ups cannot compute one field.</summary>
    [Fact]
    public async Task TwoRollupsCannotComputeOneField()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        await RollupAsync(app, new DefineRollup(
            world.SiteCount, world.Edge, RollupAggregate.Count, null, null));

        var again = await app.PostAsync(
            Rollups,
            new DefineRollup(world.SiteCount, world.Edge, RollupAggregate.Sum, world.FloorArea, null),
            CrmTokens.NorthwindManager);

        again.StatusCode.ShouldBe(
            HttpStatusCode.Conflict,
            "one field with two roll-ups has two answers and no rule about which wins.");

        (await Problem(again)).GetProperty("code").GetString()
            .ShouldBe("crm.rollup_field_already_computed");
    }

    /// <summary>A representative cannot declare a roll-up.</summary>
    /// <remarks>Declaring one takes a field away from every writer, which is a bigger act than writing one.</remarks>
    [Fact]
    public async Task ARepresentativeCannotDeclareARollup()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);
        var world = await WorldAsync(app);

        (await app.PostAsync(
            Rollups,
            new DefineRollup(world.SiteCount, world.Edge, RollupAggregate.Count, null, null),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ------------------------------------------------------------------------------- fixtures

    private sealed record World(
        Guid Account, Guid Site, Guid Edge, Guid SiteCount, Guid TotalArea, Guid FloorArea);

    /// <summary>Two objects, a one-to-many edge, and the fields both ends need.</summary>
    private static async Task<World> WorldAsync(CrmApplication app)
    {
        var account = await ObjectAsync(app, "managed_account");
        var site = await ObjectAsync(app, "site");

        var siteCount = await FieldAsync(app, new DefineField(
            null, account, "site_count", "Sites", CustomFieldType.Number, IsRequired: false));
        var totalArea = await FieldAsync(app, new DefineField(
            null, account, "total_area", "Total area", CustomFieldType.Number, IsRequired: false));
        var floorArea = await FieldAsync(app, new DefineField(
            null, site, "floor_area", "Floor area", CustomFieldType.Number, IsRequired: false));

        await FieldAsync(app, new DefineField(
            null, site, "label", "Label", CustomFieldType.Text, IsRequired: false));

        var edge = await CrmApplication.ReadAsync<RelationshipDefined>(
            await app.PostAsync(
                Relationships,
                new DefineRelationship("account_site", account, site, CustomCardinality.OneToMany),
                CrmTokens.NorthwindManager));

        return new World(account, site, edge.RelationshipId, siteCount, totalArea, floorArea);
    }

    private static Dictionary<string, string?> Site(string label, int area) =>
        new(StringComparer.Ordinal)
        {
            ["label"] = label,
            ["floor_area"] = area.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

    private static async Task<Guid> ObjectAsync(CrmApplication app, string name)
    {
        var response = await app.PostAsync(
            Objects, new DefineObject(name, name), CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "declaring '" + name + "' failed.");

        return (await CrmApplication.ReadAsync<ObjectDefined>(response)).ObjectId;
    }

    private static async Task<Guid> FieldAsync(CrmApplication app, DefineField field)
    {
        var response = await app.PostAsync(Fields, field, CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "declaring '" + field.Name + "' failed.");

        return (await CrmApplication.ReadAsync<FieldDefined>(response)).FieldId;
    }

    private static async Task RollupAsync(CrmApplication app, DefineRollup rollup)
    {
        var response = await app.PostAsync(Rollups, rollup, CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "declaring the roll-up failed.");
    }

    private static async Task<Guid> RecordAsync(
        CrmApplication app, Guid target, Dictionary<string, string?> values)
    {
        var response = await app.PostAsync(
            Records, new CreateRecord(target, values), CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return (await CrmApplication.ReadAsync<RecordCreated>(response)).RecordId;
    }

    private static async Task LinkAsync(CrmApplication app, Guid edge, Guid parent, Guid child)
    {
        var response = await app.PostAsync(
            Links, new LinkRecords(edge, parent, child), CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static ValueTask<string?> ValueAsync(CrmApplication app, Guid record, string field) =>
        app.Crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Northwind,
            "SELECT values->>'" + field + "' FROM custom_record WHERE record_id = @id",
            Cancellation,
            ("id", record));

    private static async Task<JsonElement> Problem(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
