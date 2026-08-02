using FlowX.Ai;
using Shouldly;
using Xunit;

namespace AiAgent.Tests;

/// <summary>
/// The reviewer, against manifests written here and against the sample's own.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written documents for the rules, because each rule needs a manifest shaped to trip it and
/// a near-identical one that must not — and a rule with false positives is suppressed everywhere
/// and then protects nothing. The sample's real manifest is used for the properties that must
/// hold on a document nobody wrote for a test.
/// </para>
/// <para>
/// The reviewer links no FlowX assembly, so these construct their inputs as strings. That is the
/// same position a third-party tool is in, which is the point of ADR-0005 and the reason the
/// project has no project references.
/// </para>
/// </remarks>
public sealed class ManifestReviewTests
{
    /// <summary>An event with a producer and no consumer is reported; one with both is not.</summary>
    [Fact]
    public void AnEventWithOneEndIsReported()
    {
        var reported = ManifestReview.Of(
            """
            {"application":{"name":"X"},
             "events":[{"type":"order.placed","producedBy":["order.place"],"consumedBy":[]}]}
            """);

        reported.Findings.Select(static finding => finding.Code)
            .ShouldBe([FlowFinding.OrphanEvent]);

        reported.Findings[0].Subject.ShouldBe("order.placed");

        var quiet = ManifestReview.Of(
            """
            {"application":{"name":"X"},
             "events":[{"type":"order.placed","producedBy":["order.place"],"consumedBy":["ship.create"]}]}
            """);

        quiet.Findings.ShouldBeEmpty();
    }

    /// <summary>
    /// A public capability with declared side effects is a warning; one with neither half is not.
    /// </summary>
    /// <remarks>
    /// docs/13 §2 gives this as its security-review query. Both negative arms are checked: a
    /// public capability that changes nothing, and an effectful capability that has a stance.
    /// </remarks>
    [Fact]
    public void APublicCapabilityWithSideEffectsIsReported()
    {
        var reported = ManifestReview.Of(Capabilities("Public", """["payment-gateway"]"""));

        reported.Findings.Select(static finding => finding.Code)
            .ShouldBe([FlowFinding.PublicSideEffect]);

        reported.Findings[0].Severity.ShouldBe(FindingSeverity.Warning);
        reported.Findings[0].Subject.ShouldBe("payment.capture@1.0.0");

        ManifestReview.Of(Capabilities("Public", "[]")).Findings.ShouldBeEmpty();
        ManifestReview.Of(Capabilities("Authenticated", """["payment-gateway"]""")).Findings.ShouldBeEmpty();
    }

    /// <summary>
    /// A flow that compensates some effectful steps and not others is a warning; one that
    /// compensates none is not.
    /// </summary>
    /// <remarks>
    /// The negative arm is the whole design of the rule. Nearly every flow ever written
    /// compensates nothing, and a reviewer that reported on all of them would be turned off on
    /// its first run.
    /// </remarks>
    [Fact]
    public void APartiallyCompensatedSagaIsReported()
    {
        var reported = ManifestReview.Of(Saga(compensateSecond: false));

        reported.Findings.Select(static finding => finding.Code)
            .ShouldBe([FlowFinding.PartialSaga]);

        reported.Findings[0].Message.ShouldContain("payment.capture@1.0.0");

        ManifestReview.Of(Saga(compensateSecond: true)).Findings.ShouldBeEmpty();
        ManifestReview.Of(Saga(compensateSecond: false, compensateFirst: false)).Findings.ShouldBeEmpty();
    }

    /// <summary>
    /// An agent tool declaring <c>Never</c> over declared side effects is a warning; one with no
    /// side effects is not.
    /// </summary>
    /// <remarks>
    /// The same condition <c>FLOWX1046</c> reports in the repository that owns the flow. Both are
    /// worth having and they see different things: the analyzer stops a build, and this reads a
    /// manifest from any build — including one whose source this reader does not have.
    /// </remarks>
    [Fact]
    public void AnAgentToolThatAsksNobodyIsReported()
    {
        var reported = ManifestReview.Of(AgentTool("Never", """["payment-gateway"]"""));

        reported.Findings.Select(static finding => finding.Code)
            .ShouldContain(FlowFinding.UnconfirmedAgentTool);

        ManifestReview.Of(AgentTool("Never", "[]")).Findings
            .Select(static finding => finding.Code)
            .ShouldNotContain(FlowFinding.UnconfirmedAgentTool);

        ManifestReview.Of(AgentTool("RequiredForSideEffects", """["payment-gateway"]""")).Findings
            .Select(static finding => finding.Code)
            .ShouldNotContain(FlowFinding.UnconfirmedAgentTool);
    }

    /// <summary>Warnings sort before notes, and the order is stable.</summary>
    /// <remarks>
    /// Stability is what makes two reviews of two builds diffable, which is the difference between
    /// a report and a thing somebody reads once.
    /// </remarks>
    [Fact]
    public void FindingsAreOrderedWarningsFirstAndDeterministically()
    {
        var review = ManifestReview.Of(FlowX.Generated.FlowXManifest.Json);
        var again = ManifestReview.Of(FlowX.Generated.FlowXManifest.Json);

        review.Findings.Select(static finding => finding.Severity)
            .ShouldBeInOrder(SortDirection.Descending);

        review.ToText().ShouldBe(again.ToText());
    }

    /// <summary>The sample's own manifest raises what the sample is built to raise.</summary>
    /// <remarks>
    /// A review of a real document rather than a fixture. <c>ticket.refund</c> takes a
    /// <c>[Sensitive]</c> argument as an agent tool, which is a note a reader of this application
    /// should see — and nothing in it is a warning, which is what makes the sample a reference
    /// rather than a demonstration of the findings.
    /// </remarks>
    [Fact]
    public void TheSamplesOwnManifestIsReviewedAndIsClean()
    {
        var review = ManifestReview.Of(FlowX.Generated.FlowXManifest.Json);

        review.ApplicationName.ShouldBe("AiAgent");

        review.Findings
            .Where(static finding => finding.Severity == FindingSeverity.Warning)
            .ShouldBeEmpty(
                "The reference sample raises a warning about itself. Fix the sample, or the " +
                "rule is wrong.");

        review.Findings.Select(static finding => finding.Code)
            .ShouldContain(FlowFinding.AgentToolTakesSecret);
    }

    /// <summary>What a model is handed carries the findings and says where they came from.</summary>
    [Fact]
    public void ThePromptIsTheReportAndItsProvenance()
    {
        var review = ManifestReview.Of(FlowX.Generated.FlowXManifest.Json);

        review.ToPrompt().ShouldContain(review.ToText());
        review.ToPrompt().ShouldContain("no source code");
    }

    /// <summary>A document that is not JSON is refused with a message naming what was handed in.</summary>
    [Fact]
    public void ADocumentThatIsNotJsonIsRefused() =>
        Should.Throw<ArgumentException>(static () => ManifestReview.Of("not json"))
            .ParamName.ShouldBe("manifestJson");

    /// <summary>A manifest carrying none of the members the rules read raises nothing.</summary>
    /// <remarks>
    /// A newer compiler's document, or an older one's. Every reader answers "what does this say"
    /// rather than "what should it have said", so an absent member is absent and not a finding
    /// about the document's shape.
    /// </remarks>
    [Fact]
    public void AManifestWithNothingToSayRaisesNothing() =>
        ManifestReview.Of("""{"schemaVersion":"0.1.0"}""").Findings.ShouldBeEmpty();

    private static string Capabilities(string mode, string sideEffects) =>
        $$"""
        {"application":{"name":"X"},
         "capabilities":[{"id":"payment.capture","version":"1.0.0",
                          "authorization":{"mode":"{{mode}}"},"sideEffects":{{sideEffects}}}]}
        """;

    private static string Saga(bool compensateSecond, bool compensateFirst = true) =>
        $$"""
        {"application":{"name":"X"},
         "capabilities":[
           {"id":"inventory.reserve","version":"1.0.0","authorization":{"mode":"Authenticated"},
            "sideEffects":["inventory-ledger"]},
           {"id":"payment.capture","version":"1.0.0","authorization":{"mode":"Authenticated"},
            "sideEffects":["payment-gateway"]}],
         "flows":[{"id":"order.place","steps":[
           {"id":0,"capability":"inventory.reserve@1.0.0"
             {{(compensateFirst ? ""","compensation":"inventory.release@1.0.0" """ : "")}}},
           {"id":1,"capability":"payment.capture@1.0.0"
             {{(compensateSecond ? ""","compensation":"payment.refund@1.0.0" """ : "")}}}]}]}
        """;

    private static string AgentTool(string confirmation, string sideEffects) =>
        $$"""
        {"application":{"name":"X"},
         "capabilities":[{"id":"payment.capture","version":"1.0.0",
                          "authorization":{"mode":"Authenticated"},"sideEffects":{{sideEffects}}}],
         "flows":[{"id":"order.place",
                   "triggers":[{"kind":"Agent","confirmation":"{{confirmation}}"}],
                   "steps":[{"id":0,"capability":"payment.capture@1.0.0"}]}]}
        """;
}
