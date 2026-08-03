using System.Security.Claims;
using Crm;
using FlowX;
using FlowX.Conformance.InMemory;
using FlowX.Hosting;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// The two sweeps against the tables, of <c>docs/26-CRM-Sample.md</c> §10 package 10.
/// </summary>
/// <remarks>
/// <strong>What is checked here is that the SQL agrees with <see cref="SlaRules"/>.</strong> The
/// schedule itself — one occurrence across a fleet, a lease, a replay — is the platform's, and
/// <c>samples/scheduler</c> is where it is demonstrated. A sample re-asserting it would be
/// testing somebody else's code in a place nobody would look for the failure.
/// </remarks>
public sealed class TaskSweepTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Due = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------------------- creating

    [Fact]
    public async Task ATaskIsWrittenAgainstItsSubject()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var account = await crm.AccountAsync(CrmSchemaHarness.Northwind, Lifecycle.Customer, Cancellation);

        var created = await CreateAsync(crm, new RelatedRef(EntityKind.Account, account), Due);

        created.IsSuccess.ShouldBeTrue(Because(created));

        (await crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Northwind,
            "SELECT status FROM activity WHERE activity_id = @id",
            Cancellation,
            ("id", created.Value!.ActivityId))).ShouldBe("Open");
    }

    /// <summary>
    /// The discriminated reference has no foreign key, so migration 0003's trigger is what
    /// refuses a task against nothing — and the caller is told, not shown a constraint.
    /// </summary>
    [Fact]
    public async Task ATaskAgainstSomethingThatIsNotThereIsRefused()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var created = await CreateAsync(
            crm, new RelatedRef(EntityKind.Account, Guid.NewGuid()), Due);

        created.Error!.Code.ShouldBe("crm.task_subject_not_found");
    }

    [Fact]
    public async Task ATaskAgainstAnotherTenantsAccountIsRefused()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var account = await crm.AccountAsync(CrmSchemaHarness.Contoso, Lifecycle.Customer, Cancellation);

        var created = await CreateAsync(crm, new RelatedRef(EntityKind.Account, account), Due);

        created.Error!.Code.ShouldBe("crm.task_subject_not_found");
    }

    // ----------------------------------------------------------------------------- escalating

    /// <summary>
    /// §10 package 10's "done when": an overdue task escalates once per window.
    /// </summary>
    [Fact]
    public async Task AnOverdueTaskEscalatesOncePerWindow()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var task = await OverdueTaskAsync(crm);

        // The moment it falls due.
        (await EscalateAsync(crm, Due)).Value!.Escalated.ShouldBe(1);
        (await CountAsync(crm, task)).ShouldBe(1);

        // Every sweep inside the same window does nothing at all.
        (await EscalateAsync(crm, Due.AddHours(1))).Value!.Escalated.ShouldBe(0);
        (await EscalateAsync(crm, Due.AddHours(23))).Value!.Escalated.ShouldBe(0);
        (await CountAsync(crm, task)).ShouldBe(1, "the window has not passed.");

        // The next window opens exactly a day after it fell due.
        (await EscalateAsync(crm, Due + SlaPolicy.EscalationWindow)).Value!.Escalated.ShouldBe(1);
        (await CountAsync(crm, task)).ShouldBe(2);
    }

    [Fact]
    public async Task ATaskThatIsNotYetDueIsLeftAlone()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var task = await OverdueTaskAsync(crm);

        (await EscalateAsync(crm, Due.AddSeconds(-1))).Value!.Escalated.ShouldBe(0);
        (await CountAsync(crm, task)).ShouldBe(0);
    }

    /// <summary>
    /// A sweep that has not run for a week works the backlog off one window at a time.
    /// </summary>
    [Fact]
    public async Task ALateSweepDoesNotEscalateTheWholeBacklogAtOnce()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var task = await OverdueTaskAsync(crm);

        (await EscalateAsync(crm, Due.AddDays(7))).Value!.Escalated.ShouldBe(1);

        (await CountAsync(crm, task)).ShouldBe(
            1, "a week of missed sweeps is not seven notifications in one morning.");

        SlaRules.EscalationsDue(Due, Due.AddDays(7), SlaPolicy.EscalationWindow)
            .ShouldBe(SlaPolicy.MaxEscalations, "what it owes is a different question from what it did.");
    }

    [Fact]
    public async Task EscalationStopsAtTheCeiling()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var task = await OverdueTaskAsync(crm);

        for (var day = 0; day < SlaPolicy.MaxEscalations + 2; day++)
        {
            await EscalateAsync(crm, Due.AddDays(day));
        }

        (await CountAsync(crm, task)).ShouldBe(SlaPolicy.MaxEscalations);
    }

    [Fact]
    public async Task ACompletedTaskIsNotEscalated()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var task = await OverdueTaskAsync(crm);

        await crm.AsTenantAsync(
            CrmSchemaHarness.Northwind,
            "UPDATE activity SET status = 'Completed', completed_at = now() WHERE activity_id = @id",
            Cancellation,
            ("id", task));

        (await EscalateAsync(crm, Due.AddDays(3))).Value!.Escalated.ShouldBe(0);
    }

    [Fact]
    public async Task ATaskWithNoDueDateIsNotEscalated()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var account = await crm.AccountAsync(CrmSchemaHarness.Northwind, Lifecycle.Customer, Cancellation);

        await CreateAsync(crm, new RelatedRef(EntityKind.Account, account), dueAt: null);

        (await EscalateAsync(crm, Due.AddYears(1))).Value!.Escalated.ShouldBe(0);
    }

    [Fact]
    public async Task ASweepEscalatesOnlyItsOwnTenantsTasks()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        await OverdueTaskAsync(crm);

        (await EscalateAsync(crm, Due.AddDays(1), CrmTokens.ContosoTenant)).Value!.Escalated
            .ShouldBe(0, "the connection is narrowed to the tenant the occurrence fired for.");
    }

    // ------------------------------------------------------------------------------- sweeping

    [Fact]
    public async Task AnOpportunityNobodyHasMovedIsCountedStale()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var opportunity = await OpportunityAsync(crm);

        await EnteredStageAsync(crm, opportunity, Due);

        (await SweepAsync(crm, Due + SlaPolicy.StaleAfter - TimeSpan.FromSeconds(1))).Value!.Stale
            .ShouldBe(0);

        (await SweepAsync(crm, Due + SlaPolicy.StaleAfter)).Value!.Stale.ShouldBe(1);
    }

    [Fact]
    public async Task AClosedOpportunityIsNotCountedHoweverLongItSits()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var opportunity = await OpportunityAsync(crm);

        await EnteredStageAsync(crm, opportunity, Due);

        await crm.AsTenantAsync(
            CrmSchemaHarness.Northwind,
            "UPDATE opportunity SET outcome = 'Won' WHERE opportunity_id = @id",
            Cancellation,
            ("id", opportunity));

        (await SweepAsync(crm, Due.AddYears(1))).Value!.Stale.ShouldBe(0);
    }

    [Fact]
    public async Task TheSweepCountsAndChangesNothing()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var opportunity = await OpportunityAsync(crm);

        await EnteredStageAsync(crm, opportunity, Due);
        await SweepAsync(crm, Due.AddYears(1));

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind, "SELECT count(*) FROM activity", Cancellation))
            .ShouldBe(0L, "what to do about a stale deal is §7's to configure, not this sweep's.");
    }

    // ------------------------------------------------------------------------------- fixtures

    /// <summary>The sample's own representative token, so what is tested is what it grants.</summary>
    private static ClaimsPrincipal Representative =>
        new(new ClaimsIdentity(CrmTokens.Claims[CrmTokens.Northwind], "CrmTokenTest"));

    private static async Task<Guid> OverdueTaskAsync(CrmSchemaHarness crm)
    {
        var account = await crm.AccountAsync(CrmSchemaHarness.Northwind, Lifecycle.Customer, Cancellation);

        var created = await CreateAsync(crm, new RelatedRef(EntityKind.Account, account), Due);

        return created.Value!.ActivityId;
    }

    private static async Task<Guid> OpportunityAsync(CrmSchemaHarness crm)
    {
        var account = await crm.AccountAsync(CrmSchemaHarness.Northwind, Lifecycle.Prospect, Cancellation);
        var contact = await crm.ContactAsync(CrmSchemaHarness.Northwind, account, Cancellation);
        var (_, stage) = await crm.ProcessAsync(CrmSchemaHarness.Northwind, 1, true, Cancellation);

        return await crm.OpportunityAsync(CrmSchemaHarness.Northwind, account, contact, stage, Cancellation);
    }

    private static ValueTask<int> EnteredStageAsync(CrmSchemaHarness crm, Guid opportunity, DateTimeOffset at) =>
        crm.AsTenantAsync(
            CrmSchemaHarness.Northwind,
            "UPDATE opportunity SET stage_entered_at = @at WHERE opportunity_id = @id",
            Cancellation,
            ("at", at),
            ("id", opportunity));

    private static async ValueTask<int> CountAsync(CrmSchemaHarness crm, Guid activity) =>
        await crm.ScalarAsTenantAsync<int>(
            CrmSchemaHarness.Northwind,
            "SELECT escalation_count FROM activity WHERE activity_id = @id",
            Cancellation,
            ("id", activity)).ConfigureAwait(false);

    private static ValueTask<FlowExecutionResult<TaskCreated>> CreateAsync(
        CrmSchemaHarness crm, RelatedRef relatesTo, DateTimeOffset? dueAt) =>
        RunAsync(
            CreateTaskFlow.Plan,
            new CreateTaskFlow.Dispatcher(
                createTaskForSubject: new CreateTaskForSubject(new WorkStore(crm.DataSource))),
            new CreateTask(ActivityKind.Task, "Call them back", relatesTo, Guid.NewGuid(), dueAt),
            CreateTaskFlow.Projection,
            CrmTokens.NorthwindTenant,
            Representative);

    private static ValueTask<FlowExecutionResult<TasksEscalated>> EscalateAsync(
        CrmSchemaHarness crm, DateTimeOffset occurrence, string? tenant = null) =>
        RunAsync(
            EscalateOverdueTasksFlow.Plan,
            new EscalateOverdueTasksFlow.Dispatcher(
                escalateOverdueTasks: new EscalateOverdueTasks(new WorkStore(crm.DataSource))),
            new ScheduledFire(occurrence, "0 * * * *", "UTC"),
            EscalateOverdueTasksFlow.Projection,
            tenant ?? CrmTokens.NorthwindTenant);

    private static ValueTask<FlowExecutionResult<StaleOpportunitiesSwept>> SweepAsync(
        CrmSchemaHarness crm, DateTimeOffset occurrence) =>
        RunAsync(
            SweepStaleOpportunitiesFlow.Plan,
            new SweepStaleOpportunitiesFlow.Dispatcher(
                sweepStaleOpportunities: new SweepStaleOpportunities(new WorkStore(crm.DataSource))),
            new ScheduledFire(occurrence, "0 6 * * *", "UTC"),
            SweepStaleOpportunitiesFlow.Projection,
            CrmTokens.NorthwindTenant);

    private static ValueTask<FlowExecutionResult<TOut>> RunAsync<TIn, TOut>(
        ExecutionPlan plan,
        IStepDispatcher dispatcher,
        TIn input,
        Func<FlowContext, TOut> projection,
        string tenant,
        ClaimsPrincipal? principal = null)
        where TIn : notnull
    {
        var host = new FlowHost(
            new FlowEngine(SystemClock.Instance),
            new FlowXOptions
            {
                ApplicationName = "Crm",
                NodeName = "test-node",
                TenantIsolation = TenantIsolation.Row,
            },
            new FlowDurability(new InMemoryFlowJournal(), new InMemoryLeaseStore()));

        return host.RunAsync(
            plan,
            dispatcher,
            new FlowInvocation(
                "corr-" + Guid.NewGuid(),
                tenant,
                tenant,
                Deadline: null,
                Principal: principal,
                IsContinuation: true,
                TenantAttested: true),
            input,
            projection,
            Cancellation);
    }

    private static string Because<T>(FlowExecutionResult<T> run) =>
        run.Error is null ? "the run failed with no error" : run.Error.Code + ": " + run.Error.Message;
}
