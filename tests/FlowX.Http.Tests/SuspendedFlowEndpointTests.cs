using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowX.Conformance.InMemory;
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

/// <summary>The offer a caller posts to start the flow.</summary>
public sealed record OfferToAccept(string CandidateId);

/// <summary>What the flow returns once the offer has been countersigned.</summary>
public sealed record AcceptedOffer(string CandidateId);

/// <summary>The signal a person delivers, days later.</summary>
public sealed record OfferCountersigned(string SignedBy);

/// <summary>Source-generated metadata for all three contracts on this wire.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OfferToAccept))]
[JsonSerializable(typeof(AcceptedOffer))]
[JsonSerializable(typeof(OfferCountersigned))]
internal sealed partial class SuspensionJsonContext : JsonSerializerContext;

/// <summary>
/// The HTTP shape of a flow that suspends
/// (<a href="../../../docs/adr/ADR-0022-http-shape-of-a-suspending-flow.md">ADR-0022</a>):
/// <c>202</c> from the run endpoint, and a generated route to continue it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Before this, a suspending flow behind an <c>[HttpTrigger]</c> produced a
/// <c>500</c>.</strong> <c>HandleAsync</c> had two outcomes and needed three: a suspended
/// result is not <c>IsFailure</c> — nothing failed — and not <c>IsSuccess</c> either, so the
/// <c>200</c> branch was taken and <c>FlowExecutionResult&lt;TOut&gt;.Value</c> threw,
/// deliberately, because the flow's <c>.Return(...)</c> reads values the steps after the wait
/// have not produced. That is why <c>samples/workflow</c>'s <c>offer.accept</c> declared no
/// trigger and mapped two routes by hand.
/// </para>
/// <para>
/// Everything is asserted on the wire, through a real server, because a status code and a
/// <c>Location</c> header are what an intermediary reads and a unit test of a handler cannot
/// see. The journal and the lease store are the reference in-memory pair from
/// <c>FlowX.Conformance.Tests</c> — a second pair written here would be a second pair to
/// keep correct.
/// </para>
/// </remarks>
public sealed class SuspendedFlowEndpointTests : IAsyncLifetime
{
    private const string Route = "/api/v1/offers";
    private const string Signal = "offer.countersigned";

    private IHost _host = null!;
    private HttpClient _client = null!;

    /// <summary>Send · wait for the countersignature · start onboarding.</summary>
    private static ExecutionPlan Plan() => ExecutionPlan.Create(
        FlowDescriptor.Create("offer.accept", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromDays(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, CapabilityDescriptor.Create("offer.send", "1.0.0", isIdempotent: true)),
            StepNode.ForAwaitSignal(1, Signal, TimeSpan.FromDays(7)),
            StepNode.ForCapability(
                2, CapabilityDescriptor.Create("onboarding.start", "1.0.0", isIdempotent: true)),
        ]));

    public async ValueTask InitializeAsync()
    {
        var journal = new InMemoryFlowJournal();

        _host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddFlowX(o => o.ApplicationName = "Workflow");
                    services.AddSingleton<IFlowJournal>(journal);
                    services.AddSingleton<IRecoveryIndex>(new InMemoryRecoveryIndex(journal));
                    services.AddSingleton<ILeaseStore>(new InMemoryLeaseStore());
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapFlow<OfferToAccept, AcceptedOffer>(
                            "POST",
                            Route,
                            Plan(),
                            _ => new SigningDispatcher(),
                            static ctx => new AcceptedOffer(ctx.Get<OfferToAccept>().CandidateId),
                            SuspensionJsonContext.Default);

                        endpoints.MapFlowSignal<OfferCountersigned>(
                            "POST",
                            Route + "/{instanceId:guid}/signals/" + Signal,
                            Signal,
                            Plan(),
                            _ => new SigningDispatcher(),
                            SuspensionJsonContext.Default);
                    });
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

    // ------------------------------------------------------------------- 202

    /// <summary>
    /// A flow that suspends answers 202 with the instance and where to continue it.
    /// </summary>
    /// <remarks>
    /// The status code, not a "pending" envelope behind a 200: every proxy, client and retry
    /// policy between the caller and this process reads the code and none of them parse the
    /// body.
    /// </remarks>
    [Fact]
    public async Task AFlowThatSuspendsAnswers202WithWhereToContinueIt()
    {
        var response = await StartAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");

        using var body = await ReadAsync(response);

        var instanceId = body.RootElement.GetProperty("instanceId").GetString();

        instanceId.ShouldNotBeNullOrWhiteSpace(
            "Without the id the instance is unreachable: it is minted inside FlowHost, so a " +
            "caller that was not told it cannot name the flow it just started.");

        body.RootElement.GetProperty("status").GetString().ShouldBe("suspended");

        var awaiting = body.RootElement.GetProperty("awaiting").EnumerateArray().Single();

        awaiting.GetProperty("signal").GetString().ShouldBe(Signal);
        awaiting.GetProperty("deliverTo").GetString()
            .ShouldBe($"{Route}/{instanceId}/signals/{Signal}");
    }

    /// <summary>
    /// One suspension point means one place to go, so <c>Location</c> names it.
    /// </summary>
    /// <remarks>
    /// Set only when the flow declares exactly one wait. A header that can name one of three
    /// addresses misleads two callers out of three, and RFC 9110's <c>Location</c> has no
    /// plural form where the body does.
    /// </remarks>
    [Fact]
    public async Task TheLocationHeaderNamesTheOneWaitThisFlowDeclares()
    {
        var response = await StartAsync();

        using var body = await ReadAsync(response);

        response.Headers.Location!.ToString().ShouldBe(
            $"{Route}/{body.RootElement.GetProperty("instanceId").GetString()}/signals/{Signal}");
    }

    // -------------------------------------------------------- signal delivery

    /// <summary>
    /// The generated route delivers the signal and the flow runs on.
    /// </summary>
    /// <remarks>
    /// <c>202</c> rather than the flow's projected output, and that is a decision rather than
    /// a limitation (ADR-0022 §2.3). The person who countersigns an offer is not the person
    /// who requested it, so projecting <c>.Return(...)</c> here would hand the second party a
    /// document assembled from the first party's request — and a delivery may complete the
    /// flow, suspend it again, fail it, or be inert, of which <c>202</c> is an honest
    /// description and <c>200</c> is an honest description of one.
    /// </remarks>
    [Fact]
    public async Task DeliveringTheSignalResumesTheInstance()
    {
        var started = await StartAsync();

        using var pending = await ReadAsync(started);

        var response = await SignalAsync(
            pending.RootElement.GetProperty("awaiting")[0].GetProperty("deliverTo").GetString()!,
            "{\"signedBy\":\"ada\"}");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        using var body = await ReadAsync(response);

        body.RootElement.GetProperty("status").GetString().ShouldBe("completed");
        body.RootElement.GetProperty("instanceId").GetString()
            .ShouldBe(pending.RootElement.GetProperty("instanceId").GetString());
    }

    /// <summary>
    /// A signal identity nothing waits for is a routing miss, not a comparison in a handler.
    /// </summary>
    /// <remarks>
    /// The identity is a literal in the generated route, so the router answers before any
    /// code runs — which is what <c>09 §6</c>'s table always specified, produced once instead
    /// of by a <c>string.Equals</c> every application writes slightly differently.
    /// </remarks>
    [Fact]
    public async Task ASignalNothingWaitsForIsNotFound()
    {
        var started = await StartAsync();

        using var pending = await ReadAsync(started);

        var response = await SignalAsync(
            $"{Route}/{pending.RootElement.GetProperty("instanceId").GetString()}/signals/offer.rejected",
            "{\"signedBy\":\"ada\"}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>An instance id that is not a GUID never reaches the handler either.</summary>
    [Fact]
    public async Task AnInstanceIdThatIsNotAGuidIsNotFound()
    {
        var response = await SignalAsync($"{Route}/not-a-guid/signals/{Signal}", "{\"signedBy\":\"ada\"}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>A malformed signal body is refused the way a malformed request body is.</summary>
    /// <remarks>
    /// The same RFC 7807 mapping, because the signal endpoint's whole contribution is
    /// translation and a second error shape would be a second thing for a client to handle.
    /// </remarks>
    [Fact]
    public async Task AMalformedSignalBodyIsRefusedAsProblemDetails()
    {
        var started = await StartAsync();

        using var pending = await ReadAsync(started);

        var response = await SignalAsync(
            pending.RootElement.GetProperty("awaiting")[0].GetProperty("deliverTo").GetString()!,
            "{\"signedBy\":");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        using var problem = await ReadAsync(response);

        problem.RootElement.GetProperty("code").GetString().ShouldBe("http.malformed_body");
    }

    /// <summary>
    /// Delivering the same signal twice does not run the flow twice, and does not fail.
    /// </summary>
    /// <remarks>
    /// Not a check written for signals, and not an <c>Idempotency-Key</c> rule either. The
    /// second delivery re-enters an instance whose wait now has a committed row, so the
    /// journal's frontier steps over it and over everything after it. At-least-once delivery
    /// is the ordinary case for a transport, and this is what makes it safe — which is the
    /// reason ADR-0022 §2.4 declines to demand a key here.
    /// </remarks>
    [Fact]
    public async Task ARedeliveredSignalIsAcceptedAndChangesNothing()
    {
        var started = await StartAsync();

        using var pending = await ReadAsync(started);

        var deliverTo = pending.RootElement.GetProperty("awaiting")[0].GetProperty("deliverTo").GetString()!;

        (await SignalAsync(deliverTo, "{\"signedBy\":\"ada\"}")).StatusCode
            .ShouldBe(HttpStatusCode.Accepted);

        var again = await SignalAsync(deliverTo, "{\"signedBy\":\"ada\"}");

        again.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        using var body = await ReadAsync(again);

        body.RootElement.GetProperty("status").GetString().ShouldBe("completed");
    }

    // -------------------------------------------------------------------- 200

    /// <summary>
    /// The flow that does not suspend still answers 200 with its projected output.
    /// </summary>
    /// <remarks>
    /// The third outcome is added, not substituted. A flow that runs to completion on the
    /// request that starts it is unchanged, which is what makes this a widening of the
    /// endpoint rather than a new one.
    /// </remarks>
    [Fact]
    public async Task AFlowThatCompletesStillAnswers200WithItsOutput()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddFlowX(o => o.ApplicationName = "Workflow");
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapFlow<OfferToAccept, AcceptedOffer>(
                        "POST",
                        Route,
                        ExecutionPlan.Create(
                            FlowDescriptor.Create(
                                "offer.accept", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromMinutes(1)),
                            StepGraph.Create([
                                StepNode.ForCapability(
                                    0, CapabilityDescriptor.Create("offer.send", "1.0.0", isIdempotent: true)),
                            ])),
                        _ => new SigningDispatcher(),
                        static ctx => new AcceptedOffer(ctx.Get<OfferToAccept>().CandidateId),
                        SuspensionJsonContext.Default));
                }))
            .StartAsync(TestContext.Current.CancellationToken);

        using var client = host.GetTestClient();

        var response = await client.PostAsync(
            Route,
            new StringContent("{\"candidateId\":\"c-42\"}", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("\"candidateId\":\"c-42\"");

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    // --------------------------------------------------------------- plumbing

    private Task<HttpResponseMessage> StartAsync() => _client.PostAsync(
        Route,
        new StringContent("{\"candidateId\":\"c-42\"}", Encoding.UTF8, "application/json"),
        TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> SignalAsync(string route, string body) => _client.PostAsync(
        route,
        new StringContent(body, Encoding.UTF8, "application/json"),
        TestContext.Current.CancellationToken);

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    /// <summary>
    /// Every capability step succeeds; the suspension point is the engine's business.
    /// </summary>
    /// <remarks>
    /// A dispatcher is never asked to run an <c>AwaitSignal</c> step it has not been given a
    /// signal for — the engine breaks out of the loop before dispatching — so there is
    /// nothing here that knows this flow waits.
    /// </remarks>
    private sealed class SigningDispatcher : IStepDispatcher
    {
        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct) =>
            new(StepOutcome.Success);

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct) =>
            new(StepOutcome.Success);

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
}
