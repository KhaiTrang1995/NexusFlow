using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace FlowX.Runtime.Tests;

/// <summary>
/// Composition as the engine sees it: a <see cref="StepKind.SubFlow"/> node that names
/// another flow's whole plan, run as a second, independent execution.
/// </summary>
/// <remarks>
/// <para>
/// The tests are shaped around the four claims that are easy to get wrong and invisible
/// from a passing smoke test. First, that the parent's array really is unchanged — one
/// index, no target, the child's steps nowhere in it. Second, that the child's budget is
/// the smaller of the two deadlines, because a composition that could lengthen one would
/// let a caller buy a flow more time than its author allowed. Third, that a child which
/// succeeded is still undone when the <em>parent</em> later fails, in strict reverse across
/// the boundary — which is the whole difference between composing a saga and losing one.
/// Fourth, that a detached child is genuinely detached and is still drained.
/// </para>
/// <para>
/// Two dispatchers throughout, with one shared <see cref="RecordingDispatcher.Trace"/>.
/// Two lists of step indices cannot answer "did the parent's undo run before the child's",
/// because index 2 means a different step in each; a shared, named log can.
/// </para>
/// </remarks>
public sealed class SubFlowTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static FlowEngine Engine() => new(new FakeClock(T0));

    private static readonly Error Declined =
        new("payment.declined", "declined", ErrorCategory.Conflict);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The child's input contract — a value, mapped before the child starts.</summary>
    private sealed record FulfilOrder(string OrderId);

    /// <summary>Builds a parent and its child, sharing one trace.</summary>
    private static (RecordingDispatcher Parent, RecordingDispatcher Child, List<string> Trace) Pair(
        int at = 1, ExecutionPlan? childPlan = null)
    {
        var trace = new List<string>();
        var child = new RecordingDispatcher().As("child", trace);
        var parent = new RecordingDispatcher().As("parent", trace);

        parent.ComposeAt(at, childPlan ?? Plans.Child(), child, new FulfilOrder("o-1"));

        return (parent, child, trace);
    }

    // ------------------------------------------------------------- the flat array survives

    [Fact]
    public async Task TheCompositionIsOneIndexAndTheChildsStepsAreNotInTheParentsArray()
    {
        var (parent, child, trace) = Pair();

        var result = await Engine().ExecuteAsync(Plans.Composing(), parent, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeTrue();

        parent.Executed.ShouldBe([0, 2, 3],
            "Step 1 is the composition. The parent's dispatcher is never asked to execute " +
            "it — the engine handles it — and control resumes at the ordinary next index, " +
            "because a sub-flow node carries no target.");

        child.Executed.ShouldBe([0, 1],
            "The child's steps are numbered in the child's own array, from zero. If they " +
            "had been spliced into the parent they would be 2 and 3 here.");

        trace.ShouldBe(
            ["parent.run.0", "child.run.0", "child.run.1", "parent.run.2", "parent.run.3"],
            "The child runs to completion between the step before the composition and the " +
            "step after it, which is what 'synchronous' means.");

        result.CompletedSteps.ShouldBe(5,
            "The child's steps count towards the parent's total. They really ran, and the " +
            "whole point of composing is that the child's effects are the flow's effects.");
    }

    [Fact]
    public async Task TheMappingRunsOnceAndOnTheParentsThreadBeforeTheChildStarts()
    {
        var (parent, child, trace) = Pair();

        await Engine().ExecuteAsync(Plans.Composing(), parent, Plans.Invocation, Ct);

        parent.Composed.ShouldBe([1],
            "Once. In a durable flow the mapping has to produce the same input on replay, " +
            "and evaluating it twice is both a cost and an invitation to disagree with " +
            "itself.");

        trace.IndexOf("parent.run.0").ShouldBeLessThan(trace.IndexOf("child.run.0"));
        child.Snapshots.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task TheChildSeesItsOwnInputAndItsOwnFlowIdentity()
    {
        var (parent, child, _) = Pair();

        // Read while the step is running: the child's context is pooled too, so anything
        // taken off it afterwards reads a cleared instance.
        child.Observe = ctx => ctx.Get<FulfilOrder>().OrderId;

        await Engine().ExecuteAsync(Plans.Composing(), parent, Plans.Invocation, Ct);

        child.Observed[0].ShouldBe("o-1",
            "Seeded into the child's own context by the parent's dispatcher, which is the " +
            "one piece of code that knows the child's input type.");

        child.Snapshots[0].FlowId.ShouldBe("order.fulfil",
            "The child runs under its own plan, so it has its own identity in traces and " +
            "in errors — not the parent's.");
    }

    [Fact]
    public async Task TheChildsContextIsNotTheParents()
    {
        // The property that makes a detached child safe, asserted for the inline case too
        // because it is the same mechanism. If the child ran on the parent's context, a
        // detached one outliving its parent would be reading a pooled instance that had
        // since been reset and rented to another tenant's flow.
        var (parent, child, _) = Pair();

        await Engine().ExecuteAsync(Plans.Composing(), parent, Plans.Invocation, Ct);

        foreach (var childContext in child.ContextsSeen)
        {
            parent.ContextsSeen.ShouldNotContain(childContext);
        }
    }

    // ----------------------------------------------------------- deadline and correlation

    [Fact]
    public async Task TheChildInheritsCorrelationTenantAndIdempotency()
    {
        var (parent, child, _) = Pair();

        await Engine().ExecuteAsync(Plans.Composing(), parent, Plans.Invocation, Ct);

        var seen = child.Snapshots[0];

        seen.CorrelationId.ShouldBe("corr-1");
        seen.TenantId.ShouldBe("acme");
        seen.IdempotencyKey.ShouldBe("idem-1",
            "One operation, so one deduplication identity: a retry of the parent has to " +
            "present downstream systems with the key they already saw.");
    }

    [Fact]
    public async Task TheChildsBudgetIsTheSmallerOfTheTwoDeadlines()
    {
        // The parent declares thirty seconds and the child ten, so the child gets ten.
        var (parent, child, _) = Pair();

        await Engine().ExecuteAsync(Plans.Composing(), parent, Plans.Invocation, Ct);

        parent.DeadlinesSeen[0].ShouldBe(T0.AddSeconds(30));
        child.DeadlinesSeen[0].ShouldBe(T0.AddSeconds(10));
    }

    [Fact]
    public async Task AChildCannotBuyItselfMoreTimeThanItsParentHas()
    {
        // The child declares a *longer* deadline than the parent. Composition may only ever
        // shorten — a caller must not be able to reach past a flow's own budget by wrapping
        // it, which is the same rule FlowInvocation.Deadline states for a trigger.
        var (parent, child, _) = Pair(childPlan: Plans.Child(TimeSpan.FromMinutes(5)));

        await Engine().ExecuteAsync(Plans.Composing(), parent, Plans.Invocation, Ct);

        child.DeadlinesSeen[0].ShouldBe(T0.AddSeconds(30),
            "The parent's absolute instant, not the child's five minutes. A composition " +
            "that lengthened the budget would make the parent's deadline advisory.");
    }

    // ------------------------------------------------------------------- failure downwards

    [Fact]
    public async Task AChildsFailureFailsTheParentAndKeepsItsOwnReason()
    {
        var (parent, child, trace) = Pair();
        child.FailAt(1, Declined);

        var result = await Engine().ExecuteAsync(Plans.Composing(), parent, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("payment.declined",
            "The child's own error, not a wrapper. Replacing it would throw away the one " +
            "fact an operator needs and make every composition look identical in the logs.");
        result.Error.Category.ShouldBe(ErrorCategory.Conflict,
            "The category is the child's too: a declined payment is a conflict whether or " +
            "not it happened one flow down, and relabelling it at the boundary would change " +
            "what the HTTP mapping returns and whether the caller retries.");

        var data = result.Error!.Data.ShouldNotBeNull();

        data["subFlowId"].ShouldBe("order.fulfil");
        data["subFlowStepIndex"].ShouldBe(1);
        data["subFlowOf"].ShouldBe("order.place");

        trace.ShouldNotContain("parent.run.2",
            "The step after the composition must not run: a synchronous child's failure is " +
            "the parent's failure.");
    }

    [Fact]
    public async Task AFailingChildUnwindsItselfBeforeTheParentHearsAboutIt()
    {
        var (parent, child, trace) = Pair();
        child.FailAt(1, Declined);

        var result = await Engine().ExecuteAsync(Plans.Composing(), parent, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeFalse();
        child.Compensated.ShouldBe([0],
            "The child's step 0 reserved something and its step 1 failed, so the child undoes " +
            "its own work. Handing the parent a half-finished saga to think about would mean " +
            "the child's failure left the child's own effects standing.");

        trace.ShouldBe(["parent.run.0", "child.run.0", "child.run.1", "child.undo.0"]);
    }

    [Fact]
    public async Task AMappingThatThrowsFailsTheFlowAndCompensatesWhatRanBeforeIt()
    {
        var (parent, child, _) = Pair();
        parent.ThrowAtSubFlow = 1;

        var result = await Engine().ExecuteAsync(Plans.Composing(), parent, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("flow.subflow_mapping_failed",
            "Its own code, not the switch selector's or the loop's: the three point at " +
            "different lines and at different mistakes.");
        child.Executed.ShouldBeEmpty("The child never started, because there was no input.");
        parent.Executed.ShouldBe([0]);
    }

    // ------------------------------------------------------- compensation, upwards

    [Fact]
    public async Task AChildThatSucceededIsUndoneWhenTheParentLaterFails()
    {
        // The claim this whole shape rests on. A saga's guarantee is that when the
        // operation fails, everything it completed is undone — and FLOWX1005 tells people
        // to extract shared steps into a sub-flow, so composing must not silently weaken it.
        var (parent, child, trace) = Pair();
        parent.FailAt(2, Declined);

        var result = await Engine().ExecuteAsync(Plans.Composing(), parent, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeFalse();
        result.Compensation.ShouldBe(CompensationOutcome.Succeeded);

        child.Compensated.ShouldBe([0],
            "The child succeeded, so it did not undo itself. The parent then failed, and " +
            "the reservation the child made is part of what the parent has to give back.");

        trace.ShouldBe(
            ["parent.run.0", "child.run.0", "child.run.1", "parent.run.2", "child.undo.0"]);
    }

    [Fact]
    public async Task StrictReverseSurvivesTheBoundary()
    {
        // Parent completes A, child completes X, parent completes B; B fails nothing but a
        // later step does. The unwind must be B, X, A — exactly what it would have been had
        // the child's steps been written inline.
        var trace = new List<string>();
        var child = new RecordingDispatcher().As("child", trace);
        var parent = new RecordingDispatcher().As("parent", trace);

        parent.ComposeAt(1, Plans.Child(), child, new FulfilOrder("o-1"));

        var plan = ExecutionPlan.Create(
            FlowDescriptor.Create("order.place", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([
                StepNode.ForCapability(0, Plans.Reserve, Plans.Release),
                StepNode.ForSubFlow(1, "order.fulfil"),
                StepNode.ForCapability(2, Plans.Capture, Plans.Refund),
                StepNode.ForCapability(3, Plans.Validate),
            ]));

        parent.FailAt(3, Declined);

        var result = await Engine().ExecuteAsync(plan, parent, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeFalse();

        trace[^3..].ShouldBe(["parent.undo.2", "child.undo.0", "parent.undo.0"],
            "Newest first, and the composition unwinds as one unit in the position it " +
            "occupied. The parent's stack supplies the outer order and the child's the " +
            "inner; neither has to know about the other.");
    }

    [Fact]
    public async Task ThePlanIsCompensableBecauseOfTheCompositionAlone()
    {
        // A parent with no compensable step of its own. If an inline sub-flow node did not
        // report IsCompensable, this plan would carry no compensation stack at all and the
        // child's completed work would be silently unrecoverable.
        var plan = Plans.ComposingOnly();

        plan.HasCompensation.ShouldBeTrue();
        plan.CompensableStepIndices.ShouldBe([0]);

        var trace = new List<string>();
        var child = new RecordingDispatcher().As("child", trace);
        var parent = new RecordingDispatcher().As("parent", trace);

        parent.ComposeAt(0, Plans.Child(), child, new FulfilOrder("o-1"));
        parent.FailAt(1, Declined);

        var result = await Engine().ExecuteAsync(plan, parent, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeFalse();
        child.Compensated.ShouldBe([0]);
    }

    [Fact]
    public async Task ACompensationThatFailsInsideAChildIsReportedByTheParent()
    {
        var (parent, child, _) = Pair();
        parent.FailAt(2, Declined);
        child.FailCompensationAt(0, Declined);

        var result = await Engine().ExecuteAsync(Plans.Composing(), parent, Plans.Invocation, Ct);

        result.Compensation.ShouldBe(CompensationOutcome.PartiallyFailed,
            "A broken undo one flow down is still a broken undo. Reporting the parent's " +
            "unwind as successful would hide it exactly where it matters most.");
    }

    [Fact]
    public async Task NestedCompositionUnwindsInnermostFirst()
    {
        var trace = new List<string>();
        var inner = new RecordingDispatcher().As("inner", trace);
        var middle = new RecordingDispatcher().As("middle", trace);
        var outer = new RecordingDispatcher().As("outer", trace);

        middle.ComposeAt(0, Plans.Child(), inner, new FulfilOrder("o-1"));
        outer.ComposeAt(1, Plans.ComposingChild(), middle, new FulfilOrder("o-1"));
        outer.FailAt(2, Declined);

        var result = await Engine().ExecuteAsync(Plans.Composing(), outer, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeFalse();
        inner.Compensated.ShouldBe([0],
            "Two boundaries deep, and the undo still reaches it. Each level records its " +
            "child as one entry on its own stack, so the recursion is the same at every " +
            "level rather than a special case at the second.");
    }

    // --------------------------------------------------------------------------- detached

    [Fact]
    public async Task ADetachedChildDoesNotFailItsParent()
    {
        var (parent, child, _) = Pair();
        child.FailAt(0, Declined);

        var engine = Engine();
        var result = await engine.ExecuteAsync(
            Plans.Composing(SubFlowMode.Detached), parent, Plans.Invocation, Ct);

        (await engine.WaitForDetachedAsync(TimeSpan.FromSeconds(10), Ct)).ShouldBeTrue();

        result.IsSuccess.ShouldBeTrue(
            "Fire and forget. The parent does not wait for the child and does not hear " +
            "about its failure — that is what the mode means.");
        parent.Executed.ShouldBe([0, 2, 3]);
    }

    [Fact]
    public async Task ADetachedChildGetsItsOwnDeadlineRatherThanTheParentsRemaining()
    {
        var (parent, child, _) = Pair();

        var engine = Engine();
        await engine.ExecuteAsync(Plans.Composing(SubFlowMode.Detached), parent, Plans.Invocation, Ct);
        await engine.WaitForDetachedAsync(TimeSpan.FromSeconds(10), Ct);

        child.DeadlinesSeen[0].ShouldBe(T0.AddSeconds(10),
            "Its own ten seconds, measured from its own start. A child that outlives its " +
            "parent cannot inherit a budget that expires with the parent.");
    }

    [Fact]
    public async Task ADetachedChildCompensatesItselfAndNotThroughTheParent()
    {
        var (parent, child, _) = Pair();
        child.FailAt(1, Declined);

        var engine = Engine();
        var result = await engine.ExecuteAsync(
            Plans.Composing(SubFlowMode.Detached), parent, Plans.Invocation, Ct);

        (await engine.WaitForDetachedAsync(TimeSpan.FromSeconds(10), Ct)).ShouldBeTrue();

        result.Compensation.ShouldBe(CompensationOutcome.NotRequired,
            "The parent succeeded, so it has nothing to undo — including the detached " +
            "child, whose lifecycle is its own.");
        child.Compensated.ShouldBe([0],
            "The child still undoes its own work. Detached means nobody else is " +
            "responsible for it, not that nobody is.");
    }

    [Fact]
    public async Task ADetachedChildIsCountedSoADrainCanWaitForIt()
    {
        // The property that makes Detached shippable at all. FlowHost counts the flows it
        // starts; a child started from inside a step is not one of them, so without this a
        // drain would report success while a fire-and-forget saga was mid-way through.
        var (parent, child, _) = Pair();
        child.YieldAt(0);

        var engine = Engine();

        engine.DetachedInFlight.ShouldBe(0);

        await engine.ExecuteAsync(Plans.Composing(SubFlowMode.Detached), parent, Plans.Invocation, Ct);

        (await engine.WaitForDetachedAsync(TimeSpan.FromSeconds(10), Ct)).ShouldBeTrue();

        engine.DetachedInFlight.ShouldBe(0);
        child.Executed.ShouldBe([0, 1],
            "And it really finished, rather than the wait merely returning.");
    }

    [Fact]
    public async Task ADetachedChildIsNotCancelledByItsParentsToken()
    {
        var (parent, child, _) = Pair();
        child.YieldAt(0);

        using var cancellation = new CancellationTokenSource();
        var engine = Engine();

        await engine.ExecuteAsync(
            Plans.Composing(SubFlowMode.Detached), parent, Plans.Invocation, cancellation.Token);

        await cancellation.CancelAsync();

        (await engine.WaitForDetachedAsync(TimeSpan.FromSeconds(10), Ct)).ShouldBeTrue();

        child.Executed.ShouldBe([0, 1],
            "A child whose whole point is to outlive its parent must not be cancelled when " +
            "the parent's token is.");
    }

    // ------------------------------------------------------------------------ termination

    [Fact]
    public async Task CompositionDeeperThanTheCapFailsTheFlowRatherThanTheProcess()
    {
        // A cycle FLOWX1021 cannot see — one closed through a referenced assembly — is
        // unbounded recursion. The cap turns it into one failed flow.
        var trace = new List<string>();
        var dispatcher = new RecordingDispatcher().As("loop", trace);

        var plan = ExecutionPlan.Create(
            FlowDescriptor.Create("order.loop", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromMinutes(5)),
            StepGraph.Create([StepNode.ForSubFlow(0, "order.loop")]));

        // The flow composes itself, and its dispatcher is its own child's dispatcher.
        dispatcher.ComposeAt(0, plan, dispatcher, new FulfilOrder("o-1"));

        var result = await Engine().ExecuteAsync(plan, dispatcher, Plans.Invocation, Ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("flow.subflow_too_deep");
        result.Error!.Data.ShouldNotBeNull()["maxDepth"].ShouldBe(FlowEngine.MaxSubFlowDepth);

        dispatcher.Composed.Count.ShouldBe(FlowEngine.MaxSubFlowDepth,
            "One composition per level up to the cap, and then a refusal taken before the " +
            "mapping runs — not a stack overflow, and not silence.");
    }

    [Fact]
    public void AwaitCompletionIsUnrepresentableInAPlan()
    {
        // FLOWX1026 refuses it at build time; this refuses it in the one place a plan can
        // be built by hand. Neither degenerate form is honest: running it inline changes the
        // parent's deadline and failure semantics, and skipping it drops business logic.
        var thrown = Should.Throw<InvalidFlowPlanException>(
            () => StepNode.ForSubFlow(0, "order.fulfil", SubFlowMode.AwaitCompletion));

        thrown.Message.ShouldContain("journal");
    }
}
