using Banking;
using FlowX;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace Banking.Tests;

/// <summary>
/// The saga's control flow and recovery, over a real journal and no server.
/// </summary>
/// <remarks>
/// <para>
/// The flow level of the test pyramid (<c>docs/23-Testing-Strategy.md §3</c>): the real
/// generated <c>Plan</c>, the real generated <c>Dispatcher</c>, the real engine, the real
/// <see cref="FlowX.Hosting.FlowHost"/> and the real in-memory ledger, with one capability
/// substituted wherever a failure is the point. <see cref="TransferHarness"/> says why it is
/// not <c>FlowTestHost</c>.
/// </para>
/// <para>
/// Everything asserted here is a property of the flow — which arm was taken, which steps
/// ran, in which order they were undone, and what the ledger holds afterwards. None of it is
/// observable from an endpoint: a saga that unwound in the wrong order and one that unwound
/// in the right order return the same status code.
/// </para>
/// </remarks>
public sealed class ExecuteTransferFlowTests
{
    private const string Debtor = "GB33BUKB20201555555555";
    private const string Creditor = "DE89370400440532013000";
    private const string Closed = "FR7630006000011234567890189";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ABookTransferSettlesBothLegsAndIsScreenedByNothing()
    {
        var harness = new TransferHarness();

        var result = await harness.RunAsync(Transfer(120m, TransferChannel.Book), "t-1", Cancellation);

        result.IsSuccess.ShouldBeTrue(result.Error?.ToString());

        // The switch has no Default, so a channel that matches no case continues after it.
        // That is the documented behaviour of ISwitchBuilder, and it is the modelling here:
        // money that never leaves the bank is screened by nobody.
        harness.Executed.ShouldBe(
            [
                "transfer.validate",
                "ledger.post_debit",
                "ledger.post_credit",
                "settlement.record",
                "emit:transfer.completed",
            ]);

        harness.Compensated.ShouldBeEmpty();
        result.Compensation.ShouldBe(CompensationOutcome.NotRequired);

        result.Value.TransferId.ShouldBe("t-1");
        result.Value.Amount.ShouldBe(120m);
        result.Value.Currency.ShouldBe("EUR");
        result.Value.DebitEntryId.ShouldBe("t-1:ledger.post_debit");
        result.Value.CreditEntryId.ShouldBe("t-1:ledger.post_credit");

        // Both legs, on the real ledger: 500 - 120 out, 0 + 120 in.
        (await harness.Ledger.AvailableAsync(Debtor, Cancellation)).ShouldBe(380m);
        (await harness.Ledger.AvailableAsync(Creditor, Cancellation)).ShouldBe(120m);
    }

    [Fact]
    public async Task ASepaTransferIsScreenedAndNotRouted()
    {
        var harness = new TransferHarness();

        var result = await harness.RunAsync(Transfer(120m, TransferChannel.Sepa), "t-2", Cancellation);

        result.IsSuccess.ShouldBeTrue(result.Error?.ToString());

        harness.Executed.ShouldBe(
            [
                "transfer.validate",
                "compliance.screen_sanctions",
                "ledger.post_debit",
                "ledger.post_credit",
                "settlement.record",
                "emit:transfer.completed",
            ]);
    }

    [Fact]
    public async Task ASwiftTransferIsScreenedAndRouted()
    {
        var harness = new TransferHarness();

        var result = await harness.RunAsync(Transfer(120m, TransferChannel.Swift), "t-3", Cancellation);

        result.IsSuccess.ShouldBeTrue(result.Error?.ToString());

        // The Swift arm is the only one with two steps in it. A switch that matched the
        // wrong arm would still settle the transfer, so the arm is asserted rather than
        // inferred from the transfer having succeeded.
        harness.Executed.ShouldBe(
            [
                "transfer.validate",
                "compliance.screen_sanctions",
                "correspondent.resolve",
                "ledger.post_debit",
                "ledger.post_credit",
                "settlement.record",
                "emit:transfer.completed",
            ]);
    }

    /// <summary>
    /// A short balance ends the flow as a business outcome, and unwinds nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ADR-0007: an outcome a caller could reasonably handle is a value, not an exception.
    /// The rejection is <c>.Fail(...)</c> in the flow, so it is a node in the compiled plan
    /// and a <c>"kind": "Fail"</c> in the manifest rather than a hidden <c>if</c> inside a
    /// capability.
    /// </para>
    /// <para>
    /// <strong>And it unwinds nothing, which is the second half of the claim.</strong>
    /// <c>.Fail</c> takes the failure path exactly as a declined capability does — the
    /// completed compensable steps <em>do</em> unwind — and at this point in the flow there
    /// are none, because the arm is taken before either ledger leg. A compensation that ran
    /// here would be reversing a debit that never happened, which on a ledger is a credit
    /// nobody asked for.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AShortBalanceFailsAsABusinessOutcomeAndUnwindsNothing()
    {
        var harness = new TransferHarness();

        var result = await harness.RunAsync(Transfer(5_000m, TransferChannel.Book), "t-4", Cancellation);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("transfer.insufficient_funds");
        result.Error.Category.ShouldBe(ErrorCategory.Conflict);

        // Validation ran; the Fail node ended the flow before either leg was posted.
        harness.Executed.ShouldBe(["transfer.validate", "fail"]);

        harness.Compensated.ShouldBeEmpty();
        result.Compensation.ShouldBe(CompensationOutcome.NotRequired);

        (await harness.Ledger.AvailableAsync(Debtor, Cancellation)).ShouldBe(500m);
        (await harness.Ledger.AvailableAsync(Creditor, Cancellation)).ShouldBe(0m);

        // Nothing was emitted either: the outbox is staged by a step's own commit, and the
        // Emit step is after the arm that ended the flow.
        var instance = harness.Instances.ShouldHaveSingleItem();
        (await harness.OutboxAsync(instance.InstanceId, Cancellation)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AFailedCreditReversesTheDebitAndLeavesTheLedgerWhereItStarted()
    {
        var harness = new TransferHarness()
            .Substitute("ledger.post_credit", TransferErrors.AccountUnknown());

        var result = await harness.RunAsync(Transfer(120m, TransferChannel.Book), "t-5", Cancellation);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("ledger.account_unknown");
        result.Compensation.ShouldBe(CompensationOutcome.Succeeded);

        harness.Compensated.ShouldBe(["ledger.reverse_debit"]);

        // The property the whole sample exists for: money is never left in one account only.
        // The debtor is whole and the creditor was never touched.
        (await harness.Ledger.AvailableAsync(Debtor, Cancellation)).ShouldBe(500m);
        (await harness.Ledger.AvailableAsync(Creditor, Cancellation)).ShouldBe(0m);
    }

    /// <summary>
    /// The unwind, with nothing substituted at all.
    /// </summary>
    /// <remarks>
    /// Every other failure test in this file makes a capability fail on purpose. This one
    /// does not: the beneficiary account is one the sample's own ledger holds and has closed,
    /// <see cref="PostCredit"/> refuses it for real, and the debit that had already been
    /// posted is backed out by a real contra entry. It is the path a live request takes, and
    /// the sample's README shows it as a <c>curl</c>.
    /// </remarks>
    [Fact]
    public async Task AClosedBeneficiaryUnwindsTheDebitWithNothingSubstituted()
    {
        var harness = new TransferHarness();

        var result = await harness.RunAsync(
            new ExecuteTransfer(Debtor, Closed, 120m, "EUR", TransferChannel.Book),
            "t-10",
            Cancellation);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("ledger.account_closed");
        result.Compensation.ShouldBe(CompensationOutcome.Succeeded);

        harness.Executed.ShouldBe(["transfer.validate", "ledger.post_debit", "ledger.post_credit"]);
        harness.Compensated.ShouldBe(["ledger.reverse_debit"]);

        // Debit, refused credit, contra entry. The debtor is whole and the closed account
        // was never touched.
        harness.Ledger.Keys.ShouldBe(["t-10:ledger.post_debit", "t-10:ledger.reverse_debit"]);

        (await harness.Ledger.AvailableAsync(Debtor, Cancellation)).ShouldBe(500m);
        (await harness.Ledger.AvailableAsync(Closed, Cancellation)).ShouldBe(0m);

        // The instance ends Compensated, and the journal says so.
        var instance = harness.Instances.ShouldHaveSingleItem();
        instance.State.ShouldBe(FlowInstanceState.Failed);

        var steps = await harness.StepsAsync(instance.InstanceId, Cancellation);

        steps.Where(step => step.Outcome == JournalOutcome.Compensated)
            .Select(step => step.CapabilityId)
            .ShouldBe(["ledger.reverse_debit"]);
    }

    /// <summary>
    /// Two completed legs unwind in strict reverse order.
    /// </summary>
    /// <remarks>
    /// The failure is placed after both ledger writes, because that is the only arrangement
    /// in which the order is observable at all: with one compensable step there is exactly
    /// one correct sequence and every implementation produces it.
    /// </remarks>
    [Fact]
    public async Task AFailedSettlementUnwindsBothLegsInStrictReverseOrder()
    {
        var harness = new TransferHarness()
            .Substitute("settlement.record", TransferErrors.NoCorrespondent("GB"));

        var result = await harness.RunAsync(Transfer(120m, TransferChannel.Book), "t-6", Cancellation);

        result.IsFailure.ShouldBeTrue();
        result.Compensation.ShouldBe(CompensationOutcome.Succeeded);

        // Credit first, then debit — the reverse of the order they completed in. The other
        // order would leave the creditor holding money the debtor has already been given
        // back, for as long as the second reversal takes.
        harness.Compensated.ShouldBe(["ledger.reverse_credit", "ledger.reverse_debit"]);

        (await harness.Ledger.AvailableAsync(Debtor, Cancellation)).ShouldBe(500m);
        (await harness.Ledger.AvailableAsync(Creditor, Cancellation)).ShouldBe(0m);
    }

    [Fact]
    public async Task ASanctionsHitStopsTheTransferBeforeAnyMoneyMoves()
    {
        var harness = new TransferHarness()
            .Substitute("compliance.screen_sanctions", TransferErrors.SanctionsHit("OFAC-SDN"));

        var result = await harness.RunAsync(Transfer(120m, TransferChannel.Sepa), "t-7", Cancellation);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("compliance.sanctions_hit");

        harness.Executed.ShouldNotContain("ledger.post_debit");
        harness.Compensated.ShouldBeEmpty();

        (await harness.Ledger.AvailableAsync(Debtor, Cancellation)).ShouldBe(500m);
    }

    // ------------------------------------------------------- the policies that now bite

    /// <summary>
    /// <c>Policies.ExternalRead</c>'s retry survives a screening provider that blinks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The sample's first assertion that a declared policy changes what happens.</strong>
    /// Every policy statement in this project used to be of the form "declared, published, and
    /// executed by nothing"; <c>Policies.ExternalRead</c> declares
    /// <c>Retry(attempts: 3, ExponentialJitter(200ms))</c> over
    /// <c>compliance.screen_sanctions</c>, and this is the transfer that would have been
    /// refused before the policy engine and settles now.
    /// </para>
    /// <para>
    /// Every precondition FLOWX1014 protects is real here rather than hypothetical:
    /// <c>compliance.screen_sanctions</c> declares <c>Idempotent = true</c>, so the build
    /// permits the retry, and the failure is <see cref="ErrorCategory.Unavailable"/> — the
    /// provider did not answer, which is worth asking again, unlike the sanctions <em>hit</em>
    /// two tests above, which is <see cref="ErrorCategory.Forbidden"/> and terminal.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATransientScreeningFailureIsRetriedAndTheTransferSettles()
    {
        var harness = new TransferHarness()
            .SubstituteForAttempts("compliance.screen_sanctions", attempts: 1, ProviderDown);

        var result = await harness.RunAsync(Transfer(120m, TransferChannel.Sepa), "t-p1", Cancellation);

        result.IsSuccess.ShouldBeTrue(result.Error?.ToString());

        harness.Executed.ShouldBe(
            [
                "transfer.validate",
                "compliance.screen_sanctions",
                "compliance.screen_sanctions",
                "ledger.post_debit",
                "ledger.post_credit",
                "settlement.record",
                "emit:transfer.completed",
            ],
            "The provider failed once and the declared Retry asked again. Before the policy " +
            "engine this list held one screening call and ended there.");

        (await harness.Ledger.AvailableAsync(Creditor, Cancellation)).ShouldBe(120m);
    }

    /// <summary>
    /// And the retry is bounded by what the author declared, not by patience.
    /// </summary>
    /// <remarks>
    /// The half that stops the test above from being a statement about an engine that retries
    /// for ever. Three attempts are declared and a provider that never recovers gets exactly
    /// three, after which the transfer is refused with the provider's own error and no money
    /// has moved — the screening step is upstream of both ledger legs, which is why the arm is
    /// ordered the way it is.
    /// </remarks>
    [Fact]
    public async Task AScreeningProviderThatNeverRecoversIsAskedExactlyTheDeclaredNumberOfTimes()
    {
        var harness = new TransferHarness()
            .Substitute("compliance.screen_sanctions", ProviderDown);

        var result = await harness.RunAsync(Transfer(120m, TransferChannel.Sepa), "t-p2", Cancellation);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("compliance.screening_unavailable");

        harness.Executed.Count(static id => id == "compliance.screen_sanctions").ShouldBe(
            3,
            "Policies.ExternalRead declares Retry(attempts: 3), and attempts includes the " +
            "first. A fourth would mean the count is read as 'retries' rather than 'attempts'.");

        harness.Executed.ShouldNotContain("ledger.post_debit");
        harness.Compensated.ShouldBeEmpty();

        (await harness.Ledger.AvailableAsync(Debtor, Cancellation)).ShouldBe(500m);
    }

    /// <summary>
    /// The transient failure this bank's <c>ExternalRead</c> set exists for.
    /// </summary>
    /// <remarks>
    /// Declared here rather than in <c>TransferErrors</c>, because no capability in
    /// <c>samples/banking</c> returns it: <c>ISanctionsScreening</c>'s in-memory
    /// implementation always answers. Putting it in the sample's published catalogue would
    /// add a code to the manifest that nothing can produce, which is the shape of claim this
    /// repository removes rather than adds.
    /// </remarks>
    private static readonly Error ProviderDown = new(
        "compliance.screening_unavailable",
        "The sanctions screening provider did not answer.",
        ErrorCategory.Unavailable);

    /// <summary>
    /// A step on an untaken arm never runs, which is what makes the arm assertions mean
    /// something.
    /// </summary>
    /// <remarks>
    /// The mirror of <c>FlowTestRun.UnusedSubstitutions</c>, which this project cannot use.
    /// A substitution that never fires is how a test passes for the wrong reason — the arm
    /// carrying it was not taken — so the fact is asserted rather than assumed.
    /// </remarks>
    [Fact]
    public async Task AStandInOnAnUntakenArmNeverRuns()
    {
        var harness = new TransferHarness()
            .Substitute("compliance.screen_sanctions", TransferErrors.SanctionsHit("OFAC-SDN"));

        // A Book transfer matches no case, so screening is never dispatched and the
        // stand-in never gets the chance to fail the flow.
        var result = await harness.RunAsync(Transfer(120m, TransferChannel.Book), "t-8", Cancellation);

        result.IsSuccess.ShouldBeTrue(result.Error?.ToString());
        harness.Executed.ShouldNotContain("compliance.screen_sanctions");
    }

    /// <summary>
    /// The journal and the ledger are told the same thing about what ran.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>These two answers used to differ, and the difference is what made a real
    /// loss invisible.</strong> The audit trail said <c>ledger.reverse_debit</c> ran —
    /// <c>FlowEngine</c> has always written the compensating capability onto the row — while
    /// the capability that ran saw <c>ctx.CapabilityId == "ledger.post_debit"</c>, because
    /// the engine entered a compensation with the forward step. A reversal keying its contra
    /// write on the context therefore produced the debit's key exactly, the ledger returned
    /// the debit's entry and moved nothing, and the journal recorded a successful undo over
    /// it. The keys below are the half that would have been wrong: the reversals derive them
    /// from the context alone, so four distinct keys is the evidence that the engine hands a
    /// compensation its own identity and the contra entries were written rather than
    /// deduplicated away.
    /// </para>
    /// <para>
    /// Both directions are still pinned, because agreement is the property: the ledger keys
    /// are what the running code saw, and the journal rows are what an operator reading the
    /// audit trail is told, and neither is worth much while the other can silently differ.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheJournalAndTheLedgerAgreeOnWhichCapabilitiesUnwoundTheTransfer()
    {
        var harness = new TransferHarness()
            .Substitute("settlement.record", TransferErrors.NoCorrespondent("GB"));

        await harness.RunAsync(Transfer(120m, TransferChannel.Book), "t-9", Cancellation);

        harness.Ledger.Keys.ShouldBe(
            [
                "t-9:ledger.post_debit",
                "t-9:ledger.post_credit",
                "t-9:ledger.reverse_credit",
                "t-9:ledger.reverse_debit",
            ]);

        var instance = harness.Instances.ShouldHaveSingleItem();
        var steps = await harness.StepsAsync(instance.InstanceId, Cancellation);

        steps.Where(s => s.Outcome == JournalOutcome.Compensated)
            .Select(s => s.CapabilityId)
            .ShouldBe(["ledger.reverse_credit", "ledger.reverse_debit"]);
    }

    private static ExecuteTransfer Transfer(decimal amount, TransferChannel channel) =>
        new(Debtor, Creditor, amount, "EUR", channel);
}
