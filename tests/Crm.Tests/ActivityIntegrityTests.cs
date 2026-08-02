using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// The one relationship in this schema that no foreign key can express, and the trigger that
/// holds it up.
/// </summary>
/// <remarks>
/// <c>ACTIVITY.relates_to_kind</c> plus <c>ACTIVITY.relates_to_id</c> names one of four
/// parents, so there is no column a foreign key could be declared on. §6 states that and states
/// the price: "referential integrity for this one relationship is enforced by a trigger rather
/// than by a constraint, and that is stated rather than hidden". A statement of intent is not
/// integrity; these are.
/// </remarks>
public sealed class ActivityIntegrityTests
{
    /// <summary>What a foreign key would have raised, and therefore what the trigger raises.</summary>
    private const string ForeignKeyViolation = "23503";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>An activity pointing at a row that does not exist is refused.</summary>
    /// <remarks>
    /// Asserted for all four kinds rather than one. The trigger is a <c>CASE</c> with four arms
    /// and one of them being wrong — a mistyped column, a table named twice — is invisible to a
    /// test that only exercises the first.
    /// </remarks>
    [Theory]
    [InlineData(EntityKind.Lead)]
    [InlineData(EntityKind.Account)]
    [InlineData(EntityKind.Contact)]
    [InlineData(EntityKind.Opportunity)]
    public async Task ADanglingReferenceIsRefused(EntityKind kind)
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var refusal = await crm.ActivityRefusalAsync(
            CrmSchemaHarness.Northwind, kind, Guid.NewGuid(), Cancellation);

        refusal.ShouldNotBeNull(
            $"an activity was filed against a {kind} that does not exist. The polymorphic pair " +
            "has no foreign key, so this trigger is the only thing between the design and a " +
            "table full of references to nothing.");

        refusal.SqlState.ShouldBe(
            ForeignKeyViolation,
            "the SQLSTATE a foreign key would have raised. A caller that catches " +
            "foreign_key_violation should not have to know that one of thirteen tables " +
            "enforces integrity differently from the other twelve.");
    }

    /// <summary>An activity pointing at a parent that does exist stands.</summary>
    /// <remarks>
    /// Without this, the theory above passes for a trigger that refuses everything — which is
    /// integrity of a sort and is not the one that was asked for.
    /// </remarks>
    [Fact]
    public async Task AReferenceToARowThatExistsIsAccepted()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var account = await crm.AccountAsync(CrmSchemaHarness.Northwind, Lifecycle.Prospect, Cancellation);
        var contact = await crm.ContactAsync(CrmSchemaHarness.Northwind, account, Cancellation);
        var lead = await crm.LeadAsync(CrmSchemaHarness.Northwind, Cancellation);
        var (_, stage) = await crm.ProcessAsync(CrmSchemaHarness.Northwind, 1, true, Cancellation);
        var opportunity = await crm.OpportunityAsync(
            CrmSchemaHarness.Northwind, account, contact, stage, Cancellation);

        foreach (var (kind, parent) in new[]
        {
            (EntityKind.Lead, lead),
            (EntityKind.Account, account),
            (EntityKind.Contact, contact),
            (EntityKind.Opportunity, opportunity),
        })
        {
            (await crm.ActivityRefusalAsync(CrmSchemaHarness.Northwind, kind, parent, Cancellation))
                .ShouldBeNull($"a {kind} that exists was refused as an activity's parent.");
        }

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind, "SELECT count(*) FROM activity", Cancellation))
            .ShouldBe(4);
    }

    /// <summary>An activity cannot hang off another tenant's row, even a real one.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Two walls, and the test is written so that removing either one fails it.</strong>
    /// The trigger's lookups run with invoker rights, so on a scoped connection the parent is a
    /// row this tenant cannot see at all; and the lookup compares <c>tenant_id</c> as well,
    /// which is what still refuses the insert if somebody runs a fix-up script as the schema's
    /// owner with row-level security disabled.
    /// </para>
    /// <para>
    /// The refusal is <c>23503</c> rather than <c>42501</c>, and that is the honest answer:
    /// under the policy the parent is not there. Inventing a distinct "forbidden" would tell
    /// the caller that the row exists and belongs to somebody else.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnActivityCannotHangOffAnotherTenantsRow()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var contosos = await crm.AccountAsync(CrmSchemaHarness.Contoso, Lifecycle.Prospect, Cancellation);

        var refusal = await crm.ActivityRefusalAsync(
            CrmSchemaHarness.Northwind, EntityKind.Account, contosos, Cancellation);

        refusal.ShouldNotBeNull(
            "Northwind filed an activity against Contoso's account. The row exists, so the " +
            "only thing that can have refused this is the tenant half of the trigger's " +
            "lookup — or the policy that hides the row from the lookup in the first place.");

        refusal.SqlState.ShouldBe(ForeignKeyViolation);
    }

    /// <summary>Repointing an activity at nothing is refused, as inserting one is.</summary>
    /// <remarks>
    /// The trigger fires <c>BEFORE INSERT OR UPDATE OF</c> the three columns that can
    /// invalidate the reference. A trigger written for <c>INSERT</c> alone leaves the table
    /// exactly as consistent as nobody ever running an <c>UPDATE</c>.
    /// </remarks>
    [Fact]
    public async Task RepointingAnActivityAtNothingIsRefused()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var account = await crm.AccountAsync(CrmSchemaHarness.Northwind, Lifecycle.Prospect, Cancellation);

        (await crm.ActivityRefusalAsync(
            CrmSchemaHarness.Northwind, EntityKind.Account, account, Cancellation))
            .ShouldBeNull();

        var refusal = await crm.RefusalAsync(
            CrmSchemaHarness.Northwind,
            "UPDATE activity SET relates_to_id = @orphan",
            Cancellation,
            ("orphan", Guid.NewGuid()));

        refusal.ShouldNotBeNull("an activity was repointed at a row that does not exist.");
        refusal.SqlState.ShouldBe(ForeignKeyViolation);
    }

    /// <summary>An update that touches neither the reference nor the tenant is not re-checked.</summary>
    /// <remarks>
    /// <c>UPDATE OF</c> names three columns for a reason: completing a task, escalating one or
    /// moving a due date would otherwise re-run four lookups per write for a reference nothing
    /// touched. This is what says the narrowing is real rather than decorative.
    /// </remarks>
    [Fact]
    public async Task AnUpdateThatLeavesTheReferenceAloneStands()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var account = await crm.AccountAsync(CrmSchemaHarness.Northwind, Lifecycle.Prospect, Cancellation);

        (await crm.ActivityRefusalAsync(
            CrmSchemaHarness.Northwind, EntityKind.Account, account, Cancellation))
            .ShouldBeNull();

        (await crm.RefusalAsync(
            CrmSchemaHarness.Northwind,
            "UPDATE activity SET status = 'Completed', completed_at = now()",
            Cancellation))
            .ShouldBeNull();

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind,
            "SELECT count(*) FROM activity WHERE status = 'Completed'",
            Cancellation))
            .ShouldBe(1);
    }
}
