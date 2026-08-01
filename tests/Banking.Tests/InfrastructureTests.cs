using Banking;
using Shouldly;
using Xunit;

namespace Banking.Tests;

/// <summary>
/// The sample's in-memory adapters.
/// </summary>
/// <remarks>
/// These are the sample's infrastructure, not its story — but the ledger carries the
/// behaviour that makes every idempotency claim in the README true, and a claim demonstrated
/// only by a <c>curl</c> in a terminal is a claim nothing protects.
/// </remarks>
public sealed class InfrastructureTests
{
    private const string Debtor = "GB33BUKB20201555555555";
    private const string Creditor = "DE89370400440532013000";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PostingMovesTheBalance()
    {
        var ledger = new InMemoryLedger();

        await ledger.PostAsync(Debtor, -120m, "EUR", "k-1", Cancellation);

        (await ledger.AvailableAsync(Debtor, Cancellation)).ShouldBe(380m);
    }

    [Fact]
    public async Task PostingTwiceUnderOneKeyMovesOnce()
    {
        // What Idempotent = true on the posting capabilities promises, and what makes a
        // client retrying on a timeout safe. Without it, the retry is a second transfer.
        var ledger = new InMemoryLedger();

        var first = await ledger.PostAsync(Debtor, -120m, "EUR", "k-1", Cancellation);
        var second = await ledger.PostAsync(Debtor, -120m, "EUR", "k-1", Cancellation);

        second.ShouldBe(first);
        (await ledger.AvailableAsync(Debtor, Cancellation)).ShouldBe(380m);
    }

    [Fact]
    public async Task DifferentKeysMoveSeparately()
    {
        var ledger = new InMemoryLedger();

        await ledger.PostAsync(Debtor, -120m, "EUR", "k-1", Cancellation);
        await ledger.PostAsync(Debtor, -80m, "EUR", "k-2", Cancellation);

        (await ledger.AvailableAsync(Debtor, Cancellation)).ShouldBe(300m);
    }

    /// <summary>
    /// A contra entry is a second write, not the removal of the first.
    /// </summary>
    /// <remarks>
    /// Both entries stay in the ledger, which is what an append-only ledger means and what
    /// makes the reversal auditable. A store that deleted the debit would leave a balance
    /// that is right and a history that is a lie.
    /// </remarks>
    [Fact]
    public async Task AContraEntryRestoresTheBalanceAndKeepsBothWrites()
    {
        var ledger = new InMemoryLedger();

        await ledger.PostAsync(Debtor, -120m, "EUR", "k-1:post", Cancellation);
        await ledger.PostAsync(Debtor, 120m, "EUR", "k-1:reverse", Cancellation);

        (await ledger.AvailableAsync(Debtor, Cancellation)).ShouldBe(500m);
        ledger.Keys.ShouldBe(["k-1:post", "k-1:reverse"]);
    }

    [Fact]
    public async Task AnAccountTheLedgerDoesNotHoldHasNoBalance()
    {
        var ledger = new InMemoryLedger();

        // Null, not zero. "This bank does not hold that account" and "that account is empty"
        // are different answers, and only one of them is a reason to refuse the transfer.
        (await ledger.AvailableAsync("XX00NOSUCHACCOUNT0000000", Cancellation)).ShouldBeNull();
        (await ledger.AvailableAsync(Creditor, Cancellation)).ShouldBe(0m);
    }

    [Fact]
    public async Task ScreeningStopsTheListedAccountAndNothingElse()
    {
        var screening = new InMemorySanctionsScreening();

        (await screening.MatchAsync("IR580570022080010674200001", Cancellation)).ShouldBe("OFAC-SDN");
        (await screening.MatchAsync(Creditor, Cancellation)).ShouldBeNull();
    }

    [Fact]
    public async Task TheDirectoryRoutesByCountryAndAdmitsWhenItCannot()
    {
        var directory = new InMemoryCorrespondentDirectory();

        (await directory.ForAsync("DE", Cancellation))!.Bic.ShouldBe("DEUTDEFFXXX");

        // Null rather than a default correspondent: routing a cross-border payment through a
        // bank nobody chose is worse than refusing it.
        (await directory.ForAsync("ZZ", Cancellation)).ShouldBeNull();
    }

    [Fact]
    public async Task TheRegisterRecordsATransferOncePerId()
    {
        var register = new InMemorySettlementRegister();
        var settlement = new Settlement("t-1", DateTimeOffset.UnixEpoch);
        var instruction = new SettlementInstruction("t-1", "d-1", "c-1", 120m, "EUR");

        await register.RecordAsync(settlement, instruction, Cancellation);
        await register.RecordAsync(settlement, instruction, Cancellation);

        register.Count.ShouldBe(1);
    }
}
