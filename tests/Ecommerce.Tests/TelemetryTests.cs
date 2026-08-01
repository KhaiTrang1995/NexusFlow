using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ecommerce;
using FlowX.Hosting;
using FlowX.Http;
using FlowX.Observability;
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
/// What the reference application actually emits when a real request goes through it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The end of the chain, and the only assertion that covers all of it.</strong>
/// <c>TelemetryConformanceTests</c> runs a hand-built plan through a hand-built dispatcher; this
/// runs the <em>generated</em> plan and the <em>generated</em> dispatcher, over a real server,
/// through the real HTTP endpoint. A regression that only shows up once the generator is in the
/// loop — a capability whose descriptor loses its version, a step kind that stops being named —
/// shows up here and nowhere else.
/// </para>
/// <para>
/// <strong>What the sample does not emit is asserted too.</strong> Its HTTP trigger resolves no
/// tenant, so <c>flowx.tenant.id</c> is absent from its spans and its <c>tenant</c> metric label
/// is <c>other</c> — see <see cref="TheSampleResolvesNoTenantSoItsMetricsAreBucketed"/>. That is
/// the truthful reading for an application with no authentication, and recording it here stops
/// the absence being read later as a defect in the emitter.
/// </para>
/// </remarks>
public sealed class TelemetryTests
{
    private const string Route = "/api/v1/orders";

    /// <summary>A successful order produces one flow span and one span per step.</summary>
    [Fact]
    public async Task APlacedOrderEmitsAFlowSpanAndASpanPerStep()
    {
        using var spans = new SpanRecorder();
        using var host = await StartAsync();
        using var client = host.GetTestClient();

        (await PostAsync(client, "key-1", new PlaceOrder("SKU-1", 2, "tok"))).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        var names = spans.Captured.Select(static a => a.OperationName).ToList();

        names.ShouldContain("flow order.place");
        names.ShouldContain("step 0 order.validate");
        names.ShouldContain("step 1 inventory.reserve");
        names.ShouldContain("step 2 payment.capture");
        names.ShouldContain(
            "step 3 order.placed",
            "an emit is a step boundary too, and a trace that skipped it would end one step " +
            "before the thing a consumer is waiting for.");
    }

    /// <summary>
    /// The generated plan supplies the capability identity and version the schema asks for.
    /// </summary>
    [Fact]
    public async Task TheGeneratedPlanSuppliesTheCapabilityIdentityAndVersion()
    {
        using var spans = new SpanRecorder();
        using var host = await StartAsync();
        using var client = host.GetTestClient();

        await PostAsync(client, "key-1", new PlaceOrder("SKU-1", 2, "tok"));

        var capture = spans.Single("step 2 payment.capture");

        capture.GetTagItem("flowx.capability.id").ShouldBe("payment.capture");
        capture.GetTagItem("flowx.capability.version").ShouldBe(
            "2.1.0",
            "read off the [Capability] attribute the generator compiled into the plan, which " +
            "is the only place this version exists.");

        capture.GetTagItem("flowx.flow.id").ShouldBe("order.place");

        var flow = spans.Single("flow order.place");

        flow.GetTagItem("flowx.flow.version").ShouldBe("1.0.0");
        flow.GetTagItem("flowx.flow.profile").ShouldBe("Ephemeral");
    }

    /// <summary>
    /// A business failure reaches the span as the flow's own error code, not as an HTTP status.
    /// </summary>
    /// <remarks>
    /// The step's error and the endpoint's 409 are two renderings of one fact, and §9's runbook
    /// reads the first: "error spike → <c>flowx_flow_total{outcome=Failure}</c> by
    /// <c>error_code</c>". A span carrying only a status code would send an operator to the
    /// transport for a problem in the inventory.
    /// </remarks>
    [Fact]
    public async Task AnOutOfStockOrderCarriesTheFlowsErrorCodeOnItsSpans()
    {
        using var spans = new SpanRecorder();
        using var host = await StartAsync();
        using var client = host.GetTestClient();

        // The sample's in-memory store seeds SKU-1 and nothing else, so an unknown SKU is out
        // of stock by construction rather than by a double this test had to build.
        (await PostAsync(client, "key-1", new PlaceOrder("SKU-9", 1, "tok"))).StatusCode
            .ShouldBe(HttpStatusCode.Conflict);

        var step = spans.Single("step 1 inventory.reserve");

        step.GetTagItem("flowx.error.code").ShouldBe("inventory.out_of_stock");
        step.GetTagItem("flowx.error.category").ShouldBe("Conflict");
        step.Status.ShouldBe(ActivityStatusCode.Error);

        var flow = spans.Single("flow order.place");

        flow.GetTagItem("flowx.error.code").ShouldBe("inventory.out_of_stock");
        flow.Status.ShouldBe(ActivityStatusCode.Error);
    }

    /// <summary>
    /// The <c>/metrics</c> endpoint serves the series a real request produced.
    /// </summary>
    /// <remarks>
    /// Asserted on the rendered text rather than on the listener, because the rendering is what
    /// an operator's scraper reads and it is the half that can silently drop a label.
    /// </remarks>
    [Fact]
    public async Task TheMetricsEndpointServesWhatTheRequestProduced()
    {
        using var host = await StartAsync();
        using var client = host.GetTestClient();

        await PostAsync(client, "key-1", new PlaceOrder("SKU-1", 2, "tok"));

        var scrape = await client.GetStringAsync("/metrics", TestContext.Current.CancellationToken);

        scrape.ShouldContain("""flowx_flow_total{flow="order.place",outcome="Success"} 1""");

        scrape.ShouldContain(
            """flowx_step_duration_seconds_count{flow="order.place",step="1",capability="inventory.reserve",outcome="Success"} 1""");

        scrape.ShouldContain(
            """flowx_capability_duration_seconds_count{capability="payment.capture",outcome="Success"} 1""");
    }

    /// <summary>
    /// Neither the endpoint's idempotency key nor the correlation id becomes a metric label.
    /// </summary>
    /// <remarks>
    /// The cardinality rule, checked on the artifact rather than on the emitter. The endpoint
    /// requires an <c>Idempotency-Key</c> header, so this is the one place in the repository
    /// where a caller-supplied unbounded string is guaranteed to be in scope while metrics are
    /// being written — which makes it the right place to check that none of them reached one.
    /// </remarks>
    [Fact]
    public async Task NoCallerSuppliedStringReachesAMetricLabel()
    {
        using var host = await StartAsync();
        using var client = host.GetTestClient();

        await PostAsync(client, "an-unbounded-caller-supplied-key", new PlaceOrder("SKU-1", 1, "tok"));

        var scrape = await client.GetStringAsync("/metrics", TestContext.Current.CancellationToken);

        scrape.ShouldNotContain(
            "an-unbounded-caller-supplied-key",
            customMessage:
            "docs/12 §3: cardinality is a production incident waiting to happen, and an " +
            "idempotency key is one series per request.");
    }

    /// <summary>
    /// The sample authenticates nobody, so it resolves no tenant and its metrics are bucketed.
    /// </summary>
    [Fact]
    public async Task TheSampleResolvesNoTenantSoItsMetricsAreBucketed()
    {
        using var spans = new SpanRecorder();
        using var host = await StartAsync();
        using var client = host.GetTestClient();

        await PostAsync(client, "key-1", new PlaceOrder("SKU-1", 1, "tok"));

        spans.Single("flow order.place").GetTagItem("flowx.tenant.id").ShouldBeNull(
            "FlowInvocation.TenantId is documented as resolved from validated claims only, and " +
            "this sample validates none. An absent attribute is the truthful answer; a literal " +
            "'default' would be an invented tenant on every trace in the estate.");

        var scrape = await client.GetStringAsync("/metrics", TestContext.Current.CancellationToken);

        scrape.ShouldContain(
            "tenant=\"other\"",
            customMessage:
            "and an untenanted flow lands in the bucket rather than dropping the label, so the " +
            "sum over tenants still equals the total.");
    }

    /// <summary>Captures every FlowX span for the duration of a test.</summary>
    private sealed class SpanRecorder : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly List<Activity> _captured = [];
        private readonly Lock _gate = new();

        public SpanRecorder()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name == FlowXTelemetry.SourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    lock (_gate)
                    {
                        _captured.Add(activity);
                    }
                },
            };

            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<Activity> Captured
        {
            get
            {
                lock (_gate)
                {
                    return [.. _captured];
                }
            }
        }

        public Activity Single(string operationName)
        {
            var matches = Captured.Where(a => a.OperationName == operationName).ToList();

            matches.Count.ShouldBe(
                1,
                $"Expected exactly one '{operationName}'. Captured: " +
                $"[{string.Join(", ", Captured.Select(static a => a.OperationName))}].");

            return matches[0];
        }

        public void Dispose() => _listener.Dispose();
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string key, PlaceOrder order)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(order, EcommerceJsonContext.Default.PlaceOrder),
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.Add("Idempotency-Key", key);

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The sample's own composition root, plus the sample's own telemetry listener.
    /// </summary>
    /// <remarks>
    /// <see cref="SampleTelemetry.Start"/> with <c>printSpans: false</c>: the collector is the
    /// subject, and printing every span would put the reference application's traces into the
    /// test runner's output for the sake of a listener this file asserts on directly.
    /// </remarks>
    private static async Task<IHost> StartAsync()
    {
        var telemetry = SampleTelemetry.Start(printSpans: false);

        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddFlowX(options => options.ApplicationName = "Ecommerce");
                    services.AddSingleton(telemetry);
                    services.AddSingleton<IInventoryStore, InMemoryInventoryStore>();
                    services.AddSingleton<IPaymentGateway, AlwaysApprovesGateway>();
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
                        endpoints.MapGet(
                            "/metrics",
                            (SampleTelemetry collected) => collected.Scrape());

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
}
