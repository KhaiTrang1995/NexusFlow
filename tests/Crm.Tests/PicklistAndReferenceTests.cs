using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// The two field types that make the dynamic schema usable rather than merely dynamic.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A picklist is a closed set, and the closure is the whole feature.</strong> Free text
/// with a convention is the same field with the closure removed, and the first misspelling is a
/// row no report matches. What is asserted here is that the set is enforced on the way in, by
/// something that reads what the administrator declared.
/// </para>
/// <para>
/// <strong>A reference is checked against the object it names, not merely against existence.</strong>
/// The foreign keys of migration <c>0005</c> hold a record to <em>a</em> record; without the
/// capability's check a <c>contract</c> could sit in a field declared to hold a <c>site</c>, and
/// every reader of it would have to re-check.
/// </para>
/// </remarks>
public sealed class PicklistAndReferenceTests
{
    private const string Objects = "/api/v1/crm/custom/objects";
    private const string Fields = "/api/v1/crm/custom/fields";
    private const string Records = "/api/v1/crm/custom/records";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A picklist accepts a declared value and refuses anything else.</summary>
    [Fact]
    public async Task APicklistAcceptsOnlyWhatWasDeclared()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var site = await ObjectAsync(app, "site");

        await FieldAsync(app, new DefineField(
            null, site, "tier", "Tier", CustomFieldType.Picklist, IsRequired: false,
            Options: [new CustomFieldOption("gold", "Gold"), new CustomFieldOption("silver", "Silver")]));

        (await app.PostAsync(
            Records,
            new CreateRecord(site, new Dictionary<string, string?> { ["tier"] = "gold" }),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var refused = await app.PostAsync(
            Records,
            new CreateRecord(site, new Dictionary<string, string?> { ["tier"] = "platinum" }),
            CrmTokens.Northwind);

        refused.StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            "a picklist took a value nobody declared, which is the closure not being enforced.");

        var problem = await Problem(refused);

        problem.GetProperty("code").GetString().ShouldBe("crm.custom_value_not_an_option");
        problem.GetProperty("allowed").GetString().ShouldBe(
            "gold, silver",
            "the refusal must say what may be sent, or the caller has to go and find out.");
    }

    /// <summary>A picklist with no values is refused at declaration, not at first use.</summary>
    [Fact]
    public async Task APicklistWithNoOptionsIsRefusedWhenItIsDeclared()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var site = await ObjectAsync(app, "site");

        var response = await app.PostAsync(
            Fields,
            new DefineField(null, site, "tier", "Tier", CustomFieldType.Picklist, IsRequired: false),
            CrmTokens.NorthwindManager);

        response.StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            "a closed set of nothing accepts nothing, and an administrator should hear that " +
            "when they declare it rather than when somebody first tries to use it.");

        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.custom_picklist_has_no_options");
    }

    /// <summary>The options are stored in the order they were declared.</summary>
    /// <remarks>
    /// Order is what a rendered list shows, so a set that came back alphabetically would be an
    /// administrator's ordering silently discarded.
    /// </remarks>
    [Fact]
    public async Task TheOptionsKeepTheOrderTheyWereDeclaredIn()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var site = await ObjectAsync(app, "site");

        await FieldAsync(app, new DefineField(
            null, site, "tier", "Tier", CustomFieldType.Picklist, IsRequired: false,
            Options:
            [
                new CustomFieldOption("gold", "Gold"),
                new CustomFieldOption("bronze", "Bronze"),
                new CustomFieldOption("silver", "Silver"),
            ]));

        (await app.Crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Northwind,
            """
            SELECT string_agg(value, ',' ORDER BY ordinal)
            FROM custom_field_option o
            JOIN custom_field f ON f.field_id = o.field_id
            WHERE f.name = 'tier'
            """,
            Cancellation))
            .ShouldBe("gold,bronze,silver");
    }

    /// <summary>A reference resolves to a record of the object it names, and refuses others.</summary>
    [Fact]
    public async Task AReferenceMustPointAtARecordOfTheObjectItNames()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var site = await ObjectAsync(app, "site");
        var contract = await ObjectAsync(app, "contract");

        await FieldAsync(app, new DefineField(
            null, contract, "site_ref", "Site", CustomFieldType.Reference, IsRequired: false,
            References: site));

        var aSite = await RecordAsync(app, site);
        var aContract = await RecordAsync(app, contract);

        (await app.PostAsync(
            Records,
            new CreateRecord(contract, new Dictionary<string, string?> { ["site_ref"] = aSite.ToString() }),
            CrmTokens.Northwind))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var refused = await app.PostAsync(
            Records,
            new CreateRecord(
                contract, new Dictionary<string, string?> { ["site_ref"] = aContract.ToString() }),
            CrmTokens.Northwind);

        refused.StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            "a field declared to hold a site took a contract, and every reader of it would " +
            "have to re-check what it actually points at.");

        (await Problem(refused)).GetProperty("code").GetString()
            .ShouldBe("crm.custom_reference_not_resolvable");
    }

    /// <summary>A reference to another tenant's record is refused.</summary>
    /// <remarks>
    /// The id is real and of the right object; the only thing wrong with it is whose it is. The
    /// scoped read is what makes this indistinguishable from an id that never existed.
    /// </remarks>
    [Fact]
    public async Task AReferenceToAnotherTenantsRecordIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var site = await ObjectAsync(app, "site");
        var contract = await ObjectAsync(app, "contract");

        await FieldAsync(app, new DefineField(
            null, contract, "site_ref", "Site", CustomFieldType.Reference, IsRequired: false,
            References: site));

        // Contoso's row of Northwind's object cannot exist — the object is Northwind's — so the
        // elsewhere record is written straight into Contoso against an object of its own.
        var elsewhereObject = Guid.NewGuid();

        await app.Crm.AsTenantAsync(
            CrmSchemaHarness.Contoso,
            """
            INSERT INTO custom_object (object_id, tenant_id, name, label, created_at)
            VALUES (@id, @tenant, 'site', 'Site', now())
            """,
            Cancellation,
            ("id", elsewhereObject),
            ("tenant", CrmSchemaHarness.Contoso));

        var elsewhere = Guid.NewGuid();

        await app.Crm.AsTenantAsync(
            CrmSchemaHarness.Contoso,
            """
            INSERT INTO custom_record (record_id, tenant_id, object_id, values, created_at)
            VALUES (@id, @tenant, @object, '{}'::jsonb, now())
            """,
            Cancellation,
            ("id", elsewhere),
            ("tenant", CrmSchemaHarness.Contoso),
            ("object", elsewhereObject));

        var response = await app.PostAsync(
            Records,
            new CreateRecord(
                contract, new Dictionary<string, string?> { ["site_ref"] = elsewhere.ToString() }),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("code").GetString()
            .ShouldBe("crm.custom_reference_not_resolvable");
    }

    /// <summary>A reference declared without a target is refused.</summary>
    [Fact]
    public async Task AReferenceWithNoTargetIsRefused()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var site = await ObjectAsync(app, "site");

        var response = await app.PostAsync(
            Fields,
            new DefineField(
                null, site, "site_ref", "Site", CustomFieldType.Reference, IsRequired: false),
            CrmTokens.NorthwindManager);

        response.IsSuccessStatusCode.ShouldBeFalse(
            "a reference pointing at nothing is a field nothing can resolve.");
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

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK, "declaring '" + field.Name + "' failed.");
    }

    private static async Task<Guid> RecordAsync(CrmApplication app, Guid target)
    {
        var response = await app.PostAsync(
            Records, new CreateRecord(target, new Dictionary<string, string?>()), CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return (await CrmApplication.ReadAsync<RecordCreated>(response)).RecordId;
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
