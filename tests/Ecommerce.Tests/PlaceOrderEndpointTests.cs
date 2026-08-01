using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ecommerce;
using FlowX.Hosting;
using FlowX.Http;
using FlowX.Runtime;
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

    // ------------------------------------------------------- authorisation, over the wire

    /// <summary>
    /// An anonymous caller is refused at the first step, and no capability runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The whole of the defect P4's authorisation work fixed, asserted where a
    /// customer would meet it.</strong> Before it, this request placed an order:
    /// <c>order.validate</c> declared <c>Authorization.Authenticated</c>, that declaration
    /// reached <c>flowx.manifest.json</c> and <c>flowx diff</c>'s <c>FLOWX-DIFF-015</c>, and
    /// no code anywhere in <c>src/FlowX.Runtime</c> or <c>src/FlowX.Hosting</c> mentioned
    /// authorisation at all.
    /// </para>
    /// <para>
    /// <c>403</c> rather than <c>401</c>, and that is a deliberate loss of precision recorded
    /// in ADR-0029: <c>ErrorCategory</c> is a closed set with no authentication member, and
    /// the engine is transport-agnostic so it has no <c>WWW-Authenticate</c> challenge to
    /// name. The <em>code</em> still distinguishes the two, which is what an operator needs.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnAnonymousCallerIsRefusedBeforeAnyCapabilityRuns()
    {
        var inventory = new CountingInventory(available: 10);

        using var host = await StartAsync(inventory);
        using var client = host.GetTestClient();

        var response = await PostAsync(
            client, "anon-1", new PlaceOrder("SKU-1", 1, "tok"), Tokens.Anonymous);

        response.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden,
            "order.validate is the first step and declares Authorization.Authenticated.");

        var problem = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        problem.ShouldContain("authorization.not_authenticated");
        problem.ShouldContain("order.validate", Case.Sensitive);

        inventory.Reserved.ShouldBe(
            0,
            "The flow stopped at step 0, so nothing was held. A check that ran after the " +
            "dispatch would have authorised nothing.");
    }

    /// <summary>
    /// An authenticated caller without the permission is refused at the step that needs it —
    /// and the hold taken before it is released.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Authenticated is not authorised, and this is the test that says so.</strong>
    /// The shopper holds <c>orders.read</c> and <c>orders.write</c>, passes
    /// <c>order.validate</c> and <c>inventory.reserve</c>, and is refused by
    /// <c>payment.capture</c>, which names <c>payment.write</c>. A control that only ever
    /// distinguished "signed in" from "not signed in" would pass this request through and
    /// take the money.
    /// </para>
    /// <para>
    /// <strong>And the saga unwinds behind the refusal.</strong> That is the half that makes
    /// ADR-0029's "a refusal is a business outcome" concrete rather than a slogan: the
    /// refusal is an <c>Error</c> on the result, so the engine's failure path runs exactly as
    /// it does for a declined payment, and the reservation this caller did legitimately take
    /// is given back. An exception thrown out of the step loop would have skipped it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnAuthenticatedCallerWithoutThePermissionIsRefusedAndTheHoldIsReleased()
    {
        var inventory = new CountingInventory(available: 10);

        using var host = await StartAsync(inventory);
        using var client = host.GetTestClient();

        var response = await PostAsync(
            client, "shopper-1", new PlaceOrder("SKU-1", 3, "tok"), Tokens.Shopper);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var problem = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        problem.ShouldContain("authorization.permission_denied");
        problem.ShouldContain("payment.write", Case.Sensitive);

        inventory.Reserved.ShouldBe(3, "The caller was entitled to the two steps before the one that refused.");

        inventory.Released.ShouldBe(
            ["shopper-1"],
            "A refusal is a failure like any other, so the compensation runs and the stock " +
            "comes back. Leaving the hold standing would make an authorisation refusal the " +
            "one failure that leaks inventory.");
    }

    /// <summary>The caller holding the permission reaches the end.</summary>
    /// <remarks>
    /// The positive control, and not optional. Every assertion above is of the form "this was
    /// refused", and a check that refused everybody would satisfy all of them.
    /// </remarks>
    [Fact]
    public async Task TheCallerHoldingThePermissionPlacesTheOrder()
    {
        var inventory = new CountingInventory(available: 10);

        using var host = await StartAsync(inventory);
        using var client = host.GetTestClient();

        var response = await PostAsync(
            client, "cashier-1", new PlaceOrder("SKU-1", 2, "tok"), Tokens.Cashier);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await ReadAsync<OrderPlacedResult>(response);

        body.OrderId.ShouldBe("cashier-1");
        body.ReceiptId.ShouldBe("receipt-cashier-1");

        inventory.Released.ShouldBeEmpty("Nothing failed, so nothing was undone.");
    }

    /// <summary>
    /// The refusal body carries no claim of the caller's.
    /// </summary>
    /// <remarks>
    /// <c>docs/15-Security.md §3</c>'s Boundary 1 information-disclosure row: an RFC 7807
    /// body exposes what the caller needs to act and nothing about how the decision was
    /// reached. The permission is named because it is already public — it is in the manifest,
    /// which is the point of putting the stance on the capability — and the principal is not.
    /// </remarks>
    [Fact]
    public async Task ARefusalNamesTheGrantAndNotTheCaller()
    {
        using var host = await StartAsync();
        using var client = host.GetTestClient();

        var response = await PostAsync(
            client, "shopper-2", new PlaceOrder("SKU-1", 1, "tok"), Tokens.Shopper);

        var problem = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        problem.ShouldContain("payment.write", Case.Sensitive);

        problem.ShouldNotContain("shopper-1", Case.Sensitive);
        problem.ShouldNotContain("orders.read", Case.Sensitive);
        problem.ShouldNotContain(Tokens.Shopper, Case.Sensitive);
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

    // AFailedPaymentReleasesTheReservation used to be here. It stood up a server, a
    // routing table and a bespoke declining gateway to observe a property of the flow
    // that HTTP has nothing to do with — and could still not see the ordering, because an
    // endpoint returns one status code whether the hold was released before, after or
    // instead of anything else. It is now PlaceOrderFlowTests, over FlowTestHost.

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

    /// <summary>
    /// Posts an order as <paramref name="token"/>'s caller.
    /// </summary>
    /// <param name="token">
    /// Which demonstration caller to send as. Defaults to <see cref="Tokens.Cashier"/> — the
    /// one holding <c>payment.write</c> — so that a test about routing, telemetry or
    /// idempotency is not silently also a test about authorisation. The two tests that <em>are</em>
    /// about authorisation pass the other two callers explicitly.
    /// </param>
    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client, string key, PlaceOrder order, string token = Tokens.Cashier)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(order, EcommerceJsonContext.Default.PlaceOrder),
        };

        request.Headers.Add(FlowXHeaders.IdempotencyKey, key);

        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

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

                    // The same scheme Program.cs registers, because the capabilities'
                    // stances are decided against HttpContext.User and a host without it
                    // would make every one of these tests an anonymous, refused request.
                    services
                        .AddAuthentication(DemoTokenHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, DemoTokenHandler>(
                            DemoTokenHandler.SchemeName, null);
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
                    app.UseAuthentication();
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

        /// <summary>Keys whose hold was released, so an unwind is observable from a test.</summary>
        /// <remarks>
        /// This used to record nothing, on the grounds that what a release does to the ledger
        /// is asserted in <c>PlaceOrderFlowTests</c> against the sample's real store. It is
        /// recorded now because one question can only be asked here: whether a step refused
        /// by its authorisation stance unwinds the steps before it, over the real endpoint,
        /// with the refusal arriving as a status code rather than as a return value.
        /// </remarks>
        public List<string> Released { get; } = [];

        public ValueTask ReleaseAsync(string idempotencyKey, CancellationToken ct)
        {
            lock (_sync)
            {
                Released.Add(idempotencyKey);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ApprovingGateway : IPaymentGateway
    {
        public ValueTask<string?> CaptureAsync(string reservationId, string idempotencyKey, CancellationToken ct)
            => ValueTask.FromResult<string?>("receipt-" + idempotencyKey);
    }
}
