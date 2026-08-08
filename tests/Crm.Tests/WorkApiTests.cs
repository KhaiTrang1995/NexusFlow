using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// <c>POST /api/v1/crm/tasks</c> and <c>POST /api/v1/crm/opportunities/triggers</c>: the two
/// routes whose interesting behaviour is what they refuse and what they announce.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A task's parent is held up by a trigger rather than a foreign key</strong>, because
/// <c>activity.relates_to_id</c> can point at four different tables. <c>ActivityIntegrityTests</c>
/// proves the trigger refuses a dangling reference; what this proves is that a caller meets a
/// <c>404</c> naming what they got wrong instead of a constraint violation — the reason
/// <c>CreateTaskForSubject</c> checks a second time above the database.
/// </para>
/// <para>
/// <strong>Advancing an opportunity moves nothing</strong>, and that is the assertion. The route
/// records that a trigger was applied and stages <c>opportunity.stage.changed</c>; where the
/// opportunity goes is read out of the administrator's definition by a flow this one has never
/// heard of. A test that asserted a new stage here would be asserting the seam shut.
/// </para>
/// </remarks>
public sealed class WorkApiTests
{
    private const string Tasks = "/api/v1/crm/tasks";
    private const string Triggers = "/api/v1/crm/opportunities/triggers";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A task against a real lead is written and hangs off it.</summary>
    [Fact]
    public async Task ATaskAgainstALeadIsWrittenAgainstThatLead()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var lead = await app.Crm.LeadAsync(CrmSchemaHarness.Northwind, Cancellation);
        var due = DateTimeOffset.UtcNow.AddDays(2);

        var response = await app.PostAsync(
            Tasks,
            new CreateTask(
                ActivityKind.Task,
                "Follow up",
                new RelatedRef(EntityKind.Lead, lead),
                Guid.NewGuid(),
                due),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var created = await CrmApplication.ReadAsync<TaskCreated>(response);

        created.ActivityId.ShouldNotBe(Guid.Empty);

        (await app.Crm.ScalarAsTenantAsync<Guid>(
            CrmSchemaHarness.Northwind,
            "SELECT relates_to_id FROM activity WHERE activity_id = @id",
            Cancellation,
            ("id", created.ActivityId)))
            .ShouldBe(lead, "the task was written against something other than the lead it named.");

        (await app.Crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Northwind,
            "SELECT status FROM activity WHERE activity_id = @id",
            Cancellation,
            ("id", created.ActivityId)))
            .ShouldBe("Open");
    }

    /// <summary>
    /// A task against a parent this tenant does not have is a 404 naming the kind and the id.
    /// </summary>
    /// <remarks>
    /// The id belongs to Contoso and the caller is Northwind, so the row exists and the tenant
    /// cannot see it — the same answer as an id that never existed, which is the point.
    /// </remarks>
    [Fact]
    public async Task ATaskAgainstAnotherTenantsLeadIsNotFound()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var elsewhere = await app.Crm.LeadAsync(CrmSchemaHarness.Contoso, Cancellation);

        var response = await app.PostAsync(
            Tasks,
            new CreateTask(
                ActivityKind.Task,
                "Follow up",
                new RelatedRef(EntityKind.Lead, elsewhere),
                Guid.NewGuid(),
                DueAt: null),
            CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        var problem = await Problem(response);

        problem.GetProperty("code").GetString().ShouldBe("crm.task_subject_not_found");
        problem.GetProperty("kind").GetString().ShouldBe(
            nameof(EntityKind.Lead),
            "the structured detail is what lets a client say which field was wrong.");

        (await app.Crm.ScalarAsOwnerAsync<long>("SELECT count(*) FROM activity", Cancellation))
            .ShouldBe(
                0,
                "the caller was refused and an activity was written anyway — which the trigger " +
                "of migration 0003 would have caught, and nobody would have seen the 404.");
    }

    /// <summary>
    /// Advancing an opportunity records the trigger and stages the event the process reads.
    /// </summary>
    [Fact]
    public async Task AdvancingAnOpportunityAnnouncesItAndMovesNothing()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var account = await app.Crm.AccountAsync(
            CrmSchemaHarness.Northwind, Lifecycle.Prospect, Cancellation);
        var contact = await app.Crm.ContactAsync(CrmSchemaHarness.Northwind, account, Cancellation);
        var (_, stage) = await app.Crm.ProcessAsync(CrmSchemaHarness.Northwind, 1, true, Cancellation);
        var opportunity = await app.Crm.OpportunityAsync(
            CrmSchemaHarness.Northwind, account, contact, stage, Cancellation);

        var response = await app.PostAsync(
            Triggers, new AdvanceOpportunity(opportunity, "qualified"), CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var advanced = await CrmApplication.ReadAsync<OpportunityAdvanced>(response);

        advanced.Trigger.ShouldBe("qualified");
        advanced.ApplicationId.ShouldNotBe(Guid.Empty, "the handle a caller reads the outcome by.");

        // Nothing has decided anything: this route does not, and the change feed is not running
        // in this host. Pending is the answer, and it is the one that used to be unsayable — a
        // caller saw the stage unchanged and could not tell it from a move the engine refused.
        var outcome = await CrmApplication.ReadAsync<TriggerOutcomeView>(
            await app.PostAsync(
                "/api/v1/crm/opportunities/trigger-outcomes",
                new ReadTriggerOutcome(advanced.ApplicationId),
                CrmTokens.Northwind,
                idempotencyKey: null));

        outcome.Outcome.ShouldBe(TriggerOutcome.Pending);
        outcome.Trigger.ShouldBe("qualified");
        outcome.DecidedAt.ShouldBeNull();
        outcome.Stage.ShouldBe("Qualification", "where the trigger was applied from.");

        (await app.PostAsync(
            "/api/v1/crm/opportunities/trigger-outcomes",
            new ReadTriggerOutcome(advanced.ApplicationId),
            CrmTokens.Contoso,
            idempotencyKey: null))
            .StatusCode.ShouldBe(
                HttpStatusCode.NotFound,
                "another tenant read what somebody in this one applied.");

        (await app.Crm.ScalarAsOwnerAsync<long>(
            """
            SELECT count(*) FROM outbox_event
            WHERE type = 'opportunity.stage.changed' AND published_at IS NULL
            """,
            Cancellation))
            .ShouldBe(
                1,
                "the caller was told the trigger was applied and nothing was staged for the " +
                "configured process to read.");

        (await app.Crm.ScalarAsTenantAsync<Guid>(
            CrmSchemaHarness.Northwind,
            "SELECT stage_id FROM opportunity WHERE opportunity_id = @id",
            Cancellation,
            ("id", opportunity)))
            .ShouldBe(
                stage,
                "this route moved the opportunity itself. Where it goes is the administrator's " +
                "definition to decide, and a route that decided it would close the seam the " +
                "whole sample exists to demonstrate.");
    }

    /// <summary>A trigger against another tenant's opportunity is refused and stages nothing.</summary>
    [Fact]
    public async Task AdvancingAnotherTenantsOpportunityIsRefusedAndAnnouncesNothing()
    {
        await using var app = await CrmApplication.StartAsync(Cancellation);

        var account = await app.Crm.AccountAsync(
            CrmSchemaHarness.Contoso, Lifecycle.Prospect, Cancellation);
        var contact = await app.Crm.ContactAsync(CrmSchemaHarness.Contoso, account, Cancellation);
        var (_, stage) = await app.Crm.ProcessAsync(CrmSchemaHarness.Contoso, 1, true, Cancellation);
        var elsewhere = await app.Crm.OpportunityAsync(
            CrmSchemaHarness.Contoso, account, contact, stage, Cancellation);

        var response = await app.PostAsync(
            Triggers, new AdvanceOpportunity(elsewhere, "qualified"), CrmTokens.Northwind);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await app.Crm.ScalarAsOwnerAsync<long>(
            "SELECT count(*) FROM outbox_event WHERE type = 'opportunity.stage.changed'",
            Cancellation))
            .ShouldBe(
                0,
                "an event announcing a stage change was staged for an opportunity the caller " +
                "cannot see. The change feed would then hand it to the process.");
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
