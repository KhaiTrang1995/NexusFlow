using FlowX;
using FlowX.Conformance.InMemory;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace Workflow.Tests;

/// <summary>
/// What the old README promised, and what the platform does with it.
/// </summary>
/// <remarks>
/// <para>
/// <c>samples/workflow/README.md</c> used to claim "a multi-day process with human approvals,
/// escalations, timers and reversible steps". Three of those four are
/// <c>AwaitSignal</c>, <c>Delay</c> and <c>OnTimeout</c>, and none of them works. This class
/// measures the one of the three whose failure is observable from a test project: an
/// <c>AwaitSignal</c> step in a <c>Durable</c> flow does not wait.
/// </para>
/// <para>
/// <strong>The other two fail at compile time and are shown in the README instead</strong>,
/// because demonstrating them needs a flow that declares them and the whole point is that no
/// flow in this repository should. The README carries the generated output from a throwaway
/// project, with the command to reproduce it.
/// </para>
/// <para>
/// The plan below is hand-built, which every other test in this project avoids. It has to be:
/// the sample deliberately declares no suspension point, and the shape under test is one no
/// flow here is allowed to have.
/// </para>
/// </remarks>
public sealed class TheAbsentHalfTests
{
    private static readonly CapabilityDescriptor Validate =
        CapabilityDescriptor.Create("offer.validate", "1.0.0", isIdempotent: true);

    private static readonly CapabilityDescriptor Welcome =
        CapabilityDescriptor.Create("welcome.send", "1.0.0", isIdempotent: true);

    /// <summary>
    /// An <c>AwaitSignal</c> step completes immediately; nothing suspends and no signal is
    /// waited for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The generated dispatcher answers <c>StepOutcome.Success</c> for an <c>AwaitSignal</c>
    /// index — "Emit and AwaitSignal have no capability to call; the engine and the event
    /// plugin handle them" — and the engine has no case for <c>StepKind.AwaitSignal</c> at
    /// all, so the step falls through to the ordinary capability path and returns. The step
    /// after it runs on the same thread, in the same millisecond.
    /// </para>
    /// <para>
    /// So the degenerate behaviour is not a wait with a short timeout. It is no wait: a flow
    /// written to pause for a countersignature runs straight past the pause and does whatever
    /// came after it, with a clean journal and a successful result.
    /// </para>
    /// <para>
    /// <strong>Red when WP-63 lands.</strong> A durable suspension point makes this run stop
    /// at index 1, and this test with it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnAwaitSignalStepDoesNotWaitForAnything()
    {
        var plan = ExecutionPlan.Create(
            FlowDescriptor.Create(
                "probe.suspend", "1.0.0", ExecutionProfile.Durable, TimeSpan.FromSeconds(30)),
            StepGraph.Create(
            [
                StepNode.ForCapability(0, Validate),
                StepNode.ForAwaitSignal(1, "contract.signed", TimeSpan.FromDays(7)),
                StepNode.ForCapability(2, Welcome),
            ]));

        var clock = new FlowX.Testing.FlowTestClock();
        var journal = new InMemoryFlowJournal();
        var dispatcher = new CountingDispatcher();
        var ct = TestContext.Current.CancellationToken;

        var begun = await DurableExecution.BeginAsync(
            journal, plan, new FlowInvocation("corr-1", "key-1"), Guid.NewGuid(), new FencingToken(1),
            input: null, cancellationToken: ct);

        begun.IsSuccess.ShouldBeTrue("The journal opened the instance.");

        var started = clock.UtcNow;

        var result = await new FlowEngine(clock)
            .ExecuteAsync(plan, dispatcher, new FlowInvocation("corr-1", "key-1"), begun.Value, ct);

        result.IsSuccess.ShouldBeTrue("It did not suspend, and it did not fail either.");

        dispatcher.Executed.ShouldBe(
            [0, 1, 2],
            "Index 1 is the suspension point. It was dispatched like any other step and the " +
            "step after it ran.");

        clock.UtcNow.ShouldBe(started, "Nothing waited, so nothing moved the clock.");

        var frontier = await journal.ReadResumeFrontierAsync(journal.Instances[0].InstanceId, ct);

        frontier.Value.Committed.Count.ShouldBe(
            3,
            "Three step boundaries, three rows. A suspended instance would have two and a " +
            "pending timer, and there is no table for one.");

        journal.Instances[0].State.ShouldBe(
            FlowInstanceState.Completed,
            "The instance finished in a single invocation. A durable flow still runs to " +
            "completion inside one, which is what WP-63 changes.");
    }

    /// <summary>
    /// And this repository's own sample declares no suspension point, in either flow.
    /// </summary>
    /// <remarks>
    /// The guard on the finding above. A sample that used <c>AwaitSignal</c> would compile, run
    /// green, publish a manifest naming the signal, and quietly not wait — which is precisely
    /// the failure the old README documented as a feature.
    /// </remarks>
    [Fact]
    public void NeitherOfTheSamplesFlowsDeclaresASuspensionPoint()
    {
        OnboardEmployeeFlow.Plan.Graph.Steps
            .ShouldNotContain(step => step.Kind == StepKind.AwaitSignal);

        ProvisionWorkspaceFlow.Plan.Graph.Steps
            .ShouldNotContain(step => step.Kind == StepKind.AwaitSignal);
    }

    /// <summary>A dispatcher that records the indices the engine asked for and does nothing else.</summary>
    private sealed class CountingDispatcher : IStepDispatcher
    {
        private readonly List<int> _executed = [];

        public IReadOnlyList<int> Executed => _executed;

        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
        {
            _executed.Add(stepIndex);

            return ValueTask.FromResult(StepOutcome.Success);
        }

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct) =>
            ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx) => throw new NotSupportedException();

        public int Select(int stepIndex, FlowContext ctx) => throw new NotSupportedException();

        public IterationSource BeginIteration(int stepIndex, FlowContext ctx) => throw new NotSupportedException();

        public FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
            throw new NotSupportedException();
    }
}
