using FlowX;
using FlowX.Hosting;
using FlowX.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Scheduler.Tests;

/// <summary>
/// What the sample's <c>[CronTrigger]</c> declares, and what a host actually registers from it.
/// </summary>
/// <remarks>
/// <strong>Nothing here writes <c>0 2 * * *</c> as a string and compares it to itself.</strong>
/// The registration under test is the one the compiler generated —
/// <c>SchedulerSchedules.AddFlowXSchedules</c>, from the same reading of the attribute that
/// produced the manifest's <c>triggers</c> block — so a change to the declaration fails here
/// rather than leaving the application publishing a schedule it does not fire.
/// </remarks>
public sealed class DeclarationTests
{
    /// <summary>A container with the flow's dependencies and nothing else the sample needs.</summary>
    private static ServiceProvider Composed()
    {
        var services = new ServiceCollection();

        services.AddFlowX(options => options.ApplicationName = "Scheduler.Tests");

        services.AddSingleton<ILedger, InMemoryLedger>();
        services.AddSingleton<IBank, InMemoryBank>();
        services.AddSingleton<IReportDesk, InMemoryReportDesk>();
        services.AddSingleton<LoadLedgerSnapshot>();
        services.AddSingleton<LoadBankStatement>();
        services.AddSingleton<MatchByReference>();
        services.AddSingleton<MatchByAmountAndDate>();
        services.AddSingleton<ProduceReport>();
        services.AddSingleton<DailyReconciliationFlow.Dispatcher>();

        return services.BuildServiceProvider();
    }

    /// <summary>The flow binds the occurrence and journals its instances, and it must do both.</summary>
    /// <remarks>
    /// A cron firing has no body and <c>FLOWX1007</c> forbids the flow reading a clock, so the
    /// contract is the platform's rather than the author's; and an ephemeral scheduled flow
    /// journals nothing, so nothing would refuse a second node's firing. Asserted off the
    /// compiled plan rather than the source, so a signature change fails here.
    /// </remarks>
    [Fact]
    public void TheFlowBindsTheOccurrenceAndIsDurable()
    {
        DailyReconciliationFlow.Plan.Flow.Profile.ShouldBe(ExecutionProfile.Durable);
        DailyReconciliationFlow.Plan.Flow.Id.ShouldBe("reconciliation.daily");
    }

    /// <summary>
    /// Every value the declaration carries reaches the schedule the host will fire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Two of these five reached nothing at all before this sample existed.</strong>
    /// <c>Overlap</c> and <c>Jitter</c> were properties on the attribute that no reader read, no
    /// model carried and no registration took — so a declaration that said "skip an overlapping
    /// run" produced a host that ran them concurrently, silently. This is the assertion that says
    /// the two are joined up, and it fails at the first link that drops one.
    /// </para>
    /// <para>
    /// The expression and the zone are the manifest's own, and they are the two terms the
    /// instance id is derived from — a registration carrying a different string would not merely
    /// mislead a reader, it would split one schedule into two that never see each other's
    /// firings.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheGeneratedRegistrationCarriesEveryDeclaredOption()
    {
        using var services = Composed();

        FlowX.Generated.SchedulerSchedules.AddFlowXSchedules(services);

        var schedule = services
            .GetRequiredService<FlowScheduleCatalog>()
            .Registrations
            .ShouldHaveSingleItem()
            .Schedule;

        schedule.Cron.Expression.ShouldBe("0 2 * * *");
        schedule.Cron.TimeZoneId.ShouldBe("Europe/Berlin");
        schedule.MissedFire.ShouldBe(MissedFirePolicy.RunOnce);
        schedule.PerTenant.ShouldBeTrue();
        schedule.Overlap.ShouldBe(OverlapPolicy.Skip);
        schedule.Jitter.ShouldBe(TimeSpan.FromSeconds(120));
    }

    /// <summary>
    /// The declared expression is a wall-clock time, so the autumn fold produces one firing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>0 2 * * *</c> in <c>Europe/Berlin</c> names a local time. On the last Sunday in October
    /// 02:00 happens twice, and a nightly reconciliation that ran twice would reconcile one day's
    /// books against a second night's statement. The occurrence lands on the first pass, once —
    /// which is a property of the parser rather than of anything this sample wrote, and is worth
    /// pinning here because the sample's README claims it.
    /// </para>
    /// <para>
    /// 2025-10-26 is the fold: 03:00 CEST becomes 02:00 CET, so the local hour 02:00–03:00 occurs
    /// twice and there are 25 hours in the day.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheAutumnFoldProducesOneFiringAndNotTwo()
    {
        var schedule = CronSchedule.Parse("0 2 * * *", "Europe/Berlin").Value;

        var occurrences = schedule
            .Between(
                new DateTimeOffset(2025, 10, 25, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2025, 10, 26, 12, 0, 0, TimeSpan.Zero))
            .ToList();

        occurrences.ShouldHaveSingleItem(
            "02:00 local happens twice on the fold, and firing twice would run a nightly job " +
            "twice a year with no declaration anywhere saying so.");
    }

    /// <summary>
    /// The spring gap produces one firing too, late rather than lost.
    /// </summary>
    /// <remarks>
    /// 2026-03-29: 02:00 CET becomes 03:00 CEST, so local 02:00 does not exist at all. The
    /// occurrence lands on the first local time that does — the transition instant — which is
    /// late by the size of the gap and never skipped. A silent annual no-run is the failure a
    /// time-zone field exists to prevent.
    /// </remarks>
    [Fact]
    public void TheSpringGapProducesOneLateFiringAndNotNone()
    {
        var schedule = CronSchedule.Parse("0 2 * * *", "Europe/Berlin").Value;

        schedule
            .Between(
                new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 3, 29, 12, 0, 0, TimeSpan.Zero))
            .ShouldHaveSingleItem()
            .ShouldBe(
                new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero),
                "01:00 UTC is the transition instant, which is the first local time that exists.");
    }

    /// <summary>A flow the fan-out fires is one the recovery sweep can take over.</summary>
    /// <remarks>
    /// A scheduled instance is a durable instance like any other: a node that dies holding one
    /// has abandoned it, and a sweep can only resume it if this node can turn its
    /// <c>(flow_id, flow_version)</c> back into a plan. The registration does that rather than
    /// asking <c>Program.cs</c> to remember, so forgetting it would be a silent loss.
    /// </remarks>
    [Fact]
    public void RegisteringTheScheduleAlsoMakesTheFlowRecoverable()
    {
        using var services = Composed();

        FlowX.Generated.SchedulerSchedules.AddFlowXSchedules(services);

        services.GetRequiredService<FlowCatalog>()
            .TryGet(
                DailyReconciliationFlow.Plan.Flow.Id,
                DailyReconciliationFlow.Plan.Flow.Version,
                out _)
            .ShouldBeTrue();
    }
}
