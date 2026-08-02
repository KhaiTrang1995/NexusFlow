using FlowX;
using Shouldly;
using Xunit;

namespace Scheduler.Tests;

/// <summary>
/// What happens to an occurrence that falls due while the previous night's run is still going.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every assertion here is about a run that is genuinely still executing</strong>, not
/// about a row a fixture wrote to look like one. <see cref="GatedBank"/> holds the flow inside
/// <c>reconciliation.statement.read</c> — the step the sample declares a policy on, and the step
/// a real night actually overruns in — so the instance has a live row in PostgreSQL and a real
/// lease behind it while the next sweep decides what to do about the next occurrence.
/// </para>
/// <para>
/// <strong>The sweep asks the journal, not the lease store, and that is the decision worth
/// knowing about.</strong> A lease answers "is a node holding this right now", which is false
/// for the whole window between a node dying and the recovery sweep taking its instance over —
/// so an overlap policy built on leases would start a second run beside the resumed first, which
/// is the exact stacking it exists to prevent.
/// </para>
/// </remarks>
[Collection("postgres")]
public sealed class OverlapTests
{
    /// <summary>
    /// <c>Skip</c> drops the occurrence, counts it, and does not defer it.
    /// </summary>
    /// <remarks>
    /// The default, and the one the sample declares. The dropped occurrence is gone rather than
    /// queued: once the long run finishes, the schedule resumes at its <em>next</em> occurrence
    /// rather than working through a backlog — which is what "a long run must not stack on
    /// itself" means and why <c>Skip</c> rather than <c>Queue</c> is the default.
    /// </remarks>
    [Fact]
    public async Task SkipDropsAnOccurrenceWhosePredecessorIsStillRunning()
    {
        await using var bank = new GatedBank();
        await using var cluster = await SchedulerCluster.CreateAsync(bank, Cancellation.Token);

        cluster.Register(SchedulerCluster.Minutely, OverlapPolicy.Skip);

        var fleet = cluster.Nodes(3);

        cluster.Clock.Advance(TimeSpan.FromMinutes(1));

        var overrunning = fleet[0].RunOnceAsync(Cancellation.Token).AsTask();

        await bank.EnteredAsync(Cancellation.Token);

        (await cluster.StateOfAsync(SchedulerCluster.T0.AddMinutes(1), null, Cancellation.Token))
            .ShouldNotBeOneOf(
                [FlowInstanceState.Completed, FlowInstanceState.Failed, FlowInstanceState.TimedOut],
                "00:01's run is in the bank read and holding — Pending until its first step " +
                "commits, and Running after, but in neither case finished.");

        // 00:02 falls due while 00:01 is still in that read.
        cluster.Clock.Advance(TimeSpan.FromMinutes(1));

        var second = await fleet[1].RunOnceAsync(Cancellation.Token);

        second.ShouldHaveFired(0, "00:01 has not finished, and Skip means skip");
        second.Skipped.ShouldBe(1);
        second.Held.ShouldBe(0, "a skipped occurrence is dropped, not deferred");

        bank.Release();
        await overrunning;

        (await cluster.InstanceCountAsync(Cancellation.Token)).ShouldBe(
            1, "00:02 never became an instance at all");

        // 00:03 is a fresh occurrence with nothing in front of it, so the schedule resumes.
        cluster.Clock.Advance(TimeSpan.FromMinutes(1));

        (await fleet[2].RunOnceAsync(Cancellation.Token))
            .ShouldHaveFired(1, "00:01 has finished, so 00:03 has nothing to overlap with");

        cluster.Desk.Filed
            .Select(static report => report.OccurrenceAt)
            .ShouldBe([SchedulerCluster.T0.AddMinutes(1), SchedulerCluster.T0.AddMinutes(3)]);
    }

    /// <summary>
    /// <c>Queue</c> defers the occurrence and fires it once the run in front of it finishes.
    /// </summary>
    /// <remarks>
    /// The difference from <c>Skip</c> stated as an assertion rather than as a table row: the
    /// occurrence is not accounted for, so the next sweep reconsiders it — and the instant it
    /// eventually runs under is still 00:02, because the occurrence is what the work is about.
    /// </remarks>
    [Fact]
    public async Task QueueDefersAnOccurrenceAndFiresItAfterwards()
    {
        await using var bank = new GatedBank();
        await using var cluster = await SchedulerCluster.CreateAsync(bank, Cancellation.Token);

        cluster.Register(SchedulerCluster.Minutely, OverlapPolicy.Queue);

        var fleet = cluster.Nodes(2);

        cluster.Clock.Advance(TimeSpan.FromMinutes(1));

        var overrunning = fleet[0].RunOnceAsync(Cancellation.Token).AsTask();

        await bank.EnteredAsync(Cancellation.Token);

        cluster.Clock.Advance(TimeSpan.FromMinutes(1));

        var deferred = await fleet[1].RunOnceAsync(Cancellation.Token);

        deferred.ShouldHaveFired(0, "00:01 has not finished");
        deferred.Skipped.ShouldBe(0, "Queue defers rather than drops");
        deferred.Held.ShouldBe(1);

        bank.Release();
        await overrunning;

        (await fleet[1].RunOnceAsync(Cancellation.Token))
            .ShouldHaveFired(1, "the run in front of it finished, so the queued occurrence goes");

        cluster.Desk.Filed
            .Select(static report => report.OccurrenceAt)
            .ShouldBe(
                [SchedulerCluster.T0.AddMinutes(1), SchedulerCluster.T0.AddMinutes(2)],
                "a deferred firing does 00:02's work when it eventually runs, not the work of " +
                "the instant it was released.");
    }

    /// <summary>
    /// <c>Concurrent</c> lets them stack, which is the option and its cost in one test.
    /// </summary>
    /// <remarks>
    /// This is the sample README's first "thing to try", answered: with <c>Concurrent</c>
    /// declared, an overrunning night does not hold the next one up, and two instances of the
    /// same reconciliation run against the same books at once. It is a legitimate declaration —
    /// a schedule whose firings do genuinely different work, or one that parks on a signal and
    /// would otherwise block itself for ever — and it is not the default.
    /// </remarks>
    [Fact]
    public async Task ConcurrentLetsTheNextOccurrenceStartBesideTheRunningOne()
    {
        await using var bank = new GatedBank();
        await using var cluster = await SchedulerCluster.CreateAsync(bank, Cancellation.Token);

        cluster.Register(SchedulerCluster.Minutely, OverlapPolicy.Concurrent);

        var fleet = cluster.Nodes(2);

        cluster.Clock.Advance(TimeSpan.FromMinutes(1));

        var first = fleet[0].RunOnceAsync(Cancellation.Token).AsTask();

        await bank.EnteredAsync(Cancellation.Token);

        cluster.Clock.Advance(TimeSpan.FromMinutes(1));

        var second = fleet[1].RunOnceAsync(Cancellation.Token).AsTask();

        await bank.EnteredAsync(Cancellation.Token);

        (await cluster.StateOfAsync(SchedulerCluster.T0.AddMinutes(2), null, Cancellation.Token))
            .ShouldNotBeNull(
                "00:02 started while 00:01 was still in the bank read — which is what " +
                "Concurrent asks for, and why Skip is the default.");

        bank.Release();

        (await Task.WhenAll(first, second)).Sum(static r => r.Fired).ShouldBe(2);

        (await cluster.InstanceCountAsync(Cancellation.Token)).ShouldBe(2);
    }

    /// <summary>
    /// A schedule that is overlapping is not <em>also</em> refused by the store, and the two
    /// mechanisms are not the same mechanism.
    /// </summary>
    /// <remarks>
    /// Worth pinning because they look alike from the outside. The primary key refuses a
    /// <em>second attempt at one occurrence</em>, for ever; overlap refuses a <em>different
    /// occurrence</em> because an earlier one has not finished. Turning overlap off leaves the
    /// first mechanism entirely intact — which this asserts by declaring Concurrent and having four
    /// nodes race for one occurrence anyway.
    /// </remarks>
    [Fact]
    public async Task TheDuplicateRefusalIsUntouchedByTheOverlapPolicy()
    {
        await using var cluster = await SchedulerCluster.CreateAsync(Cancellation.Token);

        cluster.Register(SchedulerCluster.Hourly, OverlapPolicy.Concurrent);

        var fleet = cluster.Nodes(4);

        cluster.Clock.Advance(TimeSpan.FromHours(1));

        var reports = await Task.WhenAll(
            fleet.Select(node => node.RunOnceAsync(Cancellation.Token).AsTask()));

        reports.Sum(static r => r.Fired).ShouldBe(1);
        reports.Sum(static r => r.Contended).ShouldBe(3);

        (await cluster.InstanceCountAsync(Cancellation.Token)).ShouldBe(1);
    }
}

/// <summary>
/// A bank that answers only when the test says so.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A gate rather than a delay, because a delay is a race.</strong> The property under
/// test is "the second sweep runs while the first firing has not finished", and a test that
/// arranged that with <c>Task.Delay</c> would assert it on a fast machine and assert something
/// else on a loaded one. The gate makes the overlap window as long as the test needs and not one
/// instant longer.
/// </para>
/// <para>
/// It stands in for <c>IBank</c> rather than for the journal, deliberately: the flow, the engine,
/// the host, the lease and the instance row are all the real ones, and the only thing this
/// controls is how long an external read takes — which is where a real night's time goes too.
/// </para>
/// </remarks>
internal sealed class GatedBank : IBank, IAsyncDisposable
{
    private readonly SemaphoreSlim _entered = new(0);
    private readonly SemaphoreSlim _release = new(0);

    /// <summary>Waits until a flow has reached the bank read and is holding there.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="InvalidOperationException">No flow arrived.</exception>
    /// <remarks>
    /// Bounded rather than indefinite. A regression that stops the sweep firing at all would
    /// otherwise hang the whole suite with no output — which is how this test first behaved, and
    /// a test that hangs is harder to diagnose than one that fails.
    /// </remarks>
    public async Task EnteredAsync(CancellationToken cancellationToken)
    {
        if (!await _entered.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "No flow reached the bank read within 30 seconds. The sweep that was expected to " +
                "fire an occurrence fired nothing.");
        }
    }

    /// <summary>Lets every holder through, and every later caller straight through.</summary>
    public void Release() => _release.Release(int.MaxValue / 2);

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<StatementLine>>> StatementAsync(
        string? tenantId, DateTimeOffset closedAt, CancellationToken cancellationToken)
    {
        _entered.Release();

        await _release.WaitAsync(cancellationToken).ConfigureAwait(false);

        // One referenced line and one unreferenced, against the three entries the fixture books:
        // the same shape as the in-memory bank, so a report from a gated run reads the same.
        return Result.Ok<IReadOnlyList<StatementLine>>(
        [
            new StatementLine("ref-1", 12_50, SchedulerCluster.T0),
            new StatementLine(string.Empty, 99_00, SchedulerCluster.T0),
        ]);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _entered.Dispose();
        _release.Dispose();

        return ValueTask.CompletedTask;
    }
}
