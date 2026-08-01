using System;
using System.Linq;
using System.Text.Json;
using FlowX.Compiler.Analysis;
using FlowX.Compiler.Emit;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// Whether a <c>[CronTrigger]</c> becomes a registration, and whether that registration says
/// what the manifest says.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The same arrangement <see cref="EndpointGenerationTests"/> tests for HTTP.</strong>
/// One reading of the attribute produces the manifest's <c>triggers</c> block and the
/// generated registration, so there is no second copy of the cron expression for a schedule
/// to drift from what the manifest published — and the expression is what the instance id is
/// derived from
/// (<a href="../../docs/adr/ADR-0031-an-occurrence-names-the-instance-it-starts.md">ADR-0026</a>),
/// so a drift would not merely mislead a reader, it would split one schedule into two.
/// </para>
/// <para>
/// <strong>The host is a stub declared in the test's own source</strong>, for the reason the
/// HTTP transport is: the generator looks <c>FlowX.Hosting.FlowScheduleRegistration</c> up by
/// name in whatever compilation it is given, and links against nothing.
/// </para>
/// </remarks>
public sealed class ScheduleGenerationTests
{
    /// <summary>Stands in for the hosting assembly the user's project would reference.</summary>
    private const string HostingStub = """
        namespace FlowX.Hosting
        {
            public static class FlowScheduleRegistration
            {
            }
        }
        """;

    private const string Scheduled = """
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

        [Flow("ledger.reconcile", Version = "1.0.0", Profile = ExecutionProfile.Durable)]
        [CronTrigger("0 2 * * *", TimeZone = "Europe/Berlin")]
        public sealed partial class ReconcileLedgerFlow : Flow<ScheduledFire, ReconciliationDone>
        {
            protected override void Define(IFlowBuilder<ScheduledFire, ReconciliationDone> flow) =>
                flow.Step<Reconcile>().Return(ctx => ctx.Get<ReconciliationDone>());
        }
        """;

    private static GeneratorRun RunOn(params string[] sources) => GeneratorHarness.Run(
        GeneratorHarness.CompilationOf(
            [.. sources.Select((source, i) => ($"/src/File{i}.cs", source))]));

    private static string? SchedulesIn(GeneratorRun run) => run.Sources
        .Where(static s => s.HintName == ScheduleEmitter.FileName)
        .Select(static s => s.Source)
        .FirstOrDefault();

    /// <summary>A project that does not reference the host pays nothing for this.</summary>
    /// <remarks>
    /// A flow library compiled on its own declares its schedule and generates no registration,
    /// exactly as it declares its route and generates no endpoint. The file appears in the
    /// application that composes it.
    /// </remarks>
    [Fact]
    public void AProjectWithNoHostGetsNoScheduleFile()
    {
        SchedulesIn(RunOn(Scheduled)).ShouldBeNull();
    }

    /// <summary>A flow that declares a cron gets a registration it did not write.</summary>
    [Fact]
    public void ADeclaredCronBecomesARegistration()
    {
        var generated = SchedulesIn(RunOn(Scheduled, HostingStub));

        generated.ShouldNotBeNull();
        generated.ShouldContain("AddFlowXSchedules");
        generated.ShouldContain("\"0 2 * * *\"");
        generated.ShouldContain("\"Europe/Berlin\"");
        generated.ShouldContain("global::Sample.ReconcileLedgerFlow.Plan");
    }

    /// <summary>
    /// The cron the registration carries is the cron the manifest published.
    /// </summary>
    /// <remarks>
    /// Both come out of one <c>TriggerReader.Read</c>, so this asserts that the arrangement
    /// survived rather than that two writers agree — which is the assertion worth having,
    /// because the id every node derives is derived from this string.
    /// </remarks>
    [Fact]
    public void TheRegisteredCronIsTheOneTheManifestPublished()
    {
        var run = RunOn(Scheduled, HostingStub);

        var trigger = JsonDocument.Parse(run.ManifestJson!)
            .RootElement.GetProperty("flows")[0]
            .GetProperty("triggers")[0];

        trigger.GetProperty("kind").GetString().ShouldBe("Schedule");

        var generated = SchedulesIn(run).ShouldNotBeNull();

        generated.ShouldContain("\"" + trigger.GetProperty("cron").GetString() + "\"");
        generated.ShouldContain("\"" + trigger.GetProperty("timeZone").GetString() + "\"");
    }

    /// <summary>The declared missed-fire policy reaches the registration.</summary>
    /// <remarks>
    /// It is the one property of the six that decides whether work happens, so it is the one
    /// the runtime reads — and it reaches the registration rather than the manifest
    /// (<a href="../../docs/adr/ADR-0034-the-manifest-publishes-a-schedules-address.md">ADR-0029</a>).
    /// </remarks>
    [Fact]
    public void TheDeclaredMissedFirePolicyReachesTheRegistration()
    {
        var source = Scheduled.Replace(
            "[CronTrigger(\"0 2 * * *\", TimeZone = \"Europe/Berlin\")]",
            "[CronTrigger(\"0 2 * * *\", MissedFire = MissedFirePolicy.RunAll)]",
            StringComparison.Ordinal);

        SchedulesIn(RunOn(source, HostingStub))
            .ShouldNotBeNull()
            .ShouldContain("global::FlowX.MissedFirePolicy.RunAll");
    }

    /// <summary>
    /// A scheduled flow whose input is not <c>ScheduledFire</c> is reported, not skipped.
    /// </summary>
    /// <remarks>
    /// A silent skip is what this whole work package exists to remove: a flow that declares an
    /// address nothing serves. The rule names the contract because a cron fire has no body to
    /// bind, and the occurrence is the only fact there is to hand a flow that a clock started.
    /// </remarks>
    [Fact]
    public void AScheduledFlowThatDoesNotBindItsOccurrenceIsReported()
    {
        var source = Scheduled.Replace("ScheduledFire", "ReconciliationDone", StringComparison.Ordinal);

        GeneratorHarness.Analyze(source, new TriggerDeclarationAnalyzer()).ShouldContain("FLOWX1038");
        SchedulesIn(RunOn(source, HostingStub))
            .ShouldBeNull("a flow that cannot bind an occurrence gets no registration");
    }

    /// <summary>An ephemeral scheduled flow is reported for the same reason.</summary>
    /// <remarks>
    /// Nothing journals an ephemeral instance, so nothing can tell two nodes' fires apart: the
    /// duplicate refusal this design rests on is a primary key an ephemeral flow never writes.
    /// </remarks>
    [Fact]
    public void AnEphemeralScheduledFlowIsReported()
    {
        var source = Scheduled.Replace(
            ", Profile = ExecutionProfile.Durable", string.Empty, StringComparison.Ordinal);

        GeneratorHarness.Analyze(source, new TriggerDeclarationAnalyzer()).ShouldContain("FLOWX1038");
        SchedulesIn(RunOn(source, HostingStub)).ShouldBeNull();
    }

    /// <summary>A flow with no cron produces no registration and no rule.</summary>
    [Fact]
    public void AFlowWithNoCronIsUntouched()
    {
        var source = Scheduled.Replace(
            "[CronTrigger(\"0 2 * * *\", TimeZone = \"Europe/Berlin\")]", string.Empty, StringComparison.Ordinal);

        GeneratorHarness.Analyze(source, new TriggerDeclarationAnalyzer()).ShouldNotContain("FLOWX1038");
        SchedulesIn(RunOn(source, HostingStub)).ShouldBeNull();
    }
}
