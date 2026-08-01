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
/// <strong>Since WP-59 the resumed steps can also do something, which is the half this file
/// used to record as absent.</strong> The generated dispatcher describes a state bag at every
/// step boundary and restores it before the first resumed step, so an instance taken over by a
/// second node re-enters holding the values its earlier steps produced. Before that, the
/// dispatcher described only <c>Emit</c> steps, <c>flow_instance.state_bag</c> stayed null,
/// <c>RestoreState</c> was never called, and the first step past the frontier that bound an
/// earlier step's output failed — which is what
/// <see cref="AResumedStepBindsTheValueAnEarlierStepProduced"/> was written to assert and now
/// asserts the opposite of.
/// </para>
/// <para>
/// <strong>This was never a property of this sample.</strong> Any flow whose steps pass values
/// to each other had it, which is every flow the DSL is for. So "resumes on another node" was
/// true of the loop and not of a flow; it is now true of both.
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
    /// And the first resumed step that needs a value an earlier step produced finds it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>screening.waive</c> binds an <c>Identity</c>, which <c>identity.create</c> produced on
    /// the node that died. The row recording that step is committed — which is why the step is
    /// stepped over — and since WP-59 the value it produced is in the state-bag snapshot the
    /// same commit wrote, so the resumed dispatcher puts it back.
    /// </para>
    /// <para>
    /// <strong>This test asserted the opposite until WP-59, deliberately.</strong> It read
    /// "red when WP-59 lands", and it went red on the day the generated payload writer landed.
    /// The flow now finishes on the second node: it waives the check, sends the welcome pack
    /// and stages the event, with no substitution propping anything up.
    /// </para>
    /// <para>
    /// What a resumed flow does <em>not</em> get back is a <c>[Sensitive]</c> member's value.
    /// The snapshot stored <c>[redacted]</c>, because the journal never held anything else —
    /// see <c>OnboardingJournalTests</c>. A flow that needs a secret after a resume must fetch
    /// it, not remember it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AResumedStepBindsTheValueAnEarlierStepProduced()
    {
        var (_, second, finished) = await ResumeAfterTheChildAsync(substitute: null);

        finished.IsSuccess.ShouldBeTrue(
            "the state bag committed with the step that produced it is restored before the " +
            "first resumed step, so screening.waive binds the Identity identity.create made. " +
            second);

        second.Executed.ShouldBe(
            ["screening.waive", "welcome.send", "emit:employee.onboarded"],
            "and the flow runs to its end on the second node — the announcement included — " +
            "skipping every step that committed before the node died. " +
            second);
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
