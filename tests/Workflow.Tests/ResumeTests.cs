using FlowX;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace Workflow.Tests;

/// <summary>
/// What a second node can and cannot do with an instance the first one left behind.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The mechanism is real and the outcome is not yet usable, and both halves are worth
/// a reader's time.</strong> A lease is taken, the fence is raised, the frontier is read, and
/// the same step loop that started the flow re-enters it and steps over every
/// <c>(scope, step)</c> that committed. There is no separate recovery path to rot — which is
/// the property <c>ADR-0015</c> is about, and it holds.
/// </para>
/// <para>
/// What does not hold is that the resumed steps can do anything. No state bag is journaled
/// yet: the generated dispatcher emits <c>DescribeStep</c> only for <c>Emit</c> steps, so
/// <c>flow_instance.state_bag_json</c> stays null and <c>RestoreState</c> is never called. A
/// resumed instance therefore re-enters with an empty context, holding only the input the
/// trigger re-seeds — and the first step past the frontier that binds a value an earlier step
/// produced fails.
/// </para>
/// <para>
/// <strong>This is not a property of this sample.</strong> Any flow whose steps pass values to
/// each other has it, which is every flow the DSL is for — <c>samples/ecommerce</c>'s
/// <c>inventory.reserve</c> binds a <c>ValidatedOrder</c> the same way. So "resumes on another
/// node" is true of the loop and not yet true of a flow.
/// </para>
/// </remarks>
public sealed class ResumeTests
{
    /// <summary>A resumed instance steps over everything that committed, and runs what did not.</summary>
    /// <remarks>
    /// The half that works, asserted first so the half that does not is legible as a
    /// separate thing.
    /// </remarks>
    [Fact]
    public async Task AResumedInstanceStepsOverEverythingThatCommitted()
    {
        var (harness, second, _) = await ResumeAfterTheChildAsync(
            substitute: h => h.Substitute(
                "screening.waive",
                (_, _) => ValueTask.FromResult(StepOutcome.Success)));

        second.Executed.ShouldNotContain("offer.validate", second.ToString());
        second.Executed.ShouldNotContain("payroll.open", second.ToString());
        second.Executed.ShouldNotContain("identity.create", second.ToString());
        second.Executed.ShouldNotContain("hardware.order", second.ToString());
        second.Executed.ShouldNotContain("workspace.allocate_desk", second.ToString());

        harness.World.People.OpenPayrollRecords.ShouldBe(
            1, "One payroll record, opened by the first node and not opened again.");

        harness.World.Facilities.HeldDesks.ShouldBe(
            1, "One desk, allocated by the child under the first node and not allocated again.");
    }

    /// <summary>
    /// And the first resumed step that needs a value an earlier step produced cannot find it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>screening.waive</c> binds an <c>Identity</c>, which <c>identity.create</c> produced
    /// on the node that died. The row recording that step is committed — which is why the step
    /// is stepped over — and the value it produced is nowhere, because no state bag is written.
    /// </para>
    /// <para>
    /// <strong>Red when WP-59 lands.</strong> A dispatcher that describes its state bag and
    /// restores it makes this flow resume, and this assertion becomes the wrong one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AResumedStepCannotBindAValueAnEarlierStepProduced()
    {
        var (_, second, finished) = await ResumeAfterTheChildAsync(substitute: null);

        finished.IsSuccess.ShouldBeFalse(second.ToString());

        finished.Error!.Code.ShouldBe(
            "capability.unhandled",
            "The step was dispatched and threw looking for a contract nothing put back. " +
            second);

        // And the error names the step that could not bind, which is the one useful thing
        // about it.
        finished.Error.Message.Contains("screening.waive", StringComparison.Ordinal)
            .ShouldBeTrue(finished.Error.Message);

        second.Executed.ShouldBe(["screening.waive"], second.ToString());
    }

    /// <summary>
    /// The control flow, by contrast, is re-derived — because this flow's selectors read the
    /// input.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>Branch</c> and a <c>Switch</c> are never journaled: the engine resolves them before
    /// it asks whether a step has committed, so the arm is recomputed on every pass. That is
    /// what makes replay of control flow possible at all, and it is why
    /// <c>employee.onboard</c>'s selectors read <c>ctx.Input</c> rather than a step's output.
    /// </para>
    /// <para>
    /// Had the switch read <c>ctx.Get&lt;ValidatedOffer&gt;()</c> — the obvious way to write
    /// it — this resume would end at <c>flow.selector_failed</c> on step 1 instead of getting
    /// as far as step 22. That was this sample's first draft, and it is the practical advice
    /// the finding turns into: <strong>in a <c>Durable</c> flow, prefer control-flow delegates
    /// over the flow input.</strong>
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheSwitchAndTheConditionalAreReDerivedFromTheInput()
    {
        var (_, second, _) = await ResumeAfterTheChildAsync(
            substitute: h => h.Substitute(
                "screening.waive",
                (_, _) => ValueTask.FromResult(StepOutcome.Success)));

        second.Entries.ShouldContain("employee.onboard[1] switch: 0", second.ToString());
        second.Entries.ShouldContain("employee.onboard[19] branch: otherwise", second.ToString());
    }

    /// <summary>
    /// Runs the flow until the node dies just past the composed child, then takes it over.
    /// </summary>
    /// <param name="substitute">Applied to the second node only.</param>
    private static async Task<(OnboardingHarness Harness, DurableTrace Second, FlowExecutionResult Finished)>
        ResumeAfterTheChildAsync(Action<OnboardingHarness>? substitute)
    {
        var harness = OnboardingHarness.Create();
        var journal = new NodeDiesJournal(harness.Journal);
        var input = Offers.Permanent(equipment: []);
        var invocation = new FlowInvocation("corr-resume", "key-resume");
        var ct = TestContext.Current.CancellationToken;
        var instanceId = Guid.NewGuid();
        var engine = new FlowEngine(harness.Clock);

        // offer.validate, payroll.open, identity.create, the fork's three, the child's two,
        // the composition — nine. The node does not survive the tenth, which is screening.waive.
        journal.DiesOnCommit = 10;

        var begun = await DurableExecution.BeginAsync(
            journal, OnboardEmployeeFlow.Plan, invocation, instanceId, new FencingToken(1),
            input: null, cancellationToken: ct);

        begun.IsSuccess.ShouldBeTrue("The journal opened the instance.");

        await Should.ThrowAsync<NodeDiedException>(
            () => engine.ExecuteAsync(
                    OnboardEmployeeFlow.Plan,
                    harness.Wrap(OnboardEmployeeFlow.Plan, harness.World.Parent, new DurableTrace()),
                    invocation, input, begun.Value, ct)
                .AsTask());

        journal.DiesOnCommit = null;
        substitute?.Invoke(harness);

        var resumed = await DurableExecution.ResumeAsync(journal, instanceId, new FencingToken(2), ct);

        resumed.IsSuccess.ShouldBeTrue("The fence was raised and the frontier read.");

        var second = new DurableTrace();

        var finished = await engine.ExecuteAsync(
            OnboardEmployeeFlow.Plan,
            harness.Wrap(OnboardEmployeeFlow.Plan, harness.World.Parent, second),
            invocation, input, resumed.Value, ct);

        return (harness, second, finished);
    }
}
