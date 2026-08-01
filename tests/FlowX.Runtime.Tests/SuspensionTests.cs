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

    /// <summary>
    /// A committed row for the step before the wait, and nothing for the wait itself.
    /// </summary>
    /// <remarks>
    /// The frontier <em>is</em> the resume position, so this is the same assertion as "the
    /// instance can be picked up and continued": <c>DurableExecution.Completed(scope, 1)</c>
    /// answers null, which is what makes the loop stop at index 1 again rather than step over
    /// it.
    /// </remarks>
    [Fact]
    public async Task TheSuspensionPointItselfHasNoRow()
    {
        var journal = new InMemoryFlowJournal();
        var instanceId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        await SuspendAsync(journal, instanceId);

        var frontier = await journal.ReadResumeFrontierAsync(instanceId, ct);

        frontier.Value.Committed.Select(row => row.Key.StepId).ShouldBe(
            [0],
            "one boundary, one row. A row for the suspension point would be a wait recorded " +
            "as having happened, and the resume would step over it.");
    }

    /// <summary>
    /// The signal resumes the instance through the same <c>ExecuteAsync</c> a scan resumes it
    /// through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>There is no second entry point, and that is the assertion.</strong> The lease
    /// token is raised, the frontier is read, the signal is attached to the session, and what
    /// is called is the overload <c>FlowHost.ResumeAsync</c> — and therefore
    /// <c>FlowRecoveryScan</c> — calls. Compensation ordering, deadline handling and
    /// <c>ForEach</c> scoping cannot drift between a signalled resume and a recovered one
    /// because there is one of each (ADR-0015).
    /// </para>
    /// <para>
    /// The steps before the wait are stepped over, not re-run: their rows are committed, so
    /// the ordinary frontier scan skips them, and a signalled resume gets that for free rather
    /// than by reimplementing it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheSignalResumesItThroughTheSameLoopARecoveryScanUses()
    {
        var journal = new InMemoryFlowJournal();
        var plan = AwaitingSignature();
        var instanceId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        await SuspendAsync(journal, instanceId);

        var resumed = await DurableExecution.ResumeAsync(journal, instanceId, new FencingToken(2), ct);

        resumed.IsSuccess.ShouldBeTrue("the fence was raised and the frontier read");

        var second = new RecordingDispatcher();

        var result = await new FlowEngine(new FakeClock(T0)).ExecuteAsync(
            plan,
            second,
            Plans.Invocation,
            resumed.Value.WithSignal(FlowSignal.Of("contract.countersigned", new Countersigned("ada"))),
            ct);

        result.IsSuccess.ShouldBeTrue(
            result.Error?.ToString() ?? "the signal satisfied the wait and the flow finished");

        second.Executed.ShouldBe(
            [1, 2],
            "the validated offer is stepped over — its row is committed — the suspension " +
            "point is satisfied, and the welcome pack runs.");

        var frontier = await journal.ReadResumeFrontierAsync(instanceId, ct);

        frontier.Value.Committed.Select(row => row.Key.StepId).ShouldBe(
            [0, 1, 2],
            "the delivered signal is journaled as the AwaitSignal step's own row, through the " +
            "same commit every other boundary uses. There is no signal table.");

        var instance = await journal.ReadInstanceAsync(instanceId, ct);

        instance.Value.State.ShouldBe(FlowInstanceState.Completed);
    }

    /// <summary>The step after the wait binds what the signal carried.</summary>
    /// <remarks>
    /// The payload is seeded into the state bag under the contract
    /// <c>.AwaitSignal&lt;TSignal&gt;(...)</c> named, so a later step reads it with
    /// <c>ctx.Get&lt;Countersigned&gt;()</c> exactly as it reads any earlier step's output.
    /// That is what makes a signal worth waiting for rather than a bare wake-up.
    /// </remarks>
    [Fact]
    public async Task TheStepAfterTheWaitBindsWhatTheSignalCarried()
    {
        var journal = new InMemoryFlowJournal();
        var plan = AwaitingSignature();
        var instanceId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        await SuspendAsync(journal, instanceId);

        var resumed = await DurableExecution.ResumeAsync(journal, instanceId, new FencingToken(2), ct);

        var second = new RecordingDispatcher
        {
            Observe = ctx => ctx.TryGet<Countersigned>(out var signed) ? signed.By : null,
        };

        await new FlowEngine(new FakeClock(T0)).ExecuteAsync(
            plan,
            second,
            Plans.Invocation,
            resumed.Value.WithSignal(FlowSignal.Of("contract.countersigned", new Countersigned("ada"))),
            ct);

        second.Observed.ShouldBe(
            ["ada", "ada"],
            "seeded before the suspension point is dispatched, so both it and the step after " +
            "it see the signal.");
    }

    /// <summary>
    /// A resume carrying no signal stops at the same wait, and does not skip it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what a recovery scan issues, and it is the reason picking up a waiting instance
    /// is harmless rather than a way of walking past the wait. Both shipped recovery indexes
    /// exclude <c>Suspended</c> from their candidate sets, so it should not happen at all —
    /// but "should not happen" is not a property, and the one that matters is that it is
    /// inert if it does.
    /// </para>
    /// <para>
    /// The instance is left <c>Suspended</c> a second time rather than <c>Running</c>, so a
    /// sweep cannot leave a waiting flow looking like abandoned work.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AResumeCarryingNoSignalStopsAtTheSameWait()
    {
        var journal = new InMemoryFlowJournal();
        var plan = AwaitingSignature();
        var instanceId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        await SuspendAsync(journal, instanceId);

        var resumed = await DurableExecution.ResumeAsync(journal, instanceId, new FencingToken(2), ct);
        var second = new RecordingDispatcher();

        var result = await new FlowEngine(new FakeClock(T0))
            .ExecuteAsync(plan, second, Plans.Invocation, resumed.Value, ct);

        result.IsSuspended.ShouldBeTrue("no signal arrived, so the wait is still open");
        second.Executed.ShouldBeEmpty("nothing ran: the only unexecuted step is the wait");

        var instance = await journal.ReadInstanceAsync(instanceId, ct);

        instance.Value.State.ShouldBe(FlowInstanceState.Suspended);
    }

    /// <summary>A signal the flow is not waiting for does not satisfy the wait.</summary>
    /// <remarks>
    /// Matched on the identity the plan carries, which is the same string the manifest
    /// publishes and the only thing a transport delivering
    /// <c>POST /flows/{id}/signals/{signalType}</c> has to know. A delivery that matched
    /// anything would let one signal satisfy every open wait in a flow.
    /// </remarks>
    [Fact]
    public async Task ASignalTheFlowIsNotWaitingForDoesNotSatisfyTheWait()
    {
        var journal = new InMemoryFlowJournal();
        var plan = AwaitingSignature();
        var instanceId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        await SuspendAsync(journal, instanceId);

        var resumed = await DurableExecution.ResumeAsync(journal, instanceId, new FencingToken(2), ct);

        var result = await new FlowEngine(new FakeClock(T0)).ExecuteAsync(
            plan,
            new RecordingDispatcher(),
            Plans.Invocation,
            resumed.Value.WithSignal(FlowSignal.Of("offer.withdrawn", new Countersigned("mallory"))),
            ct);

        result.IsSuspended.ShouldBeTrue(
            "the instance waits for contract.countersigned, and that is not what arrived");
    }

    /// <summary>
    /// An instance whose deadline has already passed times out rather than waiting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order in the loop is deliberate: the deadline is checked before the wait is
    /// evaluated. A suspension is the one ending nothing else looks at again — a waiting
    /// instance is not in any recovery scan's candidate set — so suspending past a deadline
    /// would leave an instance that no signal can usefully satisfy and no sweep will ever
    /// find.
    /// </para>
    /// <para>
    /// <strong>This is the whole of the timeout story in this release, and it is the flow's
    /// own budget rather than the wait's.</strong> The duration declared on
    /// <c>.AwaitSignal&lt;T&gt;(timeout)</c> reaches the plan and the manifest and nothing
    /// arms it: there is no timer, which is the other half of WP-63.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AWaitPastTheFlowsDeadlineTimesOutRatherThanWaiting()
    {
        var journal = new InMemoryFlowJournal();
        var plan = ExecutionPlan.Create(
            FlowDescriptor.Create(
                "offer.accept", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromSeconds(30)),
            AwaitingSignature().Graph);

        var clock = new FakeClock(T0);
        var instanceId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        var begun = await DurableExecution.BeginAsync(
            journal, plan, Plans.Invocation, instanceId, First, cancellationToken: ct);

        var dispatcher = new RecordingDispatcher
        {
            BeforeStep = _ => clock.Advance(TimeSpan.FromMinutes(1)),
        };

        var result = await new FlowEngine(clock)
            .ExecuteAsync(plan, dispatcher, Plans.Invocation, begun.Value, ct);

        result.IsSuspended.ShouldBeFalse("the budget was gone before the wait was reached");
        result.Error!.Code.ShouldBe(FlowErrors.DeadlineExceededCode);

        var instance = await journal.ReadInstanceAsync(instanceId, ct);

        instance.Value.State.ShouldBe(FlowInstanceState.TimedOut);
    }

    /// <summary>
    /// An inline composed child that suspends is refused, because the parent cannot wait for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The parent writes the composition's row when the child finishes. A child that waits
    /// writes no such row, so a parent resumed afterwards would find nothing committed at the
    /// composition and start a <em>second</em> child instance — repeating every effect the
    /// first one had. Refused where it is detectable rather than left to be discovered as a
    /// duplicate payment.
    /// </para>
    /// <para>
    /// The parent unwinds, which is the ordinary treatment of a failed composition and the
    /// right one here: the child's completed steps are real and this flow is not going to
    /// finish.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnInlineCompositionThatSuspendsIsRefusedRatherThanComposedTwice()
    {
        var journal = new InMemoryFlowJournal();
        var ct = TestContext.Current.CancellationToken;

        var parent = ExecutionPlan.Create(
            FlowDescriptor.Create(
                "offer.parent", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromDays(30)),
            StepGraph.Create([
                StepNode.ForCapability(0, Plans.Validate),
                StepNode.ForSubFlow(1, "offer.accept", SubFlowMode.Inline),
            ]));

        var child = AwaitingSignature();
        var childDispatcher = new RecordingDispatcher();

        var dispatcher = new RecordingDispatcher()
            .ComposeAt(1, child, childDispatcher, "an-offer");

        var instanceId = Guid.NewGuid();

        var begun = await DurableExecution.BeginAsync(
            journal, parent, Plans.Invocation, instanceId, First, cancellationToken: ct);

        var result = await new FlowEngine(new FakeClock(T0))
            .ExecuteAsync(parent, dispatcher, Plans.Invocation, begun.Value, ct);

        result.IsSuspended.ShouldBeFalse("a parent cannot wait at a child's suspension point");

        result.Error!.Code.ShouldBe(
            "flow.suspension_inside_composition",
            "and it says so by name, rather than composing the child twice on the next resume");
    }

    /// <summary>Runs the flow up to its suspension point and leaves it there.</summary>
    private static async Task SuspendAsync(InMemoryFlowJournal journal, Guid instanceId)
    {
        var plan = AwaitingSignature();
        var ct = TestContext.Current.CancellationToken;

        var begun = await DurableExecution.BeginAsync(
            journal, plan, Plans.Invocation, instanceId, First, cancellationToken: ct);

        begun.IsSuccess.ShouldBeTrue("the journal opened the instance");

        var result = await new FlowEngine(new FakeClock(T0))
            .ExecuteAsync(plan, new RecordingDispatcher(), Plans.Invocation, begun.Value, ct);

        result.IsSuspended.ShouldBeTrue("the flow reached its suspension point");
    }

    /// <summary>What the countersignature carries. A contract, so the bag is keyed by it.</summary>
    private sealed record Countersigned(string By);
}
