using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.CodeFixes.Tests;

/// <summary>
/// FLOWX1017's fix: the flow says it suspends, so the profile is made to agree.
/// </summary>
public sealed class AwaitSignalRequiresDurableCodeFixTests
{
    private const string Suspending =
        ".Step<ReserveInventory>()\n        .AwaitSignal<PaymentConfirmed>(TimeSpan.FromHours(1))";

    [Fact]
    public void ReportsFLOWX1017ForAnEphemeralFlowThatSuspends() =>
        CodeFixHarness.DiagnosticIds(Ephemeral()).ShouldContain("FLOWX1017");

    /// <summary>Adds the argument when the flow never named a profile.</summary>
    /// <remarks>
    /// The common case, because <c>Ephemeral</c> is zero and a flow that says nothing
    /// gets it (ADR-0003). Nothing in the attribute hints at the omission, so the fix
    /// has to append rather than replace.
    /// </remarks>
    [Fact]
    public void AddsTheProfileArgumentWhenTheFlowDeclaresNone()
    {
        var fixedProject = ApplyFix(Ephemeral());

        CodeFixHarness.TextOf(fixedProject, "Flow.cs").ShouldBe(
            Sources.Flow(
                flowAttribute: """[Flow("order.place", Profile = ExecutionProfile.Durable)]""",
                steps: Suspending));
    }

    /// <summary>Replaces the value when a different profile was declared explicitly.</summary>
    /// <remarks>
    /// Appending a second <c>Profile</c> would not compile, and replacing the whole
    /// argument would discard whatever spacing the author used around the <c>=</c>.
    /// </remarks>
    [Fact]
    public void ReplacesAProfileThatWasDeclaredExplicitly()
    {
        var project = CodeFixHarness.CreateProject(Sources.Project(
            flow: Sources.Flow(
                flowAttribute: """[Flow("order.place", Version = "1.0.0", Profile = ExecutionProfile.Streaming)]""",
                steps: Suspending)));

        CodeFixHarness.TextOf(ApplyFix(project), "Flow.cs").ShouldBe(
            Sources.Flow(
                flowAttribute: """[Flow("order.place", Version = "1.0.0", Profile = ExecutionProfile.Durable)]""",
                steps: Suspending));
    }

    [Fact]
    public void TheDiagnosticIsGoneAfterTheFix()
    {
        var fixedProject = ApplyFix(Ephemeral());

        CodeFixHarness.DiagnosticIds(fixedProject).ShouldNotContain("FLOWX1017");
        CodeFixHarness.CompileErrors(fixedProject).ShouldBeEmpty();
    }

    /// <summary>
    /// And the flow it produces is not clean, which this test exists to keep visible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>FLOWX1031</c> is an error on <c>AwaitSignal</c> under <em>every</em> profile,
    /// <c>Durable</c> included, because nothing implements suspension: the step completes
    /// immediately and the plan it reached carried a one-hour timeout whatever the author
    /// declared. So this quick action clears one error and raises another, which
    /// <c>ExecutionProfileAnalyzerTests</c> calls the mark of a broken fix.
    /// </para>
    /// <para>
    /// <strong>The fix is not broken; its premise is.</strong> The provider's own remarks
    /// say it does not guess because "the author wrote <c>AwaitSignal</c>, so the flow
    /// suspends". The flow does not suspend, and <c>Durable</c> was never the missing half
    /// of a working suspension — it was the profile under which the same nothing happened
    /// without a message. Asserting the second diagnostic here rather than asserting its
    /// absence is what stops this suite reading as evidence that the quick action lands
    /// somewhere usable. <c>docs/diagnostics/FLOWX1031.md</c> argues the severity that
    /// makes this true.
    /// </para>
    /// <para>
    /// <strong>Red when WP-63 lands.</strong> A suspension point that suspends deletes
    /// <c>FLOWX1031</c>, and this test with it — at which point the quick action's
    /// destination really is clean and <c>TheDiagnosticIsGoneAfterTheFix</c> says the whole
    /// truth on its own.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheFixTradesFLOWX1017ForFLOWX1031BecauseDurableDoesNotSuspendEither()
    {
        var fixedProject = ApplyFix(Ephemeral());

        CodeFixHarness.DiagnosticIds(fixedProject).ShouldContain(
            "FLOWX1031",
            "A quick action that lands on a different error is worth stating in a test " +
            "rather than discovering in an editor.");

        CodeFixHarness.Single(fixedProject, "FLOWX1031").Severity.ShouldBe(
            DiagnosticSeverity.Error,
            "AwaitSignal is the half of FLOWX1031 that fabricates a timeout, so it refuses " +
            "the plan rather than annotating it.");
    }

    private static Project Ephemeral() =>
        CodeFixHarness.CreateProject(Sources.Project(flow: Sources.Flow(steps: Suspending)));

    private static Project ApplyFix(Project project) =>
        CodeFixHarness.ApplyOnlyFix(project, new AwaitSignalRequiresDurableCodeFixProvider(), "FLOWX1017");
}
