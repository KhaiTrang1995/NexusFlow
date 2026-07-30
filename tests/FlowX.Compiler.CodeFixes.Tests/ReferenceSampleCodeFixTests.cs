using Shouldly;
using Xunit;

namespace FlowX.Compiler.CodeFixes.Tests;

/// <summary>
/// The FLOWX1001 fix applied to the reference sample's real source, read off disk.
/// </summary>
/// <remarks>
/// <para>
/// Everything else in this suite compiles source the tests wrote, which means the tests
/// and the fix share whatever assumptions the author had about how a flow is spelled.
/// <c>samples/ecommerce/PlaceOrderFlow.cs</c> shares none of them: it has an XML doc
/// comment, two attributes, a suppression pragma in the middle of the chain and a
/// multi-line <c>Return</c> projection. Breaking it and repairing it is the only case
/// here that could fail for a reason the author did not think of.
/// </para>
/// <para>
/// The assertion is byte equality with the file on disk. Not "contains partial" — the
/// standard a fix has to meet is that the diff it produces is exactly the diff that was
/// removed, and nothing else in the file moved.
/// </para>
/// </remarks>
public sealed class ReferenceSampleCodeFixTests
{
    private const string Partial = "public sealed partial class PlaceOrderFlow";
    private const string NotPartial = "public sealed class PlaceOrderFlow";

    [Fact]
    public void RestoresThePartialModifierExactlyAsTheSampleHadIt()
    {
        var sample = ReadSample("PlaceOrderFlow.cs");
        var broken = sample.Replace(Partial, NotPartial, StringComparison.Ordinal);

        broken.ShouldNotBe(sample, "The sample no longer declares the flow the way this test expects.");

        var project = CodeFixHarness.CreateProject(
            new TestDocument("PlaceOrderFlow.cs", broken),
            new TestDocument("Contracts.cs", ReadSample("Contracts.cs")),
            new TestDocument("Capabilities.cs", ReadSample("Capabilities.cs")),
            ImplicitUsings);

        CodeFixHarness.DiagnosticIds(project).ShouldContain(
            "FLOWX1001",
            "Removing 'partial' from the real sample must raise FLOWX1001, or the rest of " +
            "this test is repairing something that was never broken.");

        var fixedProject = CodeFixHarness.ApplyOnlyFix(
            project,
            new FlowMustBePartialCodeFixProvider(),
            "FLOWX1001");

        CodeFixHarness.TextOf(fixedProject, "PlaceOrderFlow.cs").ShouldBe(sample);
        CodeFixHarness.DiagnosticIds(fixedProject).ShouldNotContain("FLOWX1001");
    }

    /// <summary>
    /// And the repaired sample compiles, generated plan included.
    /// </summary>
    /// <remarks>
    /// Only the three files the flow needs are compiled — <c>Program.cs</c> and
    /// <c>Infrastructure.cs</c> pull in ASP.NET Core and the HTTP plugin, which have
    /// nothing to do with whether the fix worked. The flow, its contracts and its
    /// capabilities are the whole of what FLOWX1001 is about.
    /// </remarks>
    [Fact]
    public void TheRepairedSampleCompilesWithItsGeneratedPlan()
    {
        var broken = ReadSample("PlaceOrderFlow.cs").Replace(Partial, NotPartial, StringComparison.Ordinal);

        var project = CodeFixHarness.CreateProject(
            new TestDocument("PlaceOrderFlow.cs", broken),
            new TestDocument("Contracts.cs", ReadSample("Contracts.cs")),
            new TestDocument("Capabilities.cs", ReadSample("Capabilities.cs")),
            ImplicitUsings);

        var fixedProject = CodeFixHarness.ApplyOnlyFix(
            project,
            new FlowMustBePartialCodeFixProvider(),
            "FLOWX1001");

        CodeFixHarness.CompileErrors(fixedProject).ShouldBeEmpty();
    }

    /// <summary>
    /// The global usings the SDK would have emitted for the sample project.
    /// </summary>
    /// <remarks>
    /// The sample is compiled by MSBuild with <c>ImplicitUsings=enable</c>, so its files
    /// name <c>ValueTask</c> and <c>ArgumentNullException</c> without importing them.
    /// A workspace built by hand gets none of that. Restating the SDK's list here is
    /// closer to the real build than editing the sample's source would be — and editing
    /// the sample to suit a test is how a reference application stops being one.
    /// </remarks>
    private static TestDocument ImplicitUsings => new(
        "GlobalUsings.cs",
        """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """);

    private static string ReadSample(string fileName) =>
        File.ReadAllText(Path.Combine(Repository.Directory("samples/ecommerce"), fileName));
}
