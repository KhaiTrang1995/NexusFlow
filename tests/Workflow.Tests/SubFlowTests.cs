using FlowX;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace Workflow.Tests;

/// <summary>
/// The composed child: its own instance, its own unwind, and the one direction its failure
/// travels.
/// </summary>
public sealed class SubFlowTests
{
    /// <summary>The child is composed as an instance of its own, not inlined into the parent.</summary>
    /// <remarks>
    /// <strong>Two journal instances is the observable form of "a sub-flow is one node".</strong>
    /// The parent's compiled graph carries a single <c>StepKind.SubFlow</c> naming
    /// <c>workspace.provision</c> and none of the child's steps, so the child needs a
    /// <c>flow_instance</c> row of its own for its step indices to mean anything.
    /// </remarks>
    [Fact]
    public async Task TheChildRunsAsItsOwnInstance()
    {
        var harness = OnboardingHarness.Create();

        var run = await harness.RunAsync(Offers.Permanent(), TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeTrue(run.ToString());

        harness.Journal.Instances.Select(i => i.FlowId).ShouldBe(
            ["employee.onboard", "workspace.provision"],
            "The parent opens first and composes the child, which opens its own.");

        run.Trace.Entries.ShouldContain("employee.onboard[18] subflow: workspace.provision:Inline");

        // One node in the parent, two steps in the child, and the child's steps are recorded
        // under the child's own flow id.
        run.Trace.Entries.ShouldContain("workspace.provision[0] step: workspace.allocate_desk");
        run.Trace.Entries.ShouldContain("workspace.provision[1] step: workspace.issue_pass");

        OnboardEmployeeFlow.Plan.Graph.Steps.Count(s => s.Kind == StepKind.SubFlow).ShouldBe(1);
    }

    /// <summary>
    /// The child's own failure unwinds the child, then fails the parent, which unwinds itself.
    /// </summary>
    /// <remarks>
    /// The order below is the whole claim of composition: the child undoes what the child did,
    /// at the moment it fails, and the parent's own stack picks up from there. Nothing the
    /// parent completed is left standing because the failure happened one level down.
    /// </remarks>
    [Fact]
    public async Task TheChildsOwnFailureUnwindsTheChildAndThenTheParent()
    {
        var harness = OnboardingHarness.Create()
            .Substitute("workspace.issue_pass", OnboardingErrors.PassRefused("security hold"));

        var run = await harness.RunAsync(Offers.Permanent(), TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeFalse(run.ToString());

        run.Error!.Code.ShouldBe("workspace.pass_refused",
            "A child's failure is the parent's failure, verbatim. " + run);

        run.Compensation.ShouldBe(CompensationOutcome.Succeeded);

        var undone = run.Trace.Compensated;

        undone[0].ShouldBe("workspace.release_desk",
            "The child unwinds its own completed step first, and there is only one: the pass " +
            "never completed, so there is no pass to cancel. " + run);

        undone.ShouldNotContain("workspace.cancel_pass", run.ToString());

        undone.Skip(1).Take(2).ShouldBe(["equipment.return", "equipment.return"], run.ToString());
        undone.TakeLast(2).ShouldBe(["identity.disable", "payroll.close"], run.ToString());

        harness.World.Facilities.HeldDesks.ShouldBe(0, "The desk the child held is back.");
        harness.World.Assets.Assigned.ShouldBeEmpty();
        harness.World.People.OpenAccounts.ShouldBe(0);
    }

    /// <summary>
    /// The child's first step failing composes nothing to undo, and the parent still unwinds.
    /// </summary>
    /// <remarks>
    /// The other half of the child's own failure surface: a child that fails before completing
    /// anything contributes nothing to the unwind, and a parent that treated "the child
    /// failed" as "the child left something behind" would compensate a step that never ran.
    /// </remarks>
    [Fact]
    public async Task AChildThatFailsImmediatelyContributesNothingToTheUnwind()
    {
        var harness = OnboardingHarness.Create()
            .Substitute("workspace.allocate_desk", OnboardingErrors.NoDeskAvailable("london"));

        var run = await harness.RunAsync(Offers.Permanent(), TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeFalse(run.ToString());
        run.Error!.Code.ShouldBe("workspace.no_desk");

        run.Trace.Compensated.ShouldNotContain("workspace.release_desk", run.ToString());
        run.Trace.Compensated.ShouldNotContain("workspace.cancel_pass", run.ToString());

        run.Trace.Compensated.TakeLast(2).ShouldBe(["identity.disable", "payroll.close"], run.ToString());
    }

    /// <summary>
    /// The child's budget is the shorter of its own and whatever the parent has left.
    /// </summary>
    /// <remarks>
    /// Composition can only ever shorten. Asserted on the declarations rather than on a clock
    /// because the derivation is <c>min(parent's remaining, child's own declared deadline)</c>
    /// and a test that measured elapsed time would be asserting on the test runner.
    /// </remarks>
    [Fact]
    public void TheChildCannotBuyItselfMoreTimeThanItsAuthorAllowed()
    {
        ProvisionWorkspaceFlow.Plan.Flow.Deadline.ShouldBe(TimeSpan.FromSeconds(20));
        OnboardEmployeeFlow.Plan.Flow.Deadline.ShouldBe(TimeSpan.FromSeconds(60));

        ProvisionWorkspaceFlow.Plan.Flow.Deadline.ShouldBeLessThan(
            OnboardEmployeeFlow.Plan.Flow.Deadline,
            "Not a requirement of the DSL — a child may declare longer — but then the parent's " +
            "remaining budget is what binds. Either way the child gets the smaller of the two.");
    }
}
