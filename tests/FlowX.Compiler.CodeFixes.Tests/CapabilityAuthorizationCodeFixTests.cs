using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.CodeFixes.Tests;

/// <summary>
/// FLOWX1010's fix — including, and mostly, what it refuses to do.
/// </summary>
/// <remarks>
/// Half of these tests assert an absence. That is the point of the diagnostic: a
/// capability with no declared stance must not acquire one by accident, and a quick
/// action that supplies the permissive value would be the accident. The rule is only
/// as strong as the tooling around it, so "Public is never offered" is asserted as
/// firmly as "Authenticated is written correctly".
/// </remarks>
public sealed class CapabilityAuthorizationCodeFixTests
{
    private const string WithoutAuthorization = """[Capability("inventory.reserve", Version = "1.2.0")]""";

    [Fact]
    public void ReportsFLOWX1010WhenTheStanceIsMissing() =>
        CodeFixHarness.DiagnosticIds(Undeclared()).ShouldContain("FLOWX1010");

    /// <summary>
    /// Only stances that are complete in themselves, and none of them permissive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Public</c> is the one that matters. A fix offering it would let a developer
    /// clear a security error with a keystroke and ship a world-readable capability
    /// believing the compiler agreed — the exact outcome FLOWX1010 exists to prevent.
    /// </para>
    /// <para>
    /// <c>Permission</c> and <c>Policy</c> are absent for a quieter reason: each needs a
    /// name nothing in the source implies, and a stance without its name reads as
    /// enforced while naming nothing to enforce.
    /// </para>
    /// </remarks>
    [Fact]
    public void OffersOnlyRestrictiveFullySpecifiedStances()
    {
        var project = Undeclared();
        var diagnostic = CodeFixHarness.Single(project, "FLOWX1010");

        var titles = CodeFixHarness
            .OfferedFixes(project, new CapabilityAuthorizationCodeFixProvider(), diagnostic)
            .Select(a => a.Title)
            .ToArray();

        titles.ShouldBe([
            "Declare Authorization.Authenticated on 'ReserveInventory'",
            "Declare Authorization.Internal on 'ReserveInventory'",
        ]);
    }

    /// <summary>The fix edits the capability's file, not the flow's.</summary>
    /// <remarks>
    /// FLOWX1010 is reported at the <c>.Step&lt;T&gt;()</c> that resolves the capability,
    /// so the lightbulb appears in the flow while the declaration that has to change is
    /// somewhere else. A provider that only ever rewrites <c>context.Document</c> would
    /// silently do nothing here.
    /// </remarks>
    [Theory]
    [InlineData("Authenticated")]
    [InlineData("Internal")]
    public void WritesTheChosenStanceOntoTheCapabilityDeclaration(string stance)
    {
        var project = Undeclared();

        var fixedProject = ApplyStance(project, stance);

        CodeFixHarness.TextOf(fixedProject, "Capabilities.cs").ShouldBe(
            Sources.Capabilities(
                $"""[Capability("inventory.reserve", Version = "1.2.0", Authorization = Authorization.{stance})]"""));

        CodeFixHarness.TextOf(fixedProject, "Flow.cs").ShouldBe(
            Sources.Flow(),
            "The flow is where the diagnostic was reported and it is not what is wrong.");
    }

    [Theory]
    [InlineData("Authenticated")]
    [InlineData("Internal")]
    public void TheDiagnosticIsGoneAfterTheFix(string stance) =>
        CodeFixHarness.DiagnosticIds(ApplyStance(Undeclared(), stance)).ShouldNotContain("FLOWX1010");

    /// <summary>
    /// The fix also clears the C# error underneath.
    /// </summary>
    /// <remarks>
    /// <c>CapabilityAttribute.Authorization</c> is a <c>required</c> member, so an
    /// omitted stance is CS9035 as well as FLOWX1010. Asserting that the project compiles
    /// afterwards is what proves the fix wrote a real enum member into a real attribute
    /// argument, rather than text that merely looks like one.
    /// </remarks>
    [Fact]
    public void TheCapabilityCompilesAfterTheFix()
    {
        var project = Undeclared();

        CodeFixHarness.CompileErrors(project).ShouldContain(
            e => e.StartsWith("CS9035", StringComparison.Ordinal),
            "A missing required member should be an error before the fix, or this test proves nothing.");

        CodeFixHarness.CompileErrors(ApplyStance(project, "Authenticated")).ShouldBeEmpty();
    }

    /// <summary>Nothing is offered when the developer has already answered the question.</summary>
    [Fact]
    public void OffersNothingWhenAStanceIsAlreadyDeclared()
    {
        var project = CodeFixHarness.CreateProject(Sources.Project());
        var stale = CodeFixHarness.Single(Undeclared(), "FLOWX1010");

        var document = project.Documents.Single(d => d.Name == "Flow.cs");

        CodeFixHarness.OfferedFixes(document, new CapabilityAuthorizationCodeFixProvider(), stale)
            .ShouldBeEmpty();
    }

    /// <summary>
    /// Fix All is not offered, and that is a decision rather than an omission.
    /// </summary>
    /// <remarks>
    /// Batch fixing would answer a security question once and apply the answer to every
    /// capability in the solution. It is also unsound here: one capability used by three
    /// flows produces three diagnostics whose fixes all rewrite the same declaration.
    /// </remarks>
    [Fact]
    public void DoesNotOfferFixAll() =>
        new CapabilityAuthorizationCodeFixProvider().GetFixAllProvider().ShouldBeNull();

    private static Project Undeclared() =>
        CodeFixHarness.CreateProject(Sources.Project(capabilities: Sources.Capabilities(WithoutAuthorization)));

    private static Project ApplyStance(Project project, string stance)
    {
        var diagnostic = CodeFixHarness.Single(project, "FLOWX1010");

        var action = CodeFixHarness
            .OfferedFixes(project, new CapabilityAuthorizationCodeFixProvider(), diagnostic)
            .Single(a => a.EquivalenceKey == "FLOWX1010:" + stance);

        return CodeFixHarness.Apply(action).GetProject(project.Id)!;
    }
}
