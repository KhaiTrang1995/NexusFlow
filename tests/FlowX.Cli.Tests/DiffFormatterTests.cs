using System.Text.Json;
using FlowX.Cli.Diffing;
using Shouldly;
using Xunit;

namespace FlowX.Cli.Tests;

/// <summary>
/// The two renderings of a verdict: one for the person reading a red build, one for
/// whatever consumes the build afterwards.
/// </summary>
/// <remarks>
/// Both come from the same report rather than from two traversals of the manifests, so
/// the gate and the log cannot disagree about what was found — the failure where a build
/// goes red and the printed output shows nothing wrong.
/// </remarks>
public sealed class DiffFormatterTests
{
    private static readonly DiffReport Report = new()
    {
        Application = "Ordering",
        BaselineVersion = "2.3.0",
        CandidateVersion = "2.4.0",
        Findings =
        [
            new DiffFinding
            {
                Code = "FLOWX-DIFF-014",
                Severity = DiffSeverity.Breaking,
                Subject = "capability payment.capture@2",
                Summary = "authorization relaxed: Permission -> Public",
                Consequence = "Security regression: reachable by principals the baseline refused.",
            },
            new DiffFinding
            {
                Code = "FLOWX-DIFF-101",
                Severity = DiffSeverity.Additive,
                Subject = "capability refund.issue@1",
                Summary = "added",
            },
            new DiffFinding
            {
                Code = "FLOWX-DIFF-203",
                Severity = DiffSeverity.Neutral,
                Subject = "flow order.place@1",
                Summary = "deadline changed: PT30S -> PT10S",
            },
        ],
    };

    private static readonly DiffReport Clean = new() { Application = "Ordering" };

    [Fact]
    public void TextNamesTheTwoThingsThatWereCompared()
        => DiffFormatter.ToText(Report).ShouldStartWith("flowx diff — Ordering: 2.3.0 -> 2.4.0");

    [Fact]
    public void TextCarriesTheCodeSubjectSummaryAndReason()
    {
        var text = DiffFormatter.ToText(Report);

        text.ShouldContain("FLOWX-DIFF-014", Case.Sensitive);
        text.ShouldContain("capability payment.capture@2", Case.Sensitive);
        text.ShouldContain("authorization relaxed: Permission -> Public", Case.Sensitive);
        text.ShouldContain("Security regression", Case.Sensitive);
        // The code is what a CI log is grepped for and what an ADR waiver cites; the
        // reason is what stops a reviewer waving the finding through.
    }

    [Fact]
    public void TextGroupsFindingsBySeverityWithBreakingFirst()
    {
        var text = DiffFormatter.ToText(Report);

        text.IndexOf("BREAKING", StringComparison.Ordinal)
            .ShouldBeLessThan(text.IndexOf("ADDITIVE", StringComparison.Ordinal));

        text.IndexOf("ADDITIVE", StringComparison.Ordinal)
            .ShouldBeLessThan(text.IndexOf("NEUTRAL", StringComparison.Ordinal));
    }

    [Fact]
    public void TextEndsWithAVerdictAScrollingReaderCannotMiss()
        => DiffFormatter.ToText(Report).ShouldEndWith(
            "1 breaking change, 1 additive, 1 neutral — INCOMPATIBLE.\n");

    [Fact]
    public void TextSaysSoExplicitlyWhenNothingChanged()
    {
        var text = DiffFormatter.ToText(Clean);

        text.ShouldContain("No contract changes.", Case.Sensitive);
        text.ShouldNotContain("BREAKING", Case.Sensitive);
        // Silence on success is indistinguishable from a tool that failed to run, and this
        // one runs in a job nobody watches until it is red.
    }

    [Fact]
    public void TextOmitsSeverityHeadingsThatWouldBeEmpty()
    {
        var oneFinding = new DiffReport { Application = "Ordering", Findings = [Report.Findings[1]] };

        var text = DiffFormatter.ToText(oneFinding);

        text.ShouldContain("ADDITIVE", Case.Sensitive);
        text.ShouldNotContain("BREAKING", Case.Sensitive);
        text.ShouldNotContain("NEUTRAL", Case.Sensitive);
    }

    [Fact]
    public void JsonLeadsWithTheVerdictAndTheCounts()
    {
        using var document = JsonDocument.Parse(DiffFormatter.ToJson(Report));

        var root = document.RootElement;

        root.GetProperty("compatible").GetBoolean().ShouldBeFalse();
        root.GetProperty("breaking").GetInt32().ShouldBe(1);
        root.GetProperty("additive").GetInt32().ShouldBe(1);
        root.GetProperty("neutral").GetInt32().ShouldBe(1);

        root.EnumerateObject().Select(p => p.Name).ToList()
            .IndexOf("compatible")
            .ShouldBeLessThan(root.EnumerateObject().Select(p => p.Name).ToList().IndexOf("findings"),
                "A consumer that only needs the gate's answer should not have to read the " +
                "whole document to find it.");
    }

    [Fact]
    public void JsonCarriesEveryFindingWithASeverityThatReadsAsAWord()
    {
        using var document = JsonDocument.Parse(DiffFormatter.ToJson(Report));

        var findings = document.RootElement.GetProperty("findings").EnumerateArray().ToList();

        findings.Count.ShouldBe(3);
        findings[0].GetProperty("code").GetString().ShouldBe("FLOWX-DIFF-014");
        findings[0].GetProperty("severity").GetString().ShouldBe("Breaking",
            "An integer here would make the format unreadable and would silently change " +
            "meaning if a severity were ever inserted.");
    }

    [Fact]
    public void JsonEscapesNothingItDoesNotHaveTo()
    {
        var json = DiffFormatter.ToJson(Report);

        json.ShouldContain("Permission -> Public", Case.Sensitive);
        json.ShouldNotContain("\\u003E", Case.Sensitive);
        // The default encoder escapes '>' so the result is safe inside markup. Nothing
        // here is ever emitted into markup, and the escaping makes every summary harder
        // for a person to read.
    }

    [Fact]
    public void JsonIsValidForAReportWithNoFindings()
    {
        using var document = JsonDocument.Parse(DiffFormatter.ToJson(Clean));

        document.RootElement.GetProperty("compatible").GetBoolean().ShouldBeTrue();
        document.RootElement.GetProperty("findings").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void BothRenderingsAgreeOnTheVerdict()
    {
        using var document = JsonDocument.Parse(DiffFormatter.ToJson(Report));

        document.RootElement.GetProperty("compatible").GetBoolean()
            .ShouldBe(!DiffFormatter.ToText(Report).Contains("INCOMPATIBLE", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsANullReport()
    {
        Should.Throw<ArgumentNullException>(() => DiffFormatter.ToText(null!));
        Should.Throw<ArgumentNullException>(() => DiffFormatter.ToJson(null!));
    }
}
