using System.Linq;
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
    /// And the flow it produces is clean, which this test exists to keep true.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This assertion used to be its own inverse, and the inversion is the whole
    /// point of WP-63.</strong> It read
    /// <c>TheFixTradesFLOWX1017ForFLOWX1031BecauseDurableDoesNotSuspendEither</c>: this quick
    /// action cleared one error and raised another, because <c>FLOWX1031</c> was an error on
    /// <c>AwaitSignal</c> under every profile including <c>Durable</c> — nothing implemented
    /// suspension, the step completed immediately, and the plan it reached carried a one-hour
    /// timeout whatever the author declared. <c>ExecutionProfileAnalyzerTests</c> calls a fix
    /// whose result is a different diagnostic a broken fix, and this one was.
    /// </para>
    /// <para>
    /// <strong>The fix was never broken; its premise was.</strong> The provider's own remarks
    /// say it does not guess, because "the author wrote <c>AwaitSignal</c>, so the flow
    /// suspends". The flow now does suspend, so the premise is true and the destination is a
    /// flow that compiles and waits — which is what the quick action was always claiming to
    /// produce.
    /// </para>
    /// <para>
    /// <c>FLOWX1006</c> is what is left, and it is the fixture's rather than the fix's: this
    /// project declares no <c>JsonSerializerContext</c> at all, so every contract a
    /// <c>Durable</c> flow's journal would write is reported — the flow's own input included,
    /// and that was true before the signal joined the state bag. It is asserted by name
    /// instead of being allowed for, so that a <em>new</em> id appearing here fails.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheFixLandsOnAFlowThatCompilesAndWaits()
    {
        var fixedProject = ApplyFix(Ephemeral());

        CodeFixHarness.DiagnosticIds(fixedProject).ShouldNotContain(
            "FLOWX1031",
            "the profile the fix writes is the one the suspension point needs, and the " +
            "suspension point is honoured under it.");

        CodeFixHarness.DiagnosticIds(fixedProject).Distinct().ShouldBe(
            ["FLOWX1006"],
            "nothing the fix produced is reported. What is left is this project having no " +
            "serialiser context for a durable flow's contracts.");
    }

    private static Project Ephemeral() =>
        CodeFixHarness.CreateProject(Sources.Project(flow: Sources.Flow(steps: Suspending)));

    private static Project ApplyFix(Project project) =>
        CodeFixHarness.ApplyOnlyFix(project, new AwaitSignalRequiresDurableCodeFixProvider(), "FLOWX1017");
}
