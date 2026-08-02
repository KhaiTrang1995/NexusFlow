using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// There is no <c>Customer</c> entity, and <c>customer_account</c> is what a report that wants
/// one reads.
/// </summary>
/// <remarks>
/// §5.1: "a customer is an <c>Account</c> whose <c>Lifecycle</c> has reached <c>Customer</c> —
/// the same row, the same identity, one field different". A view rather than a table is what
/// makes becoming a customer a transition rather than a move, and these are the two properties
/// that would be lost if somebody later "promoted" it into a table: the identity is the
/// account's, and the isolation is the account's.
/// </remarks>
public sealed class CustomerAccountViewTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>The view holds exactly the accounts that have reached <c>Customer</c>.</summary>
    /// <remarks>
    /// All three lifecycle values, because a view written as <c>lifecycle &lt;&gt; 'Prospect'</c>
    /// would pass a test that only ever created prospects and customers, and would report every
    /// churned account as a customer for the rest of the deployment's life.
    /// </remarks>
    [Fact]
    public async Task TheViewHoldsCustomersAndOnlyCustomers()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var prospect = await crm.AccountAsync(CrmSchemaHarness.Northwind, Lifecycle.Prospect, Cancellation);
        var customer = await crm.AccountAsync(CrmSchemaHarness.Northwind, Lifecycle.Customer, Cancellation);
        var churned = await crm.AccountAsync(CrmSchemaHarness.Northwind, Lifecycle.Churned, Cancellation);

        const string InView = "SELECT count(*) FROM customer_account WHERE account_id = @id";

        (await crm.ScalarAsTenantAsync<long>(CrmSchemaHarness.Northwind, InView, Cancellation, ("id", customer)))
            .ShouldBe(1);

        (await crm.ScalarAsTenantAsync<long>(CrmSchemaHarness.Northwind, InView, Cancellation, ("id", prospect)))
            .ShouldBe(0);

        (await crm.ScalarAsTenantAsync<long>(CrmSchemaHarness.Northwind, InView, Cancellation, ("id", churned)))
            .ShouldBe(0, "a churned account is in the view, so every report of this deployment's " +
                         "customers has been counting the ones that left.");
    }

    /// <summary>
    /// An account becomes a customer by changing one field, and keeps the identity it had.
    /// </summary>
    /// <remarks>
    /// The whole argument against a separate table, asserted rather than reasoned: the id in
    /// the view is the id the prospect had, so every opportunity, quote and activity that
    /// pointed at the prospect still points at the customer. A move between tables would have
    /// broken all of them or duplicated the row.
    /// </remarks>
    [Fact]
    public async Task BecomingACustomerIsATransitionAndNotAMove()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var account = await crm.AccountAsync(CrmSchemaHarness.Northwind, Lifecycle.Prospect, Cancellation);

        await crm.AsTenantAsync(
            CrmSchemaHarness.Northwind,
            "UPDATE account SET lifecycle = 'Customer' WHERE account_id = @id",
            Cancellation,
            ("id", account));

        (await crm.ScalarAsTenantAsync<Guid>(
            CrmSchemaHarness.Northwind,
            "SELECT account_id FROM customer_account WHERE account_id = @id",
            Cancellation,
            ("id", account)))
            .ShouldBe(account);

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind, "SELECT count(*) FROM account", Cancellation))
            .ShouldBe(1, "the account was copied rather than transitioned.");
    }

    /// <summary>The view is not a way round the policy on the table it reads.</summary>
    /// <remarks>
    /// <para>
    /// <strong>This is what <c>security_invoker</c> buys, and it is the reason the view
    /// declares it.</strong> A view without it runs its query as the view's <em>owner</em> —
    /// the role that ran the migration — so a scoped connection would read through the view
    /// what the policy refuses it on the table. <c>FORCE ROW LEVEL SECURITY</c> would still
    /// have the owner under the policy, so the two together happen to compose; resting a tenant
    /// boundary on that composition is resting it on a reader knowing both halves.
    /// </para>
    /// <para>
    /// Asserted in both directions, for the reason every isolation assertion in this project is.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheViewIsNotAWayRoundThePolicy()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        await crm.AccountAsync(CrmSchemaHarness.Northwind, Lifecycle.Customer, Cancellation);
        await crm.AccountAsync(CrmSchemaHarness.Contoso, Lifecycle.Customer, Cancellation);

        const string Count = "SELECT count(*) FROM customer_account";

        (await crm.ScalarAsTenantAsync<long>(CrmSchemaHarness.Northwind, Count, Cancellation))
            .ShouldBe(1, "Northwind sees a customer that is not its own through the view.");

        (await crm.ScalarAsTenantAsync<long>(CrmSchemaHarness.Contoso, Count, Cancellation))
            .ShouldBe(1, "and Contoso does, which is the other direction.");

        (await crm.ScalarAsTenantAsync<long>(null, Count, Cancellation))
            .ShouldBe(0, "and an unscoped connection reads nobody's.");
    }
}
