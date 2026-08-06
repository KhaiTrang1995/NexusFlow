using System.Net.Http.Json;
using System.Text.Json;
using FlowX;
using FlowX.Generated;
using FlowX.Hosting;
using FlowX.Postgres;
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
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly IHost _host;

    private CrmApplication(IHost host, CrmSchemaHarness crm)
    {
        _host = host;
        Crm = crm;
    }

    /// <summary>The schema, for seeding rows and for reading them back.</summary>
    public CrmSchemaHarness Crm { get; }

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
        services.AddSingleton<IntakeStore>();
        services.AddSingleton<SalesStore>();
        services.AddSingleton<WorkStore>();
        services.AddSingleton<CustomSchemaStore>();
        services.AddSingleton<ConnectorStore>();
        services.AddSingleton<FieldPolicyStore>();
        services.AddSingleton<RollupStore>();
        services.AddSingleton<QueryStore>();
        services.AddSingleton<FormulaStore>();

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
    }
}
