using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FlowX.Hosting;
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

/// <summary>
/// What the generated endpoint path answers when the node is at
/// <see cref="FlowXOptions.MaxInFlightAdmissions"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The ceiling is enforced at one seam and this is one of its four faces.</strong> The
/// bus requeues, a window is held, the worker's HTTP door answers a <c>FlowFunctionResponse</c>
/// — and this, the ASP.NET route the compiler emits, answers over the wire. What is asserted
/// here is the part only a real request can show: the status line, the <c>Retry-After</c> header
/// and the content type an HTTP client actually receives.
/// </para>
/// <para>
/// <strong>The slot is taken by the test rather than by a parked request.</strong> Holding the
/// gate directly makes "this node is full" an instant rather than a race, and the thing under
/// test is what the endpoint does when it is full — not the arithmetic of getting it there,
/// which <c>PushAdmissionTests</c> covers against a real burst.
/// </para>
/// </remarks>
public sealed class AdmissionCeilingTests : IAsyncLifetime
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
                    services.AddFlowX(o =>
                    {
                        o.ApplicationName = "Sample.App";
                        o.MaxInFlightAdmissions = 1;
                    });
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapFlow<OrderRequest, OrderReceipt>(
                        "POST",
                        Route,
                        Plans.PlaceOrder(),
                        _ => new SucceedingDispatcher(),
                        static ctx => new OrderReceipt(ctx.Get<OrderRequest>().Sku, 2),
                        (JsonTypeInfo<OrderRequest>)InputJsonContext.Default.GetTypeInfo(typeof(OrderRequest))!,
                        (JsonTypeInfo<OrderReceipt>)InputJsonContext.Default.GetTypeInfo(typeof(OrderReceipt))!));
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

    /// <summary>
    /// Over the ceiling the endpoint answers 429 with a Retry-After and an RFC 7807 body; under
    /// it, the same request succeeds.
    /// </summary>
    /// <remarks>
    /// <strong>Both arms in one test, deliberately.</strong> A 429 assertion on its own passes
    /// against an endpoint that is broken in some other way; the second half is what says the
    /// refusal was the ceiling's doing and that releasing the slot undoes it exactly.
    /// </remarks>
    [Fact]
    public async Task ARequestOverTheCeilingIsRefusedWith429AndRetryAfter()
    {
        var host = _host.Services.GetRequiredService<FlowHost>();

        host.AdmissionGate.Ceiling.ShouldBe(1, "otherwise this test is asserting against no ceiling.");

        HttpResponseMessage shed;

        using (var held = host.AdmissionGate.TryAcquire())
        {
            held.Admitted.ShouldBeTrue();

            shed = await PostAsync();
        }

        using (shed)
        {
            shed.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

            shed.Content.Headers.ContentType!.MediaType.ShouldBe(
                "application/problem+json", "a refusal is a problem document like every other one here.");

            shed.Headers.RetryAfter.ShouldNotBeNull(
                "a 429 with no Retry-After tells a client to back off and not for how long.");

            shed.Headers.RetryAfter!.Delta.ShouldBe(
                TimeSpan.FromSeconds(1),
                "a delta rather than a date, so clock skew between the node and the caller cannot " +
                "be most of a one-second advice.");

            var body = await shed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            using var problem = JsonDocument.Parse(body);

            // The taxonomy every other refusal on this endpoint uses. A shed that invented its
            // own would be a second dialect for a client to learn.
            problem.RootElement.GetProperty("type").GetString()
                .ShouldBe(ProblemDetailsMapper.TypeUriPrefix + FlowAdmissionGate.SaturatedCode);

            problem.RootElement.GetProperty("title").GetString()
                .ShouldBe(ProblemDetailsMapper.TitleFor(ErrorCategory.Unavailable));

            problem.RootElement.GetProperty("status").GetInt32().ShouldBe(429);
            problem.RootElement.GetProperty("instance").GetString().ShouldBe(Route);
            problem.RootElement.GetProperty("code").GetString().ShouldBe("host.saturated");

            problem.RootElement.GetProperty("detail").GetString().ShouldNotBeNull()
                .ShouldContain(
                    "shed rather than queued",
                    customMessage: "docs/16 §4 — shed early, do not queue — and the message is " +
                    "where an operator reads which of the two happened.");

            problem.RootElement.GetProperty("maxInFlightAdmissions").GetInt32().ShouldBe(
                1, "the structured detail names the setting to raise, so the repair is in the body.");

            problem.RootElement.TryGetProperty("correlationId", out _).ShouldBeTrue();
        }

        // The slot is back. The identical request now runs.
        using var admitted = await PostAsync();

        admitted.StatusCode.ShouldBe(HttpStatusCode.OK);
        admitted.Headers.RetryAfter.ShouldBeNull("a request that succeeded has nothing to retry.");

        host.AdmissionGate.InFlight.ShouldBe(0, "the slot the request took was given back.");
    }

    /// <summary>
    /// The worker's copy of the problem taxonomy is the same taxonomy.
    /// </summary>
    /// <remarks>
    /// <strong>Two literals, one value, and this is what holds them.</strong>
    /// <c>FlowX.Hosting</c> does not reference this plugin and must not start, so
    /// <c>FlowFunctionProblem</c> repeats the type prefix and the <c>Unavailable</c> title the way
    /// <c>FlowPushSeams.CorrelationHeader</c> repeats a header name. A copy nobody checks is a
    /// copy that drifts, and a client that learned the shape from the ASP.NET host would then
    /// find a different one on the worker.
    /// </remarks>
    [Fact]
    public void TheWorkerSurfaceRepeatsThisPluginsProblemTaxonomyExactly()
    {
        FlowFunctionProblem.TypeUriPrefix.ShouldBe(ProblemDetailsMapper.TypeUriPrefix);

        FlowFunctionProblem.SaturatedTitle.ShouldBe(
            ProblemDetailsMapper.TitleFor(ErrorCategory.Unavailable));

        FlowFunctionResponse.ProblemContentType.ShouldBe(ProblemDetailsJson.ContentType);

        // And the two surfaces advise the same wait, in the units each of them writes it in.
        ((int)Math.Ceiling(FlowAdmissionGate.RetryAfter.TotalSeconds))
            .ToString(CultureInfo.InvariantCulture)
            .ShouldBe("1");
    }

    /// <summary>Succeeds every step, so an admitted request reaches its projection.</summary>
    private sealed class SucceedingDispatcher : IStepDispatcher
    {
        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This double runs plans with no branch step.");

        public int Select(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This double runs plans with no switch step.");

        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to begin.");

        public FlowContext EnterIteration(
            int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException("This dispatcher has no iteration to enter.");
    }

    private async Task<HttpResponseMessage> PostAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StringContent(
                "{\"sku\":\"SKU-7\",\"quantity\":1}", Encoding.UTF8, "application/json"),
        };

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
