using System.Linq;
using FlowX.Compiler.Analysis;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// FLOWX1045: a <c>[CronTrigger]</c> whose <c>Jitter</c> the host would refuse.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The silent half is the half worth having, and it is most of this file.</strong> The
/// value is optional, so the overwhelmingly common declaration carries none — and a rule that
/// fired on a schedule with no jitter would be suppressed in every repository that uses cron
/// and would then protect nothing.
/// </para>
/// <para>
/// Separate from <see cref="TriggerDeclarationAnalyzerTests"/> because the flow has to be
/// fireable for these assertions to mean anything: that file's fixture is
/// <c>Flow&lt;PlaceOrder, OrderResult&gt;</c>, so every <c>[CronTrigger]</c> on it raises
/// FLOWX1038 as well and a test about the jitter would be reading around the other diagnostic.
/// </para>
/// </remarks>
public sealed class ScheduleJitterAnalyzerTests
{
    private const string Preamble = """
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record ReconciliationDone(string Id);

        [Capability("ledger.reconcile", Version = "1.0.0", Authorization = Authorization.Internal)]
        public sealed class Reconcile : ICapability<ScheduledFire, ReconciliationDone>
        {
            public ValueTask<Result<ReconciliationDone>> ExecuteAsync(
                ScheduledFire input, CapabilityContext ctx, CancellationToken ct) =>
                ValueTask.FromResult(Result.Ok(new ReconciliationDone("r")));
        }
        """;

    /// <summary>A fireable scheduled flow carrying the declarations under test.</summary>
    private static string FlowWith(string attributes) =>
        Preamble + "\n\n" + $$"""
        [Flow("ledger.reconcile", Version = "1.0.0", Profile = ExecutionProfile.Durable)]
        {{attributes}}
        public sealed partial class ReconcileLedgerFlow : Flow<ScheduledFire, ReconciliationDone>
        {
            protected override void Define(IFlowBuilder<ScheduledFire, ReconciliationDone> flow) =>
                flow.Step<Reconcile>().Return(ctx => ctx.Get<ReconciliationDone>());
        }
        """;

    private static string[] Analyze(string source)
    {
        GeneratorHarness.CompileErrorsIn(source).ShouldBeEmpty();

        return GeneratorHarness.Analyze(source, new TriggerDeclarationAnalyzer());
    }

    // ------------------------------------------------------------- it must fire

    /// <summary>Every spread the host would refuse is reported, and each for its own reason.</summary>
    /// <remarks>
    /// The four cases are the four <c>ScheduleJitter.Read</c> answers with a failure in them:
    /// not a duration at all, the two near-misses an author actually writes, and a value that
    /// parses and spreads nothing. <c>PT0S</c> is the one worth having in the list — it reads as
    /// working, which is the property that gets it through review.
    /// </remarks>
    [Theory]
    [InlineData("\"120s\"")]
    [InlineData("\"2m\"")]
    [InlineData("\"\"")]
    [InlineData("\"PT0S\"")]
    [InlineData("\"-PT2M\"")]
    public void AJitterTheHostWouldRefuseIsReported(string jitter)
    {
        Analyze(FlowWith($"[CronTrigger(\"0 2 * * *\", Jitter = {jitter})]"))
            .ShouldBe(["FLOWX1045"]);
    }

    /// <summary>The message names the flow and the value, because both are needed to find it.</summary>
    [Fact]
    public void TheReportNamesTheFlowAndTheOffendingValue()
    {
        var message = GeneratorHarness
            .AnalyzeWithMessages(
                FlowWith("""[CronTrigger("0 2 * * *", Jitter = "120s")]"""),
                new TriggerDeclarationAnalyzer())
            .ShouldHaveSingleItem();

        message.ShouldContain("ledger.reconcile");
        message.ShouldContain("120s");
        message.ShouldContain("ISO-8601");
    }

    /// <summary>
    /// A flow with two schedules is reported on the one that is wrong, and only that one.
    /// </summary>
    /// <remarks>
    /// The reason this is a rule of its own rather than a widened <c>FLOWX1038</c>. That one is
    /// a property of the flow, so every schedule on the flow is unfireable together; a spread is
    /// a property of one declaration, and a suppression written against the broken line must not
    /// silence the line beside it.
    /// </remarks>
    [Fact]
    public void OnlyTheDeclarationWithTheBadSpreadIsReported()
    {
        GeneratorHarness
            .AnalyzeWithMessages(
                FlowWith(
                    """
                    [CronTrigger("0 2 * * *", Jitter = "PT120S")]
                    [CronTrigger("0 3 * * *", Jitter = "PT0S")]
                    """),
                new TriggerDeclarationAnalyzer())
            .Where(static m => m.StartsWith("FLOWX1045", System.StringComparison.Ordinal))
            .ShouldHaveSingleItem()
            .ShouldContain("PT0S");
    }

    // --------------------------------------------------------- it must not fire

    /// <summary>A schedule that declares no spread is the ordinary declaration and is silent.</summary>
    [Fact]
    public void AScheduleWithNoJitterIsSilent()
    {
        Analyze(FlowWith("""[CronTrigger("0 2 * * *", TimeZone = "Europe/Berlin")]"""))
            .ShouldBeEmpty(
                "Jitter is optional and most schedules carry none. A rule that fired here " +
                "would be suppressed everywhere and would then protect nothing.");
    }

    /// <summary>Every spelling the host accepts is silent.</summary>
    [Theory]
    [InlineData("\"PT30S\"")]
    [InlineData("\"PT2M\"")]
    [InlineData("\"PT1H\"")]
    [InlineData("\"PT1M30S\"")]
    [InlineData("\"P1D\"")]
    public void AReadableSpreadIsSilent(string jitter)
    {
        Analyze(FlowWith($"[CronTrigger(\"0 2 * * *\", Jitter = {jitter})]")).ShouldBeEmpty();
    }

    /// <summary>
    /// A spread wider than the gap between two occurrences is not this rule's business.
    /// </summary>
    /// <remarks>
    /// <c>PT90M</c> on an hourly schedule produces firings that can overtake each other, which is
    /// a design decision rather than a defect — <c>OverlapPolicy</c> is the declaration that says
    /// what happens when two runs meet. Answering it here would need a cron evaluator inside the
    /// analyzer, which is the shape of check that starts disagreeing with the runtime the moment
    /// either changes.
    /// </remarks>
    [Fact]
    public void ASpreadWiderThanTheIntervalIsNotReported()
    {
        Analyze(FlowWith("""[CronTrigger("0 * * * *", Jitter = "PT90M")]""")).ShouldBeEmpty();
    }
}
