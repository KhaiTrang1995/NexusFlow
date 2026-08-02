using System.Text.Json;
using Banking;
using FlowX;
using Shouldly;
using Xunit;

namespace Banking.Tests;

/// <summary>
/// The audit trail a settled transfer leaves behind, read back off the sink.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This file is the sample's evidence for the claim it could not make for three
/// releases.</strong> <c>samples/banking</c>'s README and <c>Policies.cs</c> both said, in as
/// many words, that no financial audit record was written by a policy — an honest sentence
/// about a bank, and the worst possible sentence for a bank to have to write. Three steps now
/// produce one each.
/// </para>
/// <para>
/// <strong>Three things are asserted and they are different.</strong> That a record exists for
/// every audited step and for no other — an audit that records nothing is indistinguishable
/// from the inert version it replaces. That the record says on whose authority the step ran,
/// which is what makes "who authorised this transfer" answerable. And that a
/// <c>[Sensitive]</c> member and a declared <c>redact</c> member are both gone from the
/// document, by two mechanisms that are in fact one.
/// </para>
/// </remarks>
public sealed class TransferAuditTests
{
    private const string Debtor = "GB33BUKB20201555555555";
    private const string Creditor = "DE89370400440532013000";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Three steps declare an <c>Audit</c>, and three records come out.</summary>
    /// <remarks>
    /// The count is the assertion, not the presence. Eleven steps run; a record per step would
    /// make the policy a setting rather than a declaration, and would say nothing about which
    /// steps this bank thought were worth recording.
    /// </remarks>
    [Fact]
    public async Task EveryAuditedStepOfASettledTransferIsRecorded()
    {
        var harness = new TransferHarness();

        var result = await harness.RunAsync(Transfer(120m), "a-1", Cancellation);

        result.IsSuccess.ShouldBeTrue(result.Error?.ToString());

        harness.Audit.Entries.Select(e => e.Record.CapabilityId).ShouldBe(
            ["ledger.post_debit", "ledger.post_credit", "settlement.record"],
            "The two ledger legs declare Policies.LedgerPost and the settlement write declares " +
            "Policies.SettlementRegister. Nothing else in this flow declares an Audit, and the " +
            "eight steps that do not are not recorded.");

        harness.Audit.Entries.Select(e => e.Record.Category).Distinct().ShouldBe(
            ["financial"],
            "One category, because this bank declared one. FlowX defines no vocabulary for it.");

        var instance = harness.Instances.ShouldHaveSingleItem();

        harness.Audit.Entries.Select(e => e.Record.InstanceId).Distinct().ShouldBe(
            [instance.InstanceId.ToString()],
            "and the three are tied to one instance, which is what makes the trail a query " +
            "rather than a search.");

        harness.Audit.Entries.Select(e => e.Record.IdempotencyKey).Distinct().ShouldBe(
            ["a-1"],
            "keyed by what the caller supplied, which is the identifier an external auditor " +
            "and this bank can both resolve.");
    }

    /// <summary>A record names the stance the step was decided against and who satisfied it.</summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the half
    /// <a href="../../docs/adr/ADR-0028-identity-arrives-on-the-invocation.md">ADR-0028</a>
    /// asked for.</strong> That record accepted that a durable flow's authorisation is
    /// discontinuous across a wait and that "an auditor reconstructing 'who authorised this
    /// transfer' must read two events. Nothing yet writes those events."
    /// </para>
    /// <para>
    /// <strong>This transfer has one answer, and the field is what says so.</strong>
    /// <c>transfer.execute</c> has no <c>AwaitSignal</c>, so every step is decided against the
    /// principal that started the instance and all three records read <c>Starter</c>. A flow
    /// that did wait would have records reading <c>Deliverer</c> after it —
    /// <c>AuditPolicyTests.TheTrailNamesTheStarterAndTheDelivererApart</c> writes both and
    /// reads them apart. The point of asserting the uniform case here is that "one answer" is
    /// now a fact in the trail rather than something a reader infers from the absence of a
    /// wait in the graph.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EveryRecordNamesTheAuthorityTheStepRanUnder()
    {
        var harness = new TransferHarness();

        await harness.RunAsync(Transfer(120m), "a-2", Cancellation);

        harness.Audit.Entries.Select(e => e.Record.Authority).Distinct().ShouldBe(
            [AuditAuthority.Starter],
            "One principal started this instance and this flow never waits, so there is one " +
            "answer to 'who authorised this transfer' — and the trail says that rather than " +
            "leaving it to be inferred.");

        var debit = harness.Audit.Entries[0].Record;

        debit.Stance.ShouldBe(
            Authorization.Permission,
            "ledger.post_debit declares a permission stance, and the record carries it so a " +
            "reader can tell a step that was actually checked from one that admits everybody.");

        debit.Permission.ShouldBe(
            "ledger:post",
            "the grant the stance named — which is what the caller had to hold, and is not a " +
            "copy of what they did hold.");
    }

    /// <summary>
    /// A <c>[Sensitive]</c> member and a declared <c>redact</c> member are both gone, and the
    /// rest of the record is there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The two mechanisms are one mechanism, and this is what proves it.</strong> The
    /// account numbers go because <c>ExecuteTransfer</c> marks them <c>[Sensitive]</c> and the
    /// generator collects them into <c>SensitiveMembers</c>; the ledger entry ids go because
    /// <c>Policies.SettlementRegister</c> named them in its <c>redact</c> list. Both arrive at
    /// <c>JournalPayload.OfState</c> as one array and are matched by the one redaction pass, so
    /// there is no second implementation to drift and no second exit from a value — the sink
    /// receives a <c>JournalPayload</c> and its only way out is <c>ToJson</c>.
    /// </para>
    /// <para>
    /// <strong>The surviving members are asserted too.</strong> A test that only checked for
    /// the absence of four values would pass against a record that carried nothing at all,
    /// which is precisely the objection — "an audit record the engine can write carries no
    /// payload, which makes <c>redact</c> vacuous" — that had this policy declined twice.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task NoAccountNumberAndNoRedactedMemberReachesARecord()
    {
        var harness = new TransferHarness();

        await harness.RunAsync(Transfer(120m), "a-3", Cancellation);

        var debit = harness.Audit.Entries[0];

        // This has failed twice in CI and never once locally, always here, always with a null
        // document — which is JournalPayload.Empty, which is the generated DescribeAudit
        // switch reaching its default for a step the engine believed was audited. Whichever
        // record arrived is the evidence, so say what it was rather than dying inside
        // JsonDocument.Parse with the word "json".
        debit.Document.ShouldNotBeNull(
            $"the first audit record carried no payload at all: step {debit.Record.StepIndex}, "
            + $"capability '{debit.Record.CapabilityId}', category '{debit.Record.Category}', "
            + $"and {harness.Audit.Entries.Count} record(s) in total — "
            + string.Join(", ", harness.Audit.Entries.Select(
                static e => $"[{e.Record.StepIndex}] {e.Record.CapabilityId} "
                    + (e.Document is null ? "no payload" : "payload"))));

        using var posted = JsonDocument.Parse(debit.Document);

        posted.RootElement.GetProperty("request").GetProperty("debtorIban").GetString().ShouldBe(
            JournalPayload.Redacted,
            "The flow's input contract marks DebtorIban [Sensitive], so it is gone from the " +
            "record for the same reason and by the same pass it is gone from the journal row.");

        posted.RootElement.GetProperty("request").GetProperty("amount").GetDecimal().ShouldBe(
            120m,
            "and the amount survives, or the assertion above would hold for a record that " +
            "carried nothing.");

        posted.RootElement.GetProperty("result").GetProperty("entryId").GetString().ShouldNotBeNull(
            "the ledger reference is in the *financial* record of the leg that produced it — " +
            "it is redacted only out of the settlement record, and only because that policy " +
            "asked.");

        var settlement = harness.Audit.Entries[2];

        using var recorded = JsonDocument.Parse(settlement.Document!);

        var request = recorded.RootElement.GetProperty("request");

        request.GetProperty("debitEntryId").GetString().ShouldBe(
            JournalPayload.Redacted,
            "Nothing marks DebitEntryId [Sensitive]; Policies.SettlementRegister named it in " +
            "its redact list. This is what redact means, and it is the half that could not " +
            "exist while the record was empty.");

        request.GetProperty("creditEntryId").GetString().ShouldBe(JournalPayload.Redacted);

        request.GetProperty("transferId").GetString().ShouldBe(
            "a-3",
            "and the transfer is still identifiable, which is the whole point of removing the " +
            "two references rather than the record.");

        foreach (var entry in harness.Audit.Entries)
        {
            entry.Document!.Contains(Debtor, StringComparison.Ordinal).ShouldBeFalse(
                "No account number reaches any record, which is the assertion an auditor of " +
                "this control actually wants — and it holds structurally rather than by care " +
                "taken at the sink: JournalPayload has no accessor for a value.");

            entry.Document!.Contains(Creditor, StringComparison.Ordinal).ShouldBeFalse();
        }
    }

    /// <summary>An unwound transfer records the legs that happened and not the one that did not.</summary>
    /// <remarks>
    /// <c>Audit</c> is a <c>Consistency</c> policy: it records what happened. The credit into a
    /// closed account fails, so no record is written for it, and the debit's record stands —
    /// the debit did happen, and it was then reversed. A trail that recorded the failed credit
    /// would report a payment that never reached the ledger; one that dropped the debit's
    /// record on the unwind would hide a movement that did.
    /// </remarks>
    [Fact]
    public async Task AnUnwoundTransferRecordsTheLegThatHappened()
    {
        var harness = new TransferHarness();

        var result = await harness.RunAsync(
            new ExecuteTransfer(Debtor, Closed, 120m, "EUR", TransferChannel.Sepa),
            "a-4",
            Cancellation);

        result.IsSuccess.ShouldBeFalse("the creditor account is closed");

        harness.Audit.Entries.Select(e => e.Record.CapabilityId).ShouldBe(
            ["ledger.post_debit"],
            "The debit posted and was audited; the credit never posted, so there is nothing " +
            "for stage 7 to record about it.");
    }

    /// <summary>The account the ledger refuses to credit.</summary>
    private const string Closed = "FR7630006000011234567890189";

    private static ExecuteTransfer Transfer(decimal amount) =>
        new(Debtor, Creditor, amount, "EUR", TransferChannel.Sepa);
}
