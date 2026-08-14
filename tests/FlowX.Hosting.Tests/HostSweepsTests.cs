using FlowX.Hosting;
using Shouldly;
using Xunit;

namespace FlowX.Hosting.Tests;

/// <summary>
/// Which sweeps a host performs, and the default that must not move.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The default is the assertion that matters most here.</strong> Every deployment
/// running today runs every sweep it is capable of. A default of anything but
/// <see cref="HostSweeps.All"/> would silently stop work a live system depends on — a
/// scheduler that fires nothing, an outbox nothing drains — and it would do it without an
/// error anywhere. That is the one change a hosting option must never make, so it is pinned
/// rather than trusted.
/// </para>
/// <para>
/// The composite values are asserted against the roles they exist to express, because a
/// deployment writes <c>HostSweeps.Durability</c> and gets whatever this enum says it means.
/// </para>
/// </remarks>
public sealed class HostSweepsTests
{
    /// <summary>A host told nothing runs everything, exactly as it did before this type.</summary>
    [Fact]
    public void EverySweepRunsByDefault()
    {
        var options = new FlowXOptions();

        options.Sweeps.ShouldBe(
            HostSweeps.All,
            "the default stopped being every sweep. A deployment that never set this now "
            + "silently stops recovering instances, firing schedules or draining its outbox, "
            + "and nothing anywhere reports it.");

        foreach (var sweep in Individually)
        {
            options.Sweeps.HasFlag(sweep).ShouldBeTrue($"the default no longer includes {sweep}.");
        }
    }

    /// <summary>An API host runs no sweep, which is what makes it an API host.</summary>
    [Fact]
    public void NoneIncludesNothing()
    {
        foreach (var sweep in Individually)
        {
            HostSweeps.None.HasFlag(sweep).ShouldBeFalse(
                $"HostSweeps.None includes {sweep}, so an API-only host would still run it.");
        }
    }

    /// <summary>
    /// The two composites are disjoint and together are everything.
    /// </summary>
    /// <remarks>
    /// Asserted as a partition rather than as two lists, because the failure worth catching is
    /// a sweep that belongs to neither role: it would run on a host set to
    /// <see cref="HostSweeps.All"/> and on no split deployment at all, which is a gap nobody
    /// would see until a queue stopped draining in production.
    /// </remarks>
    [Fact]
    public void DurabilityAndIngestionPartitionEverySweep()
    {
        (HostSweeps.Durability & HostSweeps.Ingestion).ShouldBe(
            HostSweeps.None,
            "a sweep belongs to both roles, so the worker and the scheduler would both run it "
            + "and duplicate the work the split exists to separate.");

        (HostSweeps.Durability | HostSweeps.Ingestion).ShouldBe(
            HostSweeps.All,
            "a sweep belongs to neither role. It runs under All and under no split deployment, "
            + "which is the gap that is only found when something stops moving in production.");

        foreach (var sweep in Individually)
        {
            (HostSweeps.All.HasFlag(sweep)).ShouldBeTrue($"All does not include {sweep}.");
        }
    }

    /// <summary>The three roles 18 §1 draws are expressible, which they were not before.</summary>
    [Theory]
    [InlineData(HostSweeps.None, HostSweeps.Recovery, false)]
    [InlineData(HostSweeps.Durability, HostSweeps.Recovery, true)]
    [InlineData(HostSweeps.Durability, HostSweeps.Bus, false)]
    [InlineData(HostSweeps.Ingestion, HostSweeps.Bus, true)]
    [InlineData(HostSweeps.Ingestion, HostSweeps.Schedule, false)]
    [InlineData(HostSweeps.All, HostSweeps.Change, true)]
    public void ARoleIncludesItsOwnSweepsAndNoOthers(HostSweeps role, HostSweeps sweep, bool expected) =>
        role.HasFlag(sweep).ShouldBe(
            expected,
            $"role {role} answered {!expected} for {sweep}. The three-role topology in "
            + "docs/18-Cloud-Native.md §1 is only real if these answers are.");

    private static readonly HostSweeps[] Individually =
    [
        HostSweeps.Recovery,
        HostSweeps.Timer,
        HostSweeps.Schedule,
        HostSweeps.Bus,
        HostSweeps.Change,
        HostSweeps.Stream,
    ];
}
