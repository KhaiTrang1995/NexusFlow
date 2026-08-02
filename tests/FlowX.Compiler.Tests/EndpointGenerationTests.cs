using System;
using System.Linq;
using System.Text.Json;
using FlowX.Compiler.Emit;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// Whether the endpoint file is produced at all, and whether what it says matches what
/// the manifest says.
/// </summary>
/// <remarks>
/// <para>
/// These are the questions a hand-built model cannot ask. The first is about the user's
/// <em>compilation</em> — does it reference the HTTP transport — and the second is about
/// two outputs of one generator run agreeing, which is only meaningful if both come out
/// of the same run.
/// </para>
/// <para>
/// <strong>The transport is a stub declared in the test's own source.</strong> That is
/// not a shortcut, it is the design under test: the generator looks up
/// <c>FlowX.Http.FlowEndpointExtensions</c> by name in whatever compilation it is given
/// and links against nothing. A type by that name is the entire contract, which is why
/// this test project — like <c>FlowX.Compiler</c> itself — references no plugin.
/// </para>
/// </remarks>
public sealed class EndpointGenerationTests
{
    /// <summary>Stands in for the plugin the user's project would reference.</summary>
    private const string HttpTransport = """
        namespace FlowX.Http
        {
            public static class FlowEndpointExtensions
            {
            }
        }
        """;

    private const string JsonContext = """
        namespace Sample
        {
            [System.Text.Json.Serialization.JsonSerializable(typeof(Sample.PlaceOrder))]
            [System.Text.Json.Serialization.JsonSerializable(typeof(Sample.OrderResult))]
            public partial class SampleJsonContext : System.Text.Json.Serialization.JsonSerializerContext
            {
            }
        }
        """;

    private const string Flow = """
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record PlaceOrder(string Sku, int Quantity);
        public sealed record OrderResult(string Id);

        [Capability("order.validate", Version = "1.0.0", Authorization = Authorization.Internal)]
        public sealed class ValidateOrder : ICapability<PlaceOrder, OrderResult>
        {
            public ValueTask<Result<OrderResult>> ExecuteAsync(
                PlaceOrder input, CapabilityContext ctx, CancellationToken ct) =>
                ValueTask.FromResult(Result.Ok(new OrderResult(input.Sku)));
        }

        [Flow("order.place", Version = "1.0.0")]
        [HttpTrigger("POST", "/api/v1/orders", Idempotent = true)]
        public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
        {
            protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow) =>
                flow.Step<ValidateOrder>().Return(ctx => ctx.Get<OrderResult>());
        }
        """;

    private static GeneratorRun RunOn(params string[] sources) => GeneratorHarness.Run(
        GeneratorHarness.CompilationOf(
            [.. sources.Select((source, i) => ($"/src/File{i}.cs", source))]));

    private static string? EndpointsIn(GeneratorRun run) => run.Sources
        .Where(static s => s.HintName == EndpointEmitter.FileName)
        .Select(static s => s.Source)
        .FirstOrDefault();

    /// <summary>
    /// A project that does not reference the HTTP transport pays nothing for it.
    /// </summary>
    /// <remarks>
    /// This is the constraint the whole design is arranged around. The generator has to
    /// be able to emit code that calls a plugin without depending on one, and the only
    /// honest way to keep "a Kafka-only application costs zero" true is for there to be
    /// no file — not an empty class, not an unused method.
    /// </remarks>
    [Fact]
    public void NoHttpTransportInTheCompilationMeansNoEndpointFile()
    {
        var run = RunOn(Flow, JsonContext);

        EndpointsIn(run).ShouldBeNull(
            "The flow declares an [HttpTrigger], but nothing in this compilation can serve " +
            "one. Emitting a call into FlowX.Http here would not compile.");

        // The plan and the manifest are unaffected: the trigger is still published.
        run.Plan.ShouldNotBeNullOrEmpty();
        run.ManifestJson!.ShouldContain("/api/v1/orders");
    }

    [Fact]
    public void TheTransportBeingReferencedIsTheWholeCondition()
    {
        EndpointsIn(RunOn(Flow, JsonContext, HttpTransport)).ShouldNotBeNull(
            "FlowX.Http.FlowEndpointExtensions is in the compilation, so the endpoint the " +
            "flow declares can be generated.");
    }

    /// <summary>
    /// The endpoint and the manifest state the same address, because one read produced both.
    /// </summary>
    [Fact]
    public void TheGeneratedEndpointAgreesWithTheManifest()
    {
        var run = RunOn(Flow, JsonContext, HttpTransport);
        var endpoints = EndpointsIn(run)!;

        using var manifest = JsonDocument.Parse(run.ManifestJson!);

        var trigger = manifest.RootElement
            .GetProperty("flows")[0]
            .GetProperty("triggers")[0];

        var method = trigger.GetProperty("method").GetString()!;
        var route = trigger.GetProperty("route").GetString()!;

        trigger.GetProperty("idempotent").GetBoolean().ShouldBeTrue();

        endpoints.ShouldContainText(
            "\"" + method + "\",",
            "The manifest publishes this method. An endpoint serving another one would make " +
            "the published contract a lie.");

        endpoints.ShouldContainText(
            "\"" + route + "\",",
            "The manifest publishes this route. The two come from one TriggerReader run, so " +
            "a difference here means something re-read the attribute.");

        endpoints.ShouldContainText(
            "requireIdempotencyKey: true,",
            "The manifest publishes idempotent: true. Admission has to demand the key.");
    }

    [Fact]
    public void TheSerialiserContextIsFoundByTheContractsItDeclares()
    {
        EndpointsIn(RunOn(Flow, JsonContext, HttpTransport))!.ShouldContainText(
            "global::Sample.SampleJsonContext.Default",
            "One context declares [JsonSerializable] for both contracts, so there is nothing " +
            "for the caller to choose.");
    }

    /// <summary>
    /// Two contexts declaring the same pair is not an error and not a coin toss.
    /// </summary>
    /// <remarks>
    /// Picking the first would make the wire format depend on the order the compiler
    /// happened to walk the syntax trees in. The endpoint is still generated; the caller
    /// names the serialiser.
    /// </remarks>
    [Fact]
    public void TwoCandidateContextsLeaveTheChoiceToTheCaller()
    {
        var second = JsonContext.Replace("SampleJsonContext", "OtherJsonContext", StringComparison.Ordinal);
        var endpoints = EndpointsIn(RunOn(Flow, JsonContext, second, HttpTransport))!;

        endpoints.ShouldNotContainText(
            ".Default);",
            "Neither context may be chosen for the caller when both declare the contracts.");

        endpoints.ShouldContainText(
            "\"/api/v1/orders\",", "The address is still generated — only the serialiser is open.");
    }

    [Fact]
    public void ANonHttpTriggerProducesNoEndpoint()
    {
        var kafka = Flow.Replace(
            "[HttpTrigger(\"POST\", \"/api/v1/orders\", Idempotent = true)]",
            "[KafkaTrigger(\"orders.requested\", Group = \"placement\")]",
            StringComparison.Ordinal);

        EndpointsIn(RunOn(kafka, JsonContext, HttpTransport)).ShouldBeNull(
            "A bus trigger has no route. The HTTP transport being referenced does not make " +
            "every flow an endpoint.");
    }

    /// <summary>
    /// A flow with no <c>.Return(...)</c> has no projection, so it has no response body.
    /// </summary>
    /// <remarks>
    /// Skipped rather than mapped through the response-only overload, which would need a
    /// hand-written <c>Func&lt;FlowExecutionResult, T&gt;</c> the generator cannot invent.
    /// This is the one shape that declares an HTTP address and does not get one, and it
    /// is worth a test precisely because the absence is otherwise invisible.
    /// </remarks>
    [Fact]
    public void AFlowWithNoReturnClauseIsNotMapped()
    {
        var noReturn = Flow.Replace(
            "flow.Step<ValidateOrder>().Return(ctx => ctx.Get<OrderResult>());",
            "flow.Step<ValidateOrder>();",
            StringComparison.Ordinal);

        EndpointsIn(RunOn(noReturn, JsonContext, HttpTransport)).ShouldBeNull(
            "Without a .Return(...) there is no generated Projection for the endpoint to " +
            "write a body with.");
    }

    /// <summary>A durable flow whose poll declares a second way for its wait to end.</summary>
    private const string PollingFlow = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Polled;

        public sealed record ScanRequest(string JobId);
        public sealed record ScanStatus(string JobId, bool Done);
        public sealed record ScanFinished(string JobId);

        [Capability("scan.status", Version = "1.0.0",
            Authorization = Authorization.Internal, Idempotent = true)]
        public sealed class CheckScan : ICapability<ScanRequest, ScanStatus>
        {
            public ValueTask<Result<ScanStatus>> ExecuteAsync(
                ScanRequest input, CapabilityContext ctx, CancellationToken ct) =>
                ValueTask.FromResult(Result.Ok(new ScanStatus(input.JobId, true)));
        }

        [System.Text.Json.Serialization.JsonSerializable(typeof(ScanRequest))]
        [System.Text.Json.Serialization.JsonSerializable(typeof(ScanStatus))]
        [System.Text.Json.Serialization.JsonSerializable(typeof(ScanFinished))]
        public partial class PolledJsonContext : System.Text.Json.Serialization.JsonSerializerContext
        {
        }

        [Flow("scan.run", Version = "1.0.0", Profile = ExecutionProfile.Durable)]
        [HttpTrigger("POST", "/api/v1/scans")]
        public sealed partial class RunScanFlow : Flow<ScanRequest, ScanStatus>
        {
            protected override void Define(IFlowBuilder<ScanRequest, ScanStatus> flow) => flow
                .PollUntil<CheckScan>(
                    until: ctx => ctx.Get<ScanStatus>().Done,
                    interval: Backoff.Exponential("PT5S", "PT5M"),
                    timeout: TimeSpan.FromHours(4))
                    .OrSignal<ScanFinished>()
                .Return(ctx => ctx.Get<ScanStatus>());
        }
        """;

    /// <summary>
    /// A poll's second ending gets the route a suspension point's does.
    /// </summary>
    /// <remarks>
    /// The instance really is parked and a delivery really does continue it, so an address for
    /// it is the same kind of fact as an <c>AwaitSignal</c>'s — and withholding one would leave
    /// the flow declaring a webhook fast path with nowhere for the webhook to arrive.
    /// </remarks>
    [Fact]
    public void APollsSecondEndingGetsTheRouteASuspensionPointsDoes()
    {
        var endpoints = EndpointsIn(RunOn(PollingFlow, HttpTransport))!;

        endpoints.ShouldContainText(
            "\"/api/v1/scans/{instanceId:guid}/signals/scan.finished\",",
            "the identity is a literal segment, exactly as a wait's is.");

        endpoints.ShouldContainText(
            "MapFlowSignal<global::Polled.ScanFinished>(",
            "closed over the contract the poll declared, so the payload is deserialised with " +
            "no reflection.");
    }

    /// <summary>And a poll with one ending publishes no route.</summary>
    /// <remarks>
    /// The <c>202</c> a poll answers with lists no signal for exactly this reason: it is
    /// waiting for a clock and there is nothing to address. A route generated anyway would be
    /// an endpoint that accepts a delivery no step will ever take.
    /// </remarks>
    [Fact]
    public void APollWithOneEndingPublishesNoSignalRoute() =>
        EndpointsIn(RunOn(
                PollingFlow.Replace(
                    "        .OrSignal<ScanFinished>()\n", string.Empty, StringComparison.Ordinal),
                HttpTransport))!
            .ShouldNotContainText(
                "signals/",
                "nothing is waiting for a delivery, so nothing advertises an address for one.");
}
