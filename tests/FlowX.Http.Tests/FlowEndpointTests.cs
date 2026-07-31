using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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

namespace FlowX.Http.Tests;

/// <summary>The response body a successful flow returns.</summary>
public sealed record OrderPlaced(string OrderId, int Steps);

/// <summary>
/// Source-generated serialisation metadata.
/// </summary>
/// <remarks>
/// Required by <c>MapFlow</c> rather than optional. That requirement is what keeps the
/// response path free of runtime serialisation, and therefore what keeps an application
/// built on FlowX able to publish with NativeAOT (constraint C2).
/// </remarks>
[JsonSerializable(typeof(OrderPlaced))]
internal sealed partial class EndpointJsonContext : JsonSerializerContext;

/// <summary>
/// The whole HTTP path, end to end, over a real server: request in, flow executed,
/// response or Problem Details out.
/// </summary>
/// <remarks>
/// The unit tests cover the mapper and the reader in isolation. This covers what they
/// structurally cannot — that the pieces are wired together, that the status code
/// actually reaches the socket, and that the content type is what a client will see.
/// </remarks>
public sealed class FlowEndpointTests : IAsyncLifetime
{
    private IHost _host = null!;
    private HttpClient _client = null!;
    private ScriptedDispatcher _dispatcher = null!;

    public async ValueTask InitializeAsync()
    {
        _dispatcher = new ScriptedDispatcher();

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
                        "/api/v1/orders",
                        Plans.PlaceOrder(),
                        _ => _dispatcher,
                        result => new OrderPlaced("order-1", result.CompletedSteps),
                        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<OrderPlaced>)
                            EndpointJsonContext.Default.GetTypeInfo(typeof(OrderPlaced))!,
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

    private static HttpRequestMessage Post(string? idempotencyKey = "key-1")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders");

        if (idempotencyKey is not null)
        {
            request.Headers.Add(FlowXHeaders.IdempotencyKey, idempotencyKey);
        }

        return request;
    }

    [Fact]
    public async Task ASuccessfulFlowReturns200AndTheProjectedBody()
    {
        var response = await _client.SendAsync(Post(), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<OrderPlaced>(
            TestContext.Current.CancellationToken);

        body.ShouldNotBeNull().Steps.ShouldBe(2);
    }

    [Fact]
    public async Task AMissingIdempotencyKeyIs400WithProblemDetails()
    {
        var response = await _client.SendAsync(
            Post(idempotencyKey: null), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        using var problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        problem.RootElement.GetProperty("code").GetString().ShouldBe("http.idempotency_key_required");
    }

    [Theory]
    [InlineData(ErrorCategory.Validation, HttpStatusCode.BadRequest)]
    [InlineData(ErrorCategory.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData(ErrorCategory.NotFound, HttpStatusCode.NotFound)]
    [InlineData(ErrorCategory.Conflict, HttpStatusCode.Conflict)]
    [InlineData(ErrorCategory.Unavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task ABusinessFailureReachesTheClientAsItsMappedStatus(
        ErrorCategory category,
        HttpStatusCode expected)
    {
        _dispatcher.FailWith = new Error("order.rejected", "the order was rejected", category);

        var response = await _client.SendAsync(Post(), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(expected);

        using var problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        problem.RootElement.GetProperty("detail").GetString().ShouldBe("the order was rejected");
    }

    [Fact]
    public async Task AnInternalFailureIs500AndLeaksNothing()
    {
        _dispatcher.FailWith = new Error(
            "db.query_failed",
            "Npgsql: relation \"customer_pii\" does not exist at 10.0.4.17",
            ErrorCategory.Internal);

        var response = await _client.SendAsync(Post(), TestContext.Current.CancellationToken);
        var payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        payload.ShouldNotContain("customer_pii");
        payload.ShouldNotContain("10.0.4.17");
        payload.ShouldNotContain("Npgsql");
        // Asserted on the raw wire payload, not on the mapper's output. The mapper is
        // unit-tested; this proves nothing re-attaches the detail on the way out.
    }

    [Fact]
    public async Task TheCorrelationIdIsEchoedOnAFailureSoACallerCanQuoteIt()
    {
        _dispatcher.FailWith = new Error("order.rejected", "no", ErrorCategory.Conflict);

        var request = Post();
        request.Headers.Add(FlowXHeaders.TraceParent, "00-abc-def-01");

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        response.Headers.GetValues(FlowXHeaders.CorrelationId).ShouldContain("00-abc-def-01");
    }

    [Fact]
    public async Task AGetToAPostRouteIs405RatherThan404()
    {
        var response = await _client.GetAsync("/api/v1/orders", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed,
            "The HttpMethodMetadata is what makes routing distinguish 'wrong method' " +
            "from 'no such route'. A 404 here would send a client hunting for a typo.");
    }

    [Fact]
    public void MapFlowRejectsNullArguments()
    {
        var plan = Plans.PlaceOrder();
        var typeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<OrderPlaced>)
            EndpointJsonContext.Default.GetTypeInfo(typeof(OrderPlaced))!;

        Should.Throw<ArgumentNullException>(() => FlowEndpointExtensions.MapFlow(
            null!, "POST", "/x", plan, _ => _dispatcher, _ => new OrderPlaced("a", 0), typeInfo));
    }

    /// <summary>A dispatcher whose outcome a test chooses.</summary>
    private sealed class ScriptedDispatcher : IStepDispatcher
    {
        public Error? FailWith { get; set; }

        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => FailWith is not null && stepIndex == 1
                ? ValueTask.FromResult(StepOutcome.Failed(FailWith))
                : ValueTask.FromResult(StepOutcome.Success);

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This double runs plans with no branch step.");

        /// <inheritdoc />
        /// <remarks>This double declares no iteration, so the engine never asks it for one.</remarks>
        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to begin.");

        /// <inheritdoc />
        public FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to enter.");

        public int Select(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This double runs plans with no switch step.");
    }
}

/// <summary>Plans the HTTP tests execute.</summary>
internal static class Plans
{
    private static readonly CapabilityDescriptor Validate =
        CapabilityDescriptor.Create("order.validate", "1.0.0", isIdempotent: true);

    public static ExecutionPlan PlaceOrder() => ExecutionPlan.Create(
        FlowDescriptor.Create("order.place", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromMinutes(1)),
        StepGraph.Create([
            StepNode.ForCapability(0, Validate),
            StepNode.ForCapability(1, Validate),
        ]));
}
