using FlowX.Conformance.InMemory;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// What a <c>Durable</c> flow does when the clock, rather than a signal, is what it is
/// waiting for.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The half <c>SuspensionTests</c> deliberately left out.</strong> That class measures
/// a wait that a signal ends; this measures a wait that <em>nothing</em> ends, which until now
/// meant a wait that never ended at all. The duration an author wrote on
/// <c>.AwaitSignal&lt;T&gt;(timeout)</c> reached the plan and was armed by nothing, so the only
/// enforced budget on a waiting instance was its <c>[FlowDeadline]</c> — a coarser bound that
/// fails the flow instead of running the alternative the author declared.
/// </para>
/// <para>
/// <strong>A durable timer is state, not a thread.</strong> Nothing here sleeps. The engine
/// reads a clock, compares it against the instant the instance records that it must wake, and
/// either continues or suspends again — which is what makes a seven-day wait cost one row and
/// no process.
/// </para>
/// </remarks>
public sealed class TimerTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
    private static readonly FencingToken First = new(1);

    /// <summary>The seven-day wait from <c>06 §6</c>, under a thirty-day flow deadline.</summary>
    private static ExecutionPlan AwaitingSignature() => ExecutionPlan.Create(
        FlowDescriptor.Create(
            "offer.accept", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromDays(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Plans.Validate),
            StepNode.ForAwaitSignal(1, "contract.countersigned", TimeSpan.FromDays(7)),
            StepNode.ForCapability(2, Plans.Capture),
        ]));

    /// <summary>
    /// A wait whose declared timeout has passed ends, rather than waiting for ever.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the claim <c>FLOWX1031</c> was raised over, read from the run-time
    /// side.</strong> The plan carries the author's seven days —
    /// <c>StepNode.ForAwaitSignal(1, "contract.countersigned", TimeSpan.FromDays(7))</c> — and
    /// a resume eight days later found the instance exactly where it left it, suspended, with
    /// twenty-two days of <c>[FlowDeadline]</c> still to run.
    /// </para>
    /// <para>
    /// The flow declares no <c>OnTimeout</c> block, so the wait ending is the flow ending:
    /// <c>flow.signal_not_received</c> is a failure and the completed compensable steps unwind
    /// behind it. A flow that wants an alternative declares one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AWaitWhoseDeclaredTimeoutHasPassedEndsRatherThanWaitingForEver()
    {
        var journal = new InMemoryFlowJournal();
        var plan = AwaitingSignature();
        var clock = new FakeClock(T0);
        var instanceId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        var begun = await DurableExecution.BeginAsync(
            journal, plan, Plans.Invocation, instanceId, First, cancellationToken: ct);

        var suspended = await new FlowEngine(clock)
            .ExecuteAsync(plan, new RecordingDispatcher(), Plans.Invocation, begun.Value, ct);

        suspended.IsSuspended.ShouldBeTrue("the flow reached its suspension point");

        // Eight days: past the wait the author declared, and well inside the flow's own
        // thirty-day budget, so nothing but the wait itself can end this instance.
        clock.Advance(TimeSpan.FromDays(8));

        var resumed = await DurableExecution.ResumeAsync(journal, instanceId, new FencingToken(2), ct);

        var result = await new FlowEngine(clock)
            .ExecuteAsync(plan, new RecordingDispatcher(), Plans.Invocation, resumed.Value, ct);

        result.IsSuspended.ShouldBeFalse(
            "the countersignature had seven days to arrive and did not. An instance that " +
            "keeps waiting past the duration its author declared is waiting on nothing.");

        result.Error!.Code.ShouldBe(
            "flow.signal_not_received",
            "and it says which wait expired, rather than borrowing the flow's own deadline — " +
            "flow.deadline_exceeded would be true of a different instance twenty-two days later.");
    }
}
