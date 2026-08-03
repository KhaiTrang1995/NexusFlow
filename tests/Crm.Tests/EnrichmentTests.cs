using System.Security.Claims;
using System.Text.Json;
using Crm;
using FlowX;
using FlowX.Conformance.InMemory;
using FlowX.Hosting;
using FlowX.Runtime;
using FlowX.Testing;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// The enrichment wait, of <c>docs/26-CRM-Sample.md</c> §8.2 and §10 package 6.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The test package 6 exists for is <see cref="AWebhookBetweenAttemptsEndsTheSameWait"/>.</strong>
/// Everything else here checks a piece of the wait; that one checks the claim — a delivery
/// arriving between two attempts ends the wait the instance is already parked on, without another
/// attempt and without a lease.
/// </para>
/// <para>
/// <strong>The provider never answers on the polled path in that test.</strong> If it could, a run
/// that ended because an attempt succeeded would be indistinguishable from one that ended because
/// the webhook arrived, and the assertions would pass for the wrong reason.
/// </para>
/// </remarks>
public sealed class EnrichmentTests
{
    private const string WebhookType = "enrichment.webhook";

    private static readonly CompanyProfile Profile = new("Software", 240, "EU-WEST");

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------------------- the wait

    /// <summary>
    /// §10 package 6's "done when", and the only test that can falsify it.
    /// </summary>
    [Fact]
    public async Task AWebhookBetweenAttemptsEndsTheSameWait()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var lead = await crm.LeadAsync(CrmSchemaHarness.Northwind, Cancellation);

        var world = World(crm);

        // The provider is asked and says nothing. It will go on saying nothing.
        var started = await StartAsync(world, lead);

        started.IsSuspended.ShouldBeTrue("the poll parks between attempts: " + started.Error);

        var instance = started.InstanceId!.Value;
        var parked = await world.Journal.ReadInstanceAsync(instance, Cancellation);

        parked.Value.State.ShouldBe(FlowInstanceState.Suspended);

        parked.Value.Wake!.Value.At.ShouldBeGreaterThan(
            world.Clock.UtcNow,
            "the next attempt is seconds away, so only the delivery can end this wait.");

        (await world.Leases.ReadAsync(instance, Cancellation)).IsSuccess.ShouldBeFalse(
            "a parked instance holds no lease — that is what makes the wait free.");

        // The provider's webhook. It carries the answer, and the flow has never seen it.
        var signalled = await world.Host.SignalAsync(
            instance,
            new FlowRegistration(EnrichLeadFlow.Plan, world.Dispatcher),
            FlowSignal.Of(WebhookType, new EnrichmentWebhook(EnrichmentTickets.For(lead), Profile)),
            Representative,
            Cancellation);

        signalled.IsSuccess.ShouldBeTrue("the delivery ran the flow to the end: " + signalled.Error);

        var ended = await world.Journal.ReadInstanceAsync(instance, Cancellation);

        ended.Value.Wake.ShouldBeNull("one wait ended once.");

        (await IndustryAsync(crm, lead)).ShouldBe(
            "Software", "the profile came off the delivery, not off an attempt.");

        (await StatusAsync(crm, lead)).ShouldBe("Answered");
    }

    [Fact]
    public async Task AnAnswerAlreadyThereEndsTheWaitOnTheFirstAttempt()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var lead = await crm.LeadAsync(CrmSchemaHarness.Northwind, Cancellation);

        var world = World(crm);

        world.Provider.Answer(EnrichmentTickets.For(lead), Profile);

        var run = await StartAsync(world, lead);

        run.IsSuspended.ShouldBeFalse("the first attempt was satisfied: " + run.Error);
        run.IsSuccess.ShouldBeTrue(run.Error?.Code);
        run.Value!.Industry.ShouldBe("Software");
        run.Value.Employees.ShouldBe(240);
    }

    [Fact]
    public async Task AnAttemptThatFindsAnAnswerWritesItDown()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var lead = await crm.LeadAsync(CrmSchemaHarness.Northwind, Cancellation);

        var world = World(crm);

        // Parked, with nothing recorded but the request.
        var started = await StartAsync(world, lead);

        (await StatusAsync(crm, lead)).ShouldBe("Pending");

        // The provider answers where the next attempt will see it, rather than by webhook.
        world.Provider.Answer(EnrichmentTickets.For(lead), Profile);

        // And the next attempt falls due. Nothing else moves: the clock is the only difference
        // between this resume and one that would park again.
        world.Clock.Advance(TimeSpan.FromMinutes(5));

        var resumed = await world.Host.ResumeAsync(
            started.InstanceId!.Value,
            new FlowRegistration(EnrichLeadFlow.Plan, world.Dispatcher),
            CrmTokens.NorthwindTenant,
            Cancellation);

        resumed.IsSuccess.ShouldBeTrue(resumed.Error?.ToString());
        (await StatusAsync(crm, lead)).ShouldBe("Answered");
    }

    /// <summary>
    /// A provider that never answers ends the wait at the budget, and the row says so.
    /// </summary>
    [Fact]
    public async Task AProviderThatNeverAnswersEndsTheWaitAtTheBudget()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var lead = await crm.LeadAsync(CrmSchemaHarness.Northwind, Cancellation);

        var world = World(crm);

        var started = await StartAsync(world, lead);

        world.Clock.Advance(EnrichmentWaits.Budget + TimeSpan.FromMinutes(1));

        var resumed = await world.Host.ResumeAsync(
            started.InstanceId!.Value,
            new FlowRegistration(EnrichLeadFlow.Plan, world.Dispatcher),
            CrmTokens.NorthwindTenant,
            Cancellation);

        resumed.Error!.Code.ShouldBe("crm.enrichment_timed_out");

        (await StatusAsync(crm, lead)).ShouldBe(
            "Abandoned", "the escalation marks the request rather than leaving it Pending forever.");
    }

    // -------------------------------------------------------------------------- the recording

    [Fact]
    public async Task TheTicketIsDerivedSoTwoDeliveriesAskAboutOneRequest()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var lead = await crm.LeadAsync(CrmSchemaHarness.Northwind, Cancellation);

        var world = World(crm);

        await StartAsync(world, lead);
        await StartAsync(world, lead);

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind,
            "SELECT count(*) FROM lead_enrichment WHERE lead_id = @lead",
            Cancellation,
            ("lead", lead))).ShouldBe(1L, "one lead, one request, whatever the broker redelivers.");
    }

    [Fact]
    public async Task ALeadAnotherTenantOwnsIsNotFound()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var lead = await crm.LeadAsync(CrmSchemaHarness.Contoso, Cancellation);

        var run = await StartAsync(World(crm), lead);

        run.Error!.Code.ShouldBe("crm.lead_not_found");
    }

    /// <summary>
    /// The row holds an answer and an instant together, or neither, and the database says so.
    /// </summary>
    [Fact]
    public async Task AHalfWrittenAnswerIsRefusedByTheSchema()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var lead = await crm.LeadAsync(CrmSchemaHarness.Northwind, Cancellation);

        await StartAsync(World(crm), lead);

        var refusal = await crm.RefusalAsync(
            CrmSchemaHarness.Northwind,
            "UPDATE lead_enrichment SET status = 'Answered', industry = 'Software' WHERE lead_id = @lead",
            Cancellation,
            ("lead", lead));

        refusal.ShouldNotBeNull("an Answered row with no employees, region or instant is not an answer.");
    }

    [Fact]
    public async Task AnEnrichmentRowIsNotVisibleToAnotherTenant()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        var lead = await crm.LeadAsync(CrmSchemaHarness.Northwind, Cancellation);

        await StartAsync(World(crm), lead);

        (await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Contoso,
            "SELECT count(*) FROM lead_enrichment",
            Cancellation)).ShouldBe(0L);
    }

    // ------------------------------------------------------------------------------- fixtures

    /// <summary>The sample's own representative token, whose <c>tid</c> claim is the tenant.</summary>
    private static ClaimsPrincipal Representative =>
        new(new ClaimsIdentity(CrmTokens.Claims[CrmTokens.Northwind], "CrmTokenTest"));

    private sealed record EnrichmentWorld(
        FlowHost Host,
        FlowTestClock Clock,
        InMemoryFlowJournal Journal,
        InMemoryLeaseStore Leases,
        EnrichmentProvider Provider,
        EnrichLeadFlow.Dispatcher Dispatcher);

    private static EnrichmentWorld World(CrmSchemaHarness crm)
    {
        var journal = new InMemoryFlowJournal();
        var leases = new InMemoryLeaseStore();
        var provider = new EnrichmentProvider();
        var store = new EnrichmentStore(crm.DataSource);

        var clock = new FlowTestClock();

        var host = new FlowHost(
            new FlowEngine(clock),
            new FlowXOptions
            {
                ApplicationName = "Crm",
                NodeName = "test-node",
                TenantIsolation = TenantIsolation.Row,
            },
            new FlowDurability(journal, leases));

        var dispatcher = new EnrichLeadFlow.Dispatcher(
            requestLeadEnrichment: new RequestLeadEnrichment(store, provider),
            checkLeadEnrichment: new CheckLeadEnrichment(store, provider),
            abandonLeadEnrichment: new AbandonLeadEnrichment(store),
            applyLeadEnrichment: new ApplyLeadEnrichment(store));

        return new EnrichmentWorld(host, clock, journal, leases, provider, dispatcher);
    }

    private static ValueTask<FlowExecutionResult<LeadEnriched>> StartAsync(
        EnrichmentWorld world, Guid lead) =>
        world.Host.RunAsync(
            EnrichLeadFlow.Plan,
            world.Dispatcher,
            new FlowInvocation(
                "corr-" + lead,
                lead.ToString(),
                CrmTokens.NorthwindTenant,
                Deadline: null,
                Principal: null,
                IsContinuation: true,
                TenantAttested: true),
            new BusMessage(
                Guid.NewGuid(),
                Topic: "lead.created",
                Type: "lead.created",
                SchemaVersion: "1.0.0",
                PartitionKey: lead.ToString(),
                Payload: JsonSerializer.Serialize(
                    new LeadCreated(lead, "Northwind Traders", LeadSource.Web),
                    CrmJsonContext.Default.LeadCreated)),
            EnrichLeadFlow.Projection,
            Cancellation);

    private static async ValueTask<string?> StatusAsync(CrmSchemaHarness crm, Guid lead) =>
        await crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Northwind,
            "SELECT status FROM lead_enrichment WHERE lead_id = @lead",
            Cancellation,
            ("lead", lead)).ConfigureAwait(false);

    private static async ValueTask<string?> IndustryAsync(CrmSchemaHarness crm, Guid lead) =>
        await crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Northwind,
            "SELECT industry FROM lead_enrichment WHERE lead_id = @lead",
            Cancellation,
            ("lead", lead)).ConfigureAwait(false);
}
