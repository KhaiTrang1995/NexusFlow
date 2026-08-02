using System;
using System.Linq;
using System.Text.Json;
using FlowX.Compiler.Analysis;
using FlowX.Compiler.Emit;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// Whether a <c>[ChangeTrigger]</c> becomes a registration, and whether that registration says
/// what the manifest says.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ScheduleGenerationTests"/>'s arrangement, one transport over. One reading of the
/// attribute produces the manifest's <c>triggers</c> block and the generated registration, so
/// there is no second copy of the observed type to drift — and that string is one of the four the
/// instance id is derived from
/// (<a href="../../docs/adr/ADR-0049-a-change-names-the-instance-it-starts.md">ADR-0049</a>) and
/// one of the four the durable cursor is keyed on, so a drift would start one flow twice and read
/// another subscription's position.
/// </para>
/// <para>
/// <strong>The host is a stub declared in the test's own source</strong>: the generator looks
/// <c>FlowX.Hosting.FlowChangeSubscriptionRegistration</c> up by name in whatever compilation it
/// is given, and links against nothing.
/// </para>
/// </remarks>
public sealed class ChangeGenerationTests
{
    /// <summary>Stands in for the hosting assembly the user's project would reference.</summary>
    private const string HostingStub = """
        namespace FlowX.Hosting
        {
            public static class FlowChangeSubscriptionRegistration
            {
            }
        }
        """;

    private const string Observing = """
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record OrderProjection(string Id);

        [Capability("orders.project", Version = "1.0.0", Authorization = Authorization.Internal)]
        public sealed class Project : ICapability<BusMessage, OrderProjection>
        {
            public ValueTask<Result<OrderProjection>> ExecuteAsync(
                BusMessage input, CapabilityContext ctx, CancellationToken ct) =>
                ValueTask.FromResult(Result.Ok(new OrderProjection("p")));
        }

        [Flow("orders.project", Version = "1.0.0", Profile = ExecutionProfile.Durable)]
        [ChangeTrigger("order.placed", Group = "projection")]
        public sealed partial class ProjectOrderFlow : Flow<BusMessage, OrderProjection>
        {
            protected override void Define(IFlowBuilder<BusMessage, OrderProjection> flow) =>
                flow.Step<Project>().Return(ctx => ctx.Get<OrderProjection>());
        }
        """;

    private static GeneratorRun RunOn(params string[] sources) => GeneratorHarness.Run(
        GeneratorHarness.CompilationOf(
            [.. sources.Select((source, i) => ($"/src/File{i}.cs", source))]));

    private static string? SubscriptionsIn(GeneratorRun run) => run.Sources
        .Where(static s => s.HintName == ChangeEmitter.FileName)
        .Select(static s => s.Source)
        .FirstOrDefault();

    /// <summary>A project that does not reference the host pays nothing for this.</summary>
    [Fact]
    public void AProjectWithNoHostGetsNoChangeSubscriptionFile()
    {
        SubscriptionsIn(RunOn(Observing)).ShouldBeNull();
    }

    /// <summary>A flow that declares a change trigger gets a registration it did not write.</summary>
    [Fact]
    public void ADeclaredChangeTriggerBecomesARegistration()
    {
        var generated = SubscriptionsIn(RunOn(Observing, HostingStub));

        generated.ShouldNotBeNull();
        generated.ShouldContain("AddFlowXChangeSubscriptions");
        generated.ShouldContain("\"order.placed\"");
        generated.ShouldContain("\"projection\"");
        generated.ShouldContain("global::Sample.ProjectOrderFlow.Plan");
    }

    /// <summary>
    /// The source and group the registration carries are the ones the manifest published.
    /// </summary>
    /// <remarks>
    /// Both come out of one <c>TriggerReader.Read</c>, so this asserts that the arrangement
    /// survived rather than that two writers agree. It also pins the manifest shape ADR-0047
    /// decision 4 settles: a change subscription publishes through <c>topic</c> and <c>group</c>
    /// and adds no property to a schema that is <c>additionalProperties: false</c>.
    /// </remarks>
    [Fact]
    public void TheRegisteredAddressIsTheOneTheManifestPublished()
    {
        var run = RunOn(Observing, HostingStub);

        var trigger = JsonDocument.Parse(run.ManifestJson!)
            .RootElement.GetProperty("flows")[0]
            .GetProperty("triggers")[0];

        trigger.GetProperty("kind").GetString().ShouldBe("Change");

        var generated = SubscriptionsIn(run).ShouldNotBeNull();

        generated.ShouldContain("\"" + trigger.GetProperty("topic").GetString() + "\"");
        generated.ShouldContain("\"" + trigger.GetProperty("group").GetString() + "\"");
    }

    /// <summary>
    /// A change-triggered flow whose input is not <c>BusMessage</c> is reported, not skipped.
    /// </summary>
    [Fact]
    public void AChangeFlowThatDoesNotBindTheChangeIsReported()
    {
        var source = Observing
            .Replace(
                "ICapability<BusMessage, OrderProjection>",
                "ICapability<OrderProjection, OrderProjection>",
                StringComparison.Ordinal)
            .Replace(
                "BusMessage input, CapabilityContext ctx",
                "OrderProjection input, CapabilityContext ctx",
                StringComparison.Ordinal)
            .Replace(
                "Flow<BusMessage, OrderProjection>",
                "Flow<OrderProjection, OrderProjection>",
                StringComparison.Ordinal)
            .Replace(
                "IFlowBuilder<BusMessage, OrderProjection>",
                "IFlowBuilder<OrderProjection, OrderProjection>",
                StringComparison.Ordinal);

        GeneratorHarness.Analyze(source, new TriggerDeclarationAnalyzer()).ShouldContain("FLOWX1041");
        SubscriptionsIn(RunOn(source, HostingStub))
            .ShouldBeNull("a flow that cannot bind a change gets no registration");
    }

    /// <summary>An ephemeral change-triggered flow is reported for the second reason.</summary>
    /// <remarks>
    /// The cursor is committed after the flows have run, so a crash in between re-offers the
    /// change — and an ephemeral flow has no primary key to refuse it.
    /// </remarks>
    [Fact]
    public void AnEphemeralChangeFlowIsReported()
    {
        var source = Observing.Replace(
            ", Profile = ExecutionProfile.Durable", string.Empty, StringComparison.Ordinal);

        GeneratorHarness.Analyze(source, new TriggerDeclarationAnalyzer()).ShouldContain("FLOWX1041");
        SubscriptionsIn(RunOn(source, HostingStub)).ShouldBeNull();
    }

    /// <summary>A flow with no change trigger produces no registration and no rule.</summary>
    [Fact]
    public void AFlowWithNoChangeTriggerIsUntouched()
    {
        var source = Observing.Replace(
            "[ChangeTrigger(\"order.placed\", Group = \"projection\")]",
            string.Empty,
            StringComparison.Ordinal);

        GeneratorHarness.Analyze(source, new TriggerDeclarationAnalyzer()).ShouldNotContain("FLOWX1041");
        SubscriptionsIn(RunOn(source, HostingStub)).ShouldBeNull();
    }

    /// <summary>
    /// A bus trigger on the same flow is not reported by this rule, and the reverse.
    /// </summary>
    /// <remarks>
    /// The reason FLOWX1041 is a separate id rather than FLOWX1039 widened: the two rules read the
    /// same two facts, and a flow may declare both triggers, so a suppression of one must not
    /// silence the other. Here the flow is correct for both and neither fires — the assertion
    /// that matters is that both registrations are produced from one declaration each.
    /// </remarks>
    [Fact]
    public void AFlowMayDeclareBothTriggersAndGetBothRegistrations()
    {
        var source = Observing.Replace(
            "[ChangeTrigger(\"order.placed\", Group = \"projection\")]",
            "[ChangeTrigger(\"order.placed\", Group = \"projection\")]\n" +
            "[BusTrigger(\"order.placed\", Group = \"projection\")]",
            StringComparison.Ordinal);

        var busStub = """
            namespace FlowX.Hosting
            {
                public static class FlowBusSubscriptionRegistration
                {
                }
            }
            """;

        GeneratorHarness.Analyze(source, new TriggerDeclarationAnalyzer())
            .ShouldNotContain("FLOWX1041");

        var run = RunOn(source, HostingStub, busStub);

        SubscriptionsIn(run).ShouldNotBeNull().ShouldContain("AddProjectOrderFlowChangeSubscription");

        run.Sources
            .Single(static s => s.HintName == BusEmitter.FileName)
            .Source
            .ShouldContain("AddProjectOrderFlowSubscription");
    }
}
