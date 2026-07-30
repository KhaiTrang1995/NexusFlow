using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.CodeFixes.Tests;

/// <summary>
/// FLOWX1011's fix: an ambient read is moved onto the flow context, or nothing is offered.
/// </summary>
/// <remarks>
/// Half of this suite is about what the fix declines to do. FLOWX1011 reports six kinds of
/// impurity and only one of them has a repair the compiler can write; the others need a
/// decision about where the value should come from. A quick action that guessed would be
/// editing the flow's graph on the developer's behalf, and the tests below are what stop
/// the next person from adding one.
/// </remarks>
public sealed class PredicatePurityCodeFixTests
{
    private static string Branching(string predicate) =>
        ".Step<ReserveInventory>()\n        .When(" + predicate + ", late => late.Step<ReserveInventory>())";

    // ------------------------------------------------------------------- offered

    [Fact]
    public void ReportsFLOWX1011ForAConditionThatReadsTheClock() =>
        CodeFixHarness.DiagnosticIds(Branch("ctx => DateTime.UtcNow.Hour > 17")).ShouldContain("FLOWX1011");

    [Fact]
    public void RewritesTheAmbientClockOntoTheContext()
    {
        // DateTime, not DateTimeOffset, so the counterpart has to be
        // ctx.UtcNow.UtcDateTime or the result does not bind against a DateTime.
        var fixedProject = ApplyFix(Branch("ctx => DateTime.UtcNow.Hour > 17"));

        CodeFixHarness.TextOf(fixedProject, "Flow.cs").ShouldBe(
            Sources.Flow(steps: Branching("ctx => ctx.UtcNow.UtcDateTime.Hour > 17")));
    }

    [Fact]
    public void RewritesDateTimeOffsetOntoTheContextDirectly()
    {
        // Same type on both sides, so the shorter form is the right one.
        var fixedProject = ApplyFix(Branch("ctx => DateTimeOffset.UtcNow.Hour > 17"));

        CodeFixHarness.TextOf(fixedProject, "Flow.cs").ShouldBe(
            Sources.Flow(steps: Branching("ctx => ctx.UtcNow.Hour > 17")));
    }

    [Fact]
    public void RewritesIdentityGenerationOntoTheContext()
    {
        // The reported node is the member access, so the argument list survives and
        // 'Guid.NewGuid()' becomes 'ctx.NewId()' rather than 'ctx.NewId'.
        var fixedProject = ApplyFix(Branch("ctx => Guid.NewGuid() != Guid.Empty"));

        CodeFixHarness.TextOf(fixedProject, "Flow.cs").ShouldBe(
            Sources.Flow(steps: Branching("ctx => ctx.NewId() != Guid.Empty")));
    }

    [Fact]
    public void RewritesAmbientRandomnessOntoTheContext()
    {
        var fixedProject = ApplyFix(Branch("ctx => Random.Shared.Next(10) > 5"));

        CodeFixHarness.TextOf(fixedProject, "Flow.cs").ShouldBe(
            Sources.Flow(steps: Branching("ctx => ctx.Random.Next(10) > 5")));
    }

    [Fact]
    public void UsesTheNameTheAuthorGaveTheContextParameter()
    {
        var fixedProject = ApplyFix(Branch("context => DateTimeOffset.UtcNow.Hour > 17"));

        CodeFixHarness.TextOf(fixedProject, "Flow.cs").ShouldBe(
            Sources.Flow(steps: Branching("context => context.UtcNow.Hour > 17")));
    }

    [Fact]
    public void NamesTheContextParameterOfThePredicateAndNotOfANestedLambda()
    {
        // A read inside a nested lambda still has to be rewritten onto the context;
        // naming the nearest lambda's parameter would produce 'sku.UtcNow'.
        var fixedProject = ApplyFix(Branch(
            "ctx => Array.TrueForAll(new[] { ctx.Input.Sku }, sku => DateTimeOffset.UtcNow.Hour > sku.Length)"));

        CodeFixHarness.TextOf(fixedProject, "Flow.cs").ShouldBe(
            Sources.Flow(steps: Branching(
                "ctx => Array.TrueForAll(new[] { ctx.Input.Sku }, sku => ctx.UtcNow.Hour > sku.Length)")));
    }

    // --------------------------------------------------- the other delegates it covers

    [Fact]
    public void RewritesAnAmbientReadInAReturnProjection()
    {
        // The fix follows the diagnostic. FLOWX1011 reads the Return projection too, and
        // the rewrite is identical there because the lambda's parameter is the same
        // FlowContext — the delegates differ only in what they return.
        var project = CodeFixHarness.CreateProject(Sources.Project(
            flow: Sources.Flow(returnProjection: "ctx => new OrderResult(Guid.NewGuid().ToString())")));

        var fixedProject = CodeFixHarness.ApplyOnlyFix(project, new PredicatePurityCodeFixProvider(), "FLOWX1011");

        CodeFixHarness.TextOf(fixedProject, "Flow.cs").ShouldBe(
            Sources.Flow(returnProjection: "ctx => new OrderResult(ctx.NewId().ToString())"));

        CodeFixHarness.CompileErrors(fixedProject).ShouldBeEmpty();
    }

    [Fact]
    public void RewritesAnAmbientReadInASwitchSelector()
    {
        var project = CodeFixHarness.CreateProject(Sources.Project(flow: Sources.Flow(
            steps: ".Switch(ctx => DateTimeOffset.UtcNow.Hour)\n            .Case(9, b => b.Step<ReserveInventory>())")));

        var fixedProject = CodeFixHarness.ApplyOnlyFix(project, new PredicatePurityCodeFixProvider(), "FLOWX1011");

        CodeFixHarness.TextOf(fixedProject, "Flow.cs").ShouldBe(Sources.Flow(
            steps: ".Switch(ctx => ctx.UtcNow.Hour)\n            .Case(9, b => b.Step<ReserveInventory>())"));

        CodeFixHarness.CompileErrors(fixedProject).ShouldBeEmpty();
    }

    [Fact]
    public void NamesTheParameterOfTheDelegateItIsInRatherThanTheBranchAroundIt()
    {
        // "Outermost lambda" means outermost within the builder call being fixed, not
        // outermost in the file. A Return nested inside a When branch has its own context
        // parameter, and naming the branch's 'late' would produce something that does not
        // bind — which the compile assertion below is here to catch.
        var project = CodeFixHarness.CreateProject(Sources.Project(flow: Sources.Flow(
            steps: ".When(ctx => ctx.Get<Reservation>().Sku.Length > 1, late => late"
                 + ".Return(inner => new OrderResult(Guid.NewGuid().ToString())))")));

        var fixedProject = CodeFixHarness.ApplyOnlyFix(project, new PredicatePurityCodeFixProvider(), "FLOWX1011");

        CodeFixHarness.TextOf(fixedProject, "Flow.cs").ShouldBe(Sources.Flow(
            steps: ".When(ctx => ctx.Get<Reservation>().Sku.Length > 1, late => late"
                 + ".Return(inner => new OrderResult(inner.NewId().ToString())))"));

        CodeFixHarness.CompileErrors(fixedProject).ShouldBeEmpty();
    }

    [Fact]
    public void TheDiagnosticIsGoneAndTheProjectStillCompiles()
    {
        // Both halves. A fix that satisfies the analyzer and leaves source that does not
        // bind has made things worse than the diagnostic it removed.
        var fixedProject = ApplyFix(Branch("ctx => DateTime.UtcNow.Hour > 17"));

        CodeFixHarness.DiagnosticIds(fixedProject).ShouldNotContain("FLOWX1011");
        CodeFixHarness.CompileErrors(fixedProject).ShouldBeEmpty();
    }

    // ------------------------------------------------------------------- declined

    [Fact]
    public void OffersNothingForLocalTime() =>
        // Every context counterpart is UTC. Rewriting DateTime.Now would change which
        // branch is taken either side of midnight, silently and only in some time zones.
        OfferedFor("ctx => DateTime.Now.Hour > 17").ShouldBeEmpty();

    [Fact]
    public void OffersNothingWhenTheContextParameterHasNoUsableName() =>
        // Deliberate caution rather than necessity: a lone '_' still binds as an ordinary
        // parameter name today. The fix declines anyway, because the one thing worse than
        // no quick action is a quick action whose output does not compile.
        OfferedFor("_ => DateTimeOffset.UtcNow.Hour > 17").ShouldBeEmpty();

    [Fact]
    public void OffersNothingForACapturedVariable()
    {
        // There is no counterpart. The repair is a decision — a constant, or a step that
        // fetches the value — and a quick action cannot make it.
        var project = CodeFixHarness.CreateProject(Sources.Project(flow: """
            using System;
            using FlowX;

            namespace Sample;

            [Flow("order.place")]
            public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
            {
                protected override void Define(IFlowBuilder<PlaceOrder, OrderResult> flow)
                {
                    var cutoff = 3;

                    flow
                        .Step<ReserveInventory>()
                        .When(ctx => ctx.Input.Sku.Length > cutoff, late => late.Step<ReserveInventory>())
                        .Return(ctx => new OrderResult("id"));
                }
            }

            """));

        var diagnostic = CodeFixHarness.Single(project, "FLOWX1011");

        CodeFixHarness.OfferedFixes(project, new PredicatePurityCodeFixProvider(), diagnostic).ShouldBeEmpty();
    }

    [Fact]
    public void OffersNothingForADiagnosticThatHasOutlivedItsCause()
    {
        // The IDE keeps offering yesterday's squiggle until analysis catches up, so the
        // provider re-reads the tree instead of trusting the span it was handed.
        var stale = CodeFixHarness.Single(Branch("ctx => DateTime.UtcNow.Hour > 17"), "FLOWX1011");

        var repaired = CodeFixHarness.CreateProject(Sources.Project(
            flow: Sources.Flow(steps: Branching("ctx => ctx.UtcNow.UtcDateTime.Hour > 17"))));

        var document = repaired.Documents.Single(d => d.Name == "Flow.cs");

        CodeFixHarness.OfferedFixes(document, new PredicatePurityCodeFixProvider(), stale).ShouldBeEmpty();
    }

    // ------------------------------------------------------------------- harness

    private static Project Branch(string predicate) =>
        CodeFixHarness.CreateProject(Sources.Project(flow: Sources.Flow(steps: Branching(predicate))));

    private static ImmutableArray<CodeAction> OfferedFor(string predicate)
    {
        var project = Branch(predicate);

        return CodeFixHarness.OfferedFixes(
            project,
            new PredicatePurityCodeFixProvider(),
            CodeFixHarness.Single(project, "FLOWX1011"));
    }

    private static Project ApplyFix(Project project) =>
        CodeFixHarness.ApplyOnlyFix(project, new PredicatePurityCodeFixProvider(), "FLOWX1011");
}
