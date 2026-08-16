using System;
using System.Linq;
using FlowX.Compiler.Analysis;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// FLOWX1055: an <c>[EventSchema("…")]</c> whose value is not a semantic version.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The silent half is most of this file, and it is the half worth having.</strong>
/// The attribute is optional and the overwhelmingly common contract carries none, so a rule
/// that reported anything about an undeclared event would be suppressed in every repository
/// that emits one and would then protect nothing.
/// </para>
/// <para>
/// <strong>What makes the rule load-bearing is what happens when it is silent.</strong> An
/// unreadable value is dropped by <see cref="EventSchemaReader"/>, so the contract goes on
/// publishing <c>1.0.0</c> in the manifest and on every outbox row while the source says
/// otherwise — asserted by <c>EventSchemaTests.AMalformedVersionIsDroppedRatherThanPublished</c>,
/// which is the other end of this rule and the reason it is an error.
/// </para>
/// </remarks>
public sealed class EventSchemaAnalyzerTests
{
    private static string ContractWith(string attributes) => $$"""
        using FlowX;

        namespace Sample;

        {{attributes}}
        public sealed record OrderPlaced(string OrderId);
        """;

    private static string[] Analyze(string source)
    {
        GeneratorHarness.CompileErrorsIn(source).ShouldBeEmpty();

        return GeneratorHarness.Analyze(source, new EventSchemaAnalyzer());
    }

    // ------------------------------------------------------------- it must fire

    /// <summary>
    /// Every value the reader refuses is reported, and each is one somebody writes.
    /// </summary>
    /// <remarks>
    /// <c>2.0</c> and <c>2</c> are the two-thirds and one-third versions every ecosystem
    /// accepts somewhere; <c>v2.0.0</c> is the git-tag spelling; <c>01.0.0</c> is the padded
    /// one a release script produces. None of them reaches the manifest, and none of them
    /// fails anything at run time — which is the whole argument for reporting here.
    /// </remarks>
    [Theory]
    [InlineData("\"v2\"")]
    [InlineData("\"v2.0.0\"")]
    [InlineData("\"2\"")]
    [InlineData("\"2.0\"")]
    [InlineData("\"2.0.0.0\"")]
    [InlineData("\"01.0.0\"")]
    [InlineData("\"\"")]
    [InlineData("\" 2.0.0\"")]
    [InlineData("\"2.0.0-\"")]
    public void AVersionTheCompilerCannotReadIsReported(string version)
    {
        Analyze(ContractWith($"[EventSchema({version})]")).ShouldBe(["FLOWX1055"]);
    }

    /// <summary>The message names the contract and the value, because both are needed.</summary>
    [Fact]
    public void TheReportNamesTheContractAndTheOffendingValue()
    {
        var message = GeneratorHarness
            .AnalyzeWithMessages(ContractWith("[EventSchema(\"v2\")]"), new EventSchemaAnalyzer())
            .Single();

        message.ShouldContain("OrderPlaced");
        message.ShouldContain("v2");
        message.ShouldContain("1.0.0", Case.Sensitive, "and says what is published instead.");
    }

    /// <summary>
    /// A contract nothing emits is still reported, which is why the rule reads the type.
    /// </summary>
    /// <remarks>
    /// The fixture above declares no flow at all. Reporting only at an <c>.Emit</c> call site
    /// would leave the mistake in the source until the first emitter lands, which is exactly
    /// how long it takes for a wrong version to be believed.
    /// </remarks>
    [Fact]
    public void AContractNoFlowEmitsIsStillJudged()
    {
        Analyze(ContractWith("[EventSchema(\"v2\")]")).ShouldBe(["FLOWX1055"]);
    }

    // ------------------------------------------------------------ it must not fire

    /// <summary>Every version SemVer 2.0 admits is accepted, metadata included.</summary>
    [Theory]
    [InlineData("\"1.0.0\"")]
    [InlineData("\"2.0.0\"")]
    [InlineData("\"0.0.0\"")]
    [InlineData("\"10.20.30\"")]
    [InlineData("\"2.1.0-rc.1\"")]
    [InlineData("\"1.0.0+build.7\"")]
    [InlineData("\"1.0.0-alpha.1+build.7\"")]
    public void AVersionTheCompilerCanReadIsSilent(string version)
    {
        Analyze(ContractWith($"[EventSchema({version})]")).ShouldBeEmpty();
    }

    /// <summary>The ordinary contract — no attribute — is never mentioned.</summary>
    [Fact]
    public void AContractThatDeclaresNothingIsSilent()
    {
        Analyze(ContractWith(string.Empty)).ShouldBeEmpty();
    }

    // --------------------------------------------------------------- the grammar

    /// <summary>
    /// The compiler's grammar is the runtime's, specimen for specimen.
    /// </summary>
    /// <remarks>
    /// This assembly is netstandard2.0 and cannot reference the one holding
    /// <c>Identifiers.VersionPattern</c>, so the reader re-implements SemVer 2.0 and the two
    /// could drift. <c>FlowDescriptor.Create</c> is the public door onto the run-time pattern:
    /// putting the same values through both is what keeps a version the compiler accepts from
    /// being one the platform refuses at composition time.
    /// </remarks>
    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("2.1.0-rc.1", true)]
    [InlineData("1.0.0+build.7", true)]
    [InlineData("10.20.30", true)]
    [InlineData("v2", false)]
    [InlineData("2.0", false)]
    [InlineData("2.0.0.0", false)]
    [InlineData("01.0.0", false)]
    [InlineData("", false)]
    public void TheGrammarIsTheOneTheRuntimeEnforces(string version, bool accepted)
    {
        RuntimeAccepts(version).ShouldBe(
            accepted,
            $"'{version}' must be judged the same way by Identifiers.RequireSemanticVersion " +
            "and by EventSchemaReader, or a build accepts a version the platform will not.");

        Analyze(ContractWith($"[EventSchema(\"{version}\")]"))
            .ShouldBe(accepted ? [] : ["FLOWX1055"]);
    }

    private static bool RuntimeAccepts(string version)
    {
        try
        {
            FlowDescriptor.Create("order.place", version, ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30));

            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
