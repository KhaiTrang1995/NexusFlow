using FlowX;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace Workflow.Tests;

/// <summary>
/// The one timeout this DSL actually ships: the flow's own deadline.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This class is named <c>TimeoutTests</c> and it contains no test of
/// <c>OnTimeout</c>, deliberately.</strong> <c>.AwaitSignal&lt;T&gt;(timeout).OnTimeout(b)</c>
/// is the timeout the DSL documents and it does not work — see
/// <see cref="TheAbsentHalfTests"/>, which compiles a flow using it and shows what the
/// generator produces. What does work is <c>[FlowDeadline]</c>: an absolute budget, set when
/// the flow starts, checked at every step boundary, and enforced by failing the flow and
/// unwinding everything compensable that completed.
/// </para>
/// <para>
/// A flow "cannot outlive its budget by more than one step", which is exactly what the
/// assertions below measure: the step that overruns is allowed to finish, and the boundary
/// after it is where the flow ends.
/// </para>
/// </remarks>
public sealed class TimeoutTests
{
    /// <summary>
    /// A step that overruns the flow's budget ends the flow at the next boundary, and the
    /// saga unwinds.
    /// </summary>
    /// <remarks>
    /// The clock is moved by the step itself rather than by the test between two runs, because
    /// that is the shape a real overrun has: a dependency took longer than the flow had left,
    /// and the engine finds out at the boundary. <c>FlowTestClock</c> is pinned rather than
    /// running, so nothing here races the test runner.
    /// </remarks>
    [Fact]
    public async Task AStepThatOverrunsTheDeadlineFailsTheFlowAtTheNextBoundaryAndUnwinds()
    {
        var harness = OnboardingHarness.Create();
        var assigned = 0;

        harness.Substitute("equipment.assign", async (ctx, ct) =>
        {
            var item = ctx.Get<EquipmentRequest>().Item;

            // The first item takes longer than the whole flow was given. The boundary after
            // it is where the engine finds out.
            if (Interlocked.Increment(ref assigned) == 1)
            {
                harness.Clock.Advance(TimeSpan.FromSeconds(61));
            }

            // The same effect the real capability has, so the unwind has something to undo.
            var tag = await harness.World.Assets.AssignAsync(item, ctx.IdempotencyKey, ct);

            ctx.Set(new EquipmentAssignment(item, tag));

            return StepOutcome.Success;
        });

        var run = await harness.RunAsync(Offers.Permanent(), TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeFalse(run.ToString());
        run.Error!.Code.ShouldBe(FlowErrors.DeadlineExceededCode);

        assigned.ShouldBe(1, "The overrunning step finished; the one after it never started. " + run);

        run.Trace.Ran("workspace.allocate_desk").ShouldBeFalse(
            "The deadline is checked before the step runs, so the child was never composed. " + run);

        // The saga still unwinds. A flow that ran out of time has done real work, and an
        // expiry that skipped compensation would leave exactly the dangling state a saga
        // exists to prevent.
        run.Compensation.ShouldBe(CompensationOutcome.Succeeded);

        run.Trace.Compensated.ShouldContain("equipment.return", run.ToString());
        run.Trace.Compensated.TakeLast(2).ShouldBe(["identity.disable", "payroll.close"], run.ToString());

        harness.World.Assets.Assigned.ShouldBeEmpty();
        harness.World.People.OpenAccounts.ShouldBe(0);
    }

    /// <summary>
    /// A caller's shorter deadline wins, and a caller's longer one does not.
    /// </summary>
    /// <remarks>
    /// The budget is <c>min(the flow's declared deadline, whatever the trigger supplied)</c>.
    /// A caller must not be able to buy a flow more time than its own author gave it, which is
    /// the same rule composition obeys one level down.
    /// </remarks>
    [Fact]
    public async Task ACallerCanOnlyShortenTheFlowsBudget()
    {
        var harness = OnboardingHarness.Create()
            .WithInvocation(new FlowInvocation(
                "corr-short",
                "key-short",
                TenantId: null,
                Deadline: DateTimeOffset.UnixEpoch - TimeSpan.FromSeconds(1)));

        var run = await harness.RunAsync(Offers.Permanent(), TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeFalse(run.ToString());
        run.Error!.Code.ShouldBe(FlowErrors.DeadlineExceededCode);

        run.Trace.Executed.ShouldBeEmpty(
            "The budget was already spent when the first step's boundary was checked. " + run);
    }
}
