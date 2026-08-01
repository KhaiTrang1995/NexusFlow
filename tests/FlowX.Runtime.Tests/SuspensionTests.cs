using FlowX.Conformance.InMemory;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// What a <c>Durable</c> flow does when it reaches a suspension point, and what brings it
/// back.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The property under test is the one sentence
/// <c>docs/adr/ADR-0015-journal-schema-and-durable-execution.md</c> owed to WP-63:</strong>
/// <em>"a durable flow still runs to completion inside one invocation"</em> stops being true.
/// The invocation returns at the suspension point, the instance stays in the journal at its
/// resume frontier, and a later signal re-enters the <em>same</em> <c>ExecuteAsync</c> that a
/// recovery scan re-enters — there is no second loop to drift.
/// </para>
/// <para>
/// <strong>The journal is the conformance suite's reference implementation</strong>, for the
/// reason <c>DurableSeamTests</c> gives: a store written for a test is a store nothing holds
/// to <c>JournalConformance</c>, and fencing is the first thing it would get wrong.
/// </para>
/// </remarks>
public sealed class SuspensionTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
    private static readonly FencingToken First = new(1);

    /// <summary>
    /// Validate, wait for a countersignature, welcome — the shape <c>06 §6</c> draws.
    /// </summary>
    private static ExecutionPlan AwaitingSignature() => ExecutionPlan.Create(
        FlowDescriptor.Create(
            "offer.accept", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromDays(30)),
        StepGraph.Create([
            StepNode.ForCapability(0, Plans.Validate),
            StepNode.ForAwaitSignal(1, "contract.countersigned", TimeSpan.FromDays(7)),
            StepNode.ForCapability(2, Plans.Capture),
        ]));

    /// <summary>
    /// The invocation returns at the suspension point, with the step after it never asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The engine had no <c>case StepKind.AwaitSignal</c> at all, so the step fell through to
    /// the ordinary capability path, the generated dispatcher answered
    /// <c>StepOutcome.Success</c> for it — <em>"Emit and AwaitSignal have no capability to
    /// call"</em> — and the step after it ran in the same millisecond. That is what this
    /// asserts is no longer the case.
    /// </para>
    /// <para>
    /// Asserted on the dispatcher rather than on the clock, because the clock never moved
    /// either way: the defect was not a wait that was too short, it was no wait at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ADurableFlowThatReachesAnAwaitSignalStopsThere()
    {
        var journal = new InMemoryFlowJournal();
        var plan = AwaitingSignature();
        var dispatcher = new RecordingDispatcher();
        var instanceId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        var begun = await DurableExecution.BeginAsync(
            journal, plan, Plans.Invocation, instanceId, First, cancellationToken: ct);

        begun.IsSuccess.ShouldBeTrue("the journal opened the instance");

        var result = await new FlowEngine(new FakeClock(T0))
            .ExecuteAsync(plan, dispatcher, Plans.Invocation, begun.Value, ct);

        dispatcher.Executed.ShouldBe(
            [0],
            "index 1 is the suspension point: the flow stops there, so the welcome pack at " +
            "index 2 is never dispatched.");

        result.IsSuccess.ShouldBeFalse(
            "a flow that is waiting has not completed, and reporting success would let a " +
            "caller project a .Return clause over steps that never ran.");

        var instance = await journal.ReadInstanceAsync(instanceId, ct);

        instance.Value.State.ShouldBe(
            FlowInstanceState.Suspended,
            "the instance stays in the journal at its resume frontier — one row, no thread, " +
            "no lease.");
    }
}
