using FlowX.Compiler.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.CodeFixes.Tests;

/// <summary>
/// FLOWX1001's fix, asserted on the source text it produces.
/// </summary>
/// <remarks>
/// Every case here compares the whole file after the fix rather than searching it for
/// the word <c>partial</c>. A fix that inserts the keyword in the wrong place, or that
/// reflows the declaration on its way past, satisfies a substring assertion and produces
/// a diff nobody would accept in review.
/// </remarks>
public sealed class FlowMustBePartialCodeFixTests
{
    [Fact]
    public void AddsPartialAfterTheExistingModifiers()
    {
        var project = CodeFixHarness.CreateProject(Sources.Project(flow: Sources.Flow("public sealed class")));

        var fixedProject = CodeFixHarness.ApplyOnlyFix(project, new FlowMustBePartialCodeFixProvider(), "FLOWX1001");

        CodeFixHarness.TextOf(fixedProject, "Flow.cs")
            .ShouldBe(Sources.Flow("public sealed partial class"));
    }

    /// <summary>
    /// A flow with no accessibility modifier at all.
    /// </summary>
    /// <remarks>
    /// The case that catches naive trivia handling: the indentation in front of
    /// <c>class</c> belongs to that token, so a modifier inserted without moving it lands
    /// against the margin and glued to the keyword.
    /// </remarks>
    [Fact]
    public void AddsPartialWhenThereAreNoModifiersAtAll()
    {
        var project = CodeFixHarness.CreateProject(Sources.Project(flow: Sources.Flow("class")));

        var fixedProject = CodeFixHarness.ApplyOnlyFix(project, new FlowMustBePartialCodeFixProvider(), "FLOWX1001");

        CodeFixHarness.TextOf(fixedProject, "Flow.cs").ShouldBe(Sources.Flow("partial class"));
    }

    [Fact]
    public void TheDiagnosticIsGoneAfterTheFix()
    {
        var project = CodeFixHarness.CreateProject(Sources.Project(flow: Sources.Flow("public sealed class")));

        CodeFixHarness.DiagnosticIds(project).ShouldContain("FLOWX1001");

        var fixedProject = CodeFixHarness.ApplyOnlyFix(project, new FlowMustBePartialCodeFixProvider(), "FLOWX1001");

        CodeFixHarness.DiagnosticIds(fixedProject).ShouldNotContain("FLOWX1001");
    }

    /// <summary>
    /// The fix has to leave a compiling program, not merely a quiet analyzer.
    /// </summary>
    /// <remarks>
    /// FLOWX1001 exists because the generator emits a second part of the class. Before
    /// the fix nothing is emitted at all; after it, the emitted part has to bind against
    /// the hand-written one. Asserting only that the diagnostic cleared would miss a fix
    /// that made the class partial in a way the generator could not extend.
    /// </remarks>
    [Fact]
    public void TheFixedSourceCompilesTogetherWithTheGeneratedPlan()
    {
        var project = CodeFixHarness.CreateProject(Sources.Project(flow: Sources.Flow("public sealed class")));

        var fixedProject = CodeFixHarness.ApplyOnlyFix(project, new FlowMustBePartialCodeFixProvider(), "FLOWX1001");

        CodeFixHarness.CompileErrors(fixedProject).ShouldBeEmpty();
    }

    /// <summary>Nothing is offered for a flow that is already partial.</summary>
    /// <remarks>
    /// A stale diagnostic outlives the edit that resolved it, and a provider that trusts
    /// the diagnostic over the tree answers it with a second <c>partial</c> keyword —
    /// which does not compile.
    /// </remarks>
    [Fact]
    public void OffersNothingForAStaleDiagnosticOnAnAlreadyPartialFlow()
    {
        var project = CodeFixHarness.CreateProject(Sources.Project());
        var document = project.Documents.Single(d => d.Name == "Flow.cs");

        CodeFixHarness.OfferedFixes(document, new FlowMustBePartialCodeFixProvider(), StaleDiagnostic(document))
            .ShouldBeEmpty();
    }

    /// <summary>
    /// FLOWX1001 as the generator would report it, but against a tree that no longer
    /// warrants it. Built from the real descriptor rather than an invented one, so the
    /// id and the span are the ones a provider actually receives.
    /// </summary>
    private static Diagnostic StaleDiagnostic(Document document)
    {
        var root = document.GetSyntaxRootAsync(CancellationToken.None).GetAwaiter().GetResult()!;

        var declaration = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single();

        return Diagnostic.Create(
            FlowXDiagnostics.FlowMustBePartial,
            declaration.Identifier.GetLocation(),
            declaration.Identifier.ValueText);
    }
}
