using System.Text.Json;
using Crm;
using FlowX;
using FlowX.Conformance.InMemory;
using FlowX.Hosting;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// The configured process running end to end, of <c>docs/26-CRM-Sample.md</c> §7 and §8.3.
/// </summary>
/// <remarks>
/// <strong>The test this file exists for is <see cref="AnAdministratorChangesBehaviourWithNoRebuild"/>.</strong>
/// §7 claims a business can change its process without a deployment. Everything else here
/// checks a piece of that; that one checks the claim itself, by writing a different definition
/// into the same database between two runs of the same binary.
/// </remarks>
public sealed class TransitionTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AGuardedTransitionRunsItsActions()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var world = await WorldAsync(crm);

        await GuardAsync(crm, world.Advance, ProcessFields.Amount, GuardOperator.GreaterThan, "40000");
        var action = await ActionAsync(crm, world.Advance, ActionKind.CreateTask, """{"subject":"Call the sponsor"}""");

        var run = await RunAsync(crm, world.Opportunity, "advance", Cancellation);

        run.IsSuccess.ShouldBeTrue(Because(run));
        run.Value!.TransitionId.ShouldBe(world.Advance);
        run.Value.ActionsRun.ShouldBe(1);

        (await StageAsync(crm, world.Opportunity)).ShouldBe(world.Closing);
        (await TaskSubjectAsync(crm, ActionIds.Task(world.Opportunity, action))).ShouldBe("Call the sponsor");
    }

    [Fact]
    public async Task AGuardThatDoesNotHoldLeavesTheOpportunityWhereItIs()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var world = await WorldAsync(crm);

        // The opportunity is worth 42 000, so this does not hold.
        await GuardAsync(crm, world.Advance, ProcessFields.Amount, GuardOperator.GreaterThan, "100000");
        await ActionAsync(crm, world.Advance, ActionKind.CreateTask, "{}");

        var run = await RunAsync(crm, world.Opportunity, "advance", Cancellation);

        run.IsSuccess.ShouldBeTrue("no transition is a recorded outcome, not a failure.");
        run.Value!.TransitionId.ShouldBeNull();
        run.Value.ActionsRun.ShouldBe(0);

        (await StageAsync(crm, world.Opportunity)).ShouldBe(world.Negotiation);
        (await TaskCountAsync(crm)).ShouldBe(0L);
    }

    /// <summary>
    /// §7's claim, and the only test that can falsify it.
    /// </summary>
    [Fact]
    public async Task AnAdministratorChangesBehaviourWithNoRebuild()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var world = await WorldAsync(crm);

        // Monday: a large opportunity needs an approval, and nothing else does.
        await GuardAsync(crm, world.Advance, ProcessFields.Amount, GuardOperator.GreaterThan, "100000");
        await ActionAsync(crm, world.Advance, ActionKind.RequestApproval, """{"subject":"Approve the discount"}""");

        var monday = await RunAsync(crm, world.Opportunity, "advance", Cancellation);

        monday.Value!.TransitionId.ShouldBeNull("42 000 is not over 100 000.");
        (await TaskCountAsync(crm)).ShouldBe(0L);

        // Tuesday: the sales director lowers the threshold. No deployment, no rebuild — the same
        // binary is still running, and only a row changed.
        await crm.AsTenantAsync(
            CrmSchemaHarness.Northwind,
            "UPDATE transition_guard SET value = '40000' WHERE transition_id = @transition",
            Cancellation,
            ("transition", world.Advance));

        // Put it back where it started so the same trigger applies.
        await crm.AsTenantAsync(
            CrmSchemaHarness.Northwind,
            "UPDATE opportunity SET stage_id = @stage WHERE opportunity_id = @id",
            Cancellation,
            ("stage", world.Negotiation),
            ("id", world.Opportunity));

        var tuesday = await RunAsync(crm, world.Opportunity, "advance", Cancellation);

        tuesday.Value!.TransitionId.ShouldBe(
            world.Advance,
            "the definition changed and the behaviour followed it — that is what §7 claims.");
        tuesday.Value.ActionsRun.ShouldBe(1);
        (await TaskCountAsync(crm)).ShouldBe(1L);
        (await StageAsync(crm, world.Opportunity)).ShouldBe(world.Closing);
    }

    /// <summary>
    /// Re-offering the same change writes the same task rather than a second one.
    /// </summary>
    [Fact]
    public async Task AReofferedChangeDoesNotLeaveASecondTask()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var world = await WorldAsync(crm);

        await ActionAsync(crm, world.Advance, ActionKind.CreateTask, """{"subject":"Follow up"}""");

        await RunAsync(crm, world.Opportunity, "advance", Cancellation);

        // The feed re-offers the change. The opportunity is now in Closing, so put it back:
        // what is under test is the action's id, not the matching.
        await crm.AsTenantAsync(
            CrmSchemaHarness.Northwind,
            "UPDATE opportunity SET stage_id = @stage WHERE opportunity_id = @id",
            Cancellation,
            ("stage", world.Negotiation),
            ("id", world.Opportunity));

        await RunAsync(crm, world.Opportunity, "advance", Cancellation);

        (await TaskCountAsync(crm)).ShouldBe(
            1L, "the activity's id is derived from the opportunity and the action.");
    }

    [Fact]
    public async Task ASetFieldActionWritesTheProbability()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var world = await WorldAsync(crm);

        await ActionAsync(crm, world.Advance, ActionKind.SetField, """{"probability":90}""");

        (await RunAsync(crm, world.Opportunity, "advance", Cancellation)).Value!.ActionsRun.ShouldBe(1);

        (await crm.ScalarAsTenantAsync<int>(
            CrmSchemaHarness.Northwind,
            "SELECT probability FROM opportunity WHERE opportunity_id = @id",
            Cancellation,
            ("id", world.Opportunity))).ShouldBe(90);
    }

    /// <summary>
    /// A notification naming no connector reports that it ran nothing.
    /// </summary>
    /// <remarks>
    /// <c>SendNotification</c> is wired now — it queues against a connector the tenant
    /// registered — and this is the case where the administrator named none. Still false, and
    /// still not an error: a transition must not start failing because an integration is
    /// unconfigured. <c>ConnectorNotificationTests</c> is the wired half.
    /// </remarks>
    [Fact]
    public async Task AnUnconfiguredNotificationReportsThatItDidNothing()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var world = await WorldAsync(crm);

        await ActionAsync(crm, world.Advance, ActionKind.SendNotification, "{}");

        var run = await RunAsync(crm, world.Opportunity, "advance", Cancellation);

        run.Value!.TransitionId.ShouldBe(world.Advance, "the transition still happens.");
        run.Value.ActionsRun.ShouldBe(0, "a stub that reported success would be a lie.");
    }

    [Fact]
    public async Task AnUnreadableParameterFallsBackRatherThanFailingTheTransition()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var world = await WorldAsync(crm);

        var action = await ActionAsync(crm, world.Advance, ActionKind.CreateTask, """{"subject":42}""");

        var run = await RunAsync(crm, world.Opportunity, "advance", Cancellation);

        run.Value!.ActionsRun.ShouldBe(1);
        (await TaskSubjectAsync(crm, ActionIds.Task(world.Opportunity, action)))
            .ShouldBe("Follow up", "a typo in one action must not drop the actions after it.");
    }

    [Fact]
    public async Task AnOpportunityAnotherTenantOwnsIsNotFound()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var world = await WorldAsync(crm);

        var run = await RunAsync(crm, world.Opportunity, "advance", Cancellation, CrmTokens.ContosoTenant);

        run.IsSuccess.ShouldBeFalse();
        run.Error!.Code.ShouldBe("crm.opportunity_not_found");
    }

    // ------------------------------------------------------------------------------- fixtures

    private sealed record World(
        Guid Opportunity, Guid Negotiation, Guid Closing, Guid Advance);

    /// <summary>An account, a contact, a two-stage process and an opportunity in the first stage.</summary>
    private static async Task<World> WorldAsync(CrmSchemaHarness crm)
    {
        var account = await crm.AccountAsync(CrmSchemaHarness.Northwind, Lifecycle.Prospect, Cancellation);
        var contact = await crm.ContactAsync(CrmSchemaHarness.Northwind, account, Cancellation);
        var (process, negotiation) = await crm.ProcessAsync(CrmSchemaHarness.Northwind, 1, true, Cancellation);

        var closing = Guid.NewGuid();

        await crm.AsTenantAsync(
            CrmSchemaHarness.Northwind,
            """
            INSERT INTO process_stage (stage_id, process_id, name, ordinal, is_terminal)
            VALUES (@id, @process, 'Closing', 2, false)
            """,
            Cancellation,
            ("id", closing),
            ("process", process));

        var advance = Guid.NewGuid();

        await crm.AsTenantAsync(
            CrmSchemaHarness.Northwind,
            """
            INSERT INTO process_transition (transition_id, from_stage_id, to_stage_id, trigger, ordinal)
            VALUES (@id, @from, @to, 'advance', 1)
            """,
            Cancellation,
            ("id", advance),
            ("from", negotiation),
            ("to", closing));

        var opportunity = await crm.OpportunityAsync(
            CrmSchemaHarness.Northwind, account, contact, negotiation, Cancellation);

        return new World(opportunity, negotiation, closing, advance);
    }

    private static async Task GuardAsync(
        CrmSchemaHarness crm, Guid transition, string field, GuardOperator op, string value) =>
        await crm.AsTenantAsync(
            CrmSchemaHarness.Northwind,
            """
            INSERT INTO transition_guard (guard_id, transition_id, field, operator, value)
            VALUES (@id, @transition, @field, @operator, @value)
            """,
            Cancellation,
            ("id", Guid.NewGuid()),
            ("transition", transition),
            ("field", field),
            ("operator", op.ToString()),
            ("value", value)).ConfigureAwait(false);

    private static async Task<Guid> ActionAsync(
        CrmSchemaHarness crm, Guid transition, ActionKind kind, string parameters)
    {
        var id = Guid.NewGuid();

        await crm.AsTenantAsync(
            CrmSchemaHarness.Northwind,
            """
            INSERT INTO transition_action (action_id, transition_id, kind, parameters, ordinal)
            VALUES (@id, @transition, @kind, @parameters::jsonb, 1)
            """,
            Cancellation,
            ("id", id),
            ("transition", transition),
            ("kind", kind.ToString()),
            ("parameters", parameters));

        return id;
    }

    private static ValueTask<FlowExecutionResult<TransitionApplied>> RunAsync(
        CrmSchemaHarness crm,
        Guid opportunity,
        string trigger,
        CancellationToken ct,
        string? tenant = null)
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

        var delivery = new BusMessage(
            Guid.NewGuid(),
            Topic: "opportunity.stage.changed",
            Type: "opportunity.stage.changed",
            SchemaVersion: "1.0.0",
            PartitionKey: opportunity.ToString(),
            Payload: JsonSerializer.Serialize(
                new OpportunityStageChanged(opportunity, trigger),
                CrmJsonContext.Default.OpportunityStageChanged));

        return host.RunAsync(
            RunWorkflowTransitionFlow.Plan,
            new RunWorkflowTransitionFlow.Dispatcher(
                runConfiguredTransition: new RunConfiguredTransition(
                    new ProcessStore(crm.DataSource), new ConnectorStore(crm.DataSource))),
            new FlowInvocation(
                "corr-" + opportunity,
                opportunity.ToString(),
                tenant ?? CrmSchemaHarness.Northwind,
                Deadline: null,
                Principal: null,
                IsContinuation: true,
                TenantAttested: true),
            delivery,
            RunWorkflowTransitionFlow.Projection,
            ct);
    }

    private static async ValueTask<Guid> StageAsync(CrmSchemaHarness crm, Guid opportunity) =>
        await crm.ScalarAsTenantAsync<Guid>(
            CrmSchemaHarness.Northwind,
            "SELECT stage_id FROM opportunity WHERE opportunity_id = @id",
            Cancellation,
            ("id", opportunity)).ConfigureAwait(false);

    private static async ValueTask<long> TaskCountAsync(CrmSchemaHarness crm) =>
        await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind,
            "SELECT count(*) FROM activity WHERE kind = 'Task'",
            Cancellation).ConfigureAwait(false);

    private static async ValueTask<string?> TaskSubjectAsync(CrmSchemaHarness crm, Guid activity) =>
        await crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Northwind,
            "SELECT subject FROM activity WHERE activity_id = @id",
            Cancellation,
            ("id", activity)).ConfigureAwait(false);

    private static string Because<T>(FlowExecutionResult<T> run) =>
        run.Error is null ? "the run failed with no error" : run.Error.Code + ": " + run.Error.Message;
}
