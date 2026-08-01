using FlowX;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace Workflow.Tests;

/// <summary>
/// The unwind: strict reverse, across a fork, a loop and a composed child.
/// </summary>
/// <remarks>
/// <para>
/// A saga's guarantee is about what <em>happened</em>, not about who decided it, so these
/// cover a business rejection, a dependency failure and a branch failure with the same
/// assertions. The engine cannot tell them apart — a <c>Fail</c> arrives at the step loop
/// through the same call a declined dependency comes back on.
/// </para>
/// <para>
/// <strong>Two orders in here are deliberately asserted as a set rather than a sequence.</strong>
/// The fork's branches genuinely interleave, so which of <c>hardware.order</c> and
/// <c>access.grant</c> completed last is the thread pool's business and their unwind order
/// follows it. Everything sequential is asserted exactly.
/// </para>
/// </remarks>
public sealed class CompensationTests
{
    private static readonly string[] ForkUndo = ["access.revoke", "hardware.cancel"];

    /// <summary>
    /// A failure after everything has completed unwinds the child, the loop, the fork and the
    /// main line, in strict reverse.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the assertion the whole sample exists to make.</strong> The forward
    /// order is <c>payroll · identity · (laptop ‖ access ‖ induction) · assign · assign ·
    /// child(desk · pass)</c>, and the unwind is that order read backwards <em>through</em>
    /// the composition: the parent records the child as one entry in its own stack, so a
    /// parent that completed <c>A</c>, then a child that completed <c>X</c> and <c>Y</c>, then
    /// nothing, unwinds <c>Y, X, A</c> — the same order the steps would have had written
    /// inline. Extracting steps into a sub-flow does not weaken the saga, and this is the
    /// test that says so about this sample rather than about a hand-built plan.
    /// </para>
    /// <para>
    /// <c>induction.book</c> declares no compensation and therefore contributes nothing to the
    /// unwind. A branch with no inverse is not a hole; it is a step whose effect nobody asked
    /// to reverse.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task FailingLateUnwindsEveryCompletedStepInStrictReverse()
    {
        var harness = OnboardingHarness.Create()
            .Substitute("welcome.send", new Error("post.unavailable", "the post room is shut", ErrorCategory.Unavailable));

        var run = await harness.RunAsync(Offers.Permanent(), TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeFalse(run.ToString());
        run.Error!.Code.ShouldBe("post.unavailable");
        run.Compensation.ShouldBe(CompensationOutcome.Succeeded);

        var undone = run.Trace.Compensated;

        undone.Count.ShouldBe(8, run.ToString());

        // The composed child, innermost and last to complete, unwinds first — and in its own
        // reverse order, not the order it was declared in.
        undone.Take(2).ShouldBe(
            ["workspace.cancel_pass", "workspace.release_desk"],
            "The child's two steps, reversed. " + run);

        // Then the loop, one undo per element that completed.
        undone.Skip(2).Take(2).ShouldBe(
            ["equipment.return", "equipment.return"],
            "One per element, both of which had completed. " + run);

        // Then the fork's two compensable branches, in whichever order they finished.
        undone.Skip(4).Take(2).Order(StringComparer.Ordinal).ShouldBe(
            ForkUndo.Order(StringComparer.Ordinal),
            "A fork's branches interleave, so their completion order — and therefore their " +
            "unwind order — is the scheduler's. That both are present is the assertion. " + run);

        // Then the main line, in reverse.
        undone.Skip(6).ShouldBe(["identity.disable", "payroll.close"], run.ToString());

        // And the effects themselves, on the real adapters. A saga that leaks a desk, a
        // laptop or a directory account is the defect these tests exist to catch.
        harness.World.Facilities.HeldDesks.ShouldBe(0);
        harness.World.Assets.Assigned.ShouldBeEmpty();
        harness.World.Assets.OutstandingOrders.ShouldBe(0);
        harness.World.Access.OutstandingGrants.ShouldBe(0);
        harness.World.People.OpenAccounts.ShouldBe(0);
        harness.World.People.OpenPayrollRecords.ShouldBe(0);
    }

    /// <summary>
    /// Each element's undo reverses its own element, in reverse element order.
    /// </summary>
    /// <remarks>
    /// The two compensations above carry the same capability id, so the trace alone cannot
    /// tell which element each one undid. This substitutes the undo for a stand-in that reads
    /// the element out of the scope the engine hands it — which is exactly what the generated
    /// <c>CompensateAsync</c> does — and records what it saw.
    /// </remarks>
    [Fact]
    public async Task EachElementsUndoReversesItsOwnElement()
    {
        var undone = new List<string>();
        var harness = OnboardingHarness.Create();

        harness
            .Substitute("welcome.send", new Error("post.unavailable", "shut", ErrorCategory.Unavailable))
            .Substitute("equipment.return", (ctx, _) =>
            {
                // The element type, which is what the iteration's scope resolves. Reading
                // ApprovedEquipment here — the type the body step produced — falls through to
                // the flow's shared bag and yields whichever element wrote it last.
                undone.Add(ctx.Get<EquipmentRequest>().Item);

                return ValueTask.FromResult(StepOutcome.Success);
            });

        var run = await harness.RunAsync(Offers.Permanent(), TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeFalse(run.ToString());

        undone.ShouldBe(
            ["phone", "laptop-bag"],
            "Reverse element order, and each undo bound to its own element rather than to " +
            "whichever item the loop ended on. " + run);
    }

    /// <summary>
    /// An undo sees its own element and the loop's <em>last</em> shared value, at the same time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The two halves of the scoping rule, measured side by side.</strong>
    /// <c>EquipmentRequest</c> is the loop's element and resolves through the iteration's own
    /// view of the context, so each undo sees the item it is undoing. <c>ApprovedEquipment</c>
    /// is what a body step <em>returned</em>, which goes into the flow's shared bag keyed by
    /// contract type — so after the loop it holds one value, and both undos see the last
    /// element's.
    /// </para>
    /// <para>
    /// This is why <c>equipment.assign</c> binds the element. A compensation is invoked with
    /// the input of the step it undoes, so binding the arm's output would have made the second
    /// column the one that mattered — and the flow would have returned the same item twice
    /// while reporting a clean unwind. Fixing the scoping in the engine would turn the second
    /// assertion red, which is the only kind of note about a known limit that survives.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnUndoSeesItsOwnElementAndTheLoopsLastSharedValue()
    {
        var scoped = new List<string>();
        var shared = new List<string>();
        var harness = OnboardingHarness.Create();

        harness
            .Substitute("welcome.send", new Error("post.unavailable", "shut", ErrorCategory.Unavailable))
            .Substitute("equipment.return", (ctx, _) =>
            {
                scoped.Add(ctx.Get<EquipmentRequest>().Item);
                shared.Add(ctx.Get<ApprovedEquipment>().Item);

                return ValueTask.FromResult(StepOutcome.Success);
            });

        var run = await harness.RunAsync(Offers.Permanent(), TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeFalse(run.ToString());

        scoped.ShouldBe(["phone", "laptop-bag"], "The element is scoped to its iteration.");

        shared.ShouldBe(
            ["phone", "phone"],
            "And what a body step returned is not. One slot, keyed by contract type, holding " +
            "whichever element wrote it last.");
    }

    /// <summary>
    /// A branch that fails cancels its siblings and unwinds whatever the fork had already done.
    /// </summary>
    /// <remarks>
    /// Under <c>AllMustSucceed</c> the first failure cancels the remaining branches through a
    /// linked token. Whatever a cancelled branch had already completed is still work that
    /// happened, so it is still counted and still unwound — which is why
    /// <c>hardware.cancel</c> may or may not appear here but <c>identity.disable</c> and
    /// <c>payroll.close</c> always do.
    /// </remarks>
    [Fact]
    public async Task AFailedForkBranchUnwindsTheStepsBeforeTheFork()
    {
        var harness = OnboardingHarness.Create()
            .Substitute("access.grant", new Error("access.denied", "the role has no template", ErrorCategory.Conflict));

        var run = await harness.RunAsync(Offers.Permanent(), TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeFalse(run.ToString());
        run.Error!.Code.ShouldBe("access.denied");
        run.Compensation.ShouldBe(CompensationOutcome.Succeeded);

        run.Trace.Ran("equipment.assign").ShouldBeFalse(
            "The fork failed, so control never reached the join target. " + run);

        run.Trace.Compensated.TakeLast(2).ShouldBe(
            ["identity.disable", "payroll.close"],
            "Whatever the fork undid, the steps before it unwind after it and in order. " + run);

        run.Trace.Compensated.ShouldNotContain("access.revoke",
            "The branch that failed never completed, so there is nothing of its own to undo. " + run);

        harness.World.People.OpenAccounts.ShouldBe(0);
        harness.World.People.OpenPayrollRecords.ShouldBe(0);
        harness.World.Assets.OutstandingOrders.ShouldBe(0);
    }

    /// <summary>
    /// The contractor arm's own inverse runs, which is what makes the switch a saga rather
    /// than a fork in the road.
    /// </summary>
    /// <remarks>
    /// The <c>Permanent</c> arm's undo is covered above. This is the other one: a compensation
    /// declared inside a case block is registered when that case's step completes, and the
    /// flow's unwind reaches it wherever the case happened to be laid out.
    /// </remarks>
    [Fact]
    public async Task TheContractorArmsOwnInverseRuns()
    {
        var harness = OnboardingHarness.Create()
            .Substitute("welcome.send", new Error("post.unavailable", "shut", ErrorCategory.Unavailable));

        var run = await harness.RunAsync(Offers.Contractor(), TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeFalse(run.ToString());

        run.Trace.Compensated.TakeLast(1).ShouldBe(["supplier.void"], run.ToString());
        run.Trace.Compensated.ShouldNotContain("payroll.close", run.ToString());
    }
}
