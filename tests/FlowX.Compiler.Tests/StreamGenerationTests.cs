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

    /// <summary>A window's flow may wait, because a window's flow is journaled.</summary>
    /// <remarks>
    /// <para>
    /// <strong>FLOWX1017 asked "is this <c>Durable</c>?" and meant "does this journal?".</strong>
    /// Every clause of the rule — an in-memory wait not surviving a deployment, a timer with
    /// nowhere to record when it is due, a poll with no way to count the attempts already made —
    /// names a journal as the thing that is missing, and <c>Streaming</c> has one. So the flow
    /// below was refused at build time for a reason that was not true of it, and the message it
    /// got named its own profile back at it with a fix it could not take: a stream-triggered flow
    /// that declares <c>Durable</c> instead is <c>FLOWX1042</c>.
    /// </para>
    /// <para>
    /// Nothing downstream had to change to allow it. <c>FlowStreamScan</c> already classifies a
    /// suspended window's flow as started and moves the checkpoint past it, <c>FlowTimerScan</c>
    /// and <c>FlowHost.SignalAsync</c> resume by instance id and read no profile, and the engine
    /// asks <c>ExecutionProfiles.IsJournaled</c> in the single place it reads one at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void AStreamingFlowMayDelayBecauseItsInstancesAreJournaled()
    {
        var source = Windowing.Replace(
            "flow.Step<Aggregate>().Return",
            "flow.Step<Aggregate>().Delay(System.TimeSpan.FromMinutes(5)).Return",
            StringComparison.Ordinal);

        var run = RunOn(source, HostingStub);

        run.Ids.ShouldNotContain("FLOWX1017", run.Describe());

        run.Plan.ShouldContainText(
            "StepNode.ForDelay(1, System.TimeSpan.FromMinutes(5))",
            "and the wait is laid out, so the refusal was not standing in for a gap in the plan.");
    }

    /// <summary>
    /// A window's saga is silent under FLOWX1012, and <c>Durable</c> — the profile that rule's
    /// message prescribes — is the one this flow may not declare.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Both halves in one test, because either alone understates it.</strong> The rule
    /// promised that a compensation pending across a node death is lost, and for a window it is
    /// not: the instance is journaled (<c>OpenJournal</c> asks
    /// <c>ExecutionProfiles.IsJournaled</c>), <c>PostgresRecoveryIndex</c> lists a
    /// <c>Compensating</c> row without reading a profile,
    /// <c>FlowStreamSubscriptionRegistration.Add</c> puts the plan in <c>FlowCatalog</c> so
    /// <c>FlowRecoveryScan</c> can resume it, and the skip in the step loop puts a completed
    /// compensable step back on the unwind stack off <c>cursor.IsJournaled</c>. The window a
    /// surviving node rebuilds does not re-enter that half-run unwind either — its derived id
    /// meets the journal's primary key and <c>FlowStreamScan.DispositionFor</c> deduplicates it
    /// (<c>ADR-0055</c>).
    /// </para>
    /// <para>
    /// And the second half is why the first is not a nicety: an author who took the rule's
    /// advice would have lost their subscription. <c>Durable</c> here is <c>FLOWX1042</c>, emits
    /// no registration, and is refused by <c>FlowStreamCatalog.Add</c> at start-up.
    /// </para>
    /// </remarks>
    [Fact]
    public void AStreamingSagaIsSilentAndDurableWouldNotHaveBeenItsFix()
    {
        var saga = Windowing
            .Replace(
                "public sealed record DeviceStats(int Count);",
                """
                public sealed record DeviceStats(int Count);

                [Capability("telemetry.retract", Version = "1.0.0", Authorization = Authorization.Internal, Idempotent = true)]
                public sealed class Retract : ICapability<DeviceStats, DeviceStats>
                {
                    public ValueTask<Result<DeviceStats>> ExecuteAsync(
                        DeviceStats input, CapabilityContext ctx, CancellationToken ct) =>
                        ValueTask.FromResult(Result.Ok(input));
                }
                """,
                StringComparison.Ordinal)
            .Replace(
                "flow.Step<Aggregate>().Return",
                "flow.Step<Aggregate>().CompensateWith<Retract>().Return",
                StringComparison.Ordinal);

        GeneratorHarness.CompileErrorsIn(saga).ShouldBeEmpty();

        GeneratorHarness.Analyze(saga, new CompensationDurabilityAnalyzer()).ShouldBeEmpty(
            "A window's flow journals its step boundaries and a recovery sweep rebuilds its " +
            "unwind stack, so there is no pending compensation for a node death to take.");

        var durable = saga.Replace(
            "Profile = ExecutionProfile.Streaming",
            "Profile = ExecutionProfile.Durable",
            StringComparison.Ordinal);

        GeneratorHarness.Analyze(durable, new TriggerDeclarationAnalyzer()).ShouldContain(
            "FLOWX1042",
            "and the edit FLOWX1012 used to ask for is the one the stream refuses.");
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
