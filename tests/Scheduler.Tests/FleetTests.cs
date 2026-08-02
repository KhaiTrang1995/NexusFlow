using FlowX;
using Shouldly;
using Xunit;

namespace Scheduler.Tests;

/// <summary>
/// What happens when several nodes sweep for one occurrence, against a real PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the file the sample's README promised as "leader election", and it is not
/// one.</strong> The README drew a sequence with an <c>acquire("scheduler-leader")</c>, a
/// renewal loop, a dead leader and a successor taking a new token — and
/// <c>docs/adr/ADR-0031-an-occurrence-names-the-instance-it-starts.md</c> refuses that design in
/// as many words: an election has to be held, a dead leader has to be <em>detected</em>, and
/// hand-over has a window in each of the three where a schedule fires twice or not at all. What
/// ships instead needs no coordination at all, and these tests are what say so.
/// </para>
/// <para>
/// <strong>Nothing here is a double.</strong> The journal and the lease store are
/// <c>plugins/FlowX.Postgres</c> against a live server; the primary key that refuses the losers
/// is a real primary key. An in-memory journal would be asserting this property against code
/// written for this test, which is the arrangement that makes a green suite mean nothing —
/// and the refusal is the whole mechanism.
/// </para>
/// </remarks>
[Collection("postgres")]
public sealed class FleetTests
{
    /// <summary>One occurrence is one instance, however many nodes are sweeping for it.</summary>
    /// <remarks>
    /// <para>
    /// Five nodes, one declaration, one database — and one row. Every node computes the same
    /// occurrence from the same expression, derives the same id from it, and four of the five
    /// are refused: by the lease while the winner is running, and by the primary key for ever
    /// afterwards. There is nothing to elect and nothing to kill.
    /// </para>
    /// <para>
    /// <strong>All five reach the claim because the fleet is primed, and the count below means
    /// nothing without that.</strong> A node that has never accounted for this schedule takes its
    /// floor from the journal, so a cold node whose probe runs after the winner's row lands finds
    /// nothing due and is refused by nobody — which is the mechanism working and is
    /// <see cref="AColdFleetFiresOnceHoweverManyNodesReachTheClaim"/>'s subject. Asserting four
    /// refusals against a cold fleet asserts how a loaded box interleaved five tasks.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task FiveNodesFireOneOccurrenceOnce()
    {
        await using var cluster = await SchedulerCluster.CreateAsync(Cancellation.Token);

        var nodes = cluster.Nodes(5);

        await SchedulerCluster.PrimeAsync(nodes, Cancellation.Token);

        cluster.Clock.Advance(TimeSpan.FromHours(1));

        var reports = await Task.WhenAll(
            nodes.Select(node => node.RunOnceAsync(Cancellation.Token).AsTask()));

        (await cluster.InstanceCountAsync(Cancellation.Token)).ShouldBe(
            1,
            "the occurrence names the instance, so five nodes race to start one row.");

        reports.Sum(static r => r.Due).ShouldBe(
            5,
            "a primed node decides what is due without reading a store, so every node holds " +
            "this occurrence whatever order the five of them are scheduled in.");

        reports.Sum(static r => r.Fired).ShouldBe(1);
        reports.Sum(static r => r.Contended).ShouldBe(
            4,
            "a refusal is the mechanism working, so it is contended rather than failed — an " +
            "operator watching Contended expecting zero is watching the wrong number.");

        reports.Sum(static r => r.Failed).ShouldBe(
            0, "neither lease.held nor journal.instance_exists is a failure.");

        cluster.Desk.Filed.ShouldHaveSingleItem().Unmatched.ShouldBe(
            1,
            "three entries were booked, the bank referenced one and settled one without a " +
            "reference, and the third is what the job exists to surface.");
    }

    /// <summary>
    /// A fleet with no memory fires one occurrence once, however many of its nodes get as far as
    /// attempting it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The invariant ADR-0031 actually states, on the fleet that cannot be counted.</strong>
    /// Every node here is sweeping this schedule for the first time, so each takes its floor from
    /// the journal — and a node whose probe runs after the winner's row lands reads that row,
    /// takes the occurrence as its floor, and finds nothing due. That node is not refused because
    /// it never asks. How many nodes are in that position is decided by how the machine
    /// interleaved five tasks, so <c>Contended</c> is a number about the box and not about the
    /// scheduler; what is about the scheduler is that <em>one</em> node fired, one row exists, and
    /// every node that did reach the claim was refused rather than failed.
    /// </para>
    /// <para>
    /// The lower bound is the half that matters second: a scheduler that fired nothing at all
    /// would satisfy "no two nodes fired", and <c>Fired</c> summing to exactly one is what refuses
    /// it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AColdFleetFiresOnceHoweverManyNodesReachTheClaim()
    {
        await using var cluster = await SchedulerCluster.CreateAsync(Cancellation.Token);

        var nodes = cluster.Nodes(5);

        cluster.Clock.Advance(TimeSpan.FromHours(1));

        var reports = await Task.WhenAll(
            nodes.Select(node => node.RunOnceAsync(Cancellation.Token).AsTask()));

        (await cluster.InstanceCountAsync(Cancellation.Token)).ShouldBe(
            1, "the occurrence names the instance, and the primary key is what settles the race.");

        reports.Sum(static r => r.Fired).ShouldBe(
            1, "exactly one — not 'at most one', which a scheduler that never fires satisfies.");

        reports.Count(static r => r.Fired > 0).ShouldBe(
            1, "and it is one node's firing rather than a total that happens to sum to one.");

        reports.Sum(static r => r.Failed).ShouldBe(
            0, "a node that lost the race was refused, which is not a failure.");

        reports.Sum(static r => r.Contended).ShouldBe(
            reports.Sum(static r => r.Due) - 1,
            "every node that reached the claim and lost it was refused — the ones missing from " +
            "this count never had the occurrence due, because the journal already held it.");

        cluster.Desk.Filed.ShouldHaveSingleItem();
    }

    /// <summary>
    /// A node that goes away mid-sweep does not take the occurrence with it — and the one that
    /// already ran it is not re-run by its replacement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The honest version of the README's <c>KillLeader</c>.</strong> A node holds no
    /// schedule-level lease and renews nothing, so "the leader died" is not an event this design
    /// has. What it does have is a node with no memory: a fresh <c>FlowScheduleScan</c> over the
    /// same stores, which is exactly what a replacement pod is. It sweeps, walks back to the
    /// newest occurrence the journal holds, finds this one, and fires nothing.
    /// </para>
    /// <para>
    /// That is the permanent half of the answer and the half a lease cannot give: the second
    /// attempt is made after the first has completed and released, so the only thing left to
    /// refuse it is <c>flow_instance</c>'s primary key.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ANodeWithNoMemoryDoesNotRefireAnOccurrenceTheJournalHolds()
    {
        await using var cluster = await SchedulerCluster.CreateAsync(Cancellation.Token);

        var node = cluster.Nodes(1)[0];

        cluster.Clock.Advance(TimeSpan.FromHours(1));

        (await node.RunOnceAsync(Cancellation.Token))
            .ShouldHaveFired(1, "the first hour is due and nothing has run it");

        // The replacement pod. Same stores, same clock, no accounting of its own.
        (await cluster.Replacement().RunOnceAsync(Cancellation.Token))
            .ShouldHaveFired(0, "the journal already holds that occurrence");

        (await cluster.InstanceCountAsync(Cancellation.Token)).ShouldBe(1);
    }

    /// <summary>
    /// A fleet that was down catches the most recent firing up, and only that one.
    /// </summary>
    /// <remarks>
    /// <c>MissedFirePolicy.RunOnce</c> is what the sample declares, so a three-hour outage of an
    /// hourly schedule produces one late firing rather than three. The instant the late firing
    /// carries is the occurrence that was missed, and the report proves it: the reconciliation
    /// is filed against 04:00, not against the instant the fleet came back.
    /// </remarks>
    [Fact]
    public async Task AFleetThatWasDownCatchesUpOnce()
    {
        await using var cluster = await SchedulerCluster.CreateAsync(Cancellation.Token);

        var node = cluster.Nodes(1)[0];

        cluster.Clock.Advance(TimeSpan.FromHours(1));

        (await node.RunOnceAsync(Cancellation.Token)).ShouldHaveFired(1, "01:00 is due");

        // Nothing runs for three hours and ten minutes. The node that comes back has never seen
        // this schedule, so the journal is what tells it where to resume from.
        cluster.Clock.Advance(TimeSpan.FromMinutes(190));

        (await cluster.Replacement().RunOnceAsync(Cancellation.Token))
            .ShouldHaveFired(1, "02:00, 03:00 and 04:00 were missed and RunOnce fires the newest");

        (await cluster.InstanceCountAsync(Cancellation.Token)).ShouldBe(2);

        cluster.Desk.Filed[^1].OccurrenceAt.ShouldBe(
            SchedulerCluster.T0.AddHours(4),
            "a late firing does the work of the occurrence it was owed, not of the instant it " +
            "was noticed.");
    }

    /// <summary>
    /// One occurrence becomes one instance per tenant, each under its own id.
    /// </summary>
    /// <remarks>
    /// <strong>The fan-out is in the sweep and not in the flow, and this is the difference that
    /// buys.</strong> A flow that looped over tenants itself would be one instance, one journal
    /// row and one failure mode for all of them: a tenant whose bank was down would fail the
    /// night for everybody. Here each tenant has its own id, its own row and its own report.
    /// </remarks>
    [Fact]
    public async Task OneOccurrenceFansOutToOneInstancePerTenant()
    {
        await using var cluster = await SchedulerCluster.CreateAsync(
            Cancellation.Token, "acme", "globex", "initech");

        cluster.Schedules.Registrations.Count.ShouldBe(1);

        // Re-registered per tenant; the same expression, so the same key, so one schedule.
        cluster.Register(SchedulerCluster.Hourly, perTenant: true);

        var fleet = cluster.Nodes(3);

        cluster.Clock.Advance(TimeSpan.FromHours(1));

        var reports = await Task.WhenAll(
            fleet.Select(node => node.RunOnceAsync(Cancellation.Token).AsTask()));

        reports.Sum(static r => r.Fired).ShouldBe(3, "three tenants, one occurrence each");

        (await cluster.InstanceCountAsync(Cancellation.Token)).ShouldBe(3);

        cluster.Desk.Filed
            .Select(static report => report.TenantId)
            .Order(StringComparer.Ordinal)
            .ShouldBe(["acme", "globex", "initech"]);

        foreach (var tenant in (string[])["acme", "globex", "initech"])
        {
            (await cluster.StateOfAsync(SchedulerCluster.T0.AddHours(1), tenant, Cancellation.Token))
                .ShouldBe(
                    FlowInstanceState.Completed,
                    $"'{tenant}' has an instance of its own, keyed by an id carrying its name.");
        }
    }

    /// <summary>A per-tenant schedule on a fleet that serves nobody fires nothing.</summary>
    /// <remarks>
    /// An empty directory has asked for zero firings and gets zero, rather than one untenanted
    /// firing that a host enforcing isolation would then refuse at admission. The truthful
    /// behaviour for a deployment that was never told who it serves.
    /// </remarks>
    [Fact]
    public async Task APerTenantScheduleWithNoTenantsFiresNothing()
    {
        await using var cluster = await SchedulerCluster.CreateAsync(Cancellation.Token);

        cluster.Register(SchedulerCluster.Hourly, perTenant: true);

        var node = cluster.Nodes(1)[0];

        cluster.Clock.Advance(TimeSpan.FromHours(1));

        var report = await node.RunOnceAsync(Cancellation.Token);

        report.Due.ShouldBe(0);
        report.ShouldHaveFired(0, "the directory names nobody, so the fan-out visits nobody");

        (await cluster.InstanceCountAsync(Cancellation.Token)).ShouldBe(0);
    }
}
