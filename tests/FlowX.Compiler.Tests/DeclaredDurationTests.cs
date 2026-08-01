using FlowX.Compiler.Analysis;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// What <c>DeclaredDuration</c> can evaluate, and what it refuses to guess at.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This exists because the plan and the manifest need different things from the same
/// expression.</strong> The plan is C#, so <c>Waits.Countersignature</c> reaching it verbatim
/// <em>is</em> the duration — evaluated by the compilation that declared it, which is why
/// <c>StepModel.SignalTimeout</c> is copied and not folded. The manifest is JSON, read by
/// tools that have never seen the assembly, so the same expression has to become
/// <c>"P7D"</c> or become nothing at all
/// (<a href="../../../docs/adr/ADR-0021-manifest-publishes-the-wait.md">ADR-0021 §2.2</a>).
/// </para>
/// <para>
/// The syntactic cases are asserted here, against source text, because the folder's whole
/// job is to say <em>no</em> often and clearly: every form outside the small set it knows
/// has to produce <c>null</c> rather than a number nobody wrote. Its behaviour on a real
/// compilation — where a member reference resolves through a semantic model — is asserted by
/// <see cref="SuspensionConstructTests"/> against a flow.
/// </para>
/// </remarks>
public sealed class DeclaredDurationTests
{
    [Theory]
    [InlineData("TimeSpan.FromDays(7)", "P7D")]
    [InlineData("TimeSpan.FromHours(1)", "PT1H")]
    [InlineData("TimeSpan.FromMinutes(30)", "PT30M")]
    [InlineData("TimeSpan.FromSeconds(2)", "PT2S")]
    [InlineData("TimeSpan.FromMilliseconds(1500)", "PT1.5S")]
    [InlineData("TimeSpan.Zero", "PT0S")]
    [InlineData("System.TimeSpan.FromDays(1)", "P1D")]
    [InlineData("TimeSpan.FromHours(36)", "P1DT12H")]
    [InlineData("TimeSpan.FromMinutes(1.5)", "PT1M30S")]
    public void TheFormsAuthorsActuallyWriteFold(string expression, string expected)
        => DeclaredDuration.Fold(expression).ShouldBe(expected);

    /// <summary>
    /// Everything else is refused, and refusal is the point.
    /// </summary>
    /// <remarks>
    /// A folder that fell back to a plausible default would publish a wait the author never
    /// declared — which is the fabricated <c>TimeSpan.FromHours(1)</c> that
    /// <c>FLOWX1031</c> was raised over, moved from the plan into the manifest.
    /// </remarks>
    [Theory]
    [InlineData("ComputeWindow()")]
    [InlineData("configuration.OfferWindow")]
    [InlineData("TimeSpan.FromDays(days)")]
    [InlineData("TimeSpan.Parse(\"7.00:00:00\")")]
    [InlineData("new TimeSpan(7, 0, 0, 0)")]
    [InlineData("TimeSpan.FromTicks(1)")]
    [InlineData("isRush ? TimeSpan.FromDays(1) : TimeSpan.FromDays(7)")]
    [InlineData("")]
    public void EverythingElseFoldsToNothing(string expression)
        => DeclaredDuration.Fold(expression).ShouldBeNull();

    [Fact]
    public void ANullExpressionFoldsToNothing() => DeclaredDuration.Fold(null).ShouldBeNull();

    /// <summary>
    /// A negative duration is refused rather than rendered.
    /// </summary>
    /// <remarks>
    /// ISO-8601's grammar as the schema states it has no sign, so there is nothing correct
    /// to write. A wait of minus a day is a declaration the author should be told about, and
    /// omitting the field is what tells them nothing was published for it.
    /// </remarks>
    [Fact]
    public void ANegativeWaitIsRefused() => DeclaredDuration.Fold("TimeSpan.FromDays(-1)").ShouldBeNull();
}
