using System.Net;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// The configured process, read back.
/// </summary>
/// <remarks>
/// <strong>The claim this sample exists to prove, on the screen where somebody would check it.</strong>
/// The transitions, guards and actions live in tables and an administrator rewrites them at run
/// time — and nothing could show them. A setup screen listing stages it had been compiled with is
/// that claim contradicted.
/// </remarks>
public sealed class ProcessViewApiTests
{
    private const string Processes = "/api/v1/crm/processes";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>The active definition comes back with its stages in order.</summary>
    [Fact]
    public async Task TheActiveProcessComesBackWithItsStages()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await app.Crm.ProcessAsync(CrmTokens.NorthwindTenant, 1, true, Cancellation);

        var process = await ReadAsync(app);

        process.AppliesTo.ShouldBe("Opportunity");
        process.Version.ShouldBe(1);
        process.Stages.Count.ShouldBeGreaterThan(0);
        process.Stages.Select(stage => stage.Ordinal).ShouldBeInOrder();
    }

    /// <summary>
    /// A stage says how many opportunities are sitting in it.
    /// </summary>
    /// <remarks>
    /// The number that makes the screen worth opening: a stage nothing has ever entered is either
    /// new or a mistake, and a stage holding half the pipeline is where deals go to be forgotten.
    /// </remarks>
    [Fact]
    public async Task AStageSaysHowManyAreSittingInIt()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var definition = await app.Crm.ProcessAsync(CrmTokens.NorthwindTenant, 1, true, Cancellation);
        var account = await app.Crm.AccountAsync(
            CrmTokens.NorthwindTenant, Lifecycle.Customer, Cancellation);
        var contact = await app.Crm.ContactAsync(CrmTokens.NorthwindTenant, account, Cancellation);

        await app.Crm.OpportunityAsync(
            CrmTokens.NorthwindTenant, account, contact, definition.Stage, Cancellation);
        await app.Crm.OpportunityAsync(
            CrmTokens.NorthwindTenant, account, contact, definition.Stage, Cancellation);

        var stages = (await ReadAsync(app)).Stages;

        stages.Sum(stage => stage.Occupants).ShouldBe(2);
        stages.Count(stage => stage.Occupants > 0).ShouldBe(1, "both went into the same stage.");
    }

    /// <summary>
    /// A tenant with no published process is told so, rather than shown an empty one.
    /// </summary>
    /// <remarks>
    /// Distinct answers on purpose: a tenant that never published one has a decision to make, and
    /// a tenant whose process is empty has a bug to find.
    /// </remarks>
    [Fact]
    public async Task ATenantWithNoProcessIsToldSo()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await app.PostAsync(
            Processes, new ReadProcess(EntityKind.Opportunity), CrmTokens.Northwind, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var body = await response.Content.ReadAsStringAsync(Cancellation);

        body.ShouldContain("crm.process_not_published");
    }

    /// <summary>A superseded version is not what comes back.</summary>
    /// <remarks>
    /// An opportunity already carries the stage it entered; a screen offering last quarter's
    /// stages beside this quarter's would let somebody move a deal into a stage nothing can leave.
    /// </remarks>
    [Fact]
    public async Task OnlyTheActiveVersionComesBack()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await app.Crm.ProcessAsync(CrmTokens.NorthwindTenant, 1, false, Cancellation);
        await app.Crm.ProcessAsync(CrmTokens.NorthwindTenant, 2, true, Cancellation);

        (await ReadAsync(app)).Version.ShouldBe(2);
    }

    /// <summary>Another tenant's process is not this tenant's.</summary>
    [Fact]
    public async Task AnotherTenantsProcessIsNotVisible()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await app.Crm.ProcessAsync(CrmTokens.NorthwindTenant, 1, true, Cancellation);

        var response = await app.PostAsync(
            Processes, new ReadProcess(EntityKind.Opportunity), CrmTokens.Contoso, idempotencyKey: null);

        response.StatusCode.ShouldBe(
            HttpStatusCode.NotFound, "row-level security, not the statement.");
    }

    /// <summary>Reading the pipeline is a representative's grant.</summary>
    /// <remarks>
    /// A process only an administrator could read would be a board only an administrator could
    /// see, and the board is what a representative works from all day.
    /// </remarks>
    [Fact]
    public async Task ARepresentativeMayReadTheProcess()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await app.Crm.ProcessAsync(CrmTokens.NorthwindTenant, 1, true, Cancellation);

        (await app.PostAsync(
            Processes, new ReadProcess(EntityKind.Opportunity), CrmTokens.Northwind, idempotencyKey: null))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------------------- fixtures

    private static async Task<ProcessView> ReadAsync(CrmApplication app)
    {
        var response = await app.PostAsync(
            Processes, new ReadProcess(EntityKind.Opportunity), CrmTokens.Northwind, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<ProcessView>(response);
    }
}
