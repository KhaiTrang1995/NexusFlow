using FlowX.Conformance.InMemory;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// The order in which a poll's two endings are asked about, which is the only thing about
/// <c>.OrSignal&lt;T&gt;()</c> that is a choice rather than a consequence.
/// </summary>
/// <remarks>
/// <para>
/// A poll arriving at its node asks three questions in a fixed order: <em>is the polling
/// already over</em> (the predicate, once an attempt has committed), <em>has a signal been
/// delivered</em>, and <em>is the next attempt due</em>. The middle one sits where it does for
/// two reasons, and each of them is a test below.
/// </para>
/// <para>
/// It is asked <strong>after</strong> the predicate, because <c>DurableExecution.PendingSignal</c>
/// is one slot per invocation: a poll that is already finished and re-walked on the way to a
/// later wait must not take a delivery that wait is open for. It is asked <strong>before</strong>
/// the schedule, because that is the sentence the construct exists to make true — a signal
/// arriving between two attempts ends the wait instead of parking it again.
/// </para>
/// <para>
/// The end-to-end behaviour against a real journal is
/// <c>FlowX.Postgres.Tests.PollSignalHostTests</c>; these two are about the branch order in one
/// method, which is cheaper to pin here and easy to get wrong anywhere.
/// </para>
/// </remarks>
public sealed class PollSignalTests
{
    private const string Signal = "ocr.completed";

    private static readonly FencingToken First = new(1);

    private static readonly DateTimeOffset T0 =
        new(2026, 8, 2, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A finished poll does not consume a delivery a later wait is open for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plan waits twice on one identity — a poll's second ending, then an ordinary
    /// suspension point — which is the shape that makes the order observable. The instance is
    /// parked at the <em>second</em> of them, and the resumed loop walks over the poll on its way
    /// there. Because the poll's predicate now holds, it is over, and asking the invocation for a
    /// signal there would take the one the suspension point is waiting for and leave the instance
    /// parked for its whole timeout on a delivery that was made.
    /// </para>
    /// <para>
    /// The absence of a poll-node row is the other half: a poll that ends on its predicate
    /// commits nothing of its own, so the only row this plan's step 1 can ever have is one that
    /// says a delivery ended it — and there is none.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFinishedPollDoesNotTakeADeliveryALaterWaitIsOpenFor()
    {
        var journal = new InMemoryFlowJournal();
        var plan = PollThenWait();
        var instanceId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        // The provider answers on the first attempt, so the poll is over before the suspension
        // point after it is ever reached.
        var first = new RecordingDispatcher().AnswerAt(1, true);

        var begun = await DurableExecution.BeginAsync(
            journal, plan, Plans.Invocation, instanceId, First, cancellationToken: ct);

        var parked = await new FlowEngine(new FakeClock(T0))
            .ExecuteAsync(plan, first, Plans.Invocation, begun.Value, ct);

        parked.IsSuspended.ShouldBeTrue("the flow stops at the suspension point after the poll");
        parked.Wake!.Value.StepId.ShouldBe(3, "which is where it is parked, not the poll");

        var resumed = await DurableExecution.ResumeAsync(journal, instanceId, new FencingToken(2), ct);
        var second = new RecordingDispatcher().AnswerAt(1, true);

        var finished = await new FlowEngine(new FakeClock(T0)).ExecuteAsync(
            plan,
            second,
            Plans.Invocation,
            resumed.Value.WithSignal(FlowSignal.Of(Signal, new Completion("job-1"))),
            ct);

        finished.IsSuccess.ShouldBeTrue(
            finished.Error?.ToString() ??
            "the delivery satisfied the wait it was addressed to, and the flow finished");

        second.Executed.ShouldBe(
            [3, 4],
            "the poll is over and steps over itself, the suspension point takes the signal, " +
            "and the step after it runs. No second attempt was made.");

        var rows = (await journal.ReadResumeFrontierAsync(instanceId, ct)).Value.Committed;

        rows.ShouldNotContain(
            row => row.Key.StepId == 1,
            "a poll that ended on its predicate commits no row of its own — the only row its " +
            "node ever writes is the one a delivery causes, and no delivery ended this poll.");
    }

    /// <summary>
    /// A delivery arriving between two attempts ends the wait rather than waiting for the
    /// schedule.
    /// </summary>
    /// <remarks>
    /// The clock does not move at all: the instance is parked on an instant an hour away and
    /// the signal is delivered at <c>T0</c>. So an engine that asked "is the next attempt due"
    /// before it asked "has anything arrived" would park again, and this test is the difference
    /// between the two orders.
    /// </remarks>
    [Fact]
    public async Task ADeliveryEndsTheWaitRatherThanWaitingForTheSchedule()
    {
        var journal = new InMemoryFlowJournal();
        var plan = PollThenWait();
        var instanceId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        // Never satisfied, so the only thing that can end this poll is the delivery.
        var first = new RecordingDispatcher().AnswerAt(1, false);

        var begun = await DurableExecution.BeginAsync(
            journal, plan, Plans.Invocation, instanceId, First, cancellationToken: ct);

        var parked = await new FlowEngine(new FakeClock(T0))
            .ExecuteAsync(plan, first, Plans.Invocation, begun.Value, ct);

        parked.IsSuspended.ShouldBeTrue();
        parked.Wake!.Value.StepId.ShouldBe(1, "parked at the poll, between two attempts");
        parked.Wake!.Value.At.ShouldBeGreaterThan(T0, "with the next attempt still in the future");

        var resumed = await DurableExecution.ResumeAsync(journal, instanceId, new FencingToken(2), ct);
        var second = new RecordingDispatcher().AnswerAt(1, false);

        var after = await new FlowEngine(new FakeClock(T0)).ExecuteAsync(
            plan,
            second,
            Plans.Invocation,
            resumed.Value.WithSignal(FlowSignal.Of(Signal, new Completion("job-1"))),
            ct);

        second.Executed.ShouldNotContain(
            2, "the wait ended on the delivery, so no second attempt was made");

        after.IsSuspended.ShouldBeTrue(
            "the flow continued past the poll and stopped at the suspension point after it");

        after.Wake!.Value.StepId.ShouldBe(
            3, "which is the satisfied path — one past the poll's one-step attempt");

        (await journal.ReadResumeFrontierAsync(instanceId, ct))
            .Value.Committed
            .Single(row => row.Key.StepId == 1)
            .CapabilityId
            .ShouldBe(Signal, "and the poll's own row names the ending that happened");
    }

    /// <summary>
    /// <c>0 upload · 1 poll(or ocr.completed) · 2 attempt · 3 await ocr.completed · 4 extract</c>.
    /// </summary>
    /// <remarks>
    /// Two waits on one identity, which is a shape the DSL permits and the journal orders: the
    /// open wait is the first with no committed row. It is written out here rather than built by
    /// a helper because the layout is what these tests are about — the poll's satisfied path is
    /// index 3, which is both "one past the one-step attempt" and "the suspension point", and a
    /// helper computing that would compute it the way the emitter does.
    /// </remarks>
    private static ExecutionPlan PollThenWait() => ExecutionPlan.Create(
        FlowDescriptor.Create(
            "document.process", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromHours(6)),
        StepGraph.Create([
            StepNode.ForCapability(0, Plans.Validate),
            StepNode.ForPoll(
                1,
                Backoff.Exponential(TimeSpan.FromHours(1), TimeSpan.FromHours(2)),
                TimeSpan.FromHours(4),
                signalType: Signal),
            StepNode.ForCapability(2, Plans.Reserve),
            StepNode.ForAwaitSignal(3, Signal, TimeSpan.FromHours(5)),
            StepNode.ForCapability(4, Plans.Capture),
        ]));

    /// <summary>What the webhook delivers. A contract, so the bag is keyed by it.</summary>
    private sealed record Completion(string JobId);
}
