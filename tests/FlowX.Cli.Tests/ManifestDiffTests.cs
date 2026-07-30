using System.Text.Json;
using FlowX.Cli.Diffing;
using FlowX.Cli.Manifest;
using Shouldly;
using Xunit;

namespace FlowX.Cli.Tests;

/// <summary>
/// The classification rules — the part of <c>flowx diff</c> that is the product.
/// </summary>
/// <remarks>
/// <para>
/// Every rule is asserted in both directions: that it fires on the change it exists for,
/// and that it stays silent otherwise. The second half is not padding. A gate that
/// over-reports is a gate people route around, and the failure it produces —
/// <c>--force</c> in the pipeline, or the step deleted — is worse than no gate, because
/// the next real breaking change now ships with the appearance of having been checked.
/// </para>
/// <para>
/// Each candidate is the baseline with one thing changed, so a finding can only have
/// come from the change under test.
/// </para>
/// </remarks>
public sealed class ManifestDiffTests
{
    private const string Baseline = """
        {
          "schemaVersion": "0.1.0",
          "application": { "name": "Ordering", "version": "2.3.0" },
          "flows": [
            {
              "id": "order.place", "version": "1.2.0", "profile": "Durable", "deadline": "PT30S",
              "input":  { "type": "Ordering.PlaceOrder", "sensitive": ["PaymentToken"] },
              "output": { "type": "Ordering.OrderPlaced" },
              "triggers": [
                { "kind": "Http", "method": "POST", "route": "/api/v1/orders" },
                { "kind": "Bus", "transport": "kafka", "topic": "orders.requested" }
              ],
              "steps": [
                { "id": 0, "kind": "Capability", "capability": "order.validate@1.0.0" },
                { "id": 1, "kind": "Capability", "capability": "payment.capture@2.1.0" },
                { "id": 2, "kind": "Emit", "event": "order.placed" }
              ],
              "emits": ["order.placed"],
              "source": "src/Ordering/PlaceOrderFlow.cs:14"
            },
            {
              "id": "order.get", "version": "1.0.0", "profile": "Ephemeral",
              "input": { "type": "Ordering.GetOrder" }, "output": { "type": "Ordering.Order" },
              "steps": [ { "id": 0, "kind": "Capability", "capability": "order.read@1.0.0" } ],
              "emits": [],
              "source": "src/Ordering/GetOrderFlow.cs:9"
            }
          ],
          "capabilities": [
            { "id": "order.validate", "version": "1.0.0",
              "input": "Ordering.PlaceOrder", "output": "Ordering.ValidOrder",
              "authorization": { "mode": "Authenticated" }, "idempotent": true, "sideEffects": [] },
            { "id": "order.read", "version": "1.0.0",
              "input": "Ordering.GetOrder", "output": "Ordering.Order",
              "authorization": { "mode": "Permission", "value": "order:read" },
              "idempotent": true, "sideEffects": [] },
            { "id": "payment.capture", "version": "2.1.0",
              "input": "Ordering.CaptureRequest", "output": "Ordering.Capture",
              "authorization": { "mode": "Permission", "value": "payment:capture" },
              "idempotent": true, "sideEffects": ["payment-gateway"],
              "errors": [ { "code": "payment.declined", "category": "Conflict" } ],
              "source": "src/Ordering/CapturePayment.cs:21" }
          ],
          "events": [ { "type": "order.placed", "schemaVersion": "1.0.0" } ]
        }
        """;

    // ------------------------------------------------------------------ nothing

    [Fact]
    public void AManifestComparedWithItselfReportsNothing()
    {
        var report = ManifestDiff.Compare(Parse(), Parse());

        report.Findings.ShouldBeEmpty();
        report.Compatible.ShouldBeTrue();
        report.HasBreakingChange.ShouldBeFalse();
    }

    /// <summary>
    /// The rule that keeps the tool worth reading: a rebuild is not a change.
    /// </summary>
    /// <remarks>
    /// A line number moves whenever anyone edits above a declaration, the application
    /// version moves on every release, and a step is added by the refactoring this
    /// platform exists to make safe. If any of the three produced a finding, every build
    /// would report one, and a report that is never empty is a report nobody reads.
    /// </remarks>
    [Fact]
    public void EditsThatAreNotContractChangesProduceNoFindingAtAll()
    {
        var report = Diff(candidate =>
        {
            Flow(candidate, "order.place").Steps.Insert(
                0, new ManifestStep { Id = 9, Kind = "Capability", Capability = "order.enrich@1.0.0" });

            candidate.Application!.Version = "2.4.0";
        });

        report.Findings.ShouldBeEmpty(
            "Line numbers, the application version and a flow's step list must never be reported.");
    }

    [Fact]
    public void CollectionsAreComparedAsSetsRatherThanSequences()
    {
        var report = Diff(candidate =>
        {
            candidate.Capabilities.Reverse();
            candidate.Flows.Reverse();
            Cap(candidate, "payment.capture").SideEffects.Reverse();
            Flow(candidate, "order.place").Triggers.Reverse();
        });

        report.Findings.ShouldBeEmpty(
            "Emission order is the compiler's business. Reporting it would make the order " +
            "of a HashSet enumeration into a breaking change.");
    }

    // -------------------------------------------------------------------- flows

    [Fact]
    public void RemovingAFlowIsBreaking()
    {
        var report = Diff(candidate => candidate.Flows.RemoveAll(f => f.Id == "order.get"));

        Fired(report, "FLOWX-DIFF-001").Severity.ShouldBe(DiffSeverity.Breaking);
        Fired(report, "FLOWX-DIFF-001").Subject.ShouldBe("flow order.get@1");
    }

    [Fact]
    public void AddingAFlowIsAdditive()
    {
        var report = Diff(candidate => candidate.Flows.Add(new ManifestFlow
        {
            Id = "order.cancel",
            Version = "1.0.0",
            Profile = "Durable",
        }));

        Fired(report, "FLOWX-DIFF-100").Severity.ShouldBe(DiffSeverity.Additive);
        NotFired(report, "FLOWX-DIFF-001");
    }

    [Fact]
    public void PublishingANewMajorAlongsideTheOldOneIsAdditiveOnly()
    {
        var report = Diff(candidate => candidate.Flows.Add(new ManifestFlow
        {
            Id = "order.place",
            Version = "2.0.0",
            Profile = "Durable",
            Input = new ManifestTypeRef { Type = "Ordering.PlaceOrderV2" },
        }));

        Fired(report, "FLOWX-DIFF-100").Subject.ShouldBe("flow order.place@2");
        NotFired(report, "FLOWX-DIFF-001");
        NotFired(report, "FLOWX-DIFF-002");
        // Side-by-side majors are the supported migration path. Reading the second one as
        // a change to the first would make the safe route look like the breaking one.
    }

    [Fact]
    public void ReplacingAMajorRatherThanPublishingBesideItIsBreaking()
    {
        var report = Diff(candidate => Flow(candidate, "order.place").Version = "2.0.0");

        Fired(report, "FLOWX-DIFF-001").Subject.ShouldBe("flow order.place@1");
        Fired(report, "FLOWX-DIFF-100").Subject.ShouldBe("flow order.place@2");
    }

    [Fact]
    public void APatchBumpWithinAMajorIsNotARemoval()
    {
        var report = Diff(candidate => Flow(candidate, "order.place").Version = "1.2.1");

        report.Findings.ShouldBeEmpty(
            "The compatibility unit is the major. A patch bump that reported a removal " +
            "plus an addition would make every release look catastrophic.");
    }

    [Fact]
    public void ChangingAFlowsInputContractIsBreaking()
    {
        var report = Diff(candidate => Flow(candidate, "order.place").Input!.Type = "Ordering.PlaceOrderV2");

        Fired(report, "FLOWX-DIFF-002").Severity.ShouldBe(DiffSeverity.Breaking);
    }

    [Fact]
    public void ChangingAFlowsOutputContractIsBreaking()
    {
        var report = Diff(candidate => Flow(candidate, "order.place").Output!.Type = "Ordering.OrderPlacedV2");

        Fired(report, "FLOWX-DIFF-003").Severity.ShouldBe(DiffSeverity.Breaking);
    }

    [Fact]
    public void AnUnchangedContractIsNotReportedEvenWhenTheFlowChangesAroundIt()
    {
        var report = Diff(candidate => Flow(candidate, "order.place").Steps.Clear());

        NotFired(report, "FLOWX-DIFF-002");
        NotFired(report, "FLOWX-DIFF-003");
    }

    [Fact]
    public void LosingTheDurableProfileIsBreakingBecauseAGuaranteeIsWithdrawn()
    {
        var report = Diff(candidate => Flow(candidate, "order.place").Profile = "Ephemeral");

        var finding = Fired(report, "FLOWX-DIFF-004");

        finding.Severity.ShouldBe(DiffSeverity.Breaking);
        finding.Consequence.ShouldContain("process kill");
        // No signature moves when durability is dropped, so nothing else in the build
        // notices — which is exactly why this rule has to.
    }

    [Fact]
    public void GainingDurabilityIsNeutralRatherThanBreaking()
    {
        var report = Diff(candidate => Flow(candidate, "order.get").Profile = "Durable");

        Fired(report, "FLOWX-DIFF-202").Severity.ShouldBe(DiffSeverity.Neutral);
        NotFired(report, "FLOWX-DIFF-004");
    }

    [Fact]
    public void RemovingATriggerIsBreakingAndAddingOneIsAdditive()
    {
        var report = Diff(candidate =>
        {
            var flow = Flow(candidate, "order.place");
            flow.Triggers.RemoveAll(t => t.Kind == "Bus");
            flow.Triggers.Add(new ManifestTrigger { Kind = "Schedule", Cron = "0 * * * *" });
        });

        Fired(report, "FLOWX-DIFF-005").Summary.ShouldContain("orders.requested");
        Fired(report, "FLOWX-DIFF-103").Severity.ShouldBe(DiffSeverity.Additive);
    }

    [Fact]
    public void ChangingSomethingATriggerDoesNotExposeIsNotATriggerChange()
    {
        var report = Diff(candidate =>
            Flow(candidate, "order.place").Triggers[1].Transport = "kafka");

        NotFired(report, "FLOWX-DIFF-005");
        NotFired(report, "FLOWX-DIFF-103");
    }

    // ---------------------------------------------------------------- sensitive

    /// <summary>
    /// The asymmetric rule, and the one worth defending.
    /// </summary>
    /// <remarks>
    /// Un-marking a member is a data-exposure regression: the value reaches logs, traces
    /// and a journal retained for the replay window, so the leak is durable rather than
    /// momentary. It is classified alongside a relaxed authorisation stance for the same
    /// reason — no signature moves and the security posture got worse, which is precisely
    /// what code review misses.
    /// </remarks>
    [Fact]
    public void AMemberThatStopsBeingSensitiveIsBreaking()
    {
        var report = Diff(candidate => Flow(candidate, "order.place").Input!.Sensitive.Clear());

        var finding = Fired(report, "FLOWX-DIFF-006");

        finding.Severity.ShouldBe(DiffSeverity.Breaking);
        finding.Subject.ShouldBe("flow order.place@1 input");
        finding.Consequence.ShouldContain("journal");
    }

    /// <summary>
    /// The other half of the argument, and the reason the rule is not symmetric.
    /// </summary>
    /// <remarks>
    /// Marking a member sensitive can break something — a dashboard scraping the value out
    /// of a log line loses it — but a secret leaking into a log is not a dependency this
    /// platform undertakes to preserve. A gate that failed the build when an engineer
    /// marked a password would teach engineers not to mark passwords, which is the exact
    /// opposite of the intended effect.
    /// </remarks>
    [Fact]
    public void AMemberThatBecomesSensitiveIsAdditive()
    {
        var report = Diff(candidate =>
            Flow(candidate, "order.place").Output!.Sensitive.Add("ReceiptUrl"));

        Fired(report, "FLOWX-DIFF-107").Severity.ShouldBe(DiffSeverity.Additive);
        NotFired(report, "FLOWX-DIFF-006");
    }

    // ------------------------------------------------------------- capabilities

    [Fact]
    public void RemovingACapabilityIsBreaking()
    {
        var report = Diff(candidate => candidate.Capabilities.RemoveAll(c => c.Id == "order.read"));

        Fired(report, "FLOWX-DIFF-010").Subject.ShouldBe("capability order.read@1");
    }

    [Fact]
    public void AddingACapabilityIsAdditive()
    {
        var report = Diff(candidate => candidate.Capabilities.Add(new ManifestCapability
        {
            Id = "refund.issue",
            Version = "1.0.0",
            Input = "Ordering.RefundRequest",
            Output = "Ordering.Refund",
            Idempotent = true,
        }));

        Fired(report, "FLOWX-DIFF-101").Severity.ShouldBe(DiffSeverity.Additive);
        NotFired(report, "FLOWX-DIFF-010");
    }

    [Fact]
    public void KeepingTheOldMajorBesideANewOneIsAdditiveOnly()
    {
        var report = Diff(candidate => candidate.Capabilities.Add(new ManifestCapability
        {
            Id = "payment.capture",
            Version = "3.0.0",
            Input = "Ordering.CaptureRequestV3",
            Output = "Ordering.CaptureV3",
            Idempotent = true,
        }));

        Fired(report, "FLOWX-DIFF-101").Subject.ShouldBe("capability payment.capture@3");
        NotFired(report, "FLOWX-DIFF-010");
        NotFired(report, "FLOWX-DIFF-011");
        NotFired(report, "FLOWX-DIFF-012");
    }

    [Fact]
    public void DeletingTheOldMajorWhenTheNewOneArrivesIsBreaking()
    {
        var report = Diff(candidate =>
        {
            var capture = Cap(candidate, "payment.capture");
            capture.Version = "3.0.0";
            capture.Input = "Ordering.CaptureRequestV3";
        });

        Fired(report, "FLOWX-DIFF-010").Subject.ShouldBe("capability payment.capture@2");
        Fired(report, "FLOWX-DIFF-101").Subject.ShouldBe("capability payment.capture@3");
        NotFired(report, "FLOWX-DIFF-011");
        // Reported as a removal and an addition, not as a retyped input: within a major the
        // contract is frozen, and across majors it is meant to change.
    }

    [Fact]
    public void ChangingACapabilitysInputTypeWithinAMajorIsBreaking()
    {
        var report = Diff(candidate => Cap(candidate, "payment.capture").Input = "Ordering.CaptureRequestV2");

        Fired(report, "FLOWX-DIFF-011").Severity.ShouldBe(DiffSeverity.Breaking);
    }

    [Fact]
    public void ChangingACapabilitysOutputTypeWithinAMajorIsBreaking()
    {
        var report = Diff(candidate => Cap(candidate, "payment.capture").Output = "Ordering.CaptureV2");

        Fired(report, "FLOWX-DIFF-012").Severity.ShouldBe(DiffSeverity.Breaking);
    }

    [Fact]
    public void AMinorBumpThatChangesNoContractReportsNothing()
    {
        var report = Diff(candidate => Cap(candidate, "payment.capture").Version = "2.2.0");

        report.Findings.ShouldBeEmpty(
            "Adding an optional field is a minor bump and is compatible by definition.");
    }

    /// <summary>
    /// Losing idempotency breaks somebody else's build, not this one.
    /// </summary>
    /// <remarks>
    /// A retry policy attached to a non-idempotent capability is a compile error
    /// (<c>FLOWX1014</c>) — FlowX will not retry what is unsafe to retry. So withdrawing
    /// the declaration invalidates policies at call sites in other repositories, owned by
    /// other teams, none of which are being compiled when the change is made.
    /// </remarks>
    [Fact]
    public void WithdrawingIdempotencyIsBreaking()
    {
        var report = Diff(candidate => Cap(candidate, "payment.capture").Idempotent = false);

        var finding = Fired(report, "FLOWX-DIFF-013");

        finding.Severity.ShouldBe(DiffSeverity.Breaking);
        finding.Consequence.ShouldContain("FLOWX1014");
    }

    [Fact]
    public void GainingIdempotencyIsAdditive()
    {
        // The non-idempotent document is the baseline here, so the assertion is about the
        // direction of travel rather than about which fixture happens to be first.
        var report = ManifestDiff.Compare(
            Mutate(c => Cap(c, "payment.capture").Idempotent = false), Parse());

        Fired(report, "FLOWX-DIFF-105").Severity.ShouldBe(DiffSeverity.Additive);
        NotFired(report, "FLOWX-DIFF-013");
        report.Compatible.ShouldBeTrue("Widening what is permitted cannot invalidate a policy.");
    }

    /// <summary>
    /// A relaxed authorisation stance is stated as a security regression, in those words.
    /// </summary>
    /// <remarks>
    /// Nothing else in the build notices: no signature moves, no test fails, and the
    /// capability simply becomes reachable by principals the baseline refused. "authorization
    /// changed" in a CI log does not make anybody stop reading, so the finding says what it is.
    /// </remarks>
    [Theory]
    [InlineData("Permission", "Authenticated")]
    [InlineData("Permission", "Public")]
    [InlineData("Internal", "Authenticated")]
    [InlineData("Authenticated", "Public")]
    public void RelaxingAuthorisationIsBreakingAndSaysWhy(string before, string after)
    {
        var report = ManifestDiff.Compare(
            Mutate(c => Cap(c, "payment.capture").Authorization!.Mode = before),
            Mutate(c => Cap(c, "payment.capture").Authorization!.Mode = after));

        var finding = Fired(report, "FLOWX-DIFF-014");

        finding.Severity.ShouldBe(DiffSeverity.Breaking);
        finding.Consequence.ShouldContain("Security regression", Case.Sensitive);
    }

    /// <summary>
    /// Tightening is breaking too, which needs defending.
    /// </summary>
    /// <remarks>
    /// It is unambiguously the right change to make, and it still stops callers that
    /// worked yesterday from working today. The gate is not saying tightening is wrong; it
    /// is saying tightening needs the same coordination as any other break, because
    /// shipping it unannounced turns a security improvement into an outage. It is reported
    /// under a different code from a relaxation so the two never read as the same event.
    /// </remarks>
    [Theory]
    [InlineData("Public", "Authenticated")]
    [InlineData("Authenticated", "Permission")]
    [InlineData("Permission", "Internal")]
    public void TighteningAuthorisationIsBreakingUnderADifferentCode(string before, string after)
    {
        var report = ManifestDiff.Compare(
            Mutate(c => Cap(c, "payment.capture").Authorization!.Mode = before),
            Mutate(c => Cap(c, "payment.capture").Authorization!.Mode = after));

        Fired(report, "FLOWX-DIFF-015").Severity.ShouldBe(DiffSeverity.Breaking);
        NotFired(report, "FLOWX-DIFF-014");
    }

    [Fact]
    public void MovingBetweenPermissionAndPolicyIsNotGuessedAtInEitherDirection()
    {
        var report = ManifestDiff.Compare(
            Mutate(c => Cap(c, "payment.capture").Authorization!.Mode = "Permission"),
            Mutate(c => Cap(c, "payment.capture").Authorization!.Mode = "Policy"));

        Fired(report, "FLOWX-DIFF-015");
        NotFired(report, "FLOWX-DIFF-014");
        // A named policy is neither broader nor narrower than a named permission, so
        // claiming the change relaxed anything would be an assertion the manifest does
        // not support.
    }

    [Fact]
    public void ChangingTheNamedPermissionUnderAnUnchangedModeIsBreaking()
    {
        var report = Diff(candidate => Cap(candidate, "payment.capture").Authorization!.Value = "payment:write");

        Fired(report, "FLOWX-DIFF-015").Summary.ShouldContain("payment:write");
        // The mode alone would say nothing changed, and the set of principals that passes
        // the check just moved.
    }

    [Fact]
    public void AnUnchangedAuthorisationStanceIsNotReported()
    {
        var report = Diff(candidate => Cap(candidate, "payment.capture").Output = "Ordering.CaptureV2");

        NotFired(report, "FLOWX-DIFF-014");
        NotFired(report, "FLOWX-DIFF-015");
    }

    /// <summary>
    /// A new side effect is breaking, which is the least obvious rule here.
    /// </summary>
    /// <remarks>
    /// Nothing about the call changes. But <c>sideEffects</c> is what blast-radius review
    /// reads and what decides whether an agent asks a human before invoking the tool, so a
    /// capability that consumers assessed as touching nothing outside the process and now
    /// writes to a payment gateway has invalidated every assessment made against it.
    /// </remarks>
    [Fact]
    public void AddingASideEffectIsBreaking()
    {
        var report = Diff(candidate => Cap(candidate, "order.validate").SideEffects.Add("fraud-service"));

        var finding = Fired(report, "FLOWX-DIFF-016");

        finding.Severity.ShouldBe(DiffSeverity.Breaking);
        finding.Consequence.ShouldContain("Blast radius", Case.Sensitive);
    }

    [Fact]
    public void RemovingASideEffectIsAdditive()
    {
        var report = Diff(candidate => Cap(candidate, "payment.capture").SideEffects.Clear());

        Fired(report, "FLOWX-DIFF-106").Severity.ShouldBe(DiffSeverity.Additive);
        NotFired(report, "FLOWX-DIFF-016");
        // Narrowing the blast radius invalidates no decision anyone made.
    }

    // ------------------------------------------------------------------- errors

    [Fact]
    public void AddingAnErrorCodeIsAdditive()
    {
        var report = Diff(candidate => Cap(candidate, "payment.capture").Errors.Add(
            new ManifestError { Code = "payment.gateway_unavailable", Category = "Unavailable" }));

        Fired(report, "FLOWX-DIFF-104").Severity.ShouldBe(DiffSeverity.Additive);
        NotFired(report, "FLOWX-DIFF-017");
        // A consumer that does not know the new code falls through to whatever it already
        // does with an unrecognised failure.
    }

    [Fact]
    public void RemovingAnErrorCodeIsBreaking()
    {
        var report = Diff(candidate => Cap(candidate, "payment.capture").Errors.Clear());

        Fired(report, "FLOWX-DIFF-017").Severity.ShouldBe(DiffSeverity.Breaking);
        // Codes disappear far more often because they were renamed than because the
        // failure became impossible, and a consumer branching on one stops matching
        // silently either way.
    }

    [Fact]
    public void RecategorisingAnErrorIsBreakingEvenThoughTheCodeIsUnchanged()
    {
        var report = Diff(candidate => Cap(candidate, "payment.capture").Errors[0].Category = "Forbidden");

        var finding = Fired(report, "FLOWX-DIFF-018");

        finding.Severity.ShouldBe(DiffSeverity.Breaking);
        finding.Consequence.ShouldContain("status code");
        NotFired(report, "FLOWX-DIFF-017");
        NotFired(report, "FLOWX-DIFF-104");
        // The category is what the transport maps to a status, so a client keyed on 409
        // now sees 403 for the same business failure.
    }

    [Fact]
    public void AnUnchangedErrorCatalogueIsNotReported()
    {
        var report = Diff(candidate => Cap(candidate, "payment.capture").Errors.Add(
            new ManifestError { Code = "payment.declined", Category = "Conflict" }));

        report.Findings.ShouldBeEmpty("The catalogue is a set keyed by code; a duplicate is not a change.");
    }

    // ------------------------------------------------------------------- events

    [Fact]
    public void RemovingAnEventIsBreaking()
    {
        var report = Diff(candidate => candidate.Events.Clear());

        var finding = Fired(report, "FLOWX-DIFF-020");

        finding.Subject.ShouldBe("event order.placed@1");
        finding.Severity.ShouldBe(DiffSeverity.Breaking);
    }

    [Fact]
    public void AddingAnEventIsAdditive()
    {
        var report = Diff(candidate => candidate.Events.Add(
            new ManifestEvent { Type = "order.shipped", SchemaVersion = "1.0.0" }));

        Fired(report, "FLOWX-DIFF-102").Severity.ShouldBe(DiffSeverity.Additive);
        NotFired(report, "FLOWX-DIFF-020");
    }

    [Fact]
    public void BumpingAnEventsSchemaMajorWithoutKeepingTheOldOneIsBreaking()
    {
        var report = Diff(candidate => candidate.Events[0].SchemaVersion = "2.0.0");

        Fired(report, "FLOWX-DIFF-020").Subject.ShouldBe("event order.placed@1");
        Fired(report, "FLOWX-DIFF-102").Subject.ShouldBe("event order.placed@2");
        // A subscriber pinned to major 1 receives nothing, and nothing in its build says so.
    }

    [Fact]
    public void BumpingAnEventsSchemaMinorIsNotAChange()
    {
        var report = Diff(candidate => candidate.Events[0].SchemaVersion = "1.1.0");

        report.Findings.ShouldBeEmpty();
    }

    /// <summary>
    /// A flow's <c>emits</c> list is derived, so it is not diffed.
    /// </summary>
    /// <remarks>
    /// The compiler builds it from the flow's <c>Emit</c> steps, and the same fact is
    /// stated once more — with a version attached — in the document's <c>events</c> array.
    /// Diffing both reports every event change twice. It is also the correct semantics: a
    /// subscriber depends on the event type existing, not on a particular flow producing it.
    /// </remarks>
    [Fact]
    public void AFlowThatStopsEmittingAnEventStillDeclaredElsewhereIsNotAChange()
    {
        var report = Diff(candidate =>
        {
            var flow = Flow(candidate, "order.place");
            flow.Emits.Clear();
            flow.Steps.RemoveAll(s => s.Kind == "Emit");
        });

        report.Findings.ShouldBeEmpty();
    }

    // --------------------------------------------------------------- deprecation

    [Fact]
    public void DeprecatingACapabilityIsNeutral()
    {
        var report = Diff(candidate => Cap(candidate, "payment.capture").Deprecated = "2027-01-01, use 3.x");

        Fired(report, "FLOWX-DIFF-204").Severity.ShouldBe(DiffSeverity.Neutral);
        report.Compatible.ShouldBeTrue("A deprecation notice removes nothing today.");
    }

    [Fact]
    public void AnUnchangedDeprecationNoticeIsNotReported()
    {
        var report = Diff(candidate => Cap(candidate, "payment.capture").SideEffects.Clear());

        NotFired(report, "FLOWX-DIFF-204");
    }

    // ---------------------------------------------------------- neutral, reported

    [Fact]
    public void ChangingADeadlineIsNeutralButStillWorthALine()
    {
        var report = Diff(candidate => Flow(candidate, "order.place").Deadline = "PT5S");

        Fired(report, "FLOWX-DIFF-203").Severity.ShouldBe(DiffSeverity.Neutral);
        report.Compatible.ShouldBeTrue(
            "A deadline is an operational budget tuned against production latency, not a " +
            "promise in the contract — so shortening one must not fail a build.");
    }

    [Fact]
    public void ComparingAcrossManifestSchemaMajorsIsFlaggedAsAdvisory()
    {
        var report = Diff(candidate => candidate.SchemaVersion = "1.0.0");

        Fired(report, "FLOWX-DIFF-200").Severity.ShouldBe(DiffSeverity.Neutral);
        report.Compatible.ShouldBeTrue();
    }

    [Fact]
    public void ABumpWithinOneSchemaMajorIsNotFlagged()
    {
        var report = Diff(candidate => candidate.SchemaVersion = "0.2.0");

        NotFired(report, "FLOWX-DIFF-200");
    }

    [Fact]
    public void ComparingTwoDifferentApplicationsSaysSoBeforeAnythingElse()
    {
        var report = Diff(candidate => candidate.Application!.Name = "Billing");

        Fired(report, "FLOWX-DIFF-201").Consequence.ShouldContain("different applications");
        // Almost always the wrong pair of files rather than a rename, and worth saying
        // before a reader trusts a hundred findings produced by comparing two unrelated
        // applications.
    }

    // -------------------------------------------------------------------- report

    [Fact]
    public void OrdersFindingsBreakingFirstAndDeterministically()
    {
        var report = Diff(candidate =>
        {
            Cap(candidate, "payment.capture").Idempotent = false;
            Flow(candidate, "order.place").Deadline = "PT5S";
            candidate.Events.Add(new ManifestEvent { Type = "order.shipped", SchemaVersion = "1.0.0" });
        });

        report.Findings.Select(f => f.Severity).ShouldBe(
            [DiffSeverity.Breaking, DiffSeverity.Additive, DiffSeverity.Neutral],
            "A reader scrolling a red build needs the breaking findings without scrolling.");

        report.Breaking.ShouldBe(1);
        report.Additive.ShouldBe(1);
        report.Neutral.ShouldBe(1);
        report.Compatible.ShouldBeFalse();
    }

    [Fact]
    public void ProducesTheSameReportForTheSameInputRegardlessOfDocumentOrder()
    {
        var candidate = Mutate(c =>
        {
            c.Capabilities.RemoveAll(x => x.Id == "order.read");
            c.Flows.Reverse();
        });

        var once = ManifestDiff.Compare(Parse(), candidate);
        var twice = ManifestDiff.Compare(Parse(), candidate);

        once.Findings.Select(f => f.Code + f.Subject)
            .ShouldBe(twice.Findings.Select(f => f.Code + f.Subject));
    }

    [Fact]
    public void ToleratesAManifestWithNothingInIt()
    {
        var report = ManifestDiff.Compare(new ManifestDocument(), new ManifestDocument());

        report.Findings.ShouldBeEmpty();
        report.Application.ShouldBe("unknown");
    }

    [Fact]
    public void AnEntryWithNoIdentityIsSkippedRatherThanPairedWithAnother()
    {
        var report = ManifestDiff.Compare(
            Mutate(c => c.Capabilities.Add(new ManifestCapability { Version = "1.0.0", Input = "A" })),
            Mutate(c => c.Capabilities.Add(new ManifestCapability { Version = "1.0.0", Input = "B" })));

        report.Findings.ShouldBeEmpty(
            "Two entries with no identity cannot be shown to be the same thing, so " +
            "comparing them would invent a finding rather than report one.");
    }

    [Fact]
    public void RejectsNullManifests()
    {
        Should.Throw<ArgumentNullException>(() => ManifestDiff.Compare(null!, Parse()));
        Should.Throw<ArgumentNullException>(() => ManifestDiff.Compare(Parse(), null!));
    }

    // ------------------------------------------------------------------ plumbing

    private static ManifestDocument Parse() =>
        JsonSerializer.Deserialize(Baseline, ManifestJsonContext.Default.ManifestDocument)!;

    private static ManifestDocument Mutate(Action<ManifestDocument> change)
    {
        var document = Parse();
        change(document);
        return document;
    }

    private static DiffReport Diff(Action<ManifestDocument> change) =>
        ManifestDiff.Compare(Parse(), Mutate(change));

    private static ManifestFlow Flow(ManifestDocument document, string id) =>
        document.Flows.Single(f => f.Id == id);

    private static ManifestCapability Cap(ManifestDocument document, string id) =>
        document.Capabilities.Single(c => c.Id == id);

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
