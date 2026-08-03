using System.Security.Claims;
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
/// Lead intake and the fan-out of <c>docs/26-CRM-Sample.md</c> §8.5, against a real PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What §8.5 claims is about two subscriptions, not about a broker.</strong> One
/// capture stages one <c>crm.lead.created</c>; two flows consume it under different groups; a
/// redelivery to one does not re-run the other. Those are properties of the subscriptions and
/// of the two capabilities' idempotence, and they hold over RabbitMQ, over Redis Streams, and
/// here — where the delivery is handed to the flow directly, so the assertion is about the
/// flows rather than about whichever broker is wired.
/// </para>
/// <para>
/// The transport itself is held to its contract by <c>PublisherConformance</c> and the bus
/// consumer suites, against three implementations. Standing a broker up here would test those
/// again.
/// </para>
/// </remarks>
public sealed class IntakeTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ACapturedLeadIsWrittenAndAnnounced()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var journal = new InMemoryFlowJournal();
        var lead = Guid.Parse("aa11bb22-cc33-4d44-8e55-ff6600112233");

        var run = await CaptureAsync(crm, journal, lead, Cancellation);

        run.IsSuccess.ShouldBeTrue();
        run.Value!.LeadId.ShouldBe(lead, "the idempotency key is the lead's id, so a retry is one lead.");

        (await CountAsync(crm, lead)).ShouldBe(1L);

        var instance = journal.Instances.ShouldHaveSingleItem();
        var outbox = await journal.ReadOutboxAsync(instance.InstanceId, Cancellation);

        var staged = outbox.Value!.ShouldHaveSingleItem();

        staged.Type.ShouldBe("lead.created");
    }

    /// <summary>
    /// §8.5: one publish, two flows, and neither knows the other ran.
    /// </summary>
    [Fact]
    public async Task OnePublishReachesBothSubscriptions()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var lead = Guid.Parse("bb22cc33-dd44-4e55-9f66-001122334455");

        await CaptureAsync(crm, new InMemoryFlowJournal(), lead, Cancellation);

        var delivery = Delivery(lead);

        (await ScoreAsync(crm, delivery, Cancellation)).IsSuccess.ShouldBeTrue();
        (await AssignAsync(crm, delivery, Cancellation)).IsSuccess.ShouldBeTrue();

        var (score, owner) = await ScoreAndOwnerAsync(crm, lead);

        score.ShouldBe(40, "a Web lead scores 40.");
        owner.ShouldBe(AssignLead.OwnerFor(lead));
    }

    /// <summary>
    /// A redelivery to one subscription must not disturb what the other did.
    /// </summary>
    [Fact]
    public async Task ARedeliveryToOneSubscriptionDoesNotReRunTheOther()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var lead = Guid.Parse("cc33dd44-ee55-4f66-8077-112233445566");

        await CaptureAsync(crm, new InMemoryFlowJournal(), lead, Cancellation);

        var delivery = Delivery(lead);

        await ScoreAsync(crm, delivery, Cancellation);
        await AssignAsync(crm, delivery, Cancellation);

        var assigned = (await ScoreAndOwnerAsync(crm, lead)).Owner;

        // A representative takes the lead over by hand, as one does.
        var taken = Guid.Parse("99887766-5544-4332-a110-aabbccddeeff");

        await crm.AsTenantAsync(
            CrmSchemaHarness.Northwind,
            "UPDATE lead SET owner_id = @owner WHERE lead_id = @id",
            Cancellation,
            ("owner", taken),
            ("id", lead));

        // The broker hands the same event to the scorer again.
        (await ScoreAsync(crm, delivery, Cancellation)).IsSuccess.ShouldBeTrue();

        var (score, owner) = await ScoreAndOwnerAsync(crm, lead);

        score.ShouldBe(40, "scoring twice writes the same number.");
        owner.ShouldBe(taken, "a redelivery to the scorer must not re-run the assignment.");
        owner.ShouldNotBe(assigned);
    }

    /// <summary>
    /// The assigner's own redelivery is harmless too, and the guard is in the statement.
    /// </summary>
    [Fact]
    public async Task ARedeliveryToTheAssignerLeavesAHeldLeadAlone()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var lead = Guid.Parse("dd44ee55-ff66-4077-8188-223344556677");

        await CaptureAsync(crm, new InMemoryFlowJournal(), lead, Cancellation);

        var delivery = Delivery(lead);

        await AssignAsync(crm, delivery, Cancellation);

        var taken = Guid.Parse("11223344-5566-4778-899a-bbccddeeff00");

        await crm.AsTenantAsync(
            CrmSchemaHarness.Northwind,
            "UPDATE lead SET owner_id = @owner WHERE lead_id = @id",
            Cancellation,
            ("owner", taken),
            ("id", lead));

        (await AssignAsync(crm, delivery, Cancellation)).IsSuccess.ShouldBeTrue();

        (await ScoreAndOwnerAsync(crm, lead)).Owner
            .ShouldBe(taken, "AND owner_id IS NULL is what makes the second delivery harmless.");
    }

    /// <summary>A delivery with no body is refused rather than guessed at.</summary>
    [Fact]
    public async Task ADeliveryWithNoBodyIsRefused()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var empty = new BusMessage(
            Guid.Parse("ee55ff66-0077-4188-929a-334455667788"),
            Topic: "lead.created",
            Type: "lead.created",
            SchemaVersion: "1.0.0",
            PartitionKey: null,
            Payload: null);

        var run = await ScoreAsync(crm, empty, Cancellation);

        run.IsSuccess.ShouldBeFalse();
        run.Error!.Code.ShouldBe("crm.event_has_no_body");
    }

    private static BusMessage Delivery(Guid lead) =>
        new(Guid.Parse("f0f0f0f0-1111-4222-8333-444455556666"),
            Topic: "lead.created",
            Type: "lead.created",
            SchemaVersion: "1.0.0",
            PartitionKey: lead.ToString(),
            Payload: JsonSerializer.Serialize(
                new LeadCreated(lead, "A Company", LeadSource.Web),
                CrmJsonContext.Default.LeadCreated));

    private static ValueTask<FlowExecutionResult<LeadCaptured>> CaptureAsync(
        CrmSchemaHarness crm,
        InMemoryFlowJournal journal,
        Guid lead,
        CancellationToken ct) =>
        Host(journal).RunAsync(
            CaptureLeadFlow.Plan,
            new CaptureLeadFlow.Dispatcher(captureNewLead: new CaptureNewLead(new IntakeStore(crm.DataSource))),
            Invocation(lead.ToString()),
            new CaptureLead("A Company", "A Person", "a.person@example.test", LeadSource.Web),
            CaptureLeadFlow.Projection,
            ct);

    private static ValueTask<FlowExecutionResult<LeadScored>> ScoreAsync(
        CrmSchemaHarness crm,
        BusMessage delivery,
        CancellationToken ct) =>
        Host(new InMemoryFlowJournal()).RunAsync(
            ScoreLeadFlow.Plan,
            new ScoreLeadFlow.Dispatcher(scoreLead: new ScoreLead(new IntakeStore(crm.DataSource))),
            Invocation(delivery.EventId.ToString(), continuation: true),
            delivery,
            ScoreLeadFlow.Projection,
            ct);

    private static ValueTask<FlowExecutionResult<LeadAssigned>> AssignAsync(
        CrmSchemaHarness crm,
        BusMessage delivery,
        CancellationToken ct) =>
        Host(new InMemoryFlowJournal()).RunAsync(
            AssignLeadFlow.Plan,
            new AssignLeadFlow.Dispatcher(assignLead: new AssignLead(new IntakeStore(crm.DataSource))),
            Invocation(delivery.EventId.ToString(), continuation: true),
            delivery,
            AssignLeadFlow.Projection,
            ct);

    private static FlowHost Host(InMemoryFlowJournal journal) =>
        new(new FlowEngine(SystemClock.Instance),
            new FlowXOptions
            {
                ApplicationName = "Crm",
                NodeName = "test-node",
                TenantIsolation = TenantIsolation.Row,
            },
            new FlowDurability(journal, new InMemoryLeaseStore()));

    /// <remarks>
    /// A bus delivery is a continuation: it carries no caller, and the tenant it acts under is
    /// attested by the platform from the broker's own field rather than resolved from claims.
    /// That is why the subscriptions' capabilities are <c>Internal</c> and this invocation
    /// names no principal.
    /// </remarks>
    private static FlowInvocation Invocation(string key, bool continuation = false) =>
        new("corr-" + key,
            key,
            CrmSchemaHarness.Northwind,
            Deadline: null,
            Principal: continuation ? null : Representative,
            IsContinuation: continuation,
            TenantAttested: continuation);

    private static ClaimsPrincipal Representative { get; } =
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "rep-northwind-1"),
                new Claim("tid", CrmTokens.NorthwindTenant),
                new Claim("scope", "crm.read crm.write"),
            ],
            CrmTokenHandler.SchemeName));

    private static async ValueTask<long> CountAsync(CrmSchemaHarness crm, Guid lead) =>
        await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind,
            "SELECT count(*) FROM lead WHERE lead_id = @id",
            Cancellation,
            ("id", lead)).ConfigureAwait(false);

    private static async ValueTask<(int Score, Guid? Owner)> ScoreAndOwnerAsync(
        CrmSchemaHarness crm,
        Guid lead)
    {
        var score = await crm.ScalarAsTenantAsync<int>(
            CrmSchemaHarness.Northwind,
            "SELECT score FROM lead WHERE lead_id = @id",
            Cancellation,
            ("id", lead)).ConfigureAwait(false);

        var owner = await crm.ScalarAsTenantAsync<Guid>(
            CrmSchemaHarness.Northwind,
            "SELECT owner_id FROM lead WHERE lead_id = @id",
            Cancellation,
            ("id", lead)).ConfigureAwait(false);

        return (score, owner == Guid.Empty ? null : owner);
    }
}
