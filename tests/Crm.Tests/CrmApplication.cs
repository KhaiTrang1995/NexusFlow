using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowX;
using FlowX.Generated;
using FlowX.Conformance.InMemory;
using FlowX.Hosting;
using FlowX.Postgres;
using FlowX.Runtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// The sample served over HTTP, against a real PostgreSQL schema of its own.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this exists to reach that the rest of the project cannot.</strong> Nine of the
/// sample's flows carry an <c>[HttpTrigger]</c>; before this, two of them had ever been reached
/// over HTTP. The other tests invoke a capability directly, which is a good way to assert what
/// the SQL does and no way at all to assert the things that only exist on the wire: that the
/// generated route is the one the attribute names, that the tenant reaching the database is the
/// one the token's <c>tid</c> resolved to, that a permission on a step refuses a caller who
/// holds the wrong scope, and that a refusal comes back as <c>application/problem+json</c> with
/// a status a client can branch on.
/// </para>
/// <para>
/// <strong>And the row is read afterwards, on a connection the application does not share.</strong>
/// A 200 with a plausible body is not evidence that anything was written — an endpoint that
/// echoed its input would produce one. Every write asserted here is asserted twice: once as the
/// response a caller sees, and once as the rows a second connection finds, under the tenant
/// scope the policies of migration <c>0002</c> impose.
/// </para>
/// <para>
/// <strong>Composed by hand, and that is deliberate.</strong> <c>AddFlowXCapabilities()</c>
/// would also declare the three bus subscriptions and the two schedules, and
/// <see cref="FlowXStartupValidation"/> would then refuse to start a host that serves none of
/// them — correctly, because a declared trigger nothing registers never runs. Registering a
/// broker and a scheduler to test an HTTP endpoint would make every assertion here depend on
/// two transports it does not use. The capabilities the routes under test need are named below;
/// <c>samples/crm/Program.cs</c> is where the full composition lives and
/// <c>FlowX.Hosting.Tests</c> is where it is asserted.
/// </para>
/// <para>
/// <strong>No broker, so a staged event stays staged.</strong> Nothing drains the outbox here,
/// which is what makes <c>lead.created</c> readable as a row: the claim
/// <c>IntakeFlows</c> makes is that the event commits with the write, and an outbox a pump had
/// already emptied could not distinguish that from an event published afterwards.
/// </para>
/// </remarks>
internal sealed class CrmApplication : IAsyncDisposable
{
    /// <summary>
    /// How this test client reads and writes JSON.
    /// </summary>
    /// <remarks>
    /// <strong>The converter is here because the server has one.</strong> A test client is a
    /// client, and one whose options quietly differ from the server's is a client that agrees with
    /// nothing a real caller would see — it would keep passing while every browser in the world
    /// got an enum it could not parse.
    /// </remarks>
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IHost _host;

    private CrmApplication(IHost host, CrmSchemaHarness crm)
    {
        _host = host;
        Crm = crm;
    }

    /// <summary>The schema, for seeding rows and for reading them back.</summary>
    public CrmSchemaHarness Crm { get; }

    /// <summary>The service desk's store.</summary>
    /// <remarks>
    /// Reached directly by one test, which drives two writes at once. The HTTP surface cannot
    /// reproduce that race reliably — a durable flow's own overhead is long enough that the first
    /// request finishes before the second reads — and the defences under test are the store's.
    /// </remarks>
    public ServiceStore Service => _host.Services.GetRequiredService<ServiceStore>();

    /// <summary>What a connector delivery actually reached, and what the far end was told to say.</summary>
    public RecordingConnectorTransport Transport =>
        _host.Services.GetRequiredService<RecordingConnectorTransport>();

    /// <summary>Runs one sweep of the outbound queue, as the schedule would.</summary>
    /// <param name="tenantId">The tenant the occurrence fires for.</param>
    /// <returns>How many were delivered and how many failed.</returns>
    /// <remarks>
    /// The capability rather than the flow, because what a cron trigger does with an occurrence
    /// is the platform's contract and <c>FlowX.Hosting.Tests</c> holds it; what this sample owes
    /// is what one sweep does to the rows.
    /// </remarks>
    public ValueTask<(int Delivered, int Failed)> SweepAsync(string tenantId) =>
        _host.Services.GetRequiredService<ConnectorStore>().SweepAsync(
            tenantId,
            Transport,
            SweepConnectorDeliveries.Batch,
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);

    /// <summary>Runs one pass of the bulk-job sweep, as the schedule would.</summary>
    /// <param name="tenantId">The tenant the occurrence fires for.</param>
    /// <returns>What the pass did.</returns>
    /// <remarks>
    /// The whole flow rather than the capability alone, because a chunk that advances a job and a
    /// chunk that crashes have to leave the same row readable — and the projection is where a
    /// sweep's answer becomes something a test can assert on.
    /// </remarks>
    public ValueTask<FlowExecutionResult<JobsSwept>> SweepJobsAsync(string tenantId)
    {
        var host = new FlowHost(
            new FlowEngine(FlowX.Runtime.SystemClock.Instance),
            new FlowXOptions
            {
                ApplicationName = "Crm",
                NodeName = "test-node",
                TenantIsolation = TenantIsolation.Row,
            },
            new FlowDurability(new InMemoryFlowJournal(), new InMemoryLeaseStore()));

        return host.RunAsync(
            SweepJobsFlow.Plan,
            new SweepJobsFlow.Dispatcher(
                sweepBulkJobs: _host.Services.GetRequiredService<SweepBulkJobs>()),
            new FlowInvocation(
                "corr-" + Guid.NewGuid(),
                tenantId,
                tenantId,
                Deadline: null,
                Principal: null,
                IsContinuation: true,
                TenantAttested: true),
            new ScheduledFire(DateTimeOffset.UtcNow, "* * * * *", "UTC"),
            SweepJobsFlow.Projection,
            TestContext.Current.CancellationToken);
    }

    /// <summary>Stands the schema up, migrates it, and starts the sample over a test server.</summary>
    /// <param name="cancellationToken">Cancels the setup.</param>
    /// <returns>The running application.</returns>
    public static async ValueTask<CrmApplication> StartAsync(CancellationToken cancellationToken)
    {
        var crm = await CrmSchemaHarness.CreateAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var host = Build(crm);

            // Before StartAsync, in the order Program.cs uses. The platform's tables are what a
            // Durable flow journals into and what `.Emit<T>()` stages into; every flow reached
            // here is Durable, so a host started without them fails on the first request with a
            // relation that does not exist.
            await host.Services.GetRequiredService<PostgresMigrator>()
                .MigrateAsync(cancellationToken)
                .ConfigureAwait(false);

            await host.StartAsync(cancellationToken).ConfigureAwait(false);

            return new CrmApplication(host, crm);
        }
        catch
        {
            await crm.DisposeAsync().ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>Sends a request to a generated route.</summary>
    /// <param name="route">The path, exactly as the <c>[HttpTrigger]</c> names it.</param>
    /// <param name="body">What to send, serialised with web defaults.</param>
    /// <param name="token">A bearer token, or null to call anonymously.</param>
    /// <param name="idempotencyKey">
    /// The <c>Idempotency-Key</c> to send. Defaults to a fresh one, because eight of the
    /// sample's nine routes declare <c>Idempotent = true</c> and the endpoint answers
    /// <c>400</c> without it. Pass null to send none, or a fixed value to replay a request.
    /// </param>
    /// <returns>The response, unread.</returns>
    /// <remarks>
    /// The route is passed in rather than derived, so a test that names the wrong path fails
    /// with a 404 instead of quietly asserting against whatever the harness guessed.
    /// </remarks>
    public Task<HttpResponseMessage> PostAsync(
        string route,
        object body,
        string? token,
        string? idempotencyKey = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(body, body.GetType(), options: Web),
        };

        if (token is not null)
        {
            request.Headers.Add("Authorization", "Bearer " + token);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add(
                "Idempotency-Key",
                idempotencyKey.Length == 0 ? Guid.NewGuid().ToString("n") : idempotencyKey);
        }

        return _host.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Posts a body this test wrote by hand.</summary>
    /// <param name="route">Where.</param>
    /// <param name="json">The body, verbatim.</param>
    /// <param name="token">Who.</param>
    /// <returns>What came back.</returns>
    /// <remarks>
    /// <strong>The only way to say anything about the wire format.</strong> <see cref="PostAsync"/>
    /// serialises a C# record with the same options the server deserialises it with, so both ends
    /// move together and no assertion made through it can notice that the JSON changed shape. A
    /// client written in another language is represented only here.
    /// </remarks>
    public Task<HttpResponseMessage> PostRawAsync(string route, string json, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        request.Headers.Add("Authorization", "Bearer " + token);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("n"));

        return _host.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Reads a response body as the contract the endpoint returns.</summary>
    /// <typeparam name="T">The contract.</typeparam>
    /// <param name="response">What came back.</param>
    /// <returns>The deserialised body.</returns>
    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var body = await response.Content
            .ReadFromJsonAsync<T>(Web, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

        return body!;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _host.StopAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            _host.Dispose();
        }
        finally
        {
            // Last, because the container's own data source is disposed with the host and the
            // schema this drops is the one it was pointing at.
            await Crm.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static IHost Build(CrmSchemaHarness crm) =>
        new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services => Compose(services, crm))
                .Configure(app =>
                {
                    app.UseAuthentication();
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapFlowX());
                }))
            .Build();

    private static void Compose(IServiceCollection services, CrmSchemaHarness crm)
    {
        services.AddRouting();

        services.AddFlowX(options =>
        {
            options.ApplicationName = "Crm";
            options.TenantIsolation = TenantIsolation.Row;
            options.Tenants.Add(CrmTokens.NorthwindTenant);
            options.Tenants.Add(CrmTokens.ContosoTenant);
        });

        services
            .AddAuthentication(CrmTokenHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, CrmTokenHandler>(
                CrmTokenHandler.SchemeName, null);

        // The application's own pool, pinned to the schema the harness created. A second pool
        // rather than the harness's, because the container disposes what it builds and the
        // harness needs its own to survive that and drop the schema.
        services.AddFlowXPostgres(
            CrmDatabase.ConnectionString!,
            new PostgresJournalOptions { Schema = crm.Schema });

        // Staging only. AddFlowXPostgresChangeFeed and CrmOutboxPump are the two things that
        // would drain what these tests read.
        services.AddFlowXPostgresOutbox();

        services.AddSingleton<CrmSchemaReader>();
        services.AddSingleton<ConversionStore>();
        services.AddSingleton<IntakeStore>();
        services.AddSingleton<SalesStore>();
        services.AddSingleton<WorkStore>();
        services.AddSingleton<CustomSchemaStore>();
        services.AddSingleton<ConnectorStore>();
        services.AddSingleton<FieldPolicyStore>();
        services.AddSingleton<RollupStore>();
        services.AddSingleton<QueryStore>();
        services.AddSingleton<FormulaStore>();
        services.AddSingleton<SyncStore>();
        services.AddSingleton<BulkJobStore>();
        services.AddSingleton<ReportStore>();
        services.AddSingleton<LabelStore>();
        services.AddSingleton<PlanningStore>();
        services.AddSingleton<ManagementStore>();
        services.AddSingleton<PerformanceStore>();
        services.AddSingleton<TerritoryStore>();
        services.AddSingleton<ApprovalStore>();
        services.AddSingleton<ApproverResolver>();
        services.AddSingleton<ServiceStore>();
        services.AddSingleton<CampaignStore>();
        services.AddSingleton<EntityQueryStore>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<PlanDetailStore>();
        services.AddSingleton<ProcessViewStore>();

        // The far end, recorded rather than reached. Every other claim in these tests is checked
        // against a real PostgreSQL; a connector's far end is a network somebody else owns, and a
        // test that needed one would fail when their gateway is slow.
        services.AddSingleton<RecordingConnectorTransport>();
        services.AddSingleton<IConnectorTransport>(
            static provider => provider.GetRequiredService<RecordingConnectorTransport>());

        // The capabilities behind the five routes exercised here, and their dispatchers. Named
        // one by one so that a route whose capability nobody registered fails as a missing
        // service rather than as a test somebody forgot to write.
        services.AddSingleton<CaptureNewLead>();
        services.AddSingleton<IssueQuoteForOpportunity>();
        services.AddSingleton<ApproveQuoteDiscountCapability>();
        services.AddSingleton<PlaceOrderForQuote>();
        services.AddSingleton<ApplyOpportunityTrigger>();
        services.AddSingleton<CreateTaskForSubject>();
        services.AddSingleton<DefineCustomObject>();
        services.AddSingleton<DefineCustomField>();
        services.AddSingleton<DefineCustomRelationship>();
        services.AddSingleton<CreateCustomRecord>();
        services.AddSingleton<LinkCustomRecords>();
        services.AddSingleton<SetEntityCustomFields>();
        services.AddSingleton<DefineCrmConnector>();
        services.AddSingleton<SetCrmConnectorEnabled>();
        services.AddSingleton<PublishToCrmConnector>();
        services.AddSingleton<SweepConnectorDeliveries>();
        services.AddSingleton<DefineCustomValidationRule>();
        services.AddSingleton<DefineCustomRollup>();
        services.AddSingleton<DefineCrmListView>();
        services.AddSingleton<QueryCustomRecords>();
        services.AddSingleton<SearchCrm>();
        services.AddSingleton<DefineCustomFormula>();
        services.AddSingleton<DescribeCrmSchema>();
        services.AddSingleton<ReadRecordChanges>();
        services.AddSingleton<DeleteCustomRecord>();
        services.AddSingleton<SubmitBulkJob>();
        services.AddSingleton<ReadBulkJob>();
        services.AddSingleton<SweepBulkJobs>();
        services.AddSingleton<DefineCrmReport>();
        services.AddSingleton<RunCrmReport>();
        services.AddSingleton<DefineCrmDashboard>();
        services.AddSingleton<RunCrmDashboard>();
        services.AddSingleton<SetCrmLabel>();
        services.AddSingleton<DefinePlanPeriod>();
        services.AddSingleton<SetSalesStrategy>();
        services.AddSingleton<DefineCrmPlan>();
        services.AddSingleton<RecordQualification>();
        services.AddSingleton<SetCrmPlanStep>();
        services.AddSingleton<ReadPeriodRollUp>();
        services.AddSingleton<PlaceOrgMember>();
        services.AddSingleton<SetPlanObjective>();
        services.AddSingleton<SetPlanStakeholder>();
        services.AddSingleton<SetPlanRisk>();
        services.AddSingleton<DefineCrmKpi>();
        services.AddSingleton<ReadCrmScorecard>();
        services.AddSingleton<RecordKpiReview>();
        services.AddSingleton<ReadCrmPlanTree>();
        services.AddSingleton<ReadCrmSalesPerformance>();
        services.AddSingleton<ReadCrmDealPerformance>();
        services.AddSingleton<ReadExecutiveBoard>();
        services.AddSingleton<DefineCrmTerritory>();
        services.AddSingleton<RouteToTerritory>();
        services.AddSingleton<ReadTerritoryCoverage>();
        services.AddSingleton<SetCrmQuota>();
        services.AddSingleton<ReadCrmQuotaAttainment>();
        services.AddSingleton<DefineCrmApprovalProcess>();
        services.AddSingleton<SubmitCrmApproval>();
        services.AddSingleton<DecideCrmApproval>();
        services.AddSingleton<ReadCrmApprovalInbox>();
        services.AddSingleton<SetCrmBusinessHours>();
        services.AddSingleton<DefineCrmSlaPolicy>();
        services.AddSingleton<OpenCrmCase>();
        services.AddSingleton<CommentOnCrmCase>();
        services.AddSingleton<ReadCrmCaseWorklist>();
        services.AddSingleton<DefineCrmCampaign>();
        services.AddSingleton<RecordCrmCampaignTouch>();
        services.AddSingleton<RecordCrmCampaignCost>();
        services.AddSingleton<ReadCrmCampaignPerformance>();
        services.AddSingleton<ReadCrmDealAttribution>();
        services.AddSingleton<ReadCrmEntityPage>();
        services.AddSingleton<ReadCrmConfig>();
        services.AddSingleton<ReadCrmPlan>();
        services.AddSingleton<ReadCrmProcess>();

        // The conversion saga and the eight capabilities it unwinds through. Registered here
        // because the endpoint is one a seller reaches every day, and a harness that could not
        // reach it left the whole path to a test that composes its own host.
        services.AddSingleton<CreateAccount>();
        services.AddSingleton<CreateContact>();
        services.AddSingleton<CreateOpportunity>();
        services.AddSingleton<MarkLeadConverted>();
        services.AddSingleton<ReadLeadForConversion>();
        services.AddSingleton<RemoveAccount>();
        services.AddSingleton<RemoveContact>();
        services.AddSingleton<RemoveOpportunity>();
        services.AddSingleton<ConvertLeadFlow.Dispatcher>();

        services.AddSingleton<CaptureLeadFlow.Dispatcher>();
        services.AddSingleton<IssueQuoteFlow.Dispatcher>();
        services.AddSingleton<ApproveDiscountFlow.Dispatcher>();
        services.AddSingleton<PlaceOrderFlow.Dispatcher>();
        services.AddSingleton<AdvanceOpportunityFlow.Dispatcher>();
        services.AddSingleton<CreateTaskFlow.Dispatcher>();
        services.AddSingleton<DefineObjectFlow.Dispatcher>();
        services.AddSingleton<DefineFieldFlow.Dispatcher>();
        services.AddSingleton<DefineRelationshipFlow.Dispatcher>();
        services.AddSingleton<CreateRecordFlow.Dispatcher>();
        services.AddSingleton<LinkRecordsFlow.Dispatcher>();
        services.AddSingleton<SetCustomFieldsFlow.Dispatcher>();
        services.AddSingleton<DefineConnectorFlow.Dispatcher>();
        services.AddSingleton<SetConnectorEnabledFlow.Dispatcher>();
        services.AddSingleton<PublishToConnectorFlow.Dispatcher>();
        services.AddSingleton<DefineValidationRuleFlow.Dispatcher>();
        services.AddSingleton<DefineRollupFlow.Dispatcher>();
        services.AddSingleton<DefineListViewFlow.Dispatcher>();
        services.AddSingleton<QueryRecordsFlow.Dispatcher>();
        services.AddSingleton<SearchFlow.Dispatcher>();
        services.AddSingleton<DefineFormulaFlow.Dispatcher>();
        services.AddSingleton<DescribeSchemaFlow.Dispatcher>();
        services.AddSingleton<SyncChangesFlow.Dispatcher>();
        services.AddSingleton<DeleteRecordFlow.Dispatcher>();
        services.AddSingleton<SubmitImportFlow.Dispatcher>();
        services.AddSingleton<SubmitExportFlow.Dispatcher>();
        services.AddSingleton<ReadJobFlow.Dispatcher>();
        services.AddSingleton<SweepJobsFlow.Dispatcher>();
        services.AddSingleton<DefineReportFlow.Dispatcher>();
        services.AddSingleton<RunReportFlow.Dispatcher>();
        services.AddSingleton<DefineDashboardFlow.Dispatcher>();
        services.AddSingleton<RunDashboardFlow.Dispatcher>();
        services.AddSingleton<SetLabelFlow.Dispatcher>();
        services.AddSingleton<DefinePeriodFlow.Dispatcher>();
        services.AddSingleton<SetStrategyFlow.Dispatcher>();
        services.AddSingleton<DefinePlanFlow.Dispatcher>();
        services.AddSingleton<AnswerQualificationFlow.Dispatcher>();
        services.AddSingleton<SetPlanStepFlow.Dispatcher>();
        services.AddSingleton<RollUpFlow.Dispatcher>();
        services.AddSingleton<SetOrgMemberFlow.Dispatcher>();
        services.AddSingleton<SetObjectiveFlow.Dispatcher>();
        services.AddSingleton<SetStakeholderFlow.Dispatcher>();
        services.AddSingleton<SetRiskFlow.Dispatcher>();
        services.AddSingleton<DefineKpiFlow.Dispatcher>();
        services.AddSingleton<ScorecardFlow.Dispatcher>();
        services.AddSingleton<ReviewKpiFlow.Dispatcher>();
        services.AddSingleton<PlanTreeFlow.Dispatcher>();
        services.AddSingleton<SalesPerformanceFlow.Dispatcher>();
        services.AddSingleton<DealPerformanceFlow.Dispatcher>();
        services.AddSingleton<BoardFlow.Dispatcher>();
        services.AddSingleton<DefineTerritoryFlow.Dispatcher>();
        services.AddSingleton<RouteFlow.Dispatcher>();
        services.AddSingleton<CoverageFlow.Dispatcher>();
        services.AddSingleton<SetQuotaFlow.Dispatcher>();
        services.AddSingleton<QuotaAttainmentFlow.Dispatcher>();
        services.AddSingleton<DefineApprovalProcessFlow.Dispatcher>();
        services.AddSingleton<SubmitApprovalFlow.Dispatcher>();
        services.AddSingleton<DecideApprovalFlow.Dispatcher>();
        services.AddSingleton<ApprovalInboxFlow.Dispatcher>();
        services.AddSingleton<SetBusinessHoursFlow.Dispatcher>();
        services.AddSingleton<DefineSlaPolicyFlow.Dispatcher>();
        services.AddSingleton<OpenCaseFlow.Dispatcher>();
        services.AddSingleton<CommentOnCaseFlow.Dispatcher>();
        services.AddSingleton<CaseWorklistFlow.Dispatcher>();
        services.AddSingleton<DefineCampaignFlow.Dispatcher>();
        services.AddSingleton<RecordTouchFlow.Dispatcher>();
        services.AddSingleton<RecordCampaignCostFlow.Dispatcher>();
        services.AddSingleton<CampaignPerformanceFlow.Dispatcher>();
        services.AddSingleton<DealAttributionFlow.Dispatcher>();
        services.AddSingleton<EntityPageFlow.Dispatcher>();
        services.AddSingleton<ConfigListFlow.Dispatcher>();
        services.AddSingleton<PlanDetailFlow.Dispatcher>();
        services.AddSingleton<ProcessViewFlow.Dispatcher>();
    }
}
