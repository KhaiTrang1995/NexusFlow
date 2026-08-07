using System.Net;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// One plan, whole.
/// </summary>
/// <remarks>
/// <strong>The gap this closes.</strong> A plan could be committed, given objectives, qualified,
/// have steps agreed and risks recorded, and be read back only as a row in a roll-up. Everything
/// underneath it was write-only, so the account-plan and deal-plan screens had nothing to draw.
/// </remarks>
public sealed class PlanDetailApiTests
{
    private const string Plan = "/api/v1/crm/planning/plan";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Everything hung off a plan comes back with it.</summary>
    [Fact]
    public async Task APlanComesBackWithEverythingUnderIt()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await SeedAsync(app);

        var plan = await ReadAsync(app, "northwind_fy26");

        plan.Label.ShouldBe("Northwind FY26");
        plan.Kind.ShouldBe("Account");
        plan.Period.ShouldBe("fy26_q3");
        plan.TargetAmount.ShouldBe(500_000m);

        plan.Objectives.Count.ShouldBe(2);
        plan.Objectives[0]!.Description.ShouldBe("Land the platform team");

        plan.Steps.Count.ShouldBe(2);
        plan.Qualification.Count.ShouldBe(1);
        plan.Risks.Count.ShouldBe(1);
        plan.Risks[0]!.Severity.ShouldBe("High");
    }

    /// <summary>
    /// Whether a step is overdue is the server's answer, not the reader's clock.
    /// </summary>
    /// <remarks>
    /// A client comparing a due date against its own clock reports a step overdue in Sydney and
    /// not in Lisbon on the same afternoon — and an overdue step is the earliest signal a deal has
    /// stopped moving, so the two readers disagree about the thing the screen exists to show.
    /// </remarks>
    [Fact]
    public async Task AnOverdueStepIsReportedAsOverdue()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await SeedAsync(app);

        var plan = await ReadAsync(app, "northwind_fy26");

        var late = plan.Steps.Single(step => step.Ordinal == 0);
        var soon = plan.Steps.Single(step => step.Ordinal == 1);

        late.IsOverdue.ShouldBeTrue("a step due last month, not complete, is overdue.");
        soon.IsOverdue.ShouldBeFalse("a step due next month is not.");
        soon.IsComplete.ShouldBeFalse();
    }

    /// <summary>A completed step is never overdue, whatever its date says.</summary>
    [Fact]
    public async Task ACompletedStepIsNotOverdue()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await SeedAsync(app);

        await app.PostAsync(
            "/api/v1/crm/planning/steps",
            new SetPlanStep(
                "northwind_fy26",
                0,
                "Agree the security review",
                "rep-northwind-1",
                new DateOnly(2020, 1, 1),
                IsComplete: true),
            CrmTokens.Northwind);

        var step = (await ReadAsync(app, "northwind_fy26")).Steps.Single(row => row.Ordinal == 0);

        step.IsComplete.ShouldBeTrue();
        step.IsOverdue.ShouldBeFalse("a step that was done is not late, it is done.");
    }

    /// <summary>
    /// A plan's rows are its own, and not every plan's.
    /// </summary>
    /// <remarks>
    /// <strong>Added because a mutation survived.</strong> Dropping the <c>plan_id</c> filter
    /// from the step query changed nothing while every test had one plan — which is exactly the
    /// shape of a defect that reaches production, because the second plan is created by a
    /// customer rather than by a test.
    /// </remarks>
    [Fact]
    public async Task OnePlansRowsAreNotAnothers()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await SeedAsync(app);

        var other = await app.Crm.AccountAsync(
            CrmTokens.NorthwindTenant, Lifecycle.Prospect, Cancellation);

        await app.PostAsync(
            "/api/v1/crm/planning/plans",
            new DefinePlan(
                PlanKind.Account, "fy26_q3", "meridian_fy26", "Meridian FY26",
                "rep-northwind-1", Account: other, TargetAmount: 90_000m, Currency: "EUR"),
            CrmTokens.Northwind);

        await app.PostAsync(
            "/api/v1/crm/planning/steps",
            new SetPlanStep(
                "meridian_fy26", 0, "A step that belongs to the other plan", "rep-northwind-1",
                new DateOnly(2099, 6, 1), IsComplete: false),
            CrmTokens.Northwind);

        await app.PostAsync(
            "/api/v1/crm/planning/objectives",
            new SetObjective(
                "meridian_fy26", 0, "An objective of the other plan",
                ObjectiveMeasure.Revenue, 90_000m, ObjectiveStatus.NotStarted),
            CrmTokens.Northwind);

        var first = await ReadAsync(app, "northwind_fy26");

        first.Steps.Count.ShouldBe(2, "the other plan's step arrived in this plan.");
        first.Steps.ShouldAllBe(step => !step.Description.Contains("other plan"));
        first.Objectives.Count.ShouldBe(2, "the other plan's objective arrived in this plan.");

        (await ReadAsync(app, "meridian_fy26")).Steps.Count.ShouldBe(1);
    }

    /// <summary>A plan this tenant does not have is not found.</summary>
    [Fact]
    public async Task APlanThatDoesNotExistIsNotFound()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var response = await app.PostAsync(
            Plan, new ReadPlan("nothing_by_that_name"), CrmTokens.Northwind, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Another tenant's plan is not found rather than refused, and that is the same answer.
    /// </summary>
    /// <remarks>
    /// Row-level security makes it invisible, so a caller cannot learn from the status code
    /// whether a name exists in somebody else's tenant.
    /// </remarks>
    [Fact]
    public async Task AnotherTenantsPlanIsNotFound()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        await SeedAsync(app);

        var response = await app.PostAsync(
            Plan, new ReadPlan("northwind_fy26"), CrmTokens.Contoso, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------------------- fixtures

    private static async Task<PlanDetail> ReadAsync(CrmApplication app, string name)
    {
        var response = await app.PostAsync(
            Plan, new ReadPlan(name), CrmTokens.Northwind, idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await CrmApplication.ReadAsync<PlanDetail>(response);
    }

    /// <summary>A period, a plan, and one of everything under it.</summary>
    /// <remarks>
    /// An account plan names an account — the schema's own <c>CHECK ((kind = 'Account') =
    /// (account_id IS NOT NULL))</c>, which is what stops a plan about nothing.
    /// </remarks>
    private static async Task SeedAsync(CrmApplication app)
    {
        var account = await app.Crm.AccountAsync(
            CrmTokens.NorthwindTenant, Lifecycle.Customer, Cancellation);

        (await app.PostAsync(
            "/api/v1/crm/planning/periods",
            new DefinePeriod("fy26_q3", "FY26 Q3", new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30), null),
            CrmTokens.NorthwindManager)).StatusCode.ShouldBe(HttpStatusCode.OK, "period");

        (await app.PostAsync(
            "/api/v1/crm/planning/plans",
            new DefinePlan(
                PlanKind.Account,
                "fy26_q3",
                "northwind_fy26",
                "Northwind FY26",
                "rep-northwind-1",
                Account: account,
                TargetAmount: 500_000m,
                Currency: "EUR"),
            CrmTokens.Northwind)).StatusCode.ShouldBe(HttpStatusCode.OK, "plan");

        await app.PostAsync(
            "/api/v1/crm/planning/objectives",
            new SetObjective(
                "northwind_fy26", 0, "Land the platform team",
                ObjectiveMeasure.Revenue, 300_000m, ObjectiveStatus.InProgress),
            CrmTokens.Northwind);

        await app.PostAsync(
            "/api/v1/crm/planning/objectives",
            new SetObjective(
                "northwind_fy26", 1, "Two reference calls",
                ObjectiveMeasure.Meetings, 2m, ObjectiveStatus.NotStarted),
            CrmTokens.Northwind);

        // One in the past and one ahead of it, so the overdue answer has something to be wrong
        // about in both directions.
        await app.PostAsync(
            "/api/v1/crm/planning/steps",
            new SetPlanStep(
                "northwind_fy26", 0, "Agree the security review", "rep-northwind-1",
                new DateOnly(2020, 1, 1), IsComplete: false),
            CrmTokens.Northwind);

        await app.PostAsync(
            "/api/v1/crm/planning/steps",
            new SetPlanStep(
                "northwind_fy26", 1, "Sign the order form", "rep-northwind-1",
                new DateOnly(2099, 1, 1), IsComplete: false),
            CrmTokens.Northwind);

        await app.PostAsync(
            "/api/v1/crm/planning/qualifications",
            new AnswerQualification(
                "northwind_fy26", QualificationElement.EconomicBuyer, true, "The CFO, met in April."),
            CrmTokens.Northwind);

        await app.PostAsync(
            "/api/v1/crm/planning/risks",
            new SetRisk(
                "northwind_fy26", 0, "Security review could slip",
                RiskSeverity.High, "Book the review before the quote expires", IsOpen: true),
            CrmTokens.Northwind);
    }
}
