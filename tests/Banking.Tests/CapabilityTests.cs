using Banking;
using FlowX;
using FlowX.Testing;
using Shouldly;
using Xunit;

namespace Banking.Tests;

/// <summary>
/// Quality goal Q2: a capability is testable by constructing it and calling it.
/// </summary>
/// <remarks>
/// <para>
/// There is no host in this file, no service provider and no journal — a capability is a
/// class with a method, and money movement is not an exception to that. The six
/// capabilities below hold every business rule this application has, so this is where the
/// rules are checked and everything above it is about order and recovery.
/// </para>
/// <para>
/// The key each ledger write is deduplicated on is asserted here rather than inferred,
/// because it is the whole of the sample's "safe under retry and replay" claim and it is
/// the one thing a reader would otherwise have to take on trust.
/// </para>
/// </remarks>
public sealed class CapabilityTests
{
    private const string Debtor = "GB33BUKB20201555555555";
    private const string Creditor = "DE89370400440532013000";

    private static TestCapabilityContext ContextFor(string capabilityId) =>
        new("idem-1", capabilityId: capabilityId);

    [Fact]
    public async Task ValidateTransferPricesAWellFormedRequest()
    {
        var ledger = new FakeLedger(available: 500m);

        var result = await new ValidateTransfer(ledger).ExecuteAsync(
            new ExecuteTransfer(Debtor, Creditor, 120.50m, "EUR", TransferChannel.Sepa),
            ContextFor("transfer.validate"),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.DebtorIban.ShouldBe(Debtor);
        result.Value.CreditorIban.ShouldBe(Creditor);
        result.Value.Amount.ShouldBe(120.50m);
        result.Value.AvailableBalance.ShouldBe(500m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ValidateTransferRejectsANonPositiveAmount(decimal amount)
    {
        var result = await new ValidateTransfer(new FakeLedger(available: 500m)).ExecuteAsync(
            new ExecuteTransfer(Debtor, Creditor, amount, "EUR", TransferChannel.Sepa),
            ContextFor("transfer.validate"),
            TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("transfer.invalid_amount");
        result.Error.Category.ShouldBe(ErrorCategory.Validation);
    }

    [Fact]
    public async Task ValidateTransferRefusesAnAccountTheLedgerDoesNotHold()
    {
        var result = await new ValidateTransfer(new FakeLedger(available: null)).ExecuteAsync(
            new ExecuteTransfer(Debtor, Creditor, 10m, "EUR", TransferChannel.Sepa),
            ContextFor("transfer.validate"),
            TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("ledger.account_unknown");
        result.Error.Category.ShouldBe(ErrorCategory.NotFound);
    }

    /// <summary>
    /// A short balance is reported, not refused.
    /// </summary>
    /// <remarks>
    /// The refusal is the flow's, in the arm that reads this value — see
    /// <see cref="ExecuteTransferFlowTests"/>. Validation's job is to state the balance;
    /// deciding what a short one means is control flow, and putting it here would hide the
    /// decision from the graph, the manifest and the diagram.
    /// </remarks>
    [Fact]
    public async Task ValidateTransferReportsAShortBalanceRatherThanRefusingIt()
    {
        var result = await new ValidateTransfer(new FakeLedger(available: 10m)).ExecuteAsync(
            new ExecuteTransfer(Debtor, Creditor, 5_000m, "EUR", TransferChannel.Sepa),
            ContextFor("transfer.validate"),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.AvailableBalance.ShouldBe(10m);
    }

    [Fact]
    public async Task ScreeningClearsAnUnlistedCounterparty()
    {
        var result = await new ScreenSanctions(new FakeScreening(listed: null)).ExecuteAsync(
            Validated(120m),
            ContextFor("compliance.screen_sanctions"),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Reference.ShouldBe("idem-1:compliance.screen_sanctions");
    }

    [Fact]
    public async Task ScreeningStopsAListedCounterparty()
    {
        var result = await new ScreenSanctions(new FakeScreening(listed: Creditor)).ExecuteAsync(
            Validated(120m),
            ContextFor("compliance.screen_sanctions"),
            TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("compliance.sanctions_hit");
        result.Error.Category.ShouldBe(ErrorCategory.Forbidden);

        // The account is business data and the error travels to the caller as Problem
        // Details, so the code carries the decision and the structured detail carries the
        // screening reference — never the account itself.
        result.Error.ToString().ShouldNotContain(Creditor);
    }

    [Fact]
    public async Task PostingADebitMovesMoneyOutOfTheDebtor()
    {
        var ledger = new FakeLedger(available: 500m);

        var result = await new PostDebit(ledger).ExecuteAsync(
            new DebitInstruction(Debtor, 120m, "EUR"),
            ContextFor("ledger.post_debit"),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Amount.ShouldBe(120m);
        ledger.Posted.ShouldBe([-120m]);
    }

    /// <summary>
    /// The key a debit is deduplicated on is the flow's key and the capability's id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the assertion the sample's whole retry story rests on.</strong>
    /// <c>ctx.IdempotencyKey</c> is the <em>flow's</em> key — one value for the whole
    /// instance, stable across a retry and across a replay — so a debit and a credit that
    /// used it unqualified would present the same key to the ledger and the second write
    /// would be deduplicated away as a repeat of the first.
    /// </para>
    /// <para>
    /// The sample README used to say the key was <c>instanceId:stepId</c>. It is not:
    /// <c>CapabilityContext</c> exposes no step id, and <c>FlowInstanceId</c> is null for
    /// an ephemeral flow. <c>ctx.CapabilityId</c> is what changes per step and is stable
    /// under replay, so it is what qualifies the key.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ADebitAndACreditUnderOneFlowKeyDoNotCollide()
    {
        var ledger = new FakeLedger(available: 500m);

        await new PostDebit(ledger).ExecuteAsync(
            new DebitInstruction(Debtor, 120m, "EUR"),
            ContextFor("ledger.post_debit"),
            TestContext.Current.CancellationToken);

        await new PostCredit(ledger).ExecuteAsync(
            new CreditInstruction(Creditor, 120m, "EUR"),
            ContextFor("ledger.post_credit"),
            TestContext.Current.CancellationToken);

        ledger.Keys.ShouldBe(["idem-1:ledger.post_debit", "idem-1:ledger.post_credit"]);
        ledger.Posted.ShouldBe([-120m, 120m]);
    }

    [Fact]
    public async Task PostingTheSameDebitTwiceMovesMoneyOnce()
    {
        var ledger = new FakeLedger(available: 500m);
        var capability = new PostDebit(ledger);
        var instruction = new DebitInstruction(Debtor, 120m, "EUR");

        var first = await capability.ExecuteAsync(
            instruction, ContextFor("ledger.post_debit"), TestContext.Current.CancellationToken);

        var second = await capability.ExecuteAsync(
            instruction, ContextFor("ledger.post_debit"), TestContext.Current.CancellationToken);

        // Both succeed and both name the same entry: the second call is the first one's
        // answer, replayed. That is what Idempotent = true on this capability promises,
        // and it is why a Retry policy may legally be attached to it (FLOWX1014).
        first.Value.EntryId.ShouldBe(second.Value.EntryId);
        ledger.Posted.ShouldBe([-120m]);
    }

    /// <summary>
    /// An undo is a contra entry, and it consumes the instruction the step it undoes ran with.
    /// </summary>
    /// <remarks>
    /// The input type is not a preference. <c>PostDebit</c> is declared in the flow with an
    /// input mapping, and the generated <c>CompensateAsync</c> re-evaluates that mapping to
    /// build the compensation's argument — so a <c>ReverseDebit</c> written against
    /// <c>DebitPosted</c> compiles in this file and fails inside generated code. This test
    /// exists at the shape the dispatcher actually calls.
    /// </remarks>
    [Fact]
    public async Task PostingACreditToAClosedAccountIsRefusedAfterTheDebit()
    {
        // The only failure in this application that happens with money already moved. It is
        // what makes the flow a saga: the debit is posted, the credit is refused, and the
        // engine unwinds.
        var ledger = new FakeLedger(available: 500m, closed: true);

        var result = await new PostCredit(ledger).ExecuteAsync(
            new CreditInstruction(Creditor, 120m, "EUR"),
            ContextFor("ledger.post_credit"),
            TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("ledger.account_closed");
        result.Error.Category.ShouldBe(ErrorCategory.Conflict);

        // And nothing was written. A refusal that still posted would leave the unwind
        // reversing a credit that also happened.
        ledger.Posted.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReversingADebitPostsAContraEntry()
    {
        var ledger = new FakeLedger(available: 500m);
        var instruction = new DebitInstruction(Debtor, 120m, "EUR");

        await new PostDebit(ledger).ExecuteAsync(
            instruction, ContextFor("ledger.post_debit"), TestContext.Current.CancellationToken);

        var reversed = await new ReverseDebit(ledger).ExecuteAsync(
            instruction, ContextFor("ledger.reverse_debit"), TestContext.Current.CancellationToken);

        reversed.IsSuccess.ShouldBeTrue();
        reversed.Value.EntryId.ShouldBe("idem-1:ledger.reverse_debit");
        ledger.Posted.ShouldBe([-120m, 120m]);
    }

    [Fact]
    public async Task ReversingTwiceReversesOnce()
    {
        // A recovered instance may reach an undo a dead node already ran, and a compensation
        // carrying a retry may be dispatched more than once. A reversal that ran twice is a
        // second, unasked-for transfer.
        var ledger = new FakeLedger(available: 500m);
        var instruction = new DebitInstruction(Debtor, 120m, "EUR");

        await new PostDebit(ledger).ExecuteAsync(
            instruction, ContextFor("ledger.post_debit"), TestContext.Current.CancellationToken);

        var reverse = new ReverseDebit(ledger);

        await reverse.ExecuteAsync(
            instruction, ContextFor("ledger.reverse_debit"), TestContext.Current.CancellationToken);

        await reverse.ExecuteAsync(
            instruction, ContextFor("ledger.reverse_debit"), TestContext.Current.CancellationToken);

        ledger.Posted.ShouldBe([-120m, 120m]);
    }

    [Fact]
    public async Task ReversingACreditPostsAContraEntry()
    {
        var ledger = new FakeLedger(available: 500m);
        var instruction = new CreditInstruction(Creditor, 120m, "EUR");

        await new PostCredit(ledger).ExecuteAsync(
            instruction, ContextFor("ledger.post_credit"), TestContext.Current.CancellationToken);

        var reversed = await new ReverseCredit(ledger).ExecuteAsync(
            instruction, ContextFor("ledger.reverse_credit"), TestContext.Current.CancellationToken);

        reversed.IsSuccess.ShouldBeTrue();
        ledger.Posted.ShouldBe([120m, -120m]);
    }

    [Fact]
    public async Task RecordingASettlementUsesTheContextClock()
    {
        // ctx.UtcNow, never DateTimeOffset.UtcNow. Under a Durable profile that is
        // FLOWX1007 — an error, not a warning — because a replayed instance must record the
        // instant the transfer settled rather than the instant it was replayed. The test
        // context pins the clock to the Unix epoch, which is what makes this observable.
        var register = new InMemorySettlementRegister();

        var result = await new RecordSettlement(register).ExecuteAsync(
            new SettlementInstruction("transfer-1", "debit-1", "credit-1", 120m, "EUR"),
            ContextFor("settlement.record"),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.RecordedAt.ShouldBe(DateTimeOffset.UnixEpoch);
        register.Count.ShouldBe(1);
    }

    /// <summary>
    /// An undo keyed on the context would be keyed identically to the write it undoes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The engine sets <c>ctx.CapabilityId</c> from <c>step.Capability.Id</c> before
    /// dispatching a compensation, so inside <see cref="ReverseDebit"/> the context reports
    /// <c>ledger.post_debit</c>. The first argument below is that context, and the two keys
    /// are what a reversal would use with and without <see cref="LedgerKeys.ForUndo"/>.
    /// </para>
    /// <para>
    /// This is pinned as its own assertion because the failure it prevents is silent: a
    /// ledger deduplicating the contra entry against the debit returns the debit's entry, the
    /// compensation reports success, and the engine reports
    /// <c>CompensationOutcome.Succeeded</c> over money that never came back.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnUndoKeyedOnTheContextWouldCollideWithTheWriteItUndoes()
    {
        var duringTheUnwind = ContextFor("ledger.post_debit");

        LedgerKeys.For(duringTheUnwind).ShouldBe("idem-1:ledger.post_debit");

        LedgerKeys.ForUndo(duringTheUnwind, "ledger.reverse_debit")
            .ShouldBe("idem-1:ledger.reverse_debit");
    }

    [Fact]
    public void EveryCapabilityRejectsANullDependency()
    {
        Should.Throw<ArgumentNullException>(() => new ValidateTransfer(null!));
        Should.Throw<ArgumentNullException>(() => new ScreenSanctions(null!));
        Should.Throw<ArgumentNullException>(() => new PostDebit(null!));
        Should.Throw<ArgumentNullException>(() => new PostCredit(null!));
        Should.Throw<ArgumentNullException>(() => new ReverseDebit(null!));
        Should.Throw<ArgumentNullException>(() => new ReverseCredit(null!));
        Should.Throw<ArgumentNullException>(() => new RecordSettlement(null!));
    }

    private static ValidatedTransfer Validated(decimal amount) =>
        new(Debtor, Creditor, amount, "EUR", TransferChannel.Sepa, AvailableBalance: 500m);

    /// <summary>A ledger that records what it was asked to do, and under which key.</summary>
    private sealed class FakeLedger(decimal? available, bool closed = false) : ILedger
    {
        private readonly Dictionary<string, LedgerEntry> _entries = new(StringComparer.Ordinal);

        public List<decimal> Posted { get; } = [];

        public List<string> Keys { get; } = [];

        public ValueTask<decimal?> AvailableAsync(string iban, CancellationToken ct) =>
            ValueTask.FromResult(available);

        public ValueTask<bool> IsClosedAsync(string iban, CancellationToken ct) =>
            ValueTask.FromResult(closed);

        public ValueTask<LedgerEntry> PostAsync(
            string iban, decimal signedAmount, string currency, string key, CancellationToken ct)
        {
            Keys.Add(key);

            if (_entries.TryGetValue(key, out var existing))
            {
                return ValueTask.FromResult(existing);
            }

            Posted.Add(signedAmount);

            var entry = new LedgerEntry(key, iban, signedAmount, currency);
            _entries[key] = entry;

            return ValueTask.FromResult(entry);
        }
    }

    private sealed class FakeScreening(string? listed) : ISanctionsScreening
    {
        public ValueTask<string?> MatchAsync(string iban, CancellationToken ct) =>
            ValueTask.FromResult(string.Equals(iban, listed, StringComparison.Ordinal) ? "OFAC-SDN" : null);
    }
}
