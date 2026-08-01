using FlowX.Runtime;
using FlowX.Testing;
using Shouldly;
using Xunit;

namespace Workflow.Tests;

/// <summary>
/// The shipped test host cannot run this sample's flow, and this is the attempt that shows it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This test was written first and it is the reason <see cref="OnboardingHarness"/>
/// exists.</strong> The obvious first move for a flow test is the one
/// <c>tests/Ecommerce.Tests</c> makes: <c>FlowTestHost.For(Plan, new Dispatcher(...))</c>,
/// run, assert on the trace. It does not work here, and the failure is not subtle — the flow
/// is refused before its first step and the trace is empty.
/// </para>
/// <para>
/// <strong>The refusal is correct.</strong> <c>employee.onboard</c> declares
/// <see cref="ExecutionProfile.Durable"/>. Every <c>RunAsync</c> on <see cref="FlowTestHost"/>
/// reaches the <c>FlowEngine.ExecuteAsync</c> overload that passes <c>durable: null</c>, and
/// <c>FlowEngine.OpenJournal</c> answers a durable plan with no instance by refusing it rather
/// than running it ephemerally. Running it ephemerally is the one thing that would be worse:
/// it would give a saga an unwind stack that dies with the process while the manifest went on
/// saying <c>Durable</c>.
/// </para>
/// <para>
/// <strong>So the gap is in the test host, not in the engine.</strong> <c>FlowTestHost</c> has
/// no seam for a journal — no <c>WithJournal</c>, no durable overload, nothing that reaches
/// the durable <c>ExecuteAsync</c> — so the whole <c>Durable</c> half of the DSL is
/// untestable through the surface the platform ships for testing flows. That is worth a test
/// of its own rather than a sentence in a README, and this one turns red the day the seam
/// lands, which is when <see cref="OnboardingHarness"/> should be deleted.
/// </para>
/// </remarks>
public sealed class WhyTheseTestsDoNotUseFlowTestHostTests
{
    [Fact]
    public async Task FlowTestHostRefusesADurableFlowBeforeItsFirstStep()
    {
        var world = new OnboardingWorld();

        var host = FlowTestHost
            .For(OnboardEmployeeFlow.Plan, world.Parent)
            .WithInvocation(new FlowInvocation("corr-1", "key-1"))
            .Build();

        var run = await host.RunAsync(
            Offers.Permanent(),
            TestContext.Current.CancellationToken);

        run.IsFailure.ShouldBeTrue(
            "If this passes, FlowTestHost has grown a journal seam — delete OnboardingHarness " +
            "and move every test in this project onto the host the platform ships.");

        run.Error!.Code.ShouldBe("flow.durability_not_configured");

        run.Trace.Executed.ShouldBeEmpty(
            "Refused before the first step, so not one capability ran. A host that had run " +
            "the flow ephemerally would look identical from the caller's side and would have " +
            "left the saga's unwind stack in the memory of one process.");
    }

    /// <summary>
    /// The same flow, on a host with the reference stores behind it, runs.
    /// </summary>
    /// <remarks>
    /// The control arm. Without it, the refusal above would be evidence that the flow is
    /// broken rather than that the test host is missing something.
    /// </remarks>
    [Fact]
    public async Task TheSameFlowOnAHostWithAJournalRuns()
    {
        var harness = OnboardingHarness.Create();

        var run = await harness.RunAsync(Offers.Permanent(), TestContext.Current.CancellationToken);

        run.IsSuccess.ShouldBeTrue(run.ToString());
        harness.Journal.Instances.Count.ShouldBe(2, "The parent and the composed child are two instances.");
    }
}
