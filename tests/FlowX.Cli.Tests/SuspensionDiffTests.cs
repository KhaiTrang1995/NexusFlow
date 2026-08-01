using System.Text.Json;
using FlowX.Cli.Diffing;
using FlowX.Cli.Manifest;
using Shouldly;
using Xunit;

namespace FlowX.Cli.Tests;

/// <summary>
/// The three rules that make an <c>AwaitSignal</c> step's two new fields mean something:
/// <c>FLOWX-DIFF-021</c>, <c>022</c> and <c>206</c>
/// (<a href="../../../docs/adr/ADR-0021-manifest-publishes-the-wait.md">ADR-0021 §2.3</a>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every candidate here is built by editing the baseline's JSON text, not its object
/// model, and that is the point of the class.</strong> This repository has already shipped a
/// <c>FLOWX-DIFF</c> code that could never fire — <c>FLOWX-DIFF-015</c> compared
/// <c>authorization.value</c>, which <c>ManifestWriter</c> did not write — and neither
/// <c>ManifestSchemaTests</c> nor <c>DiffCodeDocumentationTests</c> could see it, because one
/// knows schema and instance and the other knows codes and documentation. Nothing knew fields
/// and rules.
/// </para>
/// <para>
/// Going through the text means going through the real property names and the real
/// deserialiser: if <c>ManifestDocument</c> ever reads <c>signalType</c> while
/// <c>ManifestWriter</c> writes <c>signal</c>, the assertions below stop firing rather than
/// passing against an object nobody could have parsed. The other half of the loop is
/// <c>Workflow.Tests.ManifestTests</c>, which asserts the same two literal property names
/// against a manifest a real compilation produced.
/// </para>
/// <para>
/// The baseline is the shape <c>ManifestWriter</c> emits for <c>samples/workflow</c>'s
/// <c>offer.accept</c>: a compensable step, a wait, and the step that binds what the signal
/// delivered.
/// </para>
/// </remarks>
public sealed class SuspensionDiffTests
{
    /// <summary>The steps the edits below target, written exactly as the baseline holds them.</summary>
    /// <remarks>
    /// Named rather than repeated inside each edit, so an edit that stops matching fails the
    /// whole class instead of one test — and <see cref="Diff"/> asserts that every edit
    /// changed something, because a replacement that matched nothing produces a candidate
    /// identical to the baseline and a report with nothing in it, which is how a test about a
    /// rule that cannot fire passes.
    /// </remarks>
    private const string SendStep =
        "{ \"id\": 0, \"kind\": \"Capability\", \"capability\": \"offer.send@1.0.0\", " +
        "\"compensation\": \"offer.withdraw@1.0.0\" }";

    private const string WaitStep =
        "{ \"id\": 1, \"kind\": \"AwaitSignal\", \"signal\": \"offer.countersigned\", \"timeout\": \"P7D\" }";

    private const string PriceStep =
        "{ \"id\": 0, \"kind\": \"Capability\", \"capability\": \"offer.price@1.0.0\" }";

    private const string Baseline = """
        {
          "schemaVersion": "0.1.0",
          "application": { "name": "Workflow", "version": "1.0.0" },
          "flows": [
            {
              "id": "offer.accept", "version": "1.0.0", "profile": "Durable", "deadline": "P30D",
              "input":  { "type": "Workflow.OfferToAccept" },
              "output": { "type": "Workflow.AcceptedOffer" },
              "triggers": [
                { "kind": "Http", "method": "POST", "route": "/api/v1/offers", "idempotent": true }
              ],
              "steps": [
                { "id": 0, "kind": "Capability", "capability": "offer.send@1.0.0", "compensation": "offer.withdraw@1.0.0" },
                { "id": 1, "kind": "AwaitSignal", "signal": "offer.countersigned", "timeout": "P7D" },
                { "id": 2, "kind": "Capability", "capability": "onboarding.start@1.0.0" }
              ],
              "emits": []
            },
            {
              "id": "offer.quote", "version": "1.0.0", "profile": "Durable",
              "input": { "type": "Workflow.QuoteRequest" }, "output": { "type": "Workflow.Quote" },
              "steps": [ { "id": 0, "kind": "Capability", "capability": "offer.price@1.0.0" } ],
              "emits": []
            }
          ],
          "capabilities": [
            { "id": "offer.send", "version": "1.0.0",
              "input": "Workflow.OfferToAccept", "output": "Workflow.OfferSent",
              "authorization": { "mode": "Authenticated" },
              "idempotent": true, "sideEffects": ["e-signature"] }
          ],
          "events": []
        }
        """;

    // ------------------------------------------------------------------ nothing

    [Fact]
    public void AManifestComparedWithItselfReportsNothing()
    {
        var report = ManifestDiff.Compare(Parse(Baseline), Parse(Baseline));

        report.Findings.ShouldBeEmpty();
        report.Compatible.ShouldBeTrue();
    }

    /// <summary>
    /// Steps around a wait are still not compared, and neither is moving the wait.
    /// </summary>
    /// <remarks>
    /// ADR-0021 §2.4 narrows 22-CLI §2.2's "a flow's steps are never reported" to two
    /// properties of one kind of step. It does not open the door: adding a capability
    /// beside the wait, and pushing the wait one index along, changes what the flow
    /// <em>does</em> and not what it <em>requires from outside</em>.
    /// </remarks>
    [Fact]
    public void RefactoringTheStepsAroundAWaitIsStillNotAChange()
    {
        var report = Diff(candidate => candidate.Replace(
            SendStep,
            "{ \"id\": 9, \"kind\": \"Capability\", \"capability\": \"offer.enrich@1.0.0\" }, " + SendStep,
            StringComparison.Ordinal));

        report.Findings.ShouldBeEmpty(
            "Only an AwaitSignal step's signal and timeout take part. Every other step is " +
            "implementation, and reporting it would punish the refactoring FlowX exists to " +
            "make safe.");
    }

    // ------------------------------------------------------------- 021: removed

    /// <summary>
    /// A flow that stops waiting for a signal is Breaking, and the consequence names why.
    /// </summary>
    /// <remarks>
    /// This is the quietest break the tool reports. <c>FlowHost.SignalAsync</c> treats a
    /// signal for an instance that is not waiting for it as <em>inert, not an error</em> — so
    /// every existing sender keeps posting the old identity, every delivery is accepted, and
    /// nothing at all happens until the instances hit their deadline. No status code moves and
    /// no exception is raised.
    /// </remarks>
    [Fact]
    public void AFlowThatNoLongerWaitsForASignalIsBreaking()
    {
        var report = Diff(candidate => candidate.Replace(WaitStep + ",", string.Empty, StringComparison.Ordinal));

        var finding = Fired(report, "FLOWX-DIFF-021");

        finding.Severity.ShouldBe(DiffSeverity.Breaking);
        finding.Subject.ShouldBe("flow offer.accept@1");
        finding.Summary.ShouldContain("offer.countersigned");
        report.HasBreakingChange.ShouldBeTrue();
    }

    // --------------------------------------------------------------- 022: added

    /// <summary>
    /// A flow that gains a wait is Breaking, because its callers stop getting an answer.
    /// </summary>
    /// <remarks>
    /// Over HTTP the response changes from <c>200</c> with the flow's projected output to
    /// <c>202</c> with an instance id (ADR-0022), and the work does not finish until somebody
    /// delivers a signal the baseline never mentioned. Nothing in the flow's signature moves.
    /// </remarks>
    [Fact]
    public void AFlowThatGainsAWaitIsBreaking()
    {
        var report = Diff(candidate => candidate.Replace(
            PriceStep,
            PriceStep + ", { \"id\": 1, \"kind\": \"AwaitSignal\", \"signal\": \"offer.approved\", " +
            "\"timeout\": \"PT2H\" }",
            StringComparison.Ordinal));

        var finding = Fired(report, "FLOWX-DIFF-022");

        finding.Severity.ShouldBe(DiffSeverity.Breaking);
        finding.Subject.ShouldBe("flow offer.quote@1");
        finding.Summary.ShouldContain("offer.approved");
    }

    /// <summary>
    /// Renaming the signal a flow waits for reports both halves, not one merged finding.
    /// </summary>
    /// <remarks>
    /// One address disappeared and another appeared, and the two break different parties:
    /// the sender loses the identity it addresses, and any caller now has to learn a name
    /// nobody published. A single "signals changed" code would have made the reader
    /// decompose that themselves.
    /// </remarks>
    [Fact]
    public void RenamingTheSignalReportsARemovalAndAnAddition()
    {
        var report = Diff(candidate => candidate.Replace(
            "offer.countersigned", "offer.signed", StringComparison.Ordinal));

        Fired(report, "FLOWX-DIFF-021").Summary.ShouldContain("offer.countersigned");
        Fired(report, "FLOWX-DIFF-022").Summary.ShouldContain("offer.signed");
        NotFired(report, "FLOWX-DIFF-206");
    }

    // ------------------------------------------------------------- 206: timeout

    /// <summary>
    /// A changed wait window is Neutral, for the reason a changed deadline is.
    /// </summary>
    /// <remarks>
    /// It is an operational budget tuned against how long real people take, not a term of
    /// the contract: nothing stops compiling and no request stops binding because an offer
    /// is open for fourteen days instead of seven. It is reported so a reviewer sees it, and
    /// not gated, because a gate that fails a build on a tuning change is a gate people
    /// route around.
    /// </remarks>
    [Fact]
    public void AChangedWaitWindowIsNeutral()
    {
        var report = Diff(candidate => candidate.Replace(
            "\"timeout\": \"P7D\"", "\"timeout\": \"P14D\"", StringComparison.Ordinal));

        var finding = Fired(report, "FLOWX-DIFF-206");

        finding.Severity.ShouldBe(DiffSeverity.Neutral);
        finding.Subject.ShouldBe("flow offer.accept@1 signal offer.countersigned");
        finding.Summary.ShouldBe("declared wait changed: P7D -> P14D");
        report.HasBreakingChange.ShouldBeFalse();
        NotFired(report, "FLOWX-DIFF-021");
        NotFired(report, "FLOWX-DIFF-022");
    }

    /// <summary>
    /// A wait whose duration stopped being foldable is reported, and says so.
    /// </summary>
    /// <remarks>
    /// The field is omitted when the compiler cannot evaluate the author's expression, so
    /// this is a change in what the <em>build</em> could read and not necessarily a change in
    /// what the author declared. <c>(none)</c> is how the rest of the tool renders that, and
    /// the severity is unchanged: nobody is broken by a manifest that says less.
    /// </remarks>
    [Fact]
    public void AWaitThatStoppedPublishingItsWindowIsStillOnlyNeutral()
    {
        var report = Diff(candidate => candidate.Replace(
            ", \"timeout\": \"P7D\"", string.Empty, StringComparison.Ordinal));

        Fired(report, "FLOWX-DIFF-206").Summary.ShouldBe("declared wait changed: P7D -> (none)");
        report.HasBreakingChange.ShouldBeFalse();
    }

    // -------------------------------------------------------------------- plumbing

    private static ManifestDocument Parse(string json) =>
        JsonSerializer.Deserialize(json, ManifestJsonContext.Default.ManifestDocument)!;

    /// <summary>Diffs the baseline against a candidate produced by editing its text.</summary>
    private static DiffReport Diff(Func<string, string> edit)
    {
        var candidate = edit(Baseline);

        candidate.ShouldNotBe(Baseline, "The edit matched nothing, so this test proves nothing.");

        return ManifestDiff.Compare(Parse(Baseline), Parse(candidate));
    }

    private static DiffFinding Fired(DiffReport report, string code)
    {
        var matches = report.Findings.Where(f => f.Code == code).ToList();

        matches.Count.ShouldBe(1, $"Expected exactly one {code}. Report held: {Describe(report)}");

        return matches[0];
    }

    private static void NotFired(DiffReport report, string code) =>
        report.Findings.ShouldNotContain(
            f => f.Code == code, $"{code} fired when it should not have. Report held: {Describe(report)}");

    private static string Describe(DiffReport report) => report.Findings.Count == 0
        ? "(nothing)"
        : string.Join(", ", report.Findings.Select(f => $"{f.Code} {f.Subject}"));
}
