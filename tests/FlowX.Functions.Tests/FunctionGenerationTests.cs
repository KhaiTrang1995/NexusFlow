using Shouldly;
using Xunit;

namespace FlowX.Functions.Tests;

/// <summary>
/// What each declared trigger becomes on a host that scales to zero, asserted as the text that
/// is actually emitted.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Text, and not a shape.</strong> The generated file is C# in the user's assembly and
/// its whole value is that a deployment does not hand-write it, so what a test must pin is the
/// binding a reader would see: the attribute, its arguments, and the one call the body makes. A
/// test asserting "an HTTP function was produced" passes while the route is wrong.
/// </para>
/// <para>
/// <strong>What is deliberately not asserted here.</strong> That the text compiles against the
/// real <c>Microsoft.Azure.Functions.Worker</c> — <c>samples/functions</c> does that, under
/// <c>TreatWarningsAsErrors</c>, which is the only honest proof and cannot be had in a suite
/// that does not reference the SDK.
/// </para>
/// </remarks>
public sealed class FunctionGenerationTests
{
    /// <summary>
    /// A flow with no trigger attribute generates nothing at all.
    /// </summary>
    /// <remarks>
    /// WP-141's first acceptance criterion. Nothing rather than an empty class: an assembly of
    /// manually started flows has no serverless surface, and a type with no members would put a
    /// name in the worker's assembly that means nothing and that a reader would go looking for
    /// the meaning of.
    /// </remarks>
    [Fact]
    public void AFlowWithNoTriggerGeneratesNothing()
    {
        var run = GeneratorHarness.Run(Source("""
            [Flow("orders.place", Version = "1.0.0")]
            public sealed partial class PlaceOrderFlow : Flow<Order, Receipt>
            {
                protected override void Define(IFlowBuilder<Order, Receipt> flow) =>
                    flow.Step<Charge>().Return(ctx => new Receipt("ok"));
            }
            """));

        run.Functions.ShouldBeNull(
            "a flow nobody triggers declares no address, so there is nothing for a platform " +
            "to bind and no file to write.");
    }

    /// <summary>
    /// A project that has not referenced the worker SDK gets no file, whatever it declares.
    /// </summary>
    /// <remarks>
    /// The opt-in, asserted from the outside. This is why the gate is a reference rather than an
    /// MSBuild property: the same source produces a file or does not, decided entirely by
    /// whether the thing the file needs is there.
    /// </remarks>
    [Fact]
    public void AProjectWithoutTheWorkerSdkGeneratesNothing()
    {
        var run = GeneratorHarness.Run(Source(HttpFlow), optedIn: false);

        run.Functions.ShouldBeNull(
            "every attribute the emitted file names lives in the worker SDK, so a project " +
            "without it would be handed a file that cannot compile.");
    }

    /// <summary>An <c>[HttpTrigger]</c> becomes an <c>HttpTrigger</c> binding on the declared route.</summary>
    [Fact]
    public void AnHttpTriggerBecomesAnHttpTriggerOnTheDeclaredRoute()
    {
        var generated = GeneratorHarness.Run(Source(HttpFlow)).Functions.ShouldNotBeNull();

        generated.ShouldContain("""[global::Microsoft.Azure.Functions.Worker.Function("PlaceOrderFlow")]""");
        generated.ShouldContain("""[global::Microsoft.Azure.Functions.Worker.HttpTrigger(""");
        generated.ShouldContain("""        "post",""");

        generated.ShouldContain(
            """        Route = "api/v1/orders")]""",
            customMessage: "the platform's route template has no leading slash, and the " +
            "declared one does — so a route copied verbatim would serve /api/v1/orders at " +
            "//api/v1/orders.");

        generated.ShouldContain(
            ".HttpAsync<global::Sample.Order, global::Sample.Receipt>(",
            customMessage: "both contracts come off Flow<TIn, TOut>, so the entry point " +
            "deserialises and projects the types the flow itself declares.");

        generated.ShouldContain("global::Sample.PlaceOrderFlow.Plan,");
        generated.ShouldContain("global::Sample.SampleJson.Default,");
    }

    /// <summary>
    /// <c>Idempotent = true</c> reaches the entry point, and its absence reaches it too.
    /// </summary>
    /// <remarks>
    /// <strong>Both directions, because only one of them is a promise.</strong> A generated
    /// endpoint that passed <c>false</c> for a trigger declaring <c>Idempotent = true</c> would
    /// advertise deduplication in the manifest and not deduplicate — and
    /// <c>EveryMutatingHttpFlowRequiresAnIdempotencyKey</c> would still be green, because that
    /// gate reads the declaration rather than what serves it.
    /// </remarks>
    [Fact]
    public void TheIdempotencyRuleIsCopiedOntoTheEntryPoint()
    {
        var required = GeneratorHarness.Run(Source("""
            [Flow("orders.place", Version = "1.0.0", Profile = ExecutionProfile.Durable)]
            [HttpTrigger("POST", "/api/v1/orders", Idempotent = true)]
            public sealed partial class PlaceOrderFlow : Flow<Order, Receipt>
            {
                protected override void Define(IFlowBuilder<Order, Receipt> flow) =>
                    flow.Step<Charge>().Return(ctx => new Receipt("ok"));
            }
            """)).Functions.ShouldNotBeNull();

        required.ShouldContain("global::Sample.SampleJson.Default,\n                    true,");

        var open = GeneratorHarness.Run(Source(HttpFlow)).Functions.ShouldNotBeNull();

        open.ShouldContain("global::Sample.SampleJson.Default,\n                    false,");
    }

    /// <summary>A bus trigger becomes a <c>ServiceBusTrigger</c> that settles from the disposition.</summary>
    /// <remarks>
    /// The three settles are the whole of what a push host adds to WP-140's seam, and each is
    /// pinned separately: an entry point that completed a requeue would pass a test asserting
    /// only that <c>BusAsync</c> was called.
    /// </remarks>
    [Fact]
    public void ABusTriggerBecomesAServiceBusTriggerThatSettlesEachDisposition()
    {
        var generated = GeneratorHarness.Run(Source("""
            [Flow("pricing.reprice", Version = "1.0.0", Profile = ExecutionProfile.Durable)]
            [BusTrigger("order.placed", Group = "pricing")]
            public sealed partial class RepriceFlow : Flow<Order, Receipt>
            {
                protected override void Define(IFlowBuilder<Order, Receipt> flow) =>
                    flow.Step<Charge>().Return(ctx => new Receipt("ok"));
            }
            """)).Functions.ShouldNotBeNull();

        generated.ShouldContain("""[global::Microsoft.Azure.Functions.Worker.ServiceBusTrigger(""");
        generated.ShouldContain("""        "order.placed",""");
        generated.ShouldContain("""        "pricing",""");

        generated.ShouldContain(
            "        AutoCompleteMessages = false)]",
            customMessage: "left on, the platform completes a message the moment the function " +
            "returns — including one the runtime said to requeue because nothing journalled " +
            "it, which is the silent loss ADR-0036 exists to prevent.");

        generated.ShouldContain(".BusAsync(");

        generated.ShouldContain("case global::FlowX.Hosting.BusDisposition.DeadLetter:");
        generated.ShouldContain(".DeadLetterMessageAsync(");
        generated.ShouldContain("deadLetterErrorDescription: admission.Reason,");

        generated.ShouldContain("case global::FlowX.Hosting.BusDisposition.Requeue:");
        generated.ShouldContain(".AbandonMessageAsync(message, cancellationToken: cancellationToken)");

        generated.ShouldContain(".CompleteMessageAsync(message, cancellationToken)");
    }

    /// <summary>
    /// A <c>[CronTrigger]</c> becomes a <c>TimerTrigger</c> that runs the schedule pass — and the
    /// declared expression is not copied into it.
    /// </summary>
    /// <remarks>
    /// <strong>The second assertion is the load-bearing one.</strong> A generator that put
    /// <c>0 2 * * *</c> into the <c>TimerTrigger</c> would look right and be wrong twice: the
    /// platform's timer has no field for the declaration's IANA zone, so a Berlin 02:00 would
    /// fire an hour out for half the year; and ADR-0031 derives every occurrence's instance id
    /// from the declared expression, so a firing at the wrong minute is a different occurrence
    /// rather than a late one. The platform says "you may look now" and the declaration decides
    /// what is due.
    /// </remarks>
    [Fact]
    public void ACronTriggerBecomesATimerThatRunsTheScheduleSweep()
    {
        var generated = GeneratorHarness.Run(Source("""
            [Flow("ledger.reconcile", Version = "1.0.0", Profile = ExecutionProfile.Durable)]
            [CronTrigger("0 2 * * *", TimeZone = "Europe/Berlin")]
            public sealed partial class ReconcileFlow : Flow<ScheduledFire, Receipt>
            {
                protected override void Define(IFlowBuilder<ScheduledFire, Receipt> flow) =>
                    flow.Step<Charge>().Return(ctx => new Receipt("ok"));
            }
            """)).Functions.ShouldNotBeNull();

        generated.ShouldContain(
            """[global::Microsoft.Azure.Functions.Worker.TimerTrigger("0 * * * * *")]""");

        generated.ShouldContain("_flowx.ScheduleAsync(cancellationToken).AsTask();");

        generated.ShouldNotContain(
            "0 2 * * *",
            customMessage: "the declared expression must not reach the platform's timer. " +
            "FlowScheduleScan fires it from the model this generator read, and a second copy " +
            "in the deployment would split one schedule into two that never see each other's " +
            "firings (ADR-0031).");

        generated.ShouldNotContain("Europe/Berlin");
    }

    /// <summary>
    /// A <c>[ChangeTrigger]</c> becomes a timer too, because a scaled-to-zero host cannot LISTEN.
    /// </summary>
    /// <remarks>
    /// The one place the binding map is not the kind map. WP-142 accelerates the change feed
    /// with <c>NOTIFY</c>/<c>LISTEN</c> on a dedicated connection; a host that holds no
    /// connection between invocations has only the correctness backstop, and this is it.
    /// </remarks>
    [Fact]
    public void AChangeTriggerBecomesATimerThatRunsTheChangeSweep()
    {
        var generated = GeneratorHarness.Run(Source("""
            [Flow("orders.project", Version = "1.0.0", Profile = ExecutionProfile.Durable)]
            [ChangeTrigger("order.placed", Group = "projection")]
            public sealed partial class ProjectFlow : Flow<Order, Receipt>
            {
                protected override void Define(IFlowBuilder<Order, Receipt> flow) =>
                    flow.Step<Charge>().Return(ctx => new Receipt("ok"));
            }
            """)).Functions.ShouldNotBeNull();

        generated.ShouldContain(
            """[global::Microsoft.Azure.Functions.Worker.TimerTrigger("0 * * * * *")]""");

        generated.ShouldContain("_flowx.ChangeAsync(cancellationToken).AsTask();");
    }

    /// <summary>
    /// A <c>[StreamTrigger]</c> generates the documented fallback rather than silence.
    /// </summary>
    /// <remarks>
    /// WP-141's fourth acceptance criterion, and the shape chosen for it: a comment in the
    /// generated file naming the flow, the kind and the reason. A reader who opens the file
    /// looking for the entry point they expected finds the answer where they are looking. A
    /// diagnostic was the alternative and is wrong — nothing is defective, the manifest
    /// publishes the declaration and the ASP.NET host serves it.
    /// </remarks>
    [Fact]
    public void AStreamTriggerIsRecordedAsUnboundWithItsReason()
    {
        var generated = GeneratorHarness.Run(Source("""
            [Flow("telemetry.aggregate", Version = "1.0.0", Profile = ExecutionProfile.Streaming)]
            [StreamTrigger("device.telemetry", Window = "tumbling:1m")]
            public sealed partial class AggregateFlow : Flow<Order, Receipt>
            {
                protected override void Define(IFlowBuilder<Order, Receipt> flow) =>
                    flow.Step<Charge>().Return(ctx => new Receipt("ok"));
            }
            """)).Functions.ShouldNotBeNull();

        generated.ShouldContain("// Declared, and deliberately not bound here:");
        generated.ShouldContain("//   Stream on 'telemetry.aggregate'");
        generated.ShouldContain("A window is wider than an invocation.");

        generated.ShouldNotContain(
            "EventHubsTrigger",
            customMessage: "binding it would mean a window per batch, which is not the " +
            "declared window, or resident state on a host that keeps none.");
    }

    /// <summary>A flow declaring two triggers gets two entry points, and two distinct names.</summary>
    /// <remarks>
    /// The platform refuses two functions with one name, and it refuses them at deployment —
    /// which is a host that starts and serves neither. Resolved here instead.
    /// </remarks>
    [Fact]
    public void TwoTriggersOnOneFlowGetTwoDistinctlyNamedEntryPoints()
    {
        var generated = GeneratorHarness.Run(Source("""
            [Flow("orders.place", Version = "1.0.0", Profile = ExecutionProfile.Durable)]
            [HttpTrigger("POST", "/api/v1/orders")]
            [BusTrigger("order.placed", Group = "orders")]
            public sealed partial class PlaceOrderFlow : Flow<Order, Receipt>
            {
                protected override void Define(IFlowBuilder<Order, Receipt> flow) =>
                    flow.Step<Charge>().Return(ctx => new Receipt("ok"));
            }
            """)).Functions.ShouldNotBeNull();

        generated.ShouldContain("""Function("PlaceOrderFlow")]""");
        generated.ShouldContain("""Function("PlaceOrderFlow2")]""");
    }

    private const string HttpFlow = """
        [Flow("orders.place", Version = "1.0.0", Profile = ExecutionProfile.Durable)]
        [HttpTrigger("POST", "/api/v1/orders")]
        public sealed partial class PlaceOrderFlow : Flow<Order, Receipt>
        {
            protected override void Define(IFlowBuilder<Order, Receipt> flow) =>
                flow.Step<Charge>().Return(ctx => new Receipt("ok"));
        }
        """;

    /// <summary>One flow, plus the contracts and the capability every flow here needs.</summary>
    private static string Source(string flow) => $$"""
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using System.Text.Json.Serialization;
        using FlowX;

        namespace Sample;

        public sealed record Order(string Sku);

        public sealed record Receipt(string Status);

        [Capability("orders.charge", "1.0.0")]
        public sealed class Charge : ICapability<Order, Receipt>
        {
            public ValueTask<Result<Receipt>> ExecuteAsync(Order input, CapabilityContext context, CancellationToken ct) =>
                new(Result<Receipt>.Success(new Receipt("ok")));
        }

        [JsonSerializable(typeof(Order))]
        [JsonSerializable(typeof(Receipt))]
        public sealed partial class SampleJson : JsonSerializerContext
        {
        }

        {{flow}}
        """;
}
