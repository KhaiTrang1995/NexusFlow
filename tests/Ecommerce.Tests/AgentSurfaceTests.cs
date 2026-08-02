using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FlowX.Generated;
using FlowX.Hosting;
using FlowX.Http;
using FlowX.Mcp;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Ecommerce.Tests;

/// <summary>
/// The sample's second transport: <c>order.place</c> reached by an agent over MCP, in the same
/// process and against the same plan the HTTP endpoint serves.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This file composes what <c>Program.cs</c> composes.</strong> Every other test project
/// here builds the graph a test needs; this one builds the graph the sample ships, including the
/// two generated subscription registrations, because the sample's own composition is what was
/// never exercised — <c>ProjectOrderFlow.Dispatcher</c> went unregistered and
/// <c>AddFlowXChangeSubscriptions</c> resolves it <em>while registering</em>, so
/// <c>dotnet run --project samples/ecommerce</c> threw during start-up and no test could see it.
/// <see cref="TheApplicationStartsAndServesBothTransports"/> is what sees it now.
/// </para>
/// <para>
/// <strong>The claim under test is that a transport is not an authorisation boundary.</strong>
/// <c>payment.capture</c> declares <c>payment.write</c> and nothing else in this application
/// mentions it. So the same token is refused at the same step whether it arrives as JSON-RPC or
/// as a POST, and the refusal is compared here rather than remembered from a document.
/// </para>
/// </remarks>
public sealed class AgentSurfaceTests
{
    private const string McpRoute = "/mcp";
    private const string OrderRoute = "/api/v1/orders";
    private const string ToolName = "order_place";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// The sample's own composition root starts, and answers on both of the routes it maps.
    /// </summary>
    [Fact]
    public async Task TheApplicationStartsAndServesBothTransports()
    {
        using var host = await StartAsync();
        using var client = host.GetTestClient();

        using var health = await client.GetAsync(new Uri("/health", UriKind.Relative), Cancellation);

        health.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var initialize = await RpcAsync(
            client, """{"jsonrpc":"2.0","id":1,"method":"initialize"}""", token: null);

        initialize.RootElement
            .GetProperty("result").GetProperty("serverInfo").GetProperty("name")
            .GetString()
            .ShouldBe("Ecommerce");
    }

    /// <summary>
    /// <c>tools/list</c> publishes the flow's <c>[AgentTrigger]</c>, projected from the manifest.
    /// </summary>
    /// <remarks>
    /// Every value asserted here is one the sample declares somewhere other than in a tool
    /// definition: the name is derived from the flow id, the description is the attribute's, the
    /// permission is <c>payment.capture</c>'s stance and the side effects are the three
    /// <c>[Capability]</c> declarations. A hand-written tool list would satisfy none of them by
    /// construction, which is the point.
    /// </remarks>
    [Fact]
    public async Task TheToolListIsAProjectionOfTheManifest()
    {
        using var host = await StartAsync();
        using var client = host.GetTestClient();

        using var listed = await RpcAsync(
            client, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", token: null);

        var tools = listed.RootElement.GetProperty("result").GetProperty("tools");

        // One tool, because order.place is the only flow carrying an [AgentTrigger]. The three
        // durable flows are reachable by a broker, a change feed and HTTP and by no agent.
        tools.GetArrayLength().ShouldBe(1);

        var tool = tools[0];

        tool.GetProperty("name").GetString().ShouldBe(ToolName);
        tool.GetProperty("description").GetString()
            .ShouldNotBeNull()
            .ShouldContain("Reserves inventory before charging");

        var annotations = tool.GetProperty("annotations");

        annotations.GetProperty("flowId").GetString().ShouldBe("order.place");
        annotations.GetProperty("requiredPermissions").EnumerateArray()
            .Select(static permission => permission.GetString())
            .ShouldContain("payment.write");

        // The marker that redacts the token out of a Problem Details body reaches the tool
        // schema too, so a model is told which argument holds a secret.
        tool.GetProperty("inputSchema").GetProperty("x-flowx-sensitive").EnumerateArray()
            .Select(static member => member.GetString())
            .ShouldContain("PaymentToken");
    }

    /// <summary>An agent holding <c>payment.write</c> places an order.</summary>
    [Fact]
    public async Task AnAuthorisedAgentPlacesAnOrder()
    {
        var inventory = new CountingInventory(available: 10);

        using var host = await StartAsync(inventory);
        using var client = host.GetTestClient();

        using var called = await RpcAsync(client, Call("SKU-1", 2), Tokens.Cashier);

        var result = called.RootElement.GetProperty("result");

        result.GetProperty("isError").GetBoolean().ShouldBeFalse();
        result.GetProperty("structuredContent").GetProperty("output")
            .GetProperty("receiptId").GetString().ShouldNotBeNullOrEmpty();

        // The stock moved, so the flow ran rather than the surface answering for it.
        inventory.Reserved.ShouldBe(2);
        inventory.Released.ShouldBe(0);
    }

    /// <summary>
    /// The stance refuses the same caller over both transports, and the saga unwinds behind
    /// both refusals.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Tokens.Shopper</c> is authenticated and holds no <c>payment.write</c>, so it passes
    /// <c>order.validate</c> and <c>inventory.reserve</c> and is refused at <c>payment.capture</c>
    /// — <em>after</em> the hold is taken. The compensation gives the stock back on both paths,
    /// which is the half of the equality that a status-code comparison would miss.
    /// </para>
    /// <para>
    /// The shapes differ and are meant to: an agent gets a <c>result</c> carrying
    /// <c>isError</c>, because the call happened and this is its answer, and an HTTP caller gets
    /// <c>403</c> and RFC 7807. The <em>code</em> is the same string, and that is what makes it
    /// one decision rather than two that agree today.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task OneStanceRefusesTheSameCallerOnBothTransports()
    {
        var overHttp = new CountingInventory(available: 10);
        var overAgent = new CountingInventory(available: 10);

        using var httpHost = await StartAsync(overHttp);
        using var agentHost = await StartAsync(overAgent);
        using var httpClient = httpHost.GetTestClient();
        using var agentClient = agentHost.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, OrderRoute)
        {
            Content = new StringContent(
                """{"sku":"SKU-1","quantity":1,"paymentToken":"tok"}""",
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.Add("Idempotency-Key", "both-1");
        request.Headers.Add("Authorization", "Bearer " + Tokens.Shopper);

        using var refusedOverHttp = await httpClient.SendAsync(request, Cancellation);

        refusedOverHttp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var problem = JsonDocument.Parse(
            await refusedOverHttp.Content.ReadAsStringAsync(Cancellation));

        var httpCode = problem.RootElement.GetProperty("code").GetString();

        using var refusedOverAgent = await RpcAsync(agentClient, Call("SKU-1", 1), Tokens.Shopper);

        var result = refusedOverAgent.RootElement.GetProperty("result");

        result.GetProperty("isError").GetBoolean().ShouldBeTrue();

        var agentCode = result
            .GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString();

        agentCode.ShouldBe("authorization.permission_denied");
        agentCode.ShouldBe(httpCode);

        // And the hold is given back on both, because a refusal is a failure like any other.
        overHttp.Reserved.ShouldBe(1);
        overHttp.Released.ShouldBe(1);
        overAgent.Reserved.ShouldBe(1);
        overAgent.Released.ShouldBe(1);
    }

    /// <summary>An anonymous agent is refused at the first step, exactly as an anonymous POST is.</summary>
    [Fact]
    public async Task AnAnonymousAgentIsRefusedBeforeAnyCapabilityRuns()
    {
        var inventory = new CountingInventory(available: 10);

        using var host = await StartAsync(inventory);
        using var client = host.GetTestClient();

        using var called = await RpcAsync(client, Call("SKU-1", 1), token: null);

        var result = called.RootElement.GetProperty("result");

        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        result.GetProperty("structuredContent").GetProperty("error").GetProperty("code")
            .GetString().ShouldBe("authorization.not_authenticated");

        inventory.Reserved.ShouldBe(0);
    }

    /// <summary>A tool the manifest does not publish is a JSON-RPC error, not a flow failure.</summary>
    [Fact]
    public async Task ATooltheManifestDoesNotPublishIsRefusedBySurface()
    {
        using var host = await StartAsync();
        using var client = host.GetTestClient();

        using var called = await RpcAsync(
            client,
            """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"order_reprice","arguments":{}}}""",
            Tokens.Cashier);

        called.RootElement.TryGetProperty("result", out _).ShouldBeFalse();
        called.RootElement.GetProperty("error").GetProperty("data").GetProperty("code")
            .GetString().ShouldBe("mcp.unknown_tool");
    }

    /// <summary>One <c>tools/call</c> message for <c>order_place</c>.</summary>
    /// <remarks>
    /// The arguments are the flow's own input contract, camelCased by the same serialiser
    /// context the HTTP endpoint uses — there is no agent-shaped DTO in this application.
    /// </remarks>
    private static string Call(string sku, int quantity) => string.Format(
        CultureInfo.InvariantCulture,
        "{{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":" +
        "{{\"name\":\"{0}\",\"arguments\":" +
        "{{\"sku\":\"{1}\",\"quantity\":{2},\"paymentToken\":\"tok\"}}}}}}",
        ToolName,
        sku,
        quantity);

    private static async Task<JsonDocument> RpcAsync(HttpClient client, string body, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, McpRoute)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Add("Authorization", "Bearer " + token);
        }

        using var response = await client.SendAsync(request, Cancellation);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));
    }

    /// <summary>
    /// The sample's composition root, with the test server in place of Kestrel and nothing else
    /// substituted but the inventory.
    /// </summary>
    /// <remarks>
    /// The four registrations after the capabilities are the ones <c>Program.cs</c> makes and no
    /// other test in this project does: both generated subscription registrations, the generated
    /// agent-tool registration, and the MCP route. Composing less than this is what let a missing
    /// dispatcher ship.
    /// </remarks>
    private static async Task<IHost> StartAsync(IInventoryStore? inventory = null) =>
        await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddFlowX(options => options.ApplicationName = "Ecommerce");

                    services
                        .AddAuthentication(DemoTokenHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, DemoTokenHandler>(
                            DemoTokenHandler.SchemeName, null);

                    services.AddSingleton(inventory ?? new CountingInventory(available: 10));

                    services.AddSingleton<IPaymentGateway>(new ApprovingGateway());

                    services.AddSingleton<ValidateOrder>();
                    services.AddSingleton<ReserveInventory>();
                    services.AddSingleton<ReleaseInventory>();
                    services.AddSingleton<CapturePayment>();
                    services.AddSingleton<RepriceBasket>();
                    services.AddSingleton<ProjectOrder>();
                    services.AddSingleton<PlaceOrderFlow.Dispatcher>();
                    services.AddSingleton<ConfirmOrderFlow.Dispatcher>();
                    services.AddSingleton<RepriceOrderFlow.Dispatcher>();
                    services.AddSingleton<ProjectOrderFlow.Dispatcher>();

                    services.AddFlowXAgentTools();
                })
                .Configure(app =>
                {
                    app.UseAuthentication();
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapHealthChecks("/health");
                        endpoints.MapFlowX();
                        endpoints.MapFlowXMcp(McpRoute);
                    });

                    app.ApplicationServices.AddFlowXSubscriptions();
                    app.ApplicationServices.AddFlowXChangeSubscriptions();
                }))
            .StartAsync(Cancellation);

    /// <summary>
    /// Stock, counting what was taken and what came back.
    /// </summary>
    /// <remarks>
    /// Its own rather than the sample's <c>InMemoryInventoryStore</c>, because what these tests
    /// assert about a refusal is that the <em>compensation ran</em>, and a store that only
    /// reports a balance cannot distinguish "released" from "never reserved".
    /// </remarks>
    private sealed class CountingInventory(int available) : IInventoryStore
    {
        private readonly Lock _sync = new();

        public int Reserved { get; private set; }

        public int Released { get; private set; }

        public ValueTask<int> AvailableAsync(string sku, CancellationToken ct) =>
            ValueTask.FromResult(available);

        public ValueTask ReserveAsync(string sku, int quantity, string idempotencyKey, CancellationToken ct)
        {
            lock (_sync)
            {
                Reserved += quantity;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask ReleaseAsync(string idempotencyKey, CancellationToken ct)
        {
            lock (_sync)
            {
                Released += 1;
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A gateway that always approves, so a refusal here is never the payment's.</summary>
    private sealed class ApprovingGateway : IPaymentGateway
    {
        public ValueTask<string?> CaptureAsync(
            string reservationId, string idempotencyKey, CancellationToken ct) =>
            ValueTask.FromResult<string?>("receipt-" + idempotencyKey);
    }
}
