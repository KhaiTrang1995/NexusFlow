using FlowX;
using FlowX.Hosting;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace Scheduler.Tests;

/// <summary>
/// The property that makes jitter work at all: every node computes the same offset for one
/// firing, and different offsets for different firings.
/// </summary>
/// <remarks>
/// <strong>These are unit tests and deliberately not fleet tests.</strong> The claim is that the
/// offset is a <em>pure function</em> of the firing, so the useful assertion is arithmetic —
/// there is nothing a database could add, and a test that ran five nodes and observed the same
/// instant would be observing a consequence rather than the property.
/// <see cref="FleetTests"/> and <see cref="OverlapTests"/> are where the store is the subject.
/// </remarks>
public sealed class JitterTests
{
    private const string Cron = "0 * * * *";

    private static readonly DateTimeOffset Occurrence = DateTimeOffset.UnixEpoch.AddHours(2);

    private static FlowSchedule ScheduleWith(string? jitter) => FlowSchedule.Create(
        DailyReconciliationFlow.Plan.Flow.Id,
        DailyReconciliationFlow.Plan.Flow.Version,
        Cron,
        "UTC",
        MissedFirePolicy.RunOnce,
        perTenant: true,
        overlap: OverlapPolicy.Skip,
        jitter: jitter);

    /// <summary>The offset lands inside the declared window and never on its far edge.</summary>
    /// <remarks>
    /// The upper bound is exclusive on purpose: an offset of exactly the window would put a
    /// firing on the boundary that the next occurrence's own window starts from, on a schedule
    /// dense enough for the two to meet.
    /// </remarks>
    [Fact]
    public void AFiringIsReleasedInsideItsWindow()
    {
        var schedule = ScheduleWith("PT120S");

        foreach (var tenant in Enumerable.Range(0, 200).Select(static i => "t-" + i))
        {
            var release = schedule.ReleaseFor(Occurrence, tenant);

            release.ShouldBeGreaterThanOrEqualTo(Occurrence);
            release.ShouldBeLessThan(Occurrence + TimeSpan.FromSeconds(120));
        }
    }

    /// <summary>
    /// Two hundred tenants of one schedule are spread across the window rather than bunched.
    /// </summary>
    /// <remarks>
    /// <strong>This is the option's entire purpose, asserted rather than described.</strong> The
    /// tenant is one of the terms the instance id is derived from, so the fan-out's firings get
    /// different offsets for free — which is what stops five hundred tenants hitting one
    /// downstream at 02:00:00. The bound is loose because the claim is "spread", not "uniform":
    /// a hash is not a shuffle, and asserting a tight distribution would be asserting a property
    /// of SHA-256.
    /// </remarks>
    [Fact]
    public void TenantsOfOneScheduleAreSpreadAcrossTheWindow()
    {
        var schedule = ScheduleWith("PT120S");

        var buckets = Enumerable.Range(0, 200)
            .Select(i => schedule.ReleaseFor(Occurrence, "t-" + i) - Occurrence)
            .Select(static offset => (int)(offset.TotalSeconds / 12))
            .Distinct()
            .Count();

        buckets.ShouldBe(
            10,
            "two hundred tenants over ten twelve-second buckets: every bucket should have " +
            "somebody in it, which is what 'spread across the window' means for the downstream " +
            "being protected.");
    }

    /// <summary>The same firing gets the same offset, for ever, on any node.</summary>
    /// <remarks>
    /// <strong>The decision <c>ADR-0059</c> exists for.</strong> With no leader, every node races
    /// for one occurrence — so <em>n</em> nodes drawing independent random delays fire at
    /// min(<em>n</em> draws), and the spread collapses towards zero exactly as the fleet grows
    /// large enough to need it. A derived offset moves the firing as a unit instead. There is no
    /// process state, no seed and no clock in the derivation, which is what this asserts.
    /// </remarks>
    [Fact]
    public void TheOffsetIsAPureFunctionOfTheFiring()
    {
        var one = ScheduleWith("PT120S");
        var another = ScheduleWith("PT120S");

        foreach (var tenant in (string?[])[null, "acme", "globex"])
        {
            one.ReleaseFor(Occurrence, tenant)
                .ShouldBe(
                    another.ReleaseFor(Occurrence, tenant),
                    "two schedules built from the same declaration are two nodes, and they must " +
                    "agree without talking to each other.");
        }
    }

    /// <summary>Consecutive occurrences of one schedule get different offsets.</summary>
    /// <remarks>
    /// Otherwise the fleet would simply run a fixed amount late every night, which spreads
    /// nothing against a downstream whose other callers are also on the hour.
    /// </remarks>
    [Fact]
    public void ConsecutiveOccurrencesDoNotShareAnOffset()
    {
        var schedule = ScheduleWith("PT120S");

        Enumerable.Range(0, 24)
            .Select(hour => schedule.ReleaseFor(Occurrence.AddHours(hour), "acme") - Occurrence.AddHours(hour))
            .Distinct()
            .Count()
            .ShouldBeGreaterThan(20, "24 occurrences should not collapse onto a handful of offsets");
    }

    /// <summary>A schedule that declares no jitter is released on its occurrence.</summary>
    [Fact]
    public void NoJitterIsNoDelay() =>
        ScheduleWith(null).ReleaseFor(Occurrence, "acme").ShouldBe(Occurrence);

    /// <summary>
    /// The jitter is not in the instance id, and that is load-bearing rather than incidental.
    /// </summary>
    /// <remarks>
    /// An id that depended on the spread would change the moment a deployment widened
    /// <c>PT60S</c> to <c>PT120S</c>, and every occurrence inside the catch-up horizon would fire
    /// a second time under a new key. The id is the occurrence's address; the jitter is when a
    /// node acts on it.
    /// </remarks>
    [Fact]
    public void WideningTheSpreadDoesNotMoveTheInstanceId() =>
        ScheduleWith("PT120S").InstanceIdFor(Occurrence, "acme")
            .ShouldBe(ScheduleWith("PT60S").InstanceIdFor(Occurrence, "acme"));

    /// <summary>A spread the host cannot read is a registration that throws, not one that ignores it.</summary>
    /// <remarks>
    /// A schedule this host cannot read in full should be a pod that never becomes ready rather
    /// than a job that silently never runs — the stance <c>CronSchedule.Parse</c> already takes
    /// for an unreadable expression. <c>FLOWX1045</c> is the earlier half of the same rule and
    /// stops the build before a deployment ever meets this.
    /// </remarks>
    [Theory]
    [InlineData("120s")]
    [InlineData("")]
    [InlineData("PT0S")]
    [InlineData("-PT2M")]
    public void ASpreadTheHostCannotReadIsRefusedAtRegistration(string jitter) =>
        Should.Throw<ArgumentException>(() => ScheduleWith(jitter))
            .Message.ShouldContain("Jitter");

    /// <summary>The reader answers a code a caller can branch on rather than throwing.</summary>
    [Fact]
    public void TheReaderAnswersARefusalRatherThanThrowing()
    {
        var refused = ScheduleJitter.Read("2 minutes");

        refused.IsFailure.ShouldBeTrue();
        refused.Error.Code.ShouldBe(ScheduleJitter.UnreadableCode);

        ScheduleJitter.Read(null).Value.ShouldBe(TimeSpan.Zero);
        ScheduleJitter.Read("PT1M30S").Value.ShouldBe(TimeSpan.FromSeconds(90));
    }
}
