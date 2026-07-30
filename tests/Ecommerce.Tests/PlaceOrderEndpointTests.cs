using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ecommerce;
using FlowX.Hosting;
using FlowX.Http;
using FlowX.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Ecommerce.Tests;

/// <summary>
/// The reference application, end to end, over a real server.
/// </summary>
/// <remarks>
/// <para>
/// Asserted on the wire — status codes, media types and JSON bodies — because those are
/// the contract a caller actually depends on, and none of them can be verified by
/// invoking the flow directly.
/// </para>
/// <para>
/// The plan, the dispatcher and the output projection under test are all generated. If
/// the generator regresses, this file is where it shows up as a failed HTTP response
/// rather than as a diff in a snapshot nobody reads.
/// </para>
/// </remarks>
public sealed class PlaceOrderEndpointTests
{
    private const string Route = "/api/v1/orders";

    [Fact]
    public async Task APlacedOrderReturnsTheFlowsDeclaredOutput()
    {
        using var host = await StartAsync();
        using var client = host.GetTestClient();

        var response = await PostAsync(client, "key-1", new PlaceOrder("SKU-1", 2, "tok"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");

        var body = await ReadAsync<OrderPlacedResult>(response);

        // The reservation id is the idempotency key: the flow's output is derived from
        // what the steps actually produced, not from anything the endpoint invented.
        body.OrderId.ShouldBe("key-1");
        body.ReceiptId.ShouldBe("receipt-key-1");
    }

    [Fact]
    public async Task TheWireContractIsCamelCase()
    {
        using var host = await StartAsync();
        using var client = host.GetTestClient();

        var response = await PostAsync(client, "key-2", new PlaceOrder("SKU-1", 1, "tok"));
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Pinned because the failure is silent in the other direction: a client sending
        // "quantity" against a PascalCase contract gets a zero, not an error.
        json.ShouldContain("\"orderId\"");
        json.ShouldContain("\"receiptId\"");
    }

    [Fact]
    public async Task ReplayingAnIdempotencyKeyReservesStockOnce()
    {
        var inventory = new CountingInventory(available: 10);
        using var host = await StartAsync(inventory);
        using var client = host.GetTestClient();

        await PostAsync(client, "key-replay", new PlaceOrder("SKU-1", 3, "tok"));
        var second = await PostAsync(client, "key-replay", new PlaceOrder("SKU-1", 3, "tok"));

        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Two requests, one hold. This is the whole point of requiring the header: the
        // capability passes it downstream, and the downstream system deduplicates.
        inventory.Reserved.ShouldBe(3);
    }

    [Fact]
    public async Task ARejectedOrderMapsToProblemDetails()
    {
        using var host = await StartAsync();
        using var client = host.GetTestClient();

        var response = await PostAsync(client, "key-3", new PlaceOrder("SKU-1", 0, "tok"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        using var problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        problem.RootElement.GetProperty("code").GetString().ShouldBe("order.invalid_quantity");
        problem.RootElement.GetProperty("status").GetInt32().ShouldBe(400);
    }

    [Fact]
    public async Task AConflictMapsTo409()
    {
        using var host = await StartAsync();
        using var client = host.GetTestClient();

        var response = await PostAsync(client, "key-4", new PlaceOrder("SKU-1", 9_999, "tok"));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AFailedPaymentReleasesTheReservation()
    {
        // The compensation path, which no happy-path run reaches: the reservation
        // succeeds, the capture fails, and the hold must be given back. A saga that
        // leaks inventory on a declined card is the defect this exists to catch.
        var inventory = new CountingInventory(available: 10);
        using var host = await StartAsync(inventory, new DecliningGateway());
        using var client = host.GetTestClient();

        var response = await PostAsync(client, "key-5", new PlaceOrder("SKU-1", 4, "tok"));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        inventory.Released.ShouldBe("key-5");
    }

    [Fact]
    public void TheEndpointIsGivenTheFlowsSensitiveMembers()
    {
        // The generated list is what the endpoint redacts against. The sample's own
        // capabilities do not attach the token to an error — so this asserts the wiring,
        // and FlowX.Http.Tests proves the behaviour end to end with one that does.
        PlaceOrderFlow.SensitiveMembers.ShouldBe(["PaymentToken"]);
    }

    [Fact]
    public async Task AMissingIdempotencyKeyIsRefused()
    {
        using var host = await StartAsync();
        using var client = host.GetTestClient();

        var response = await client.PostAsync(
            Route,
            JsonContent.Create(new PlaceOrder("SKU-1", 1, "tok")),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        problem.RootElement.GetProperty("code").GetString().ShouldBe("http.idempotency_key_required");
    }

    [Theory]
    [InlineData("{\"sku\":")]
    [InlineData("")]
    public async Task AnUnreadableBodyIsRefused(string body)
    {
        using var host = await StartAsync();
        using var client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        request.Headers.Add(FlowXHeaders.IdempotencyKey, "key-6");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task TheHealthProbeReportsReady()
    {
        // AddFlowX registers the probe as well as the type. Before it did, this endpoint
        // threw at startup — which is what the sample found on its first run.
        using var host = await StartAsync();
        using var client = host.GetTestClient();

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldBe("Healthy");
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string key, PlaceOrder order)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(order, EcommerceJsonContext.Default.PlaceOrder),
        };

        request.Headers.Add(FlowXHeaders.IdempotencyKey, key);

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return JsonSerializer.Deserialize<T>(json, EcommerceJsonContext.Default.Options)
            ?? throw new InvalidOperationException("The response body was null: " + json);
    }

    /// <summary>
    /// Composes the same graph the sample's <c>Program.cs</c> does, with the two
    /// infrastructure adapters swapped for observable ones.
    /// </summary>
    private static async Task<IHost> StartAsync(
        IInventoryStore? inventory = null,
        IPaymentGateway? gateway = null)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddFlowX(options => options.ApplicationName = "Ecommerce");
                    services.AddSingleton(inventory ?? new CountingInventory(available: 10));
                    services.AddSingleton(gateway ?? new ApprovingGateway());
                    services.AddSingleton<ValidateOrder>();
                    services.AddSingleton<ReserveInventory>();
                    services.AddSingleton<ReleaseInventory>();
                    services.AddSingleton<CapturePayment>();
                    services.AddSingleton<PlaceOrderFlow.Dispatcher>();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapHealthChecks("/health");
                        endpoints.MapFlow(
                            "POST",
                            Route,
                            PlaceOrderFlow.Plan,
                            services => services.GetRequiredService<PlaceOrderFlow.Dispatcher>(),
                            PlaceOrderFlow.Projection,
                            EcommerceJsonContext.Default.PlaceOrder,
                            EcommerceJsonContext.Default.OrderPlacedResult,
                            requireIdempotencyKey: true,
                            sensitiveMembers: PlaceOrderFlow.SensitiveMembers);
                    });
                }))
            .StartAsync(TestContext.Current.CancellationToken);

        return host;
    }

    private sealed class CountingInventory(int available) : IInventoryStore
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly Lock _sync = new();

        public int Reserved { get; private set; }

        public string? Released { get; private set; }

        public ValueTask<int> AvailableAsync(string sku, CancellationToken ct)
        {
            lock (_sync)
            {
                return ValueTask.FromResult(available - Reserved);
            }
        }

        public ValueTask ReserveAsync(string sku, int quantity, string idempotencyKey, CancellationToken ct)
        {
            lock (_sync)
            {
                if (_seen.Add(idempotencyKey))
                {
                    Reserved += quantity;
                }
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask ReleaseAsync(string idempotencyKey, CancellationToken ct)
        {
            lock (_sync)
            {
                Released = idempotencyKey;
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ApprovingGateway : IPaymentGateway
    {
        public ValueTask<string?> CaptureAsync(string reservationId, string idempotencyKey, CancellationToken ct)
            => ValueTask.FromResult<string?>("receipt-" + idempotencyKey);
    }

    private sealed class DecliningGateway : IPaymentGateway
    {
        public ValueTask<string?> CaptureAsync(string reservationId, string idempotencyKey, CancellationToken ct)
            => ValueTask.FromResult<string?>(null);
    }
}
