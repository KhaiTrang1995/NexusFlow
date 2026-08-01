using System.Text.Json;
using Banking;
using FlowX;
using Shouldly;
using Xunit;

namespace Banking.Tests;

/// <summary>
/// What a settled transfer leaves behind in the journal, read back off the store.
/// </summary>
/// <remarks>
/// <para>
/// This is the sample's evidence for two of the three claims its README makes: that the
/// event announcing a transfer is staged by the transfer's own transaction and staged once,
/// and that an account number marked <c>[Sensitive]</c> is not in any row a retention window
/// keeps. Both are asserted by reading the store rather than by reasoning about the code
/// that wrote to it.
/// </para>
/// <para>
/// The third claim — an immutable audit trail — is the one WP-59 made true. Until then the
/// instance row recorded no input at all and no step recorded a payload, so "immutable audit
/// trail" described a table of step boundaries. See
/// <see cref="TheInstanceRowHoldsTheRequestAndStillNoPrincipal"/>.
/// </para>
/// </remarks>
public sealed class TransferJournalTests
{
    private const string Debtor = "GB33BUKB20201555555555";
    private const string Creditor = "DE89370400440532013000";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheEmittedEventIsStagedExactlyOnce()
    {
        var harness = new TransferHarness();

        await harness.RunAsync(Transfer(120m), "j-1", Cancellation);

        var instance = harness.Instances.ShouldHaveSingleItem();
        var staged = (await harness.OutboxAsync(instance.InstanceId, Cancellation)).ShouldHaveSingleItem();

        staged.Type.ShouldBe("transfer.completed");
        staged.InstanceId.ShouldBe(instance.InstanceId);
        staged.SchemaVersion.ShouldBe("1.0.0");

        // Per-key ordering is the only ordering ADR-0018 offers, and the key an instance's
        // own events share is the instance.
        staged.PartitionKey.ShouldBe(instance.InstanceId.ToString());
    }

    /// <summary>
    /// The staged event was written by a committed step's own transaction.
    /// </summary>
    /// <remarks>
    /// The outbox and the step rows come out of one <c>CommitAsync</c>, which is what makes
    /// "never published before the step is durable" true rather than hopeful. Asserted as
    /// counts here because the reference journal exposes no sequence join; the PostgreSQL
    /// adapter's own suite asserts the same fact through <c>flow_step.sequence</c>.
    /// </remarks>
    [Fact]
    public async Task TheEventIsStagedByTheStepBoundaryAndNotBeforeIt()
    {
        var harness = new TransferHarness();

        await harness.RunAsync(Transfer(120m), "j-2", Cancellation);

        var instance = harness.Instances.ShouldHaveSingleItem();
        var steps = await harness.StepsAsync(instance.InstanceId, Cancellation);
        var outbox = await harness.OutboxAsync(instance.InstanceId, Cancellation);

        // One row per dispatched step, the Emit step included: a durable flow journals its
        // boundaries, and the event's own boundary is the one that staged it.
        steps.Select(s => s.CapabilityId).ShouldBe(
            [
                "transfer.validate",
                "compliance.screen_sanctions",
                "ledger.post_debit",
                "ledger.post_credit",
                "settlement.record",

                // The Emit step invokes no capability, so the journal records it under the
                // event's own identity. That is what makes an outbox row traceable to the
                // boundary that staged it.
                "transfer.completed",
            ]);

        outbox.Count.ShouldBe(1);
    }

    /// <summary>
    /// A <c>[Sensitive]</c> account number is in no row this journal holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The strong form of the claim: not "the event body is redacted" but "search every
    /// text column of every row for the value and find nothing". That is the assertion worth
    /// making, because a journal is retained for months and read by people who are not
    /// reading it for the field you were thinking about.
    /// </para>
    /// <para>
    /// It passes for two different reasons and it is worth knowing which is which. The
    /// event body is redacted — the generated <c>DescribeStep</c> builds a
    /// <c>JournalPayload</c> carrying <c>ExecuteTransferFlow.SensitiveMembers</c>, and a
    /// <c>JournalPayload</c>'s only exit is <c>ToJson</c>, which replaces every matching
    /// member. <strong>Until WP-59 the step rows and the instance row were clean for a much
    /// weaker reason: nothing wrote a payload into them at all.</strong> They now carry the
    /// input, the state bag and every step result, and they are clean for the same reason the
    /// event is — which is what
    /// <see cref="ASensitiveMemberIsRedactedInTheInputTheStateBagAndEveryStepResult"/>
    /// asserts from the other side.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task NoAccountNumberAppearsInAnyJournalRow()
    {
        var harness = new TransferHarness();

        await harness.RunAsync(Transfer(120m), "j-3", Cancellation);

        var instance = harness.Instances.ShouldHaveSingleItem();
        var steps = await harness.StepsAsync(instance.InstanceId, Cancellation);
        var outbox = await harness.OutboxAsync(instance.InstanceId, Cancellation);

        var written = new List<string>
        {
            instance.InputJson ?? string.Empty,
            instance.StateBagJson ?? string.Empty,
            instance.CorrelationId,
            instance.TenantId ?? string.Empty,
        };

        written.AddRange(steps.Select(s => s.ResultJson ?? string.Empty));
        written.AddRange(steps.Select(s => s.CapabilityId));
        written.AddRange(outbox.Select(o => o.PayloadJson ?? string.Empty));
        written.AddRange(outbox.Select(o => o.PartitionKey ?? string.Empty));

        var everything = string.Join('\n', written);

        everything.ShouldNotContain(Debtor, Case.Sensitive,
            "an account number reached a table that is retained for as long as the " +
            "regulator asks for, and every operator with read access to it.");

        everything.ShouldNotContain(Creditor, Case.Sensitive,
            "the counterparty's account is no less sensitive for belonging to someone else.");
    }

    [Fact]
    public async Task TheEmittedEventCarriesNoAccountNumberAndSaysSo()
    {
        var harness = new TransferHarness();

        await harness.RunAsync(Transfer(120m), "j-4", Cancellation);

        var instance = harness.Instances.ShouldHaveSingleItem();
        var staged = (await harness.OutboxAsync(instance.InstanceId, Cancellation)).ShouldHaveSingleItem();

        using var body = JsonDocument.Parse(staged.PayloadJson.ShouldNotBeNull());

        // Withheld rather than absent. An operator reading this during an incident needs to
        // know the field was recorded and redacted; a key that silently vanishes reads as a
        // field the flow never received.
        body.RootElement.GetProperty("debtorIban").GetString().ShouldBe(JournalPayload.Redacted);
        body.RootElement.GetProperty("creditorIban").GetString().ShouldBe(JournalPayload.Redacted);

        // The rest of the body is intact, which is what makes the event worth publishing.
        body.RootElement.GetProperty("transferId").GetString().ShouldBe("j-4");
        body.RootElement.GetProperty("amount").GetDecimal().ShouldBe(120m);
        body.RootElement.GetProperty("currency").GetString().ShouldBe("EUR");
    }

    /// <summary>
    /// The names the redaction matches on come from the flow's contracts, not the event's.
    /// </summary>
    /// <remarks>
    /// <c>SensitiveMembers</c> is read off <c>ExecuteTransfer</c> and <c>TransferResult</c> —
    /// the flow's declared input and output — and redaction then matches by property name at
    /// every depth. <c>TransferCompleted</c>'s own <c>[Sensitive]</c> markers reach nothing.
    /// So this array is the coupling, and renaming a member on one side only would silently
    /// stop redacting.
    /// </remarks>
    [Fact]
    public void TheFlowPublishesTheMembersRedactionMatchesOn() =>
        ExecuteTransferFlow.SensitiveMembers.ShouldBe(["CreditorIban", "DebtorIban"]);

    /// <summary>
    /// What the instance row records, and the one thing it still does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This test asserted the opposite until WP-59, on purpose.</strong> It read
    /// "the day a trigger starts journaling its input, this file goes red", and it did.
    /// <c>FlowHost.OpenAsync</c> passed the literal <c>input: null</c> to
    /// <c>BeginAsync</c>, so <c>flow_instance.input</c> was NULL on every row ever written:
    /// a replay could not reconstruct what was requested, the audit trail had no record of
    /// it, and <c>[Sensitive]</c> on an input contract protected nothing, because nothing
    /// was stored. It was not fixable in the host — recording an input needs a
    /// <c>JsonTypeInfo&lt;TIn&gt;</c> and only generated code can name one — so the host now
    /// asks the dispatcher, through <c>IStepDispatcher.DescribeInput</c>.
    /// </para>
    /// <para>
    /// <strong>There is still no principal.</strong> <c>FlowInstanceRecord</c> has no member
    /// for one and <c>FlowInvocation</c> carries none, so "who asked for this transfer" is
    /// not in the journal. The tenant and the correlation id are, and they are what an
    /// investigator actually has.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheInstanceRowHoldsTheRequestAndStillNoPrincipal()
    {
        var harness = new TransferHarness();

        await harness.RunAsync(Transfer(120m), "j-5", Cancellation);

        var instance = harness.Instances.ShouldHaveSingleItem();

        using var input = JsonDocument.Parse(instance.InputJson.ShouldNotBeNull(
            "the request a durable instance was started with is what a replay reconstructs " +
            "from and what an audit trail is."));

        // What was asked for, minus the two things that must never be written down.
        input.RootElement.GetProperty("amount").GetDecimal().ShouldBe(120m);
        input.RootElement.GetProperty("currency").GetString().ShouldBe("EUR");
        input.RootElement.GetProperty("debtorIban").GetString().ShouldBe(JournalPayload.Redacted);
        input.RootElement.GetProperty("creditorIban").GetString().ShouldBe(JournalPayload.Redacted);

        instance.StateBagJson.ShouldNotBeNull(
            "and the generated DescribeStep snapshots the bag at every step boundary, which " +
            "is what lets another node resume this instance rather than restart it.");

        // What the row does hold, and what an investigation actually gets.
        instance.FlowId.ShouldBe("transfer.execute");
        instance.FlowVersion.ShouldBe("1.0.0");
        instance.TenantId.ShouldBe("tenant-1");
        instance.CorrelationId.ShouldBe("corr-j-5");
        instance.State.ShouldBe(FlowInstanceState.Completed);
    }

    /// <summary>
    /// Every stored payload carries the envelope version that wrote it, and it is readable.
    /// </summary>
    /// <remarks>
    /// ADR-0008's Decision — "every persisted payload carries <c>schemaVersion</c>" — had no
    /// producer until WP-59. It versions the envelope this row was written by, not the
    /// contract: the contract's version is the <c>flow_version</c> and
    /// <c>capability_version</c> beside it, and inventing a second one here is what ADR-0017's
    /// criterion F2 refuses.
    /// </remarks>
    [Fact]
    public async Task EveryStoredPayloadIsStampedWithTheEnvelopeThatWroteIt()
    {
        var harness = new TransferHarness();

        await harness.RunAsync(Transfer(120m), "j-7", Cancellation);

        var instance = harness.Instances.ShouldHaveSingleItem();
        var steps = await harness.StepsAsync(instance.InstanceId, Cancellation);
        var outbox = await harness.OutboxAsync(instance.InstanceId, Cancellation);

        var stored = new List<string?> { instance.InputJson, instance.StateBagJson };

        stored.AddRange(steps.Select(s => s.ResultJson));
        stored.AddRange(outbox.Select(o => o.PayloadJson));

        var documents = stored.Where(json => json is not null).ToList();

        documents.Count.ShouldBeGreaterThan(
            6, "an assertion over an empty set of rows would pass for the wrong reason.");

        foreach (var json in documents)
        {
            using var document = JsonDocument.Parse(json!);

            document.RootElement.GetProperty(JournalPayload.SchemaVersionMember).GetString()
                .ShouldBe(JournalPayload.SchemaVersion, json);
        }
    }

    /// <summary>
    /// A <c>[Sensitive]</c> member is redacted in the stored input, the stored state bag and
    /// every stored step result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="NoAccountNumberAppearsInAnyJournalRow"/> asserts the absence of the value.
    /// This asserts the presence of the placeholder, in each of the three sinks the payload
    /// writer opened, because "the account number is not there" is also true of a row that
    /// stores nothing — which is exactly what the journal did before WP-59, and why the
    /// stronger test passed while the property was untested.
    /// </para>
    /// <para>
    /// <strong>All three hold for one reason.</strong> The writer never composes a document:
    /// it hands values to <c>JournalPayload</c> and every one of them leaves through
    /// <c>ToJson</c>, which redacts by name at every depth. There is no second exit and
    /// therefore no second copy of the rule that could drift from this one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASensitiveMemberIsRedactedInTheInputTheStateBagAndEveryStepResult()
    {
        var harness = new TransferHarness();

        await harness.RunAsync(Transfer(120m), "j-8", Cancellation);

        var instance = harness.Instances.ShouldHaveSingleItem();
        var steps = await harness.StepsAsync(instance.InstanceId, Cancellation);

        // The input: the request itself, with the two accounts withheld.
        RedactsBothAccounts(instance.InputJson, "the stored trigger input");

        // The state bag: the snapshot a resumed node rehydrates from. The marked members are
        // matched at every depth, so a nested ValidatedTransfer is no less protected for
        // being one level down.
        RedactsBothAccounts(instance.StateBagJson, "the stored state bag");

        // Every step result that stored one. ValidatedTransfer carries both accounts and is
        // the row this would leak through if the writer had composed its own document.
        var results = steps
            .Select(step => step.ResultJson)
            .Where(json => json is not null && json.Contains("Iban", StringComparison.OrdinalIgnoreCase))
            .ToList();

        results.ShouldNotBeEmpty(
            "no stored step result mentions an account at all, so this test would be " +
            "asserting nothing.");

        foreach (var result in results)
        {
            RedactsBothAccounts(result, "a stored step result");
        }
    }

    /// <summary>Asserts a stored document withholds both accounts and says it withheld them.</summary>
    private static void RedactsBothAccounts(string? json, string what)
    {
        json.ShouldNotBeNull(what + " is missing, so nothing about redaction can be read off it.");

        json.ShouldNotContain(Debtor, Case.Sensitive, what + " carries the debtor's account.");
        json.ShouldNotContain(Creditor, Case.Sensitive, what + " carries the creditor's account.");

        json.ShouldContain(JournalPayload.Redacted, Case.Sensitive,
            what + " withheld the accounts without saying so. An operator reading this row " +
            "during an incident would conclude the flow never received them.");
    }

    /// <summary>
    /// The same idempotency key, twice, moves money once.
    /// </summary>
    /// <remarks>
    /// Two independent instances against one ledger — which is what a caller retrying on a
    /// timeout produces, because the first request's instance is not reachable from the
    /// second. Nothing in the platform deduplicates them: the second flow runs every step
    /// again, and the effects are single because each capability presented the ledger a key
    /// derived from the caller's own. That is the whole of the "safe under retry" claim, and
    /// it lives in the capability rather than in the engine.
    /// </remarks>
    [Fact]
    public async Task ARetriedTransferUnderOneKeyMovesMoneyOnce()
    {
        var harness = new TransferHarness();

        var first = await harness.RunAsync(Transfer(120m), "j-6", Cancellation);
        var second = await harness.RunAsync(Transfer(120m), "j-6", Cancellation);

        first.IsSuccess.ShouldBeTrue(first.Error?.ToString());
        second.IsSuccess.ShouldBeTrue(second.Error?.ToString());

        // The same answer, because the output is derived from what the steps produced and
        // the steps produced the same entries.
        second.Value.ShouldBe(first.Value);

        (await harness.Ledger.AvailableAsync(Debtor, Cancellation)).ShouldBe(380m);
        (await harness.Ledger.AvailableAsync(Creditor, Cancellation)).ShouldBe(120m);

        // Two instances, though. The journal records both attempts, which is the truthful
        // record: two requests arrived.
        harness.Instances.Count.ShouldBe(2);

        // And two events. This is the cost of the design and it is stated rather than
        // glossed: nothing deduplicates a durable instance by the caller's idempotency key,
        // so a retried transfer stages `transfer.completed` a second time. The ledger is
        // safe because each capability presented a derived key; a consumer of the event is
        // safe only if it deduplicates on `transferId`, which is in the body for that
        // purpose. `Idempotent = true` on the [HttpTrigger] declares the requirement for a
        // key; it does not replay a recorded response.
        var events = new List<OutboxRecord>();

        foreach (var instance in harness.Instances)
        {
            events.AddRange(await harness.OutboxAsync(instance.InstanceId, Cancellation));
        }

        events.Count.ShouldBe(2);
        events.Select(e => JsonDocument.Parse(e.PayloadJson!).RootElement
                .GetProperty("transferId").GetString())
            .Distinct(StringComparer.Ordinal)
            .ShouldHaveSingleItem()
            .ShouldBe("j-6");
    }

    private static ExecuteTransfer Transfer(decimal amount) =>
        new(Debtor, Creditor, amount, "EUR", TransferChannel.Sepa);
}
