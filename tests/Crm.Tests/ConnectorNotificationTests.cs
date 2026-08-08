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
/// The configured process reaching a connector: an administrator's <c>SendNotification</c>
/// queuing a delivery against a system the tenant registered.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the seam the whole connector registry exists for.</strong> Before it,
/// <c>SendNotification</c> was declared, publishable, configurable and did nothing — the sample
/// said so rather than stubbing it. What closes it is that the administrator names a connector
/// by name in the action's parameters, and a delivery appears in the queue.
/// </para>
/// <para>
/// <strong>Queued, not sent.</strong> Nothing here reaches a network: a transition that made an
/// outbound request inline would hold its transaction open across somebody else's gateway. The
/// row is what the transition writes; the sweep is what delivers it.
/// </para>
/// </remarks>
public sealed class ConnectorNotificationTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A configured notification queues a delivery for the named connector.</summary>
    [Fact]
    public async Task AConfiguredNotificationQueuesADeliveryForTheNamedConnector()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var world = await WorldAsync(crm);
        var connector = await ConnectorAsync(crm, "ops_webhook", isEnabled: true);

        var action = await ActionAsync(
            crm, world.Advance, """{"connector":"ops_webhook","subject":"deal moved"}""");

        var run = await RunAsync(crm, world.Opportunity);

        run.Value!.ActionsRun.ShouldBe(1, "the notification reported that it did nothing.");

        (await crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Northwind,
            "SELECT subject FROM connector_delivery WHERE connector_id = @id",
            Cancellation,
            ("id", connector)))
            .ShouldBe("deal moved");

        (await crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Northwind,
            "SELECT payload->>'opportunityId' FROM connector_delivery WHERE connector_id = @id",
            Cancellation,
            ("id", connector)))
            .ShouldBe(
                world.Opportunity.ToString(),
                "the payload must say which opportunity moved, or nobody can act on it.");

        // The id is derived from the opportunity and the action, so a change the feed re-offers
        // does not send the notification twice.
        (await RunAsync(crm, world.Opportunity)).Value.ShouldNotBeNull();

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind,
            "SELECT count(*) FROM connector_delivery WHERE connector_id = @id",
            Cancellation,
            ("id", connector)))
            .ShouldBe(1, "a re-offered change queued the same notification a second time.");

        action.ShouldNotBe(Guid.Empty);
    }

    /// <summary>A notification naming a disabled connector queues nothing and fails nothing.</summary>
    /// <remarks>
    /// Switching an integration off must not start failing every transition that mentions it —
    /// which is what an error here would do, to a process the administrator did not touch.
    /// </remarks>
    [Fact]
    public async Task ANotificationNamingADisabledConnectorIsSkippedRatherThanFailed()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var world = await WorldAsync(crm);
        var connector = await ConnectorAsync(crm, "ops_webhook", isEnabled: false);

        await ActionAsync(crm, world.Advance, """{"connector":"ops_webhook"}""");

        var run = await RunAsync(crm, world.Opportunity);

        run.IsSuccess.ShouldBeTrue("a disabled connector failed the whole transition.");
        run.Value!.ActionsRun.ShouldBe(0);

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind,
            "SELECT count(*) FROM connector_delivery WHERE connector_id = @id",
            Cancellation,
            ("id", connector)))
            .ShouldBe(0);
    }

    /// <summary>A notification naming a connector this tenant does not have is skipped.</summary>
    [Fact]
    public async Task ANotificationNamingAnUnknownConnectorIsSkipped()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var world = await WorldAsync(crm);

        await ActionAsync(crm, world.Advance, """{"connector":"nobody_registered_this"}""");

        var run = await RunAsync(crm, world.Opportunity);

        run.IsSuccess.ShouldBeTrue();
        run.Value!.ActionsRun.ShouldBe(0);
    }

    // ------------------------------------------------------------------------------- fixtures

    private sealed record World(Guid Opportunity, Guid Advance);

    private static async Task<Guid> ConnectorAsync(
        CrmSchemaHarness crm, string name, bool isEnabled)
    {
        var id = Guid.NewGuid();

        await crm.AsTenantAsync(
            CrmSchemaHarness.Northwind,
            """
            INSERT INTO connector (
                connector_id, tenant_id, name, kind, endpoint, secret_name, is_enabled, created_at)
            VALUES (@id, @tenant, @name, 'Webhook', 'https://example.test/hook', NULL, @enabled, now())
            """,
            Cancellation,
            ("id", id),
            ("tenant", CrmSchemaHarness.Northwind),
            ("name", name),
            ("enabled", isEnabled));

        return id;
    }

    private static async Task<Guid> ActionAsync(
        CrmSchemaHarness crm, Guid transition, string parameters)
    {
        var id = Guid.NewGuid();

        await crm.AsTenantAsync(
            CrmSchemaHarness.Northwind,
            """
            INSERT INTO transition_action (action_id, transition_id, kind, parameters, ordinal)
            VALUES (@id, @transition, 'SendNotification', @parameters::jsonb, 1)
            """,
            Cancellation,
            ("id", id),
            ("transition", transition),
            ("parameters", parameters));

        return id;
    }

    /// <summary>An account, a contact, a two-stage process and an opportunity in the first.</summary>
    private static async Task<World> WorldAsync(CrmSchemaHarness crm)
    {
        var account = await crm.AccountAsync(CrmSchemaHarness.Northwind, Lifecycle.Prospect, Cancellation);
        var contact = await crm.ContactAsync(CrmSchemaHarness.Northwind, account, Cancellation);
        var (process, negotiation) = await crm.ProcessAsync(
            CrmSchemaHarness.Northwind, 1, true, Cancellation);

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

        return new World(opportunity, advance);
    }

    private static ValueTask<FlowExecutionResult<TransitionApplied>> RunAsync(
        CrmSchemaHarness crm, Guid opportunity)
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
                // No application id: this change stands for one the register has no row for, and
                // the engine has to run the configured notification anyway. What a caller is told
                // afterwards is TransitionTests's business.
                new OpportunityStageChanged(opportunity, "advance", Guid.Empty),
                CrmJsonContext.Default.OpportunityStageChanged));

        return host.RunAsync(
            RunWorkflowTransitionFlow.Plan,
            new RunWorkflowTransitionFlow.Dispatcher(
                runConfiguredTransition: new RunConfiguredTransition(
                    new ProcessStore(crm.DataSource),
                    new ConnectorStore(crm.DataSource),
                    new TriggerLogStore(crm.DataSource))),
            new FlowInvocation(
                "corr-" + opportunity,
                opportunity.ToString(),
                CrmSchemaHarness.Northwind,
                Deadline: null,
                Principal: null,
                IsContinuation: true,
                TenantAttested: true),
            delivery,
            RunWorkflowTransitionFlow.Projection,
            Cancellation);
    }
}
