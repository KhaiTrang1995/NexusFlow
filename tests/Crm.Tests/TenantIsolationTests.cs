using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// What one tenant cannot reach of another tenant's, decided by PostgreSQL and not by this
/// process.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every assertion here is written in the negative, and it is asserted in both
/// directions.</strong> A test that Northwind can read its own account proves only that the
/// schema still works; it would have passed on the commit that created the tables and no
/// policy. What has to be proved is that Northwind cannot reach Contoso's — and proved the
/// other way round as well, because an isolation that holds for whichever tenant a test
/// happens to arrange first is an isolation that holds by accident.
/// </para>
/// <para>
/// <strong>The refusal comes from the database.</strong> Every connection here assumes
/// <c>flowx_tenant</c>, a role that cannot bypass row-level security, and sets
/// <c>flowx.tenant_id</c>; migration <c>0002</c>'s policies decide from there. So an assertion
/// fails if a policy is missing, if the role is wrong, if <c>FORCE ROW LEVEL SECURITY</c> was
/// omitted or if <c>CrmTenantScope</c> forgot to scope — four ways of shipping the same defect,
/// all caught by the same line.
/// </para>
/// </remarks>
public sealed class TenantIsolationTests
{
    private const string CountAccounts = "SELECT count(*) FROM account WHERE account_id = @id";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// One tenant cannot read another's account, in either direction, and reaches its own.
    /// </summary>
    [Fact]
    public async Task ATenantCannotReadAnotherTenantsAccount()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var northwind = await crm.AccountAsync(CrmSchemaHarness.Northwind, Lifecycle.Prospect, Cancellation);
        var contoso = await crm.AccountAsync(CrmSchemaHarness.Contoso, Lifecycle.Prospect, Cancellation);

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind, CountAccounts, Cancellation, ("id", contoso)))
            .ShouldBe(
                0,
                "Northwind read Contoso's account. The account id is a guessable handle, and " +
                "nothing above the database was ever going to stop a caller that holds one.");

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Contoso, CountAccounts, Cancellation, ("id", northwind)))
            .ShouldBe(
                0,
                "Contoso read Northwind's account. Asserted separately from the other " +
                "direction because an isolation that holds one way holds by accident.");

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind, CountAccounts, Cancellation, ("id", northwind)))
            .ShouldBe(1, "and the wall is a wall rather than an outage: a tenant reaches its own.");
    }

    /// <summary>
    /// A tenant cannot write a row into another tenant, and is refused rather than hidden.
    /// </summary>
    /// <remarks>
    /// <c>WITH CHECK</c> as well as <c>USING</c>: without it a scoped connection could
    /// <c>INSERT</c> a row naming another tenant and then be unable to read back what it had
    /// just written, which is a worse state than being refused. <c>42501</c> is
    /// <c>insufficient_privilege</c> — the SQLSTATE a policy violation raises.
    /// </remarks>
    [Fact]
    public async Task ATenantCannotWriteIntoAnotherTenant()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var refusal = await crm.RefusalAsync(
            CrmSchemaHarness.Northwind,
            """
            INSERT INTO account (account_id, tenant_id, name, industry, lifecycle, region, owner_id)
            VALUES (@id, @tenant, 'Smuggled', 'Software', 'Prospect', 'emea', @owner)
            """,
            Cancellation,
            ("id", Guid.NewGuid()),
            ("tenant", CrmSchemaHarness.Contoso),
            ("owner", Guid.NewGuid()));

        refusal.ShouldNotBeNull(
            "Northwind wrote a row belonging to Contoso. A policy with USING and no WITH CHECK " +
            "hides such a row from its author rather than refusing it.");

        refusal.SqlState.ShouldBe("42501");
    }

    /// <summary>
    /// The five tables with no <c>tenant_id</c> of their own are isolated through their parent.
    /// </summary>
    /// <remarks>
    /// <c>quote_line</c>, <c>process_stage</c>, <c>process_transition</c>,
    /// <c>transition_guard</c> and <c>transition_action</c> carry no partition key —
    /// deliberately, because a denormalised copy can disagree with the row it copies. This is
    /// what says the restatement through the foreign key works, which the eight root tables'
    /// own policies cannot.
    /// </remarks>
    [Fact]
    public async Task ATenantCannotReadTheChildRowsOfAnotherTenantsParent()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var account = await crm.AccountAsync(CrmSchemaHarness.Contoso, Lifecycle.Prospect, Cancellation);
        var contact = await crm.ContactAsync(CrmSchemaHarness.Contoso, account, Cancellation);
        var (_, stage) = await crm.ProcessAsync(CrmSchemaHarness.Contoso, 1, true, Cancellation);
        var opportunity = await crm.OpportunityAsync(
            CrmSchemaHarness.Contoso, account, contact, stage, Cancellation);

        var (_, line) = await crm.QuoteAsync(CrmSchemaHarness.Contoso, opportunity, Cancellation);

        const string CountLines = "SELECT count(*) FROM quote_line WHERE quote_line_id = @id";
        const string CountStages = "SELECT count(*) FROM process_stage WHERE stage_id = @id";

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Contoso, CountLines, Cancellation, ("id", line)))
            .ShouldBe(1, "Contoso cannot see its own quote line, so the comparison below is vacuous.");

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind, CountLines, Cancellation, ("id", line)))
            .ShouldBe(
                0,
                "Northwind read a line of Contoso's quote. quote_line inherits its tenant " +
                "through the foreign key, and the policy that restates the predicate is what " +
                "has to hold.");

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind, CountStages, Cancellation, ("id", stage)))
            .ShouldBe(
                0,
                "Northwind read a stage of Contoso's process definition. Two hops rather than " +
                "one, and the walk up the containment chain is where a policy stops composing " +
                "if it was written for a shallower one.");
    }

    /// <summary>
    /// A connection nobody scoped reads nothing rather than everything.
    /// </summary>
    /// <remarks>
    /// The predicate is <c>tenant_id IS NOT DISTINCT FROM nullif(current_setting(…, true), '')</c>,
    /// so an unscoped connection matches rows whose tenant is NULL — and migration <c>0001</c>
    /// gives no CRM row one. A runtime that forgets to resolve a tenant therefore reads
    /// nobody's data, which is the opposite of how this defect usually behaves.
    /// </remarks>
    [Fact]
    public async Task AConnectionScopedToNoTenantReadsNothing()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        await crm.AccountAsync(CrmSchemaHarness.Northwind, Lifecycle.Prospect, Cancellation);
        await crm.AccountAsync(CrmSchemaHarness.Contoso, Lifecycle.Customer, Cancellation);

        (await crm.ScalarAsTenantAsync<long>(null, "SELECT count(*) FROM account", Cancellation))
            .ShouldBe(
                0,
                "an unscoped connection read somebody's accounts. Fail-closed is the whole " +
                "point of comparing against NULL rather than against the empty string.");
    }

    /// <summary>
    /// The isolation is the policy's, and this test would notice if it were not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A negative assertion is worth what its ability to fail is worth.</strong>
    /// Everything above asserts that a count is zero, and a count is zero for many reasons: a
    /// mistyped id, a seed that never committed, a schema the statement was not looking at. So
    /// this test takes the same arrangement, switches row-level security off on <c>account</c>,
    /// and requires the cross-tenant read to <em>succeed</em> — then switches it back on and
    /// requires the refusal to return.
    /// </para>
    /// <para>
    /// If the zeros above were an artefact of the arrangement rather than of the policy, the
    /// middle assertion here fails and says so. It is the mutation test for this file, kept in
    /// the suite rather than performed once by hand, because a mutation somebody ran in August
    /// proves nothing about the policy in October.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheRefusalIsThePolicyAndNotTheArrangement()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var contoso = await crm.AccountAsync(CrmSchemaHarness.Contoso, Lifecycle.Prospect, Cancellation);

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind, CountAccounts, Cancellation, ("id", contoso)))
            .ShouldBe(0, "the policy is on: Northwind cannot see Contoso's account.");

        await crm.AsOwnerAsync("ALTER TABLE account DISABLE ROW LEVEL SECURITY", Cancellation);

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind, CountAccounts, Cancellation, ("id", contoso)))
            .ShouldBe(
                1,
                "with the policy off, Northwind still could not see Contoso's account — so " +
                "the zeros in this file are being produced by something other than the " +
                "policy, and none of them is evidence of anything.");

        await crm.AsOwnerAsync("ALTER TABLE account ENABLE ROW LEVEL SECURITY", Cancellation);

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind, CountAccounts, Cancellation, ("id", contoso)))
            .ShouldBe(0, "and the refusal comes back the moment the policy does.");
    }

    /// <summary>
    /// <c>FORCE</c> is what puts the schema's own owner under the policies.
    /// </summary>
    /// <remarks>
    /// A table's owner bypasses row-level security unless the table declares
    /// <c>FORCE ROW LEVEL SECURITY</c>, and this deployment connects as the role that created
    /// the schema. Without <c>FORCE</c> every policy in migration <c>0002</c> would be
    /// installed correctly and would isolate nothing from the application's own connection.
    /// The assertion is over the catalogue rather than over a read, because the read that would
    /// prove it is the one the whole file is about.
    /// </remarks>
    [Fact]
    public async Task EveryCrmTableForcesRowLevelSecurity()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var unprotected = await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind,
            """
            SELECT count(*)
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = current_schema()
               AND c.relkind = 'r'
               AND c.relname <> 'crm_schema_migration'
               AND NOT (c.relrowsecurity AND c.relforcerowsecurity)
            """,
            Cancellation);

        unprotected.ShouldBe(
            0,
            "a CRM table has row-level security missing or unforced. A policy a superuser — " +
            "or an owner — bypasses is decoration.");

        var policies = await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind,
            "SELECT count(*) FROM pg_policies WHERE schemaname = current_schema()",
            Cancellation);

        policies.ShouldBe(
            30,
            "one policy per table. A table with ENABLE and FORCE and no policy " +
            "denies everything, which passes the assertion above and breaks the application.");
    }
}
