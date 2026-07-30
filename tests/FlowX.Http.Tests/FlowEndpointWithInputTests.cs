using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FlowX.Hosting;
using FlowX.Http;
using FlowX.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace FlowX.Http.Tests;

/// <summary>The flow's input contract, read from the request body.</summary>
public sealed record OrderRequest(string Sku, int Quantity);

/// <summary>The flow's output contract, produced by its <c>.Return(...)</c> clause.</summary>
public sealed record OrderReceipt(string Sku, int Steps);

/// <summary>Source-generated metadata for both directions of the wire.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OrderRequest))]
[JsonSerializable(typeof(OrderReceipt))]
internal sealed partial class InputJsonContext : JsonSerializerContext;

/// <summary>
/// The overload that reads a request body and returns the flow's declared output.
/// </summary>
/// <remarks>
/// The response-only overload cannot carry an input at all, so a flow whose first step
/// binds to a contract had nothing to bind to. These tests cover the path a real
/// application uses, including the two ways a body can be unusable.
/// </remarks>
public sealed class FlowEndpointWithInputTests : IAsyncLifetime
{
    private const string Route = "/api/v1/orders";

    private IHost _host = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddFlowX(o => o.ApplicationName = "Sample.App");
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapFlow(
                        "POST",
                        Route,
                        Plans.PlaceOrder(),
                        _ => new EchoDispatcher(),
                        static ctx => new OrderReceipt(ctx.Get<OrderRequest>().Sku, 2),
                        (JsonTypeInfo<OrderRequest>)InputJsonContext.Default.GetTypeInfo(typeof(OrderRequest))!,
                        (JsonTypeInfo<OrderReceipt>)InputJsonContext.Default.GetTypeInfo(typeof(OrderReceipt))!,
                        requireIdempotencyKey: true));
                }))
            .StartAsync(TestContext.Current.CancellationToken);

        _client = _host.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();

        if (_host is not null)
        {
            await _host.StopAsync(TestContext.Current.CancellationToken);
            _host.Dispose();
        }
    }

    [Fact]
    public async Task TheBodyReachesTheFlowAndTheProjectionComesBack()
    {
        var response = await PostAsync("{\"sku\":\"SKU-7\",\"quantity\":3}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // The sku came from the request body, through the context, into the projection.
        // Nothing on that path is possible with the response-only overload.
        body.ShouldContain("\"sku\":\"SKU-7\"");
        body.ShouldContain("\"steps\":2");
    }

    [Fact]
    public async Task AMalformedBodyIsRefusedAsProblemDetails()
    {
        var response = await PostAsync("{\"sku\":");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        using var problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        problem.RootElement.GetProperty("code").GetString().ShouldBe("http.malformed_body");

        // The parser's own account, which describes the caller's payload rather than
        // anything internal — see docs/16-Security.md on error hygiene.
        problem.RootElement.GetProperty("reason").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    public async Task AnAbsentBodyIsRefused(string body)
    {
        var response = await PostAsync(body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var code = problem.RootElement.GetProperty("code").GetString();

        // An empty stream and a literal `null` fail at different layers — the parser and
        // the null check — but a caller must not have to tell them apart.
        code.ShouldBeOneOf("http.malformed_body", "http.missing_body");
    }

    [Fact]
    public async Task TheIdempotencyKeyIsStillRequired()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StringContent("{\"sku\":\"SKU-7\",\"quantity\":1}", Encoding.UTF8, "application/json"),
        };

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        // The header is checked before the body is read: a request that is going to be
        // refused should not first cost a deserialisation.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        problem.RootElement.GetProperty("code").GetString().ShouldBe("http.idempotency_key_required");
    }

    [Fact]
    public async Task ASensitiveFieldIsStrippedFromTheErrorBody()
    {
        // The capability author attached a secret to an error — the mistake [Sensitive]
        // exists to survive. Without redaction it goes into the Problem Details body,
        // over the network, and into the caller's logs.
        using var host = await StartRedactingAsync();
        using var client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StringContent("{\"sku\":\"SKU-1\",\"quantity\":1}", Encoding.UTF8, "application/json"),
        };

        request.Headers.Add(FlowXHeaders.IdempotencyKey, "key-leak");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.ShouldNotContain("tok_live_secret");
        body.ShouldContain("[redacted]");

        // The rest still travels, or the caller cannot act on the error.
        body.ShouldContain("SKU-1");
    }

    /// <summary>A host whose flow fails with an error carrying a sensitive value.</summary>
    private static async Task<IHost> StartRedactingAsync() =>
        await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddFlowX(o => o.ApplicationName = "Sample.App");
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapFlow(
                        "POST",
                        Route,
                        Plans.PlaceOrder(),
                        _ => new LeakingDispatcher(),
                        static ctx => new OrderReceipt("unreachable", 0),
                        (JsonTypeInfo<OrderRequest>)InputJsonContext.Default.GetTypeInfo(typeof(OrderRequest))!,
                        (JsonTypeInfo<OrderReceipt>)InputJsonContext.Default.GetTypeInfo(typeof(OrderReceipt))!,
                        requireIdempotencyKey: true,
                        sensitiveMembers: ["PaymentToken"]));
                }))
            .StartAsync(TestContext.Current.CancellationToken);

    /// <summary>Fails with an error that carries a secret it should not have.</summary>
    private sealed class LeakingDispatcher : IStepDispatcher
    {
        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Failed(
                new Error("payment.declined", "declined", ErrorCategory.Conflict)
                    .With("paymentToken", "tok_live_secret")
                    .With("sku", "SKU-1")));

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This double runs plans with no branch step.");
    }

    [Fact]
    public void MapFlowWithInputRejectsNullArguments()
    {
        var request = (JsonTypeInfo<OrderRequest>)InputJsonContext.Default.GetTypeInfo(typeof(OrderRequest))!;
        var receipt = (JsonTypeInfo<OrderReceipt>)InputJsonContext.Default.GetTypeInfo(typeof(OrderReceipt))!;
        var plan = Plans.PlaceOrder();
        Func<IServiceProvider, IStepDispatcher> factory = _ => new EchoDispatcher();
        Func<FlowContext, OrderReceipt> projection = static _ => new OrderReceipt("x", 0);

        Should.Throw<ArgumentNullException>(() => FlowEndpointExtensions.MapFlow(
            null!, "POST", Route, plan, factory, projection, request, receipt));

        Should.Throw<ArgumentNullException>(() => FlowEndpointExtensions.MapFlow(
            NullBuilder.Instance, "POST", Route, null!, factory, projection, request, receipt));

        Should.Throw<ArgumentNullException>(() => FlowEndpointExtensions.MapFlow(
            NullBuilder.Instance, "POST", Route, plan, null!, projection, request, receipt));

        Should.Throw<ArgumentNullException>(() => FlowEndpointExtensions.MapFlow(
            NullBuilder.Instance, "POST", Route, plan, factory, null!, request, receipt));

        Should.Throw<ArgumentNullException>(() => FlowEndpointExtensions.MapFlow(
            NullBuilder.Instance, "POST", Route, plan, factory, projection,
            (JsonTypeInfo<OrderRequest>)null!, receipt));

        Should.Throw<ArgumentNullException>(() => FlowEndpointExtensions.MapFlow(
            NullBuilder.Instance, "POST", Route, plan, factory, projection, request,
            (JsonTypeInfo<OrderReceipt>)null!));

        Should.Throw<ArgumentException>(() => FlowEndpointExtensions.MapFlow(
            NullBuilder.Instance, " ", Route, plan, factory, projection, request, receipt));
    }

    private Task<HttpResponseMessage> PostAsync(string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        request.Headers.Add(FlowXHeaders.IdempotencyKey, "key-1");

        return _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Enough of an <see cref="IEndpointRouteBuilder"/> to reach the guard clauses.
    /// </summary>
    /// <remarks>
    /// The argument checks run before anything is mapped, so a builder that collects
    /// nothing is sufficient — and standing up a real host to assert a null check would
    /// make the test slower without making it stricter.
    /// </remarks>
    private sealed class NullBuilder : IEndpointRouteBuilder
    {
        public static readonly NullBuilder Instance = new();

        public IServiceProvider ServiceProvider { get; } = new ServiceCollection().BuildServiceProvider();

        public ICollection<Microsoft.AspNetCore.Routing.EndpointDataSource> DataSources { get; } = [];

        public IApplicationBuilder CreateApplicationBuilder() => throw new NotSupportedException();
    }

    /// <summary>Succeeds every step without touching the context.</summary>
    private sealed class EchoDispatcher : IStepDispatcher
    {
        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This double runs plans with no branch step.");
    }
}
