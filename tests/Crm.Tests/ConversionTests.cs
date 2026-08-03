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
/// The lead conversion saga of <c>docs/26-CRM-Sample.md</c> §8.1, against a real PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The CRM tables are real and the journal is in memory, and that split is the point.</strong>
/// What §8.1 claims is about rows in three tables — that a failure at the third write leaves
/// none of them — so those must be real, policies and constraints and all. The journal is the
/// engine's own bookkeeping, held to its contract by <c>JournalConformance</c> against two
/// implementations, and running a second copy of PostgreSQL's here would test that suite again
/// rather than this saga.
/// </para>
/// <para>
/// <strong>The failure is a real database refusal, not an injected one.</strong> A stubbed
/// capability that returns a failure would prove the engine unwinds when a step says so — which
/// the engine's own tests already prove. An amount that overflows <c>numeric(19,4)</c> makes
/// PostgreSQL refuse the third insert after the first two have committed, which is the shape
/// §8.1 actually describes.
/// </para>
/// </remarks>
public sealed class ConversionTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>More than <c>numeric(19,4)</c> can hold: fifteen digits is the limit.</summary>
    private const decimal Overflowing = 10_000_000_000_000_000m;

    [Fact]
    public async Task AConversionWritesAnAccountAContactAndAnOpportunity()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var lead = await crm.LeadAsync(CrmSchemaHarness.Northwind, Cancellation);
        var (_, stage) = await crm.ProcessAsync(CrmSchemaHarness.Northwind, 1, true, Cancellation);

        var run = await RunAsync(crm, Request(lead, stage), Cancellation);

        run.IsSuccess.ShouldBeTrue(Because(run));
        run.Value!.Account.ShouldBe(ConversionIds.Account(lead));
        run.Value.Contact.ShouldBe(ConversionIds.Contact(lead));
        run.Value.Opportunity.ShouldBe(ConversionIds.Opportunity(lead));

        (await CountAsync(crm, "account", run.Value.Account)).ShouldBe(1L);
        (await CountAsync(crm, "contact", run.Value.Contact)).ShouldBe(1L);
        (await CountAsync(crm, "opportunity", run.Value.Opportunity)).ShouldBe(1L);

        var status = await crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Northwind,
            "SELECT status FROM lead WHERE lead_id = @id",
            Cancellation,
            ("id", lead));

        status.ShouldBe("Converted");
    }

    /// <summary>
    /// §8.1's claim, and the reason this flow is a saga rather than four steps.
    /// </summary>
    [Fact]
    public async Task AFailureAtTheOpportunityLeavesNoAccountAndNoContact()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var lead = await crm.LeadAsync(CrmSchemaHarness.Northwind, Cancellation);
        var (_, stage) = await crm.ProcessAsync(CrmSchemaHarness.Northwind, 1, true, Cancellation);

        var run = await RunAsync(crm, Request(lead, stage) with { Amount = Overflowing }, Cancellation);

        run.IsSuccess.ShouldBeFalse("an amount PostgreSQL cannot store must not convert a lead.");

        (await CountAsync(crm, "account", ConversionIds.Account(lead)))
            .ShouldBe(0L, "the account was written before the opportunity failed, and RemoveAccount undoes it.");
        (await CountAsync(crm, "contact", ConversionIds.Contact(lead)))
            .ShouldBe(0L, "the contact was written before the opportunity failed, and RemoveContact undoes it.");
        (await CountAsync(crm, "opportunity", ConversionIds.Opportunity(lead)))
            .ShouldBe(0L);

        var status = await crm.ScalarAsTenantAsync<string>(
            CrmSchemaHarness.Northwind,
            "SELECT status FROM lead WHERE lead_id = @id",
            Cancellation,
            ("id", lead));

        status.ShouldBe("New", "the lead is only marked converted by the step after the opportunity.");
    }

    /// <summary>
    /// The undos run in strict reverse order, read off the journal rather than inferred.
    /// </summary>
    [Fact]
    public async Task TheUndosRunInStrictReverseOrder()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var lead = await crm.LeadAsync(CrmSchemaHarness.Northwind, Cancellation);
        var (_, stage) = await crm.ProcessAsync(CrmSchemaHarness.Northwind, 1, true, Cancellation);

        var journal = new InMemoryFlowJournal();

        await RunAsync(crm, Request(lead, stage) with { Amount = Overflowing }, Cancellation, journal);

        var instance = journal.Instances.ShouldHaveSingleItem();
        var frontier = await journal.ReadResumeFrontierAsync(instance.InstanceId, Cancellation);

        var compensations = frontier.Value!.Committed
            .Where(static step => step.Outcome == JournalOutcome.Compensated)
            .Select(static step => step.CapabilityId)
            .ToList();

        compensations.ShouldBe(["crm.contact.remove", "crm.account.remove"]);
    }

    /// <summary>
    /// The ids a conversion writes are a pure function of the lead, which is what lets a
    /// compensation name a row it never saw created.
    /// </summary>
    [Fact]
    public void TheThreeIdsAreDerivedFromTheLeadAndFromNothingElse()
    {
        var lead = Guid.Parse("2f1c9d4e-6b3a-4f1d-9c2e-7a5b8d0e4f31");

        ConversionIds.Account(lead).ShouldBe(ConversionIds.Account(lead));
        ConversionIds.Contact(lead).ShouldBe(ConversionIds.Contact(lead));
        ConversionIds.Opportunity(lead).ShouldBe(ConversionIds.Opportunity(lead));

        new[] { ConversionIds.Account(lead), ConversionIds.Contact(lead), ConversionIds.Opportunity(lead) }
            .Distinct()
            .Count()
            .ShouldBe(3, "three roles over one lead must not collide.");

        ConversionIds.Account(lead).ShouldNotBe(ConversionIds.Account(Guid.NewGuid()));
    }

    /// <summary>A lead already converted is refused, and nothing is written twice.</summary>
    [Fact]
    public async Task AlreadyConvertedIsRefused()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);

        var lead = await crm.LeadAsync(CrmSchemaHarness.Northwind, Cancellation);
        var (_, stage) = await crm.ProcessAsync(CrmSchemaHarness.Northwind, 1, true, Cancellation);

        (await RunAsync(crm, Request(lead, stage), Cancellation)).IsSuccess.ShouldBeTrue();

        var second = await RunAsync(crm, Request(lead, stage), Cancellation);

        second.IsSuccess.ShouldBeFalse();
        second.Error!.Code.ShouldBe("crm.lead_already_converted");

        (await CountAsync(crm, "account", ConversionIds.Account(lead))).ShouldBe(1L);
    }

    private static ConvertLead Request(Guid lead, Guid stage) =>
        new(lead,
            Industry: "Software",
            Region: "EU-WEST",
            Owner: Guid.Parse("7d2b1a6c-0e4f-4a39-8c5d-1b2e3f4a5b6c"),
            OpportunityName: "Platform licence",
            Amount: 42_000m,
            Currency: "EUR",
            Stage: stage,
            ExpectedClose: new DateOnly(2026, 12, 31));

    private static async ValueTask<long> CountAsync(CrmSchemaHarness crm, string table, Guid id) =>
        await crm.ScalarAsTenantAsync<long>(
            CrmSchemaHarness.Northwind,
            table switch
            {
                "account" => "SELECT count(*) FROM account WHERE account_id = @id",
                "contact" => "SELECT count(*) FROM contact WHERE contact_id = @id",
                _ => "SELECT count(*) FROM opportunity WHERE opportunity_id = @id",
            },
            Cancellation,
            ("id", id)).ConfigureAwait(false);

    private static ValueTask<FlowExecutionResult<ConversionResult>> RunAsync(
        CrmSchemaHarness crm,
        ConvertLead input,
        CancellationToken ct,
        InMemoryFlowJournal? journal = null)
    {
        var store = new ConversionStore(crm.DataSource);

        var host = new FlowHost(
            new FlowEngine(SystemClock.Instance),
            new FlowXOptions
            {
                ApplicationName = "Crm",
                NodeName = "test-node",
                TenantIsolation = TenantIsolation.Row,
            },
            new FlowDurability(journal ?? new InMemoryFlowJournal(), new InMemoryLeaseStore()));

        var dispatcher = new ConvertLeadFlow.Dispatcher(
            createAccount: new CreateAccount(store),
            createContact: new CreateContact(store),
            createOpportunity: new CreateOpportunity(store),
            markLeadConverted: new MarkLeadConverted(store),
            readLeadForConversion: new ReadLeadForConversion(store),
            removeAccount: new RemoveAccount(store),
            removeContact: new RemoveContact(store),
            removeOpportunity: new RemoveOpportunity(store));

        return host.RunAsync(
            ConvertLeadFlow.Plan,
            dispatcher,
            new FlowInvocation(
                "corr-" + input.LeadId,
                input.LeadId.ToString(),
                CrmSchemaHarness.Northwind,
                Deadline: null,
                Principal: Representative),
            input,
            ConvertLeadFlow.Projection,
            ct);
    }

    /// <summary>
    /// The caller a conversion runs as: Northwind's representative, holding no special grant.
    /// </summary>
    /// <remarks>
    /// <strong>The <c>tid</c> claim is not decoration.</strong> The tenant on the invocation is
    /// checked against what the claims support, so a principal without it is refused with
    /// <c>tenant.cross_tenant_denied</c> — which is the guard doing its job, and is what this
    /// harness ran into before the claim was here. It is the same claim
    /// <see cref="CrmTokenHandler"/> mints for the same token.
    /// </remarks>
    private static ClaimsPrincipal Representative { get; } =
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "rep-northwind-1"),
                new Claim("tid", CrmTokens.NorthwindTenant),
                new Claim("scope", "crm.read crm.write"),
            ],
            CrmTokenHandler.SchemeName));

    private static string Because<T>(FlowExecutionResult<T> run) =>
        run.Error is null ? "the run failed with no error" : run.Error.Code + ": " + run.Error.Message;
}
