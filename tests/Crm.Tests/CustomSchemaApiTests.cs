using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// The run-time schema over HTTP: an administrator declares an object, its fields and an edge
/// between two of them, and a representative writes rows against what was declared.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The whole sequence is one test's worth of setup and several tests' worth of
/// claims</strong>, so the declarations are made by a helper and each test asserts one thing
/// about what follows from them. Every declaration goes over the wire, because the point is
/// that a tenant changes the shape of its own data with no deployment — a helper that seeded
/// <c>custom_field</c> with SQL would prove the tables work and nothing about that.
/// </para>
/// <para>
/// <strong>Two grants, and the split is the reason this is not just <c>crm.write</c>.</strong>
/// Declaring is <c>crm.admin</c>, which only the manager's token carries; writing records is
/// <c>crm.write</c>, which the representative's does. A representative who could declare a
/// required field could make every future write of every other flow fail.
/// </para>
/// </remarks>
public sealed class CustomSchemaApiTests
{
    private const string Objects = "/api/v1/crm/custom/objects";
    private const string Fields = "/api/v1/crm/custom/fields";
    private const string Relationships = "/api/v1/crm/custom/relationships";
    private const string Records = "/api/v1/crm/custom/records";
    private const string Links = "/api/v1/crm/custom/links";
    private const string EntityFields = "/api/v1/crm/custom/entity-fields";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A declared object is a row, and a record of it lands in its own <c>jsonb</c>.</summary>
    [Fact]
    public async Task ADeclaredObjectTakesRecordsShapedByItsFields()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var site = await DeclareObjectAsync(app, "site", "Site");

        await DeclareFieldAsync(app, site, "postcode", CustomFieldType.Text, required: true);
        await DeclareFieldAsync(app, site, "floor_area", CustomFieldType.Number, required: false);

        var response = await app.PostAsync(
            Records,
            new CreateRecord(site, new Dictionary<string, string?>
            {
                ["postcode"] = "EC1A 1BB",
                ["floor_area"] = "1250.5",
            }),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var created = await CrmApplication.ReadAsync<RecordCreated>(response);

        (await app.Crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Northwind,
            "SELECT values->>'postcode' FROM custom_record WHERE record_id = @id",
            Cancellation,
            ("id", created.RecordId)))
            .ShouldBe("EC1A 1BB");

        // jsonb_typeof rather than the value: a Number declared and stored as a string would
        // read back the same through ->> and order as text, which is what would make every
        // numeric guard over a custom field quietly wrong.
        (await app.Crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Northwind,
            "SELECT jsonb_typeof(values->'floor_area') FROM custom_record WHERE record_id = @id",
            Cancellation,
            ("id", created.RecordId)))
            .ShouldBe("number", "a Number field was stored as text.");
    }

    /// <summary>A value for a field nobody declared is refused, and names the field.</summary>
    [Fact]
    public async Task AValueForAnUndeclaredFieldIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var site = await DeclareObjectAsync(app, "site", "Site");

        var response = await app.PostAsync(
            Records,
            new CreateRecord(site, new Dictionary<string, string?> { ["invented"] = "x" }),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var problem = await Problem(response);

        problem.GetProperty("code").GetString().ShouldBe("crm.custom_field_not_declared");
        problem.GetProperty("field").GetString().ShouldBe("invented");

        (await app.Crm.ScalarAsOwnerAsync<long>("SELECT count(*) FROM custom_record", Cancellation))
            .ShouldBe(0);
    }

    /// <summary>A value that is not of the declared type is refused, and says which type.</summary>
    [Fact]
    public async Task AValueOfTheWrongTypeIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var site = await DeclareObjectAsync(app, "site", "Site");
        await DeclareFieldAsync(app, site, "floor_area", CustomFieldType.Number, required: false);

        var response = await app.PostAsync(
            Records,
            new CreateRecord(site, new Dictionary<string, string?> { ["floor_area"] = "big" }),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var problem = await Problem(response);

        problem.GetProperty("code").GetString().ShouldBe("crm.custom_value_wrong_type");
        problem.GetProperty("type").GetString().ShouldBe(nameof(CustomFieldType.Number));
    }

    /// <summary>A record missing a required field is refused.</summary>
    [Fact]
    public async Task ARecordMissingARequiredFieldIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var site = await DeclareObjectAsync(app, "site", "Site");
        await DeclareFieldAsync(app, site, "postcode", CustomFieldType.Text, required: true);

        var response = await app.PostAsync(
            Records,
            new CreateRecord(site, new Dictionary<string, string?>()),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.custom_value_required");
    }

    /// <summary>A representative cannot change the shape of the data; a manager can.</summary>
    /// <remarks>
    /// Both tokens are Northwind's and both carry <c>crm.write</c>. The only difference is
    /// <c>crm.admin</c>, so a pass here can only come from the stance on
    /// <c>crm.custom.define_object</c>.
    /// </remarks>
    [Fact]
    public async Task ARepresentativeCannotDeclareSchemaAndAManagerCan()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var refused = await app.PostAsync(
            Objects, new DefineObject("site", "Site"), CrmTokens.Northwind);

        refused.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden,
            "a representative declared an object, and could therefore declare a required field " +
            "that every future write has to carry.");

        (await app.Crm.ScalarAsOwnerAsync<long>("SELECT count(*) FROM custom_object", Cancellation))
            .ShouldBe(0);

        var allowed = await app.PostAsync(
            Objects, new DefineObject("site", "Site"), CrmTokens.NorthwindManager);

        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);

        // And a representative writes rows against it, which is the other half of the split.
        var field = await DeclareFieldAsync(
            app,
            (await CrmApplication.ReadAsync<ObjectDefined>(allowed)).ObjectId,
            "postcode",
            CustomFieldType.Text,
            required: false);

        field.ShouldNotBe(Guid.Empty);
    }

    /// <summary>A name that is not usable is refused before it reaches the CHECK constraint.</summary>
    [Fact]
    public async Task AnUnusableNameIsRefusedWithTheRuleRatherThanAConstraint()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await app.PostAsync(
            Objects, new DefineObject("Site Name!", "Site"), CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.custom_name_not_usable");
    }

    /// <summary>Two objects cannot share a name in one tenant, and two tenants can.</summary>
    [Fact]
    public async Task ANameIsTakenWithinATenantAndFreeInAnother()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await DeclareObjectAsync(app, "site", "Site");

        var again = await app.PostAsync(
            Objects, new DefineObject("site", "Site"), CrmTokens.NorthwindManager);

        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Problem(again)).GetProperty("code").GetString().ShouldBe("crm.custom_name_taken");

        // Contoso has no manager token in this sample, so the other direction is asserted on the
        // rows: the unique index is (tenant_id, name), and one tenant's declaration must not
        // occupy the name in another.
        (await app.Crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Contoso,
            "SELECT count(*) FROM custom_object WHERE name = 'site'",
            Cancellation))
            .ShouldBe(
                0,
                "Contoso can see Northwind's declaration, so the shape of one tenant's data is " +
                "visible to another.");
    }

    /// <summary>A declared relationship holds the cardinality it promised.</summary>
    [Fact]
    public async Task AOneToManyRelationshipRefusesASecondParent()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var site = await DeclareObjectAsync(app, "site", "Site");
        var contract = await DeclareObjectAsync(app, "contract", "Contract");

        var edge = await CrmApplication.ReadAsync<RelationshipDefined>(
            await app.PostAsync(
                Relationships,
                new DefineRelationship("site_contract", site, contract, CustomCardinality.OneToMany),
                CrmTokens.NorthwindManager));

        var first = await RecordAsync(app, site);
        var second = await RecordAsync(app, site);
        var target = await RecordAsync(app, contract);

        (await app.PostAsync(
            Links, new LinkRecords(edge.RelationshipId, first, target), CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var refused = await app.PostAsync(
            Links, new LinkRecords(edge.RelationshipId, second, target), CrmTokens.Northwind);

        refused.StatusCode.ShouldBe(
            HttpStatusCode.Conflict,
            "a OneToMany contract took a second site, so the cardinality an administrator " +
            "promised is not one the database keeps.");

        (await Problem(refused)).GetProperty("code").GetString()
            .ShouldBe("crm.custom_cardinality_broken");

        (await app.Crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind, "SELECT count(*) FROM custom_link", Cancellation))
            .ShouldBe(1);
    }

    /// <summary>A link whose ends are not of the objects the edge joins is refused.</summary>
    /// <remarks>
    /// The foreign keys of migration <c>0005</c> hold each end to <em>a</em> record and cannot
    /// say which object it belongs to, so without the capability's check a contract could be
    /// linked as though it were a site.
    /// </remarks>
    [Fact]
    public async Task ALinkBetweenTheWrongKindsOfRecordIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var site = await DeclareObjectAsync(app, "site", "Site");
        var contract = await DeclareObjectAsync(app, "contract", "Contract");

        var edge = await CrmApplication.ReadAsync<RelationshipDefined>(
            await app.PostAsync(
                Relationships,
                new DefineRelationship("site_contract", site, contract, CustomCardinality.OneToMany),
                CrmTokens.NorthwindManager));

        var oneSite = await RecordAsync(app, site);
        var another = await RecordAsync(app, site);

        var response = await app.PostAsync(
            Links, new LinkRecords(edge.RelationshipId, oneSite, another), CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.custom_record_not_found");
    }

    /// <summary>A custom field on a built-in entity is set on the row and merged, not replaced.</summary>
    [Fact]
    public async Task CustomFieldsOnALeadAreMergedRatherThanReplaced()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await DeclareEntityFieldAsync(app, EntityKind.Lead, "segment", CustomFieldType.Text);
        await DeclareEntityFieldAsync(app, EntityKind.Lead, "headcount", CustomFieldType.Number);

        var lead = await app.Crm.LeadAsync(CrmSchemaHarness.Northwind, Cancellation);

        (await app.PostAsync(
            EntityFields,
            new SetCustomFields(EntityKind.Lead, lead, new Dictionary<string, string?>
            {
                ["segment"] = "enterprise",
                ["headcount"] = "400",
            }),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await app.PostAsync(
            EntityFields,
            new SetCustomFields(
                EntityKind.Lead, lead, new Dictionary<string, string?> { ["headcount"] = "550" }),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var set = await CrmApplication.ReadAsync<CustomFieldsSet>(response);

        set.Values["headcount"].ShouldBe("550");
        set.Values["segment"].ShouldBe(
            "enterprise",
            "the second call did not mention segment and it was erased, so two clients editing " +
            "different fields of one lead would destroy each other's work.");
    }

    /// <summary>Setting a custom field on another tenant's lead is a 404.</summary>
    [Fact]
    public async Task CustomFieldsCannotBeSetOnAnotherTenantsLead()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await DeclareEntityFieldAsync(app, EntityKind.Lead, "segment", CustomFieldType.Text);

        var elsewhere = await app.Crm.LeadAsync(CrmSchemaHarness.Contoso, Cancellation);

        var response = await app.PostAsync(
            EntityFields,
            new SetCustomFields(
                EntityKind.Lead, elsewhere, new Dictionary<string, string?> { ["segment"] = "x" }),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await app.Crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Contoso,
            "SELECT custom_fields->>'segment' FROM lead WHERE lead_id = @id",
            Cancellation,
            ("id", elsewhere)))
            .ShouldBeNull("Northwind wrote into Contoso's lead.");
    }

    private static async Task<Guid> DeclareObjectAsync(CrmApplication app, string name, string label)
    {
        var response = await app.PostAsync(
            Objects, new DefineObject(name, label), CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "declaring '" + name + "' failed.");

        return (await CrmApplication.ReadAsync<ObjectDefined>(response)).ObjectId;
    }

    private static async Task<Guid> DeclareFieldAsync(
        CrmApplication app,
        Guid target,
        string name,
        CustomFieldType type,
        bool required)
    {
        var response = await app.PostAsync(
            Fields,
            new DefineField(null, target, name, name, type, required),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "declaring '" + name + "' failed.");

        return (await CrmApplication.ReadAsync<FieldDefined>(response)).FieldId;
    }

    private static async Task DeclareEntityFieldAsync(
        CrmApplication app,
        EntityKind kind,
        string name,
        CustomFieldType type)
    {
        var response = await app.PostAsync(
            Fields,
            new DefineField(kind, null, name, name, type, IsRequired: false),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "declaring '" + name + "' failed.");
    }

    private static async Task<Guid> RecordAsync(CrmApplication app, Guid target)
    {
        var response = await app.PostAsync(
            Records,
            new CreateRecord(target, new Dictionary<string, string?>()),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return (await CrmApplication.ReadAsync<RecordCreated>(response)).RecordId;
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
