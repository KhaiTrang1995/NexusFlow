using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// What migration <c>0005</c> enforces, asserted against a real PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The half of the dynamic schema that no capability can be trusted with.</strong>
/// <c>CustomSchemaApiTests</c> proves the capabilities refuse an undeclared field and a broken
/// cardinality, and every one of those checks lives in a process that a repair script, a future
/// capability or an administrator with a <c>psql</c> session goes around. The triggers and the
/// policies are what hold when nothing else does, which is why they are asserted separately and
/// by writing the tables directly.
/// </para>
/// <para>
/// <strong>Isolation is asserted in both directions.</strong> These five tables hold the shape a
/// tenant invented, which is the most tenant-specific data in the schema — a competitor's object
/// names would say what they sell.
/// </para>
/// </remarks>
public sealed class DynamicSchemaTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A record cannot hold a value for a field nobody declared.</summary>
    [Fact]
    public async Task ARecordCannotHoldAnUndeclaredField()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var site = await ObjectAsync(crm, CrmSchemaHarness.Northwind, "site");

        var refusal = await crm.RefusalAsync(
            CrmSchemaHarness.Northwind,
            """
            INSERT INTO custom_record (record_id, tenant_id, object_id, values, created_at)
            VALUES (@id, @tenant, @object, '{"invented": "x"}'::jsonb, now())
            """,
            Cancellation,
            ("id", Guid.NewGuid()),
            ("tenant", CrmSchemaHarness.Northwind),
            ("object", site));

        refusal.ShouldNotBeNull(
            "a record naming a field nobody declared was written straight into the table, so " +
            "the capability's check is the only thing standing between a typo and a value " +
            "nothing will ever read.");

        refusal.MessageText.ShouldContain("invented");
    }

    /// <summary>A declared field is accepted, so the trigger is a filter and not a wall.</summary>
    [Fact]
    public async Task ARecordHoldingOnlyDeclaredFieldsIsWritten()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var site = await ObjectAsync(crm, CrmSchemaHarness.Northwind, "site");

        await FieldAsync(crm, CrmSchemaHarness.Northwind, site, "postcode");

        var refusal = await crm.RefusalAsync(
            CrmSchemaHarness.Northwind,
            """
            INSERT INTO custom_record (record_id, tenant_id, object_id, values, created_at)
            VALUES (@id, @tenant, @object, '{"postcode": "EC1A 1BB"}'::jsonb, now())
            """,
            Cancellation,
            ("id", Guid.NewGuid()),
            ("tenant", CrmSchemaHarness.Northwind),
            ("object", site));

        refusal.ShouldBeNull("a record of nothing but declared fields was refused.");
    }

    /// <summary>A field belongs to one owner, and the schema will not hold one with two.</summary>
    [Fact]
    public async Task AFieldCannotBelongToBothAKindAndAnObject()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var site = await ObjectAsync(crm, CrmSchemaHarness.Northwind, "site");

        var refusal = await crm.RefusalAsync(
            CrmSchemaHarness.Northwind,
            """
            INSERT INTO custom_field (
                field_id, tenant_id, applies_to, object_id, name, label, data_type, is_required, created_at)
            VALUES (@id, @tenant, 'Lead', @object, 'both', 'Both', 'Text', false, now())
            """,
            Cancellation,
            ("id", Guid.NewGuid()),
            ("tenant", CrmSchemaHarness.Northwind),
            ("object", site));

        refusal.ShouldNotBeNull(
            "a field belonging to a Lead and to a custom object at once would be read by both " +
            "catalogues and validated against neither consistently.");

        refusal.SqlState.ShouldBe("23514", "a CHECK violation.");
    }

    /// <summary>A one-to-one relationship refuses a second link on either end.</summary>
    /// <remarks>
    /// Both directions, because the trigger asks two separate questions and a test that only
    /// filled one end would pass with half of it deleted.
    /// </remarks>
    [Fact]
    public async Task AOneToOneRelationshipRefusesASecondLinkOnEitherEnd()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var tenant = CrmSchemaHarness.Northwind;
        var site = await ObjectAsync(crm, tenant, "site");
        var contract = await ObjectAsync(crm, tenant, "contract");
        var edge = await RelationshipAsync(crm, tenant, "site_contract", site, contract, "OneToOne");

        var siteA = await RecordAsync(crm, tenant, site);
        var siteB = await RecordAsync(crm, tenant, site);
        var contractA = await RecordAsync(crm, tenant, contract);
        var contractB = await RecordAsync(crm, tenant, contract);

        (await LinkRefusalAsync(crm, tenant, edge, siteA, contractA))
            .ShouldBeNull("the first link was refused.");

        (await LinkRefusalAsync(crm, tenant, edge, siteB, contractA))
            .ShouldNotBeNull("a second site took a contract that already had one.");

        (await LinkRefusalAsync(crm, tenant, edge, siteA, contractB))
            .ShouldNotBeNull("a site that already had a contract took a second one.");
    }

    /// <summary>A many-to-many relationship allows both, and refuses only the exact duplicate.</summary>
    [Fact]
    public async Task AManyToManyRelationshipAllowsBothEndsAndRefusesADuplicate()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var tenant = CrmSchemaHarness.Northwind;
        var site = await ObjectAsync(crm, tenant, "site");
        var contract = await ObjectAsync(crm, tenant, "contract");
        var edge = await RelationshipAsync(crm, tenant, "site_contract", site, contract, "ManyToMany");

        var siteA = await RecordAsync(crm, tenant, site);
        var siteB = await RecordAsync(crm, tenant, site);
        var contractA = await RecordAsync(crm, tenant, contract);

        (await LinkRefusalAsync(crm, tenant, edge, siteA, contractA)).ShouldBeNull();
        (await LinkRefusalAsync(crm, tenant, edge, siteB, contractA))
            .ShouldBeNull("ManyToMany refused a second site, so the cardinality is not read.");

        (await crm.ScalarAsTenantAsync<long>(
            tenant, "SELECT count(*) FROM custom_link", Cancellation))
            .ShouldBe(2);
    }

    /// <summary>One tenant cannot read another's declarations, in either direction.</summary>
    [Fact]
    public async Task ATenantCannotReadAnotherTenantsDeclarations()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var northwind = await ObjectAsync(crm, CrmSchemaHarness.Northwind, "site");
        var contoso = await ObjectAsync(crm, CrmSchemaHarness.Contoso, "depot");

        const string Count = "SELECT count(*) FROM custom_object WHERE object_id = @id";

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind, Count, Cancellation, ("id", contoso)))
            .ShouldBe(
                0,
                "Northwind read Contoso's object. What a competitor called their custom entities " +
                "says what they sell.");

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Contoso, Count, Cancellation, ("id", northwind)))
            .ShouldBe(0, "and the other direction, which holds separately or by accident.");

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind, Count, Cancellation, ("id", northwind)))
            .ShouldBe(1, "and the wall is a wall rather than an outage.");
    }

    /// <summary>A tenant cannot write a record into another tenant.</summary>
    [Fact]
    public async Task ATenantCannotWriteARecordIntoAnotherTenant()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var contoso = await ObjectAsync(crm, CrmSchemaHarness.Contoso, "depot");

        var refusal = await crm.RefusalAsync(
            CrmSchemaHarness.Northwind,
            """
            INSERT INTO custom_record (record_id, tenant_id, object_id, values, created_at)
            VALUES (@id, @tenant, @object, '{}'::jsonb, now())
            """,
            Cancellation,
            ("id", Guid.NewGuid()),
            ("tenant", CrmSchemaHarness.Contoso),
            ("object", contoso));

        refusal.ShouldNotBeNull("Northwind wrote a row of Contoso's object into Contoso.");
    }

    private static async Task<Guid> ObjectAsync(CrmSchemaHarness crm, string tenant, string name)
    {
        var id = Guid.NewGuid();

        await crm.AsTenantAsync(
            tenant,
            """
            INSERT INTO custom_object (object_id, tenant_id, name, label, created_at)
            VALUES (@id, @tenant, @name, @name, now())
            """,
            Cancellation,
            ("id", id),
            ("tenant", tenant),
            ("name", name));

        return id;
    }

    private static async Task FieldAsync(
        CrmSchemaHarness crm, string tenant, Guid target, string name)
    {
        await crm.AsTenantAsync(
            tenant,
            """
            INSERT INTO custom_field (
                field_id, tenant_id, object_id, name, label, data_type, is_required, created_at)
            VALUES (@id, @tenant, @object, @name, @name, 'Text', false, now())
            """,
            Cancellation,
            ("id", Guid.NewGuid()),
            ("tenant", tenant),
            ("object", target),
            ("name", name));
    }

    private static async Task<Guid> RelationshipAsync(
        CrmSchemaHarness crm, string tenant, string name, Guid from, Guid to, string cardinality)
    {
        var id = Guid.NewGuid();

        await crm.AsTenantAsync(
            tenant,
            """
            INSERT INTO custom_relationship (
                relationship_id, tenant_id, name, from_object_id, to_object_id, cardinality)
            VALUES (@id, @tenant, @name, @from, @to, @cardinality)
            """,
            Cancellation,
            ("id", id),
            ("tenant", tenant),
            ("name", name),
            ("from", from),
            ("to", to),
            ("cardinality", cardinality));

        return id;
    }

    private static async Task<Guid> RecordAsync(CrmSchemaHarness crm, string tenant, Guid target)
    {
        var id = Guid.NewGuid();

        await crm.AsTenantAsync(
            tenant,
            """
            INSERT INTO custom_record (record_id, tenant_id, object_id, values, created_at)
            VALUES (@id, @tenant, @object, '{}'::jsonb, now())
            """,
            Cancellation,
            ("id", id),
            ("tenant", tenant),
            ("object", target));

        return id;
    }

    private static ValueTask<Npgsql.PostgresException?> LinkRefusalAsync(
        CrmSchemaHarness crm, string tenant, Guid edge, Guid from, Guid to) =>
        crm.RefusalAsync(
            tenant,
            """
            INSERT INTO custom_link (
                link_id, tenant_id, relationship_id, from_record_id, to_record_id, created_at)
            VALUES (@id, @tenant, @edge, @from, @to, now())
            """,
            Cancellation,
            ("id", Guid.NewGuid()),
            ("tenant", tenant),
            ("edge", edge),
            ("from", from),
            ("to", to));
}
