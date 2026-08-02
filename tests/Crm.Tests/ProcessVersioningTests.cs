using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// A process definition is versioned, one version at a time is active, and an opportunity stays
/// on the one it started with.
/// </summary>
/// <remarks>
/// §6's second note: "an administrator publishes a new version; opportunities already in flight
/// keep the version they started on, exactly as <c>flow_instance</c> pins <c>flow_version</c>".
/// That is three claims and they fail differently, so they are three tests.
/// </remarks>
public sealed class ProcessVersioningTests
{
    /// <summary>The SQLSTATE a unique index raises.</summary>
    private const string UniqueViolation = "23505";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Two active definitions for one tenant and one entity kind are refused.</summary>
    [Fact]
    public async Task OnlyOneVersionIsActivePerTenantAndEntityKind()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        await crm.ProcessAsync(CrmSchemaHarness.Northwind, 1, isActive: true, Cancellation);

        var refusal = await crm.RefusalAsync(
            CrmSchemaHarness.Northwind,
            """
            INSERT INTO process_definition (process_id, tenant_id, applies_to, version, is_active, published_at)
            VALUES (@id, @tenant, 'Opportunity', 2, true, now())
            """,
            Cancellation,
            ("id", Guid.NewGuid()),
            ("tenant", CrmSchemaHarness.Northwind));

        refusal.ShouldNotBeNull(
            "two definitions are active for one tenant and one entity kind, so which one a new " +
            "opportunity starts on is whichever the query planner returns first.");

        refusal.SqlState.ShouldBe(UniqueViolation);
    }

    /// <summary>
    /// Superseded versions coexist, which is why the index is partial.
    /// </summary>
    /// <remarks>
    /// A unique index over <c>(tenant, kind, is_active)</c> would satisfy the test above and
    /// would allow exactly one inactive version too — so an administrator publishing a fourth
    /// version could not keep the three before it, and every in-flight opportunity would lose
    /// the definition it was pinned to. This is the assertion that rules that index out.
    /// </remarks>
    [Fact]
    public async Task SupersededVersionsCoexist()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        await crm.ProcessAsync(CrmSchemaHarness.Northwind, 1, isActive: false, Cancellation);
        await crm.ProcessAsync(CrmSchemaHarness.Northwind, 2, isActive: false, Cancellation);
        await crm.ProcessAsync(CrmSchemaHarness.Northwind, 3, isActive: true, Cancellation);

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind,
            "SELECT count(*) FROM process_definition WHERE applies_to = 'Opportunity'",
            Cancellation))
            .ShouldBe(3);
    }

    /// <summary>
    /// One tenant's active definition does not stop another tenant's, and neither does another
    /// entity kind's.
    /// </summary>
    [Fact]
    public async Task TheActiveVersionIsPerTenantAndPerEntityKind()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        await crm.ProcessAsync(CrmSchemaHarness.Northwind, 1, isActive: true, Cancellation);
        await crm.ProcessAsync(CrmSchemaHarness.Contoso, 1, isActive: true, Cancellation);
        await crm.ProcessAsync(
            CrmSchemaHarness.Northwind, 1, isActive: true, Cancellation, EntityKind.Lead);

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind,
            "SELECT count(*) FROM process_definition WHERE is_active",
            Cancellation))
            .ShouldBe(2, "Northwind has one active definition for opportunities and one for leads.");

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Contoso,
            "SELECT count(*) FROM process_definition WHERE is_active",
            Cancellation))
            .ShouldBe(1);
    }

    /// <summary>
    /// Publishing a new version leaves an in-flight opportunity on the old one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>There is no version column on <c>opportunity</c>, and this is why none is
    /// needed.</strong> The opportunity's <c>stage_id</c> points at a stage, a stage belongs to
    /// exactly one definition, and publishing version 2 creates new stage rows — so the
    /// existing foreign key <em>is</em> the pin, and nothing has to remember to copy a version
    /// number onto a row at the moment it is created.
    /// </para>
    /// <para>
    /// This is the test to break first if somebody adds a <c>process_id</c> to
    /// <c>opportunity</c> "for convenience": two paths to the same fact, and a row that can
    /// carry a version its stage does not belong to.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnInFlightOpportunityStaysOnTheVersionItStartedWith()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var account = await crm.AccountAsync(CrmSchemaHarness.Northwind, Lifecycle.Prospect, Cancellation);
        var contact = await crm.ContactAsync(CrmSchemaHarness.Northwind, account, Cancellation);

        var (firstProcess, firstStage) =
            await crm.ProcessAsync(CrmSchemaHarness.Northwind, 1, isActive: true, Cancellation);

        var opportunity = await crm.OpportunityAsync(
            CrmSchemaHarness.Northwind, account, contact, firstStage, Cancellation);

        // The administrator publishes version 2. Deactivating version 1 first, because the
        // partial index refuses two active definitions — which is the point of it.
        await crm.AsTenantAsync(
            CrmSchemaHarness.Northwind,
            "UPDATE process_definition SET is_active = false WHERE process_id = @id",
            Cancellation,
            ("id", firstProcess));

        var (secondProcess, _) =
            await crm.ProcessAsync(CrmSchemaHarness.Northwind, 2, isActive: true, Cancellation);

        var pinned = await crm.ScalarAsTenantAsync<Guid>(
            CrmSchemaHarness.Northwind,
            """
            SELECT d.process_id
              FROM opportunity o
              JOIN process_stage s      ON s.stage_id   = o.stage_id
              JOIN process_definition d ON d.process_id = s.process_id
             WHERE o.opportunity_id = @id
            """,
            Cancellation,
            ("id", opportunity));

        pinned.ShouldBe(
            firstProcess,
            "the opportunity moved to the newly published version. An in-flight deal whose " +
            "process changes under it is a deal whose next transition is a surprise.");

        pinned.ShouldNotBe(secondProcess);
    }

    /// <summary>
    /// A stage cannot be shared between two definitions, which is what makes the pin a pin.
    /// </summary>
    /// <remarks>
    /// <c>process_stage.process_id</c> is a single foreign key rather than a join table, so the
    /// walk from an opportunity to its version has exactly one answer. Asserted over the
    /// catalogue because the alternative — inserting a second row for the same stage — is not
    /// expressible, which is the property being claimed.
    /// </remarks>
    [Fact]
    public async Task AStageBelongsToExactlyOneDefinition()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var nullable = await crm.ScalarAsTenantAsync<bool>(
            CrmSchemaHarness.Northwind,
            """
            SELECT a.attnotnull
              FROM pg_attribute a
              JOIN pg_class c     ON c.oid = a.attrelid
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = current_schema()
               AND c.relname = 'process_stage'
               AND a.attname = 'process_id'
            """,
            Cancellation);

        nullable.ShouldBeTrue(
            "process_stage.process_id is nullable, so a stage can belong to no definition and " +
            "an opportunity pinned to it is pinned to nothing.");
    }
}
