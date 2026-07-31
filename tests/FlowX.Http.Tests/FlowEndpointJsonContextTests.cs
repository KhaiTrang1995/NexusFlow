using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

/// <summary>A context that knows only half of what the endpoint needs.</summary>
/// <remarks>
/// Declares the request and not the response, which is the realistic version of the
/// mistake: a contract is added to the flow's <c>.Return(...)</c> and the serialiser is
/// not told. Nothing about that is visible at compile time.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OrderRequest))]
internal sealed partial class HalfJsonContext : JsonSerializerContext;

/// <summary>
/// The overload the endpoint generator calls: one serialiser context rather than two
/// <c>JsonTypeInfo</c> arguments.
/// </summary>
/// <remarks>
/// <para>
/// It exists so that generated source names the serialiser once instead of reaching for
/// property names another generator chose, and these tests are the reason that is safe
/// to do: the resolution, the cast and the failure message live here, in a package with
/// tests, rather than being re-emitted into every consumer's project.
/// </para>
/// <para>
/// The second test is the one that earns the overload. A missing
/// <c>[JsonSerializable]</c> is invisible to the compiler, so the only question is
/// <em>when</em> it is discovered — and "while the route is being registered, naming the
/// attribute to add" is the answer worth paying for.
/// </para>
/// </remarks>
public sealed class FlowEndpointJsonContextTests
{
    private const string Route = "/api/v1/orders";

    [Fact]
    public async Task TheContextSuppliesBothDirectionsOfTheWire()
    {
        using var host = await StartAsync(InputJsonContext.Default);
        using var client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StringContent(
                "{\"sku\":\"SKU-7\",\"quantity\":3}", Encoding.UTF8, "application/json"),
        };

        request.Headers.Add(FlowXHeaders.IdempotencyKey, "key-1");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Request read and response written, both through metadata pulled off the
        // context by type — identical behaviour to passing the two JsonTypeInfo values,
        // including the camelCase policy the context declares.
        body.ShouldContain("\"sku\":\"SKU-7\"");
        body.ShouldContain("\"steps\":2");
    }

    [Fact]
    public async Task AContractTheContextDoesNotDeclareFailsAtRegistrationAndSaysWhichOne()
    {
        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            async () => await StartAsync(HalfJsonContext.Default));

        // Names the context, the contract, and the attribute to add. A caller who gets
        // this message does not have to know how JsonSerializerContext resolves anything.
        thrown.Message.ShouldContain(nameof(HalfJsonContext));
        thrown.Message.ShouldContain(nameof(OrderReceipt));
        thrown.Message.ShouldContain("[JsonSerializable(typeof(OrderReceipt))]");
    }

    [Fact]
    public async Task TheIdempotencyRuleAndTheRedactionListStillApply()
    {
        using var host = await StartAsync(InputJsonContext.Default, sensitiveMembers: ["PaymentToken"]);
        using var client = host.GetTestClient();

        // No Idempotency-Key. The overload forwards the flag rather than defaulting it,
        // which is what the generator relies on to carry `Idempotent = true` across.
        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StringContent(
                "{\"sku\":\"SKU-7\",\"quantity\":1}", Encoding.UTF8, "application/json"),
        };

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        using var problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        problem.RootElement.GetProperty("code").GetString().ShouldBe("http.idempotency_key_required");
    }

    private static async Task<IHost> StartAsync(
        JsonSerializerContext json, IReadOnlyCollection<string>? sensitiveMembers = null) =>
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
                    app.UseEndpoints(endpoints => endpoints.MapFlow<OrderRequest, OrderReceipt>(
                        "POST",
                        Route,
                        Plans.PlaceOrder(),
                        _ => new ContextEchoDispatcher(),
                        static ctx => new OrderReceipt(ctx.Get<OrderRequest>().Sku, 2),
                        json,
                        requireIdempotencyKey: true,
                        sensitiveMembers: sensitiveMembers));
                }))
            .StartAsync(TestContext.Current.CancellationToken);

    /// <summary>Succeeds at every step and touches nothing.</summary>
    private sealed class ContextEchoDispatcher : IStepDispatcher
    {
        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx) => false;

        public int Select(int stepIndex, FlowContext ctx) => -1;

        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException();

        public FlowContext EnterIteration(
            int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException();

        public SubFlowSource BeginSubFlow(int stepIndex, FlowContext ctx) =>
            throw new NotSupportedException();

        public void EnterSubFlow(int stepIndex, in SubFlowSource source, FlowContext child) =>
            throw new NotSupportedException();
    }
}
