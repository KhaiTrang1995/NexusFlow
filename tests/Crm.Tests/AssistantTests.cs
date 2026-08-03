using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FlowX;
using FlowX.Generated;
using FlowX.Hosting;
using FlowX.Mcp;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Crm.Tests;

/// <summary>
/// The assistant, of <c>docs/26-CRM-Sample.md</c> §10 package 11.
/// </summary>
/// <remarks>
/// <strong>The test package 11 exists for is <see cref="ACallerBothTransportsRefuseIsRefusedByBoth"/>.</strong>
/// One flow, one stance, two transports: what a model may ask about is what the person whose
/// claims it is carrying may ask about, decided in the same step loop and not by a second check
/// on the agent path.
/// </remarks>
public sealed class AssistantTests
{
    private const string Route = "/api/v1/crm/account-summaries";
    private const string McpRoute = "/mcp";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------------------- the tool

    /// <summary>
    /// One tool, and it is the read. Nothing that writes carries an <c>[AgentTrigger]</c>.
    /// </summary>
    [Fact]
    public async Task TheAgentSurfacePublishesOnlyTheFlowThatReads()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        await using var app = await StartAsync(crm);

        using var listed = await app.RpcAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", CrmTokens.Northwind);

        var tools = listed.RootElement.GetProperty("result").GetProperty("tools");

        tools.GetArrayLength().ShouldBe(
            1, "a quote, an order and a discount approval are not things a model may do here.");

        tools[0].GetProperty("name").GetString().ShouldBe(await ToolNameAsync(app));
    }

    [Fact]
    public async Task ASummaryCountsWhatTheAccountHasOpen()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        await using var app = await StartAsync(crm);

        var account = await WorldAsync(crm);

        var summary = await app.SummariseAsync(account, CrmTokens.Northwind);

        summary.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await summary.Content.ReadFromJsonAsync<AccountSummary>(
            CrmJsonContext.Default.Options, Cancellation);

        body!.OpenOpportunities.ShouldBe(1);
        body.OpenValue.ShouldBe(50_000m);
        body.Currency.ShouldBe("EUR");
        body.Lifecycle.ShouldBe(Lifecycle.Prospect);
    }

    // ---------------------------------------------------------------------- the two transports

    /// <summary>
    /// §10 package 11's "done when", and the only test that can falsify it.
    /// </summary>
    [Fact]
    public async Task ACallerBothTransportsRefuseIsRefusedByBoth()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        await using var app = await StartAsync(crm);

        var account = await WorldAsync(crm);

        // Contoso's token is a real caller with real grants, and this account is not theirs.
        var overHttp = await app.SummariseAsync(account, CrmTokens.Contoso);

        overHttp.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var overAgent = await app.CallToolAsync(account, CrmTokens.Contoso);

        Refusal(overAgent).ShouldNotBeNull().ShouldContain(
            "crm.account_not_found",
            customMessage:
                "the agent surface is the flow surface — the same stance answered the same caller.");

        // And the same account, for a caller who does have it.
        var allowed = await app.SummariseAsync(account, CrmTokens.Northwind);

        allowed.StatusCode.ShouldBe(HttpStatusCode.OK, "so the refusal above is about the caller.");

        using var allowedOverAgent = await app.CallToolAsync(account, CrmTokens.Northwind);

        Refusal(allowedOverAgent).ShouldBeNull("the tool answered: " + allowedOverAgent.RootElement);

        allowedOverAgent.RootElement
            .GetProperty("result").GetProperty("structuredContent").GetProperty("output")
            .GetProperty("openOpportunities").GetInt32()
            .ShouldBe(1, "and it answered with the same account the HTTP route just returned.");
    }

    [Fact]
    public async Task AnAnonymousCallerIsRefusedByBoth()
    {
        await using var crm = await CrmSchemaHarness.CreateAsync(Cancellation);
        await using var app = await StartAsync(crm);

        var account = await WorldAsync(crm);

        (await app.SummariseAsync(account, token: null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var overAgent = await app.CallToolAsync(account, token: null);

        Refusal(overAgent).ShouldNotBeNull(
            "an anonymous caller has no tenant to be narrowed to, over either transport.");
    }

    // ------------------------------------------------------------------------------- fixtures

    /// <summary>An account with one open opportunity worth 50 000 EUR.</summary>
    private static async Task<Guid> WorldAsync(CrmSchemaHarness crm)
    {
        var account = await crm.AccountAsync(CrmSchemaHarness.Northwind, Lifecycle.Prospect, Cancellation);
        var contact = await crm.ContactAsync(CrmSchemaHarness.Northwind, account, Cancellation);
        var (_, stage) = await crm.ProcessAsync(CrmSchemaHarness.Northwind, 1, true, Cancellation);

        await crm.OpportunityAsync(CrmSchemaHarness.Northwind, account, contact, stage, Cancellation);

        return account;
    }

    /// <summary>What a refused tool call says, or null when it was not refused.</summary>
    private static string? Refusal(JsonDocument answer)
    {
        var result = answer.RootElement.GetProperty("result");

        if (!result.TryGetProperty("isError", out var isError) || !isError.GetBoolean())
        {
            return null;
        }

        return result.GetProperty("content")[0].GetProperty("text").GetString();
    }

    private static async Task<string> ToolNameAsync(Application app)
    {
        using var listed = await app.RpcAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", CrmTokens.Northwind);

        return listed.RootElement.GetProperty("result").GetProperty("tools")[0]
            .GetProperty("name").GetString()!;
    }

    private static async Task<Application> StartAsync(CrmSchemaHarness crm)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
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

                    // The store rather than the data source, so nothing here is registered that
                    // the container would dispose out from under the harness that owns it.
                    services.AddSingleton(new AssistantStore(crm.DataSource));
                    services.AddSingleton<SummariseAccountForCaller>();
                    services.AddSingleton<SummariseAccountFlow.Dispatcher>();

                    services.AddFlowXAgentTools();
                })
                .Configure(app =>
                {
                    app.UseAuthentication();
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapFlowX();
                        endpoints.MapFlowXMcp(McpRoute);
                    });
                }))
            .StartAsync(Cancellation);

        return new Application(host);
    }

    private sealed class Application(IHost host) : IAsyncDisposable
    {
        private readonly HttpClient _client = host.GetTestClient();

        /// <summary>Asks over the HTTP route the <c>[HttpTrigger]</c> generated.</summary>
        public Task<HttpResponseMessage> SummariseAsync(Guid account, string? token)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Route)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(
                        new SummariseAccount(account), CrmJsonContext.Default.SummariseAccount),
                    Encoding.UTF8,
                    "application/json"),
            };

            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return _client.SendAsync(request, Cancellation);
        }

        /// <summary>Asks over the agent surface, as a model would.</summary>
        public async Task<JsonDocument> CallToolAsync(Guid account, string? token)
        {
            var name = await ToolNameAsync(this);

            return await RpcAsync(
                $$"""
                {"jsonrpc":"2.0","id":2,"method":"tools/call",
                 "params":{"name":"{{name}}","arguments":{"accountId":"{{account}}"} } }
                """,
                token);
        }

        /// <summary>Posts one JSON-RPC request to the MCP endpoint.</summary>
        public async Task<JsonDocument> RpcAsync(string body, string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, McpRoute)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await _client.SendAsync(request, Cancellation);

            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();

            await host.StopAsync(Cancellation);

            host.Dispose();
        }
    }
}
