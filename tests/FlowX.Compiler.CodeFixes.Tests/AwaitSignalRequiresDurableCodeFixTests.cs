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

    private static Project Ephemeral() =>
        CodeFixHarness.CreateProject(Sources.Project(flow: Sources.Flow(steps: Suspending)));

    private static Project ApplyFix(Project project) =>
        CodeFixHarness.ApplyOnlyFix(project, new AwaitSignalRequiresDurableCodeFixProvider(), "FLOWX1017");
}
