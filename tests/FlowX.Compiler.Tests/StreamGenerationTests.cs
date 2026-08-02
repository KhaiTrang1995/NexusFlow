using System;
using System.Linq;
using System.Text.Json;
using FlowX.Compiler.Analysis;
using FlowX.Compiler.Emit;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// Whether a <c>[StreamTrigger]</c> becomes a registration, what the registration carries that
/// the manifest does not, and which declarations are refused.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ChangeGenerationTests"/>'s arrangement, one transport over, with one thing to prove
/// that no other transport has: a stream registration carries the <em>window</em>, and the
/// manifest deliberately does not. <c>TriggerModel</c>'s own remarks name a stream trigger's
/// <c>Checkpoint</c> as the example of tuning that stays out of a published contract, so
/// <see cref="TheWindowReachesTheRegistrationAndNotTheManifest"/> pins both halves of that.
/// </para>
/// <para>
/// <strong>The host is a stub declared in the test's own source</strong>: the generator looks
/// <c>FlowX.Hosting.FlowStreamSubscriptionRegistration</c> up by name and links against nothing.
/// </para>
/// </remarks>
public sealed class StreamGenerationTests
{
    /// <summary>Stands in for the hosting assembly the user's project would reference.</summary>
    private const string HostingStub = """
        namespace FlowX.Hosting
        {
            public static class FlowStreamSubscriptionRegistration
            {
            }
        }
        """;

    /// <summary>The declaration a test removes to make the same flow unbound.</summary>
    private const string StreamTriggerLine =
        "[StreamTrigger(\"device.telemetry\", Window = \"tumbling:1m\", " +
        "Lateness = \"PT10S\", Checkpoint = \"PT5S\", Parallelism = 4)]";

    private const string Windowing = """
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record DeviceStats(int Count);

        [Capability("telemetry.aggregate", Version = "1.0.0", Authorization = Authorization.Internal)]
        public sealed class Aggregate : ICapability<StreamWindowBatch, DeviceStats>
        {
            public ValueTask<Result<DeviceStats>> ExecuteAsync(
                StreamWindowBatch input, CapabilityContext ctx, CancellationToken ct) =>
                ValueTask.FromResult(Result.Ok(new DeviceStats(input.Records.Count)));
        }

        [Flow("telemetry.aggregate", Version = "1.0.0", Profile = ExecutionProfile.Streaming)]
        [StreamTrigger("device.telemetry", Window = "tumbling:1m", Lateness = "PT10S", Checkpoint = "PT5S", Parallelism = 4)]
        public sealed partial class AggregateTelemetryFlow : Flow<StreamWindowBatch, DeviceStats>
        {
            protected override void Define(IFlowBuilder<StreamWindowBatch, DeviceStats> flow) =>
                flow.Step<Aggregate>().Return(ctx => ctx.Get<DeviceStats>());
        }
        """;

    private static GeneratorRun RunOn(params string[] sources) => GeneratorHarness.Run(
        GeneratorHarness.CompilationOf(
            [.. sources.Select((source, i) => ($"/src/File{i}.cs", source))]));

    private static string? SubscriptionsIn(GeneratorRun run) => run.Sources
        .Where(static s => s.HintName == StreamEmitter.FileName)
        .Select(static s => s.Source)
        .FirstOrDefault();

    /// <summary>A project that does not reference the host pays nothing for this.</summary>
    [Fact]
    public void AProjectWithNoHostGetsNoStreamSubscriptionFile()
    {
        SubscriptionsIn(RunOn(Windowing)).ShouldBeNull();
    }

    /// <summary>
    /// <c>Stream</c> is bound: a declared trigger becomes a registration nobody wrote.
    /// </summary>
    /// <remarks>
    /// The last of the eight trigger kinds to reach a running transport. Until P7 the attribute
    /// published its source into the manifest and reached nothing at all.
    /// </remarks>
    [Fact]
    public void ADeclaredStreamTriggerBecomesARegistration()
    {
        var generated = SubscriptionsIn(RunOn(Windowing, HostingStub));

        generated.ShouldNotBeNull();
        generated.ShouldContain("AddFlowXStreamSubscriptions");
        generated.ShouldContain("\"device.telemetry\"");
        generated.ShouldContain("global::Sample.AggregateTelemetryFlow.Plan");
    }

    /// <summary>
    /// The window reaches the registration and the manifest is unchanged by it.
    /// </summary>
    /// <remarks>
    /// Both halves in one test, because either alone would pass against the wrong design: a
    /// window in the manifest would be operational tuning in a published contract, and a window
    /// in neither place would be a declaration the engine could not honour.
    /// </remarks>
    [Fact]
    public void TheWindowReachesTheRegistrationAndNotTheManifest()
    {
        var run = RunOn(Windowing, HostingStub);

        var trigger = JsonDocument.Parse(run.ManifestJson!)
            .RootElement.GetProperty("flows")[0]
            .GetProperty("triggers")[0];

        trigger.GetProperty("kind").GetString().ShouldBe("Stream");
        trigger.GetProperty("topic").GetString().ShouldBe("device.telemetry");
        trigger.TryGetProperty("window", out _).ShouldBeFalse();
        trigger.TryGetProperty("checkpoint", out _).ShouldBeFalse();

        var generated = SubscriptionsIn(run).ShouldNotBeNull();

        generated.ShouldContain("\"tumbling:1m\"");
        generated.ShouldContain("\"PT10S\"");
        generated.ShouldContain("\"PT5S\"");
        generated.ShouldContain("4);");
    }

    /// <summary>The attribute's own defaults are what an omitted argument generates.</summary>
    [Fact]
    public void AnOmittedArgumentGeneratesTheAttributesDefault()
    {
        var source = Windowing.Replace(
            ", Lateness = \"PT10S\", Checkpoint = \"PT5S\", Parallelism = 4",
            string.Empty,
            StringComparison.Ordinal);

        var generated = SubscriptionsIn(RunOn(source, HostingStub)).ShouldNotBeNull();

        generated.ShouldContain("\"PT0S\"");
        generated.ShouldContain("\"PT5S\"");
        generated.ShouldContain("1);");
    }

    /// <summary>A stream flow whose input is not a window batch is reported, not skipped.</summary>
    [Fact]
    public void AStreamFlowThatDoesNotBindAWindowIsReported()
    {
        var source = Windowing
            .Replace(
                "ICapability<StreamWindowBatch, DeviceStats>",
                "ICapability<DeviceStats, DeviceStats>",
                StringComparison.Ordinal)
            .Replace(
                "StreamWindowBatch input, CapabilityContext ctx",
                "DeviceStats input, CapabilityContext ctx",
                StringComparison.Ordinal)
            .Replace(
                "Flow<StreamWindowBatch, DeviceStats>",
                "Flow<DeviceStats, DeviceStats>",
                StringComparison.Ordinal)
            .Replace(
                "IFlowBuilder<StreamWindowBatch, DeviceStats>",
                "IFlowBuilder<DeviceStats, DeviceStats>",
                StringComparison.Ordinal);

        GeneratorHarness.Analyze(source, new TriggerDeclarationAnalyzer()).ShouldContain("FLOWX1042");
        SubscriptionsIn(RunOn(source, HostingStub))
            .ShouldBeNull("a flow that cannot bind a window gets no registration");
    }

    /// <summary>A stream flow that is not <c>Streaming</c> is reported for the second reason.</summary>
    [Fact]
    public void AStreamFlowThatIsNotStreamingIsReported()
    {
        var source = Windowing.Replace(
            "Profile = ExecutionProfile.Streaming", "Profile = ExecutionProfile.Durable",
            StringComparison.Ordinal);

        GeneratorHarness.Analyze(source, new TriggerDeclarationAnalyzer()).ShouldContain("FLOWX1042");
        SubscriptionsIn(RunOn(source, HostingStub)).ShouldBeNull();
    }

    /// <summary>Every window shape but tumbling is reported, for the third reason.</summary>
    [Theory]
    [InlineData("sliding:1m:30s")]
    [InlineData("session:5m")]
    [InlineData("global")]
    public void AWindowShapeTheEngineDoesNotImplementIsReported(string window)
    {
        var source = Windowing.Replace(
            "Window = \"tumbling:1m\"",
            "Window = \"" + window + "\"",
            StringComparison.Ordinal);

        GeneratorHarness.Analyze(source, new TriggerDeclarationAnalyzer()).ShouldContain("FLOWX1042");
        SubscriptionsIn(RunOn(source, HostingStub)).ShouldBeNull();
    }

    /// <summary>
    /// A flow bound to a stream is no longer told its profile is unhonoured.
    /// </summary>
    /// <remarks>
    /// <strong>FLOWX1028's remaining half, narrowed rather than deleted.</strong> P7 makes
    /// <c>Streaming</c> mean something — for a flow a stream actually starts. A flow that declares
    /// the profile and no <c>[StreamTrigger]</c> still gets nothing the profile promises, so the
    /// rule keeps that case rather than handing it the silence it was written to close.
    /// </remarks>
    [Fact]
    public void AStreamBoundFlowIsNoLongerToldItsProfileIsUnhonoured()
    {
        GeneratorHarness.Analyze(Windowing, new ExecutionProfileAnalyzer())
            .ShouldNotContain("FLOWX1028");

        var unbound = Windowing.Replace(
            StreamTriggerLine, string.Empty, StringComparison.Ordinal);

        GeneratorHarness.Analyze(unbound, new ExecutionProfileAnalyzer()).ShouldContain("FLOWX1028");
    }

    /// <summary>A flow with no stream trigger produces no registration and no rule.</summary>
    [Fact]
    public void AFlowWithNoStreamTriggerIsUntouched()
    {
        var source = Windowing
            .Replace(StreamTriggerLine, string.Empty, StringComparison.Ordinal)
            .Replace(
                "Profile = ExecutionProfile.Streaming", "Profile = ExecutionProfile.Durable",
                StringComparison.Ordinal);

        GeneratorHarness.Analyze(source, new TriggerDeclarationAnalyzer()).ShouldNotContain("FLOWX1042");
        SubscriptionsIn(RunOn(source, HostingStub)).ShouldBeNull();
    }
}
