using System;
using System.Linq;
using FlowX.Compiler.Analysis;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// FLOWX1049: a <c>[StreamTrigger]</c> whose <c>Lateness</c>, <c>Checkpoint</c> or
/// <c>Parallelism</c> the host would refuse.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ScheduleJitterAnalyzerTests"/>'s arrangement, one transport over, with one thing
/// that file cannot do: <see cref="TheRuleAgreesWithTheHostOnEveryValue"/> runs the analyzer and
/// <c>StreamWindowSpec.Read</c> over the same values and requires the same verdict. The analyzer
/// re-implements the parse because <c>FlowX.Compiler</c> is netstandard2.0 and references no
/// runtime assembly, so "two parsers to keep in step" is the risk this rule takes on — and it is
/// only acceptable while something checks.
/// </para>
/// <para>
/// Separate from <see cref="StreamGenerationTests"/> because that file is about what a
/// declaration <em>becomes</em> — a registration, a manifest entry — and every fixture in it is
/// deliberately one the host accepts.
/// </para>
/// </remarks>
public sealed class StreamWindowArgumentAnalyzerTests
{
    private const string Preamble = """
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record DeviceStats(int Count);

        [Capability("telemetry.fold", Version = "1.0.0", Authorization = Authorization.Internal)]
        public sealed class FoldReadings : ICapability<StreamWindowBatch, DeviceStats>
        {
            public ValueTask<Result<DeviceStats>> ExecuteAsync(
                StreamWindowBatch input, CapabilityContext ctx, CancellationToken ct) =>
                ValueTask.FromResult(Result.Ok(new DeviceStats(input.Records.Count)));
        }
        """;

    /// <summary>A windowable stream flow carrying the declarations under test.</summary>
    private static string FlowWith(string attributes) =>
        Preamble + "\n\n" + $$"""
        [Flow("telemetry.aggregate", Version = "1.0.0", Profile = ExecutionProfile.Streaming)]
        {{attributes}}
        public sealed partial class AggregateTelemetryFlow : Flow<StreamWindowBatch, DeviceStats>
        {
            protected override void Define(IFlowBuilder<StreamWindowBatch, DeviceStats> flow) =>
                flow.Step<FoldReadings>().Return(ctx => ctx.Get<DeviceStats>());
        }
        """;

    /// <summary>One <c>[StreamTrigger]</c> over a tumbling window, with the arguments given.</summary>
    private static string TriggerWith(string arguments) =>
        $"[StreamTrigger(\"device.telemetry\", Window = \"tumbling:1m\"{arguments})]";

    private static string[] Analyze(string source)
    {
        GeneratorHarness.CompileErrorsIn(source).ShouldBeEmpty();

        return GeneratorHarness.Analyze(source, new TriggerDeclarationAnalyzer());
    }

    // ------------------------------------------------------------- it must fire

    /// <summary>Every value the host would refuse is reported, each for its own reason.</summary>
    /// <remarks>
    /// The near-misses an author actually writes. <c>10s</c> is the one worth having in the list:
    /// it is the short form <c>Window</c> takes on the same attribute, and docs/09 §9 printed it.
    /// </remarks>
    [Theory]
    [InlineData(", Lateness = \"10s\"")]
    [InlineData(", Lateness = \"\"")]
    [InlineData(", Lateness = \"-PT10S\"")]
    [InlineData(", Lateness = \"00:00:10\"")]
    [InlineData(", Checkpoint = \"5s\"")]
    [InlineData(", Checkpoint = \"-PT5S\"")]
    [InlineData(", Parallelism = 0")]
    [InlineData(", Parallelism = -1")]
    public void AnArgumentTheHostWouldRefuseIsReported(string arguments)
    {
        Analyze(FlowWith(TriggerWith(arguments))).ShouldBe(["FLOWX1049"]);
    }

    /// <summary>The message names the flow, the property and the value it carried.</summary>
    [Fact]
    public void TheReportNamesTheFlowAndTheOffendingDeclaration()
    {
        var message = GeneratorHarness
            .AnalyzeWithMessages(
                FlowWith(TriggerWith(", Lateness = \"10s\"")),
                new TriggerDeclarationAnalyzer())
            .ShouldHaveSingleItem();

        message.ShouldContain("telemetry.aggregate");
        message.ShouldContain("Lateness = \"10s\"");
        message.ShouldContain("ISO-8601");
    }

    /// <summary>
    /// A flow with two streams is reported on the one that is wrong, and only that one.
    /// </summary>
    /// <remarks>
    /// The reason this is a rule of its own rather than a widened <c>FLOWX1042</c>: two of that
    /// rule's three reasons are properties of the flow, so every stream on the flow is
    /// unwindowable together. These three are properties of one declaration, and a suppression
    /// written against the broken line must not silence the line beside it.
    /// </remarks>
    [Fact]
    public void OnlyTheDeclarationWithTheBadArgumentIsReported()
    {
        GeneratorHarness
            .AnalyzeWithMessages(
                FlowWith(
                    TriggerWith(", Lateness = \"PT10S\"") + "\n" +
                    "[StreamTrigger(\"device.audit\", Window = \"tumbling:5m\", Lateness = \"10s\")]"),
                new TriggerDeclarationAnalyzer())
            .Where(static m => m.StartsWith("FLOWX1049", StringComparison.Ordinal))
            .ShouldHaveSingleItem()
            .ShouldContain("Lateness = \"10s\"");
    }

    /// <summary>
    /// An unreadable argument is reported beside FLOWX1042, not instead of it.
    /// </summary>
    /// <remarks>
    /// Two independent defects with two independent fixes. Suppressing one because the other is
    /// also present would mean an author repairs the profile, rebuilds, and discovers a second
    /// error they could have seen the first time.
    /// </remarks>
    [Fact]
    public void AnUnwindowableFlowIsStillToldItsArgumentsCannotBeRead()
    {
        var source = FlowWith(TriggerWith(", Lateness = \"10s\""))
            .Replace(
                "Profile = ExecutionProfile.Streaming",
                "Profile = ExecutionProfile.Durable",
                StringComparison.Ordinal);

        Analyze(source).ShouldBe(["FLOWX1042", "FLOWX1049"], ignoreOrder: true);
    }

    // --------------------------------------------------------- it must not fire

    /// <summary>The declaration that omits all three is the ordinary one and is silent.</summary>
    [Fact]
    public void ADeclarationThatOmitsTheArgumentsIsSilent()
    {
        Analyze(FlowWith(TriggerWith(string.Empty))).ShouldBeEmpty(
            "All three properties carry a default on the attribute, and a rule that fired on " +
            "the shortest declaration would be suppressed everywhere and would protect nothing.");
    }

    /// <summary>Every value the host accepts is silent, including both zeroes.</summary>
    /// <remarks>
    /// <c>PT0S</c> is where this rule and <c>FLOWX1045</c> part company, and deliberately: a
    /// jitter of zero asks for a spread and gets none, where a lateness of zero asks for no
    /// out-of-order allowance and gets none — which is the ordinary declaration for a stream
    /// that is already in order.
    /// </remarks>
    [Theory]
    [InlineData(", Lateness = \"PT0S\"")]
    [InlineData(", Lateness = \"PT10S\"")]
    [InlineData(", Lateness = \"PT1M30S\"")]
    [InlineData(", Checkpoint = \"PT0S\"")]
    [InlineData(", Checkpoint = \"PT5S\"")]
    [InlineData(", Parallelism = 1")]
    [InlineData(", Lateness = \"PT10S\", Checkpoint = \"PT5S\", Parallelism = 8")]
    public void AnArgumentTheHostAcceptsIsSilent(string arguments)
    {
        Analyze(FlowWith(TriggerWith(arguments))).ShouldBeEmpty();
    }

    // ------------------------------------------------- it must agree with the host

    /// <summary>
    /// The analyzer and <c>StreamWindowSpec.Read</c> reach the same verdict on every value.
    /// </summary>
    /// <remarks>
    /// <strong>This is the load-bearing one.</strong> <c>FlowX.Compiler</c> is netstandard2.0 and
    /// cannot call the runtime's reader, so the rule parses the two durations itself. That is the
    /// arrangement <c>FLOWX1042</c>'s own remarks refused for the <em>window</em> — its short form
    /// has no framework parser, so a second implementation would drift. These two have one:
    /// <c>XmlConvert.ToTimeSpan</c> is what both sides call. This test is what says so.
    /// </remarks>
    [Theory]
    [InlineData("10s", "PT5S", 8)]
    [InlineData("", "PT5S", 8)]
    [InlineData("-PT10S", "PT5S", 8)]
    [InlineData("00:00:10", "PT5S", 8)]
    [InlineData("PT10S", "5s", 8)]
    [InlineData("PT10S", "-PT5S", 8)]
    [InlineData("PT10S", "PT5S", 0)]
    [InlineData("PT10S", "PT5S", -1)]
    [InlineData("PT0S", "PT0S", 1)]
    [InlineData("PT10S", "PT5S", 8)]
    [InlineData("PT1M30S", "P1D", 64)]
    public void TheRuleAgreesWithTheHostOnEveryValue(string lateness, string checkpoint, int parallelism)
    {
        var declaration =
            $", Lateness = \"{lateness}\", Checkpoint = \"{checkpoint}\", Parallelism = {parallelism}";

        var reported = Analyze(FlowWith(TriggerWith(declaration))).Contains("FLOWX1049");
        var refused = StreamWindowSpec.Read("tumbling:1m", lateness, checkpoint, parallelism).IsFailure;

        reported.ShouldBe(
            refused,
            $"FLOWX1049 {(reported ? "reported" : "said nothing about")} Lateness = \"{lateness}\", " +
            $"Checkpoint = \"{checkpoint}\", Parallelism = {parallelism}, and " +
            $"StreamWindowSpec.Read {(refused ? "refuses" : "accepts")} it. The analyzer parses " +
            "these itself because FlowX.Compiler is netstandard2.0; the two must not diverge.");
    }
}
