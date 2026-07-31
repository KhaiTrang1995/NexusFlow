using System.Text.Json;
using FlowX.Cli.Verification;
using Shouldly;
using Xunit;

namespace FlowX.Cli.Tests;

/// <summary>Rendering of the cost report, for the person and for the pipeline.</summary>
public sealed class CostFormatterTests
{
    private static CostReport Report(int durableFlows, params CostFinding[] findings) => new()
    {
        Application = "Sample.App",
        Version = "1.0.0",
        DurableFlows = durableFlows,
        Findings = findings,
    };

    private static CostFinding Finding(string subject = "flow report.daily@1.0.0") => new()
    {
        Code = "FLOWX-VERIFY-001",
        Subject = subject,
        Summary = "profile is Durable, with no compensation, no signal and no timer",
        Consequence = "Durable buys a journal write per step.",
    };

    [Fact]
    public void AManifestWithNoDurableFlowSaysSoRatherThanPrintingNothing()
    {
        var text = CostFormatter.ToText(Report(durableFlows: 0));

        text.ShouldContain("No flow declares the Durable profile", Case.Sensitive,
            "Silence is indistinguishable from a check that never ran, and this runs in a " +
            "job nobody watches until it goes red.");
    }

    [Fact]
    public void APassSaysHowManyFlowsWereActuallyExamined()
    {
        var text = CostFormatter.ToText(Report(durableFlows: 3));

        text.ShouldContain("3 checked", Case.Sensitive,
            "'No findings' over an unknown denominator is also what a manifest read from " +
            "the wrong path looks like.");
    }

    [Fact]
    public void AFindingCarriesItsCodeSubjectSummaryAndConsequence()
    {
        var text = CostFormatter.ToText(Report(durableFlows: 2, Finding()));

        text.ShouldContain("FLOWX-VERIFY-001", Case.Sensitive);
        text.ShouldContain("flow report.daily@1.0.0", Case.Sensitive);
        text.ShouldContain("no compensation, no signal and no timer", Case.Sensitive);
        text.ShouldContain("journal write", Case.Sensitive);
        text.ShouldContain("1 of 2 durable flows", Case.Sensitive);
    }

    [Fact]
    public void TheSummaryLineAgreesWithItselfWhenThereIsOneDurableFlow()
    {
        var text = CostFormatter.ToText(Report(durableFlows: 1, Finding()));

        text.ShouldContain("1 of 1 durable flow uses", Case.Sensitive);
        text.ShouldNotContain("flows use", Case.Sensitive);
    }

    [Fact]
    public void JsonCarriesTheVerdictAndTheFindings()
    {
        var json = CostFormatter.ToJson(Report(durableFlows: 2, Finding()));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.GetProperty("passed").GetBoolean().ShouldBeFalse();
        root.GetProperty("durableFlows").GetInt32().ShouldBe(2);
        root.GetProperty("flagged").GetInt32().ShouldBe(1);
        root.GetProperty("findings").GetArrayLength().ShouldBe(1);
        root.GetProperty("findings")[0].GetProperty("code").GetString().ShouldBe("FLOWX-VERIFY-001");
    }

    [Fact]
    public void TheVerdictIsSerialisedBeforeTheFindings()
    {
        var json = CostFormatter.ToJson(Report(durableFlows: 2, Finding()));

        json.IndexOf("\"passed\"", StringComparison.Ordinal)
            .ShouldBeLessThan(json.IndexOf("\"findings\"", StringComparison.Ordinal),
                "A consumer that only needs the answer should not have to read the whole " +
                "document to find it.");
    }

    [Fact]
    public void EmDashesSurviveSerialisationSoABuildLogStaysReadable()
    {
        var json = CostFormatter.ToJson(Report(2, Finding() with { Consequence = "a — b" }));

        json.ShouldContain("a — b", Case.Sensitive,
            "The HTML-safe encoder would write \\u2014 into a document nobody puts in markup.");
    }

    [Fact]
    public void RejectsANullReport()
    {
        Should.Throw<ArgumentNullException>(() => CostFormatter.ToText(null!));
        Should.Throw<ArgumentNullException>(() => CostFormatter.ToJson(null!));
    }
}
