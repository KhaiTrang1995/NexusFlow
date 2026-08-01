using Shouldly;
using Xunit;

namespace Workflow.Tests;

/// <summary>
/// Every control-flow shape the flow declares, and both ways out of each one.
/// </summary>
/// <remarks>
/// <para>
/// The flow level of the test pyramid (<c>docs/23-Testing-Strategy.md §3</c>): the real
/// generated <c>Plan</c>, the real generated <c>Dispatcher</c>, the real engine, the real
/// in-memory adapters, the reference journal and lease store — and a capability substituted
/// only where the interesting case is one no happy path reaches.
/// </para>
/// <para>
/// <strong>These assert on the trace, not only on the answer.</strong> A flow expresses order,
/// condition and recovery; a test that checks the returned error cannot tell a switch that
/// took the right arm from one that took the wrong arm and happened to produce the same
/// value, and cannot tell a loop that ran twice from one that ran once.
/// </para>
/// </remarks>
public sealed class ControlFlowTests
{
    /// <summary>The three fork branches, whose relative order is whatever the thread pool chose.</summary>
    private static readonly string[] Fork = ["hardware.order", "access.grant", "induction.book"];

    // ---------------------------------------------------------------------------------
    // The whole graph, once
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// The happy path walks the switch, the fork, the loop, the child and the conditional,
    /// in that order.
    /// </summary>
    /// <remarks>
    /// One assertion over the sequential spine, with the fork's three branches lifted out
    /// because a <c>Parallel</c> genuinely interleaves and asserting an order on it would be
    /// asserting on the scheduler. The fork is covered by count, below and in
    /// <see cref="TheForkRunsEveryBranchAndJoins"/>.
    /// </remarks>
    [Fact]
    public async Task TheHappyPathWalksEveryShapeInOrder()
    {
        var harness = OnboardingHarness.Create();

        var run = await harness.RunAsync(Offers.Permanent(), TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeTrue(run.ToString());

        Sequential(run).ShouldBe(
            [
                "offer.validate",
                "payroll.open",
                "identity.create",

                // The loop, once per item: the cleared item takes the Otherwise arm, the one
                // that needs signing takes the Then arm, and both meet at equipment.assign.
                "equipment.auto_clear",
                "equipment.assign",
                "equipment.approve",
                "equipment.assign",

                // The composed child's own steps, in the parent's trace under the child's id.
                "workspace.allocate_desk",
                "workspace.issue_pass",

                "screening.waive",
                "welcome.send",
                "emit:employee.onboarded",
            ],
            run.ToString());

        foreach (var branch in Fork)
        {
            run.Trace.Count(branch).ShouldBe(1, branch + " ran once. " + run);
        }

        run.Output.EmployeeId.ShouldBe("c-1");
        run.Output.EquipmentIssued.ShouldBe(2);
    }

    // ---------------------------------------------------------------------------------
    // Switch · Case · Default · Fail
    // ---------------------------------------------------------------------------------

    /// <summary>The <c>Permanent</c> case opens a payroll record and nothing else.</summary>
    [Fact]
    public async Task TheSwitchTakesThePermanentArm()
    {
        var harness = OnboardingHarness.Create();

        var run = await harness.RunAsync(Offers.Permanent(), TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeTrue(run.ToString());
        run.Trace.Ran("payroll.open").ShouldBeTrue(run.ToString());

        run.Trace.Ran("supplier.sign").ShouldBeFalse(
            "The Jump closing the taken case is what skips the alternative. Without it both " +
            "arms would run in sequence. " + run);

        run.Trace.Entries.ShouldContain("employee.onboard[1] switch: 0");
    }

    /// <summary>The <c>Contractor</c> case signs an agreement and nothing else.</summary>
    [Fact]
    public async Task TheSwitchTakesTheContractorArm()
    {
        var harness = OnboardingHarness.Create();

        var run = await harness.RunAsync(Offers.Contractor(), TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeTrue(run.ToString());
        run.Trace.Ran("supplier.sign").ShouldBeTrue(run.ToString());
        run.Trace.Ran("payroll.open").ShouldBeFalse(run.ToString());

        run.Trace.Entries.ShouldContain("employee.onboard[1] switch: 1");
    }

    /// <summary>
    /// A value no case matches reaches the <c>Default</c> arm, which fails the flow.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the arm that would be silent without the <c>Default</c>.</strong> A
    /// switch with no default continues past an unmatched value — a branch nobody took does
    /// nothing — so an <c>Intern</c> would have gone straight on to <c>identity.create</c> and
    /// been onboarded with no engagement record at all. The flow states the miss instead.
    /// </para>
    /// <para>
    /// Nothing compensable has run at this point, so the unwind is empty. That is a property
    /// of where the arm sits and not of <c>Fail</c>:
    /// <see cref="CompensationTests.FailingLateUnwindsEveryCompletedStepInStrictReverse"/> is
    /// the same terminal step reached with real effects behind it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AValueNoCaseMatchesReachesTheDefaultArmAndFails()
    {
        var harness = OnboardingHarness.Create();

        var run = await harness.RunAsync(Offers.Intern(), TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeFalse(run.ToString());
        run.Error!.Code.ShouldBe("onboarding.unsupported_employment");

        Sequential(run).ShouldBe(["offer.validate", "fail"], run.ToString());

        run.Trace.Entries.ShouldContain("employee.onboard[1] switch: default");

        run.Trace.Compensated.ShouldBeEmpty(
            "Nothing compensable had run when the arm rejected. " + run);

        harness.World.People.OpenAccounts.ShouldBe(0);
    }

    // ---------------------------------------------------------------------------------
    // Parallel
    // ---------------------------------------------------------------------------------

    /// <summary>All three branches run, and the step after the join sees all of them.</summary>
    [Fact]
    public async Task TheForkRunsEveryBranchAndJoins()
    {
        var harness = OnboardingHarness.Create();

        var run = await harness.RunAsync(Offers.Permanent(), TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeTrue(run.ToString());

        foreach (var branch in Fork)
        {
            run.Trace.Count(branch).ShouldBe(1, branch + ". " + run);
        }

        // The join is what makes the next step's position meaningful: every branch is
        // complete before the loop begins, so the loop can never overlap a branch.
        var executed = run.Trace.Executed;

        Position(executed, "equipment.auto_clear").ShouldBeGreaterThan(
            Fork.Max(branch => Position(executed, branch)),
            "AllMustSucceed waits for every branch before control reaches the join target. " + run);

        harness.World.Access.OutstandingGrants.ShouldBe(1);
        harness.World.Assets.OutstandingOrders.ShouldBe(1);
    }

    // ---------------------------------------------------------------------------------
    // ForEach, and the conditional inside it
    // ---------------------------------------------------------------------------------

    /// <summary>An empty collection runs the body zero times and the flow carries on.</summary>
    /// <remarks>
    /// The case a loop test usually forgets, and the one where an off-by-one in the layout
    /// shows: the body is laid out immediately after the <c>ForEach</c> node, so a loop that
    /// did not honour a count of zero would fall straight into the first body step.
    /// </remarks>
    [Fact]
    public async Task AnEmptyCollectionRunsTheBodyNotAtAll()
    {
        var harness = OnboardingHarness.Create();

        var run = await harness.RunAsync(
            Offers.Permanent(equipment: []),
            TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeTrue(run.ToString());

        run.Trace.Entries.ShouldContain("employee.onboard[12] foreach: 0");

        run.Trace.Count("equipment.assign").ShouldBe(0, run.ToString());
        run.Trace.Count("equipment.approve").ShouldBe(0, run.ToString());
        run.Trace.Count("equipment.auto_clear").ShouldBe(0, run.ToString());

        run.Trace.Ran("workspace.allocate_desk").ShouldBeTrue(
            "A loop over nothing is not a flow that stops. " + run);

        run.Output.EquipmentIssued.ShouldBe(0);
    }

    /// <summary>
    /// A populated collection runs the body once per element, taking a different arm each time.
    /// </summary>
    /// <remarks>
    /// <strong>The body appears once in the plan however many elements there are.</strong> The
    /// engine re-enters that same span per element, which is why the two entries below are two
    /// executions of the same step index and why the plan is independent of the size of the
    /// data.
    /// </remarks>
    [Fact]
    public async Task EachElementTakesItsOwnArmOfTheConditionalInsideTheLoop()
    {
        var harness = OnboardingHarness.Create();

        var run = await harness.RunAsync(Offers.Permanent(), TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeTrue(run.ToString());

        run.Trace.Entries.ShouldContain("employee.onboard[12] foreach: 2");

        // The predicate reads the element, so it answers differently per iteration. That is
        // the whole point of scoping the element to its iteration rather than putting it in
        // the flow's own bag, where every element would take the same slot.
        run.Trace.Entries.Where(e => e.Contains("branch:", StringComparison.Ordinal))
            .ShouldBe(
                [
                    "employee.onboard[13] branch: otherwise",
                    "employee.onboard[13] branch: then",
                    "employee.onboard[19] branch: otherwise",
                ],
                run.ToString());

        run.Trace.Count("equipment.auto_clear").ShouldBe(1, run.ToString());
        run.Trace.Count("equipment.approve").ShouldBe(1, run.ToString());
        run.Trace.Count("equipment.assign").ShouldBe(2, run.ToString());

        harness.World.Assets.Assigned.ShouldBe(["laptop-bag", "phone"]);
        harness.World.Assets.Approved.ShouldBe(["phone"], "Only the item that needed signing.");
    }

    // ---------------------------------------------------------------------------------
    // The top-level conditional
    // ---------------------------------------------------------------------------------

    /// <summary>The <c>then</c> arm of the trailing conditional.</summary>
    [Fact]
    public async Task ScreeningRunsWhenTheOfferAsksForIt()
    {
        var harness = OnboardingHarness.Create();

        var run = await harness.RunAsync(
            Offers.Permanent(requiresBackgroundCheck: true),
            TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeTrue(run.ToString());
        run.Trace.Ran("screening.start").ShouldBeTrue(run.ToString());
        run.Trace.Ran("screening.waive").ShouldBeFalse(run.ToString());
        run.Trace.Entries.ShouldContain("employee.onboard[19] branch: then");
    }

    /// <summary>The <c>Otherwise</c> arm of the trailing conditional.</summary>
    [Fact]
    public async Task ScreeningIsWaivedWhenTheOfferDoesNotAskForIt()
    {
        var harness = OnboardingHarness.Create();

        var run = await harness.RunAsync(
            Offers.Permanent(requiresBackgroundCheck: false),
            TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeTrue(run.ToString());
        run.Trace.Ran("screening.waive").ShouldBeTrue(run.ToString());
        run.Trace.Ran("screening.start").ShouldBeFalse(run.ToString());
        run.Trace.Entries.ShouldContain("employee.onboard[19] branch: otherwise");
    }

    /// <summary>The steps that ran, with the fork's three lifted out.</summary>
    private static IReadOnlyList<string> Sequential(DurableRun run) =>
        [.. run.Trace.Executed.Where(name => !Fork.Contains(name, StringComparer.Ordinal))];

    /// <summary>Where a capability first ran, or <c>-1</c>.</summary>
    private static int Position(IReadOnlyList<string> executed, string capabilityId)
    {
        for (var i = 0; i < executed.Count; i++)
        {
            if (string.Equals(executed[i], capabilityId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
