using FlowX.Compiler.CodeFixes;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.CodeFixes.Tests;

/// <summary>
/// The FLOWX1033 quick action: removing a <c>.WithPolicy(...)</c> that reaches no plan node.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Half of this file is about when the fix is <em>not</em> offered</strong>, which is
/// the part that matters. FLOWX1033 has two documented repairs and only one of them is
/// mechanical; a provider that offered removal for a set carrying a <c>Timeout</c> as well
/// would delete a declaration the plan does carry and a manifest entry that is true.
/// </para>
/// <para>
/// Output is compared as whole documents, so any difference the fix made anywhere in the
/// file is a difference this file reports.
/// </para>
/// </remarks>
public sealed class CompensationRetryHasNoCompensationCodeFixTests
{
    private const string Policies = """
        public static class Policies
        {
            public static readonly PolicySet Undo = PolicySet.Named("undo").CompensationRetry(attempts: 5);
            public static readonly PolicySet Mixed = PolicySet.Named("mixed")
                .Timeout(System.TimeSpan.FromSeconds(3))
                .CompensationRetry(attempts: 5);
        }
        """;

    private static TestDocument[] Project(string steps) =>
        [
            new TestDocument("Flow.cs", Sources.Flow(steps: steps)),
            new TestDocument("Capabilities.cs", Sources.Capabilities()),
            new TestDocument("Policies.cs", $"""
                using FlowX;

                namespace Sample;

                {Policies}

                """),
        ];

    // ------------------------------------------------------------------ it is offered

    /// <summary>The call is removed, and the rest of the chain is byte-identical.</summary>
    /// <remarks>
    /// The set declares <c>CompensationRetry</c> and nothing else and the step has no
    /// compensation, so <c>FlowEmitter</c> emits neither half of the policy argument: the
    /// call already compiles to nothing, and deleting it cannot change what the engine runs.
    /// </remarks>
    [Fact]
    public void RemovesACompensationOnlySetFromANonCompensableStep()
    {
        var project = CodeFixHarness.CreateProject(
            Project(".Step<ReserveInventory>().WithPolicy(Policies.Undo)"));

        CodeFixHarness.DiagnosticIds(project).ShouldContain("FLOWX1033");

        var fixedProject = CodeFixHarness.ApplyOnlyFix(
            project,
            new CompensationRetryHasNoCompensationCodeFixProvider(),
            "FLOWX1033");

        CodeFixHarness.TextOf(fixedProject, "Flow.cs")
            .ShouldBe(Sources.Flow(steps: ".Step<ReserveInventory>()"));

        CodeFixHarness.DiagnosticIds(fixedProject).ShouldNotContain("FLOWX1033");
    }

    /// <summary>The repaired flow still compiles, generated plan included.</summary>
    /// <remarks>
    /// A fix that produces text which parses but does not compile is the failure mode a
    /// text assertion cannot see: <c>.Step&lt;T&gt;()</c> and
    /// <c>.Step&lt;T&gt;().WithPolicy(p)</c> have the same type, so the splice has to leave a
    /// chain that still binds.
    /// </remarks>
    [Fact]
    public void TheRepairedFlowCompilesWithItsGeneratedPlan()
    {
        var fixedProject = CodeFixHarness.ApplyOnlyFix(
            CodeFixHarness.CreateProject(Project(".Step<ReserveInventory>().WithPolicy(Policies.Undo)")),
            new CompensationRetryHasNoCompensationCodeFixProvider(),
            "FLOWX1033");

        CodeFixHarness.CompileErrors(fixedProject).ShouldBeEmpty();
    }

    // -------------------------------------------------------------- it is not offered

    /// <summary>No fix when a comment would be deleted along with the call.</summary>
    /// <remarks>
    /// <para>
    /// A comment written above a <c>.WithPolicy</c> is prose about the step, and it attaches
    /// as leading trivia of the <c>.</c> token — <em>inside</em> the node being removed, not
    /// beside it. Carrying it across means rebuilding a trivia list, and every rule for doing
    /// that is right for one layout and wrong for the next.
    /// </para>
    /// <para>
    /// The rule the repository already states is that a quick action may cost you money and
    /// may not lose your work. Declining costs the author two keystrokes; guessing costs them
    /// a sentence they wrote.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoFixIsOfferedWhenACommentWouldGoWithIt()
    {
        const string Steps = """
            .Step<ReserveInventory>()
                    // Reserving is the step this flow exists for.
                    .WithPolicy(Policies.Undo)
            """;

        var project = CodeFixHarness.CreateProject(Project(Steps));

        CodeFixHarness.OfferedFixes(
            project,
            new CompensationRetryHasNoCompensationCodeFixProvider(),
            CodeFixHarness.Single(project, "FLOWX1033"))
            .ShouldBeEmpty("A quick action may not delete a sentence the author wrote.");
    }

    /// <summary>
    /// No fix for a set that also declares a forward policy, because removal would delete
    /// something the plan carries.
    /// </summary>
    /// <remarks>
    /// The set is shared: <c>Policies.Mixed</c>'s <c>Timeout</c> reaches
    /// <c>StepNode.Policies</c> and <c>flowx.manifest.json</c>, and the two repairs the page
    /// offers — declare the compensation, or split the set — are design decisions the author
    /// owns. A quick action that guessed here would be a quick action that deletes a
    /// published contract entry which is true.
    /// </remarks>
    [Fact]
    public void NoFixIsOfferedForAMixedSet()
    {
        var project = CodeFixHarness.CreateProject(
            Project(".Step<ReserveInventory>().WithPolicy(Policies.Mixed)"));

        var diagnostic = CodeFixHarness.Single(project, "FLOWX1033");

        CodeFixHarness.OfferedFixes(
            project,
            new CompensationRetryHasNoCompensationCodeFixProvider(),
            diagnostic)
            .ShouldBeEmpty(
                "Policies.Mixed's Timeout reaches the plan and the manifest. Removing the " +
                "call would delete a declaration that is carried, which is a design decision " +
                "and not a mechanical repair.");
    }

    /// <summary>
    /// The property the fix reads is the one the analyzer writes.
    /// </summary>
    /// <remarks>
    /// The fixes assembly must not reference <c>FlowX.Compiler</c> — a development dependency
    /// does not flow transitively, and a fixes assembly whose reference the host cannot
    /// resolve is dropped without a message — so the key is a string literal on both sides.
    /// This project references both and is the only place the two copies can be compared,
    /// which is the arrangement <c>EveryFixableIdIsARealDiagnostic</c> already uses for the
    /// ids.
    /// </remarks>
    [Fact]
    public void TheAnalyzerAndTheFixAgreeOnThePropertyKey() =>
        CompensationRetryHasNoCompensationCodeFixProvider.CallReachesNoPlanNodeProperty
            .ShouldBe(
                FlowX.Compiler.Analysis.DeclaredPolicyAnalyzer.CallReachesNoPlanNodeProperty,
                "A fix reading a key the analyzer does not write is a fix that never appears, " +
                "and nothing anywhere reports that.");
}
