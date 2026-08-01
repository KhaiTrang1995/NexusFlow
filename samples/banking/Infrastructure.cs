using System.Globalization;
using System.Text.Json.Serialization;

namespace Banking;

/// <summary>Source-generated serialisation for everything this application writes down.</summary>
/// <remarks>
/// <para>
/// Four groups of contracts, and they are here for four different reasons. The flow's
/// input and output go on the wire, so the endpoint needs them. <see cref="TransferCompleted"/>
/// goes into the outbox, and the generated <c>DescribeStep</c> will not compile without a
/// context declaring it — <c>JournalPayload.Of</c> takes a <c>JsonTypeInfo&lt;T&gt;</c> and
/// has no overload that reflects over a type, which is what keeps the write path trim- and
/// NativeAOT-safe (ADR-0008, ADR-0015 commitment 5). Exactly one context in the compilation
/// may declare a given event contract; two would make the body's shape depend on file order,
/// and the compiler reports FLOWX1024 rather than choosing.
/// </para>
/// <para>
/// <strong>The fourth group is every step result, and it is <c>FLOWX1006</c> that put it
/// here.</strong> This flow is <c>Durable</c>, so its journal records each step's result and
/// the state bag after it, and each of those needs the same generated metadata for the same
/// reason. Before WP-59 the list stopped at the event and the journal recorded no payloads at
/// all: the step rows were truthful about <em>which</em> steps had run and silent about what
/// they produced, so a resumed instance re-entered with an empty bag. Deleting a line below
/// does not make the flow cheaper — it makes the build fail and names the line.
/// </para>
/// <para>
/// The camelCase policy is not decoration. Without it the wire names are the C# ones, and a
/// client sending the conventional <c>"amount"</c> gets an <c>Amount</c> of zero rather than
/// an error — a missing member deserialises to <c>default</c>. Here <c>ValidateTransfer</c>
/// rejects a zero amount, which is the point of validating at the first step; a field whose
/// default is plausible would have gone straight through.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ExecuteTransfer))]
[JsonSerializable(typeof(TransferResult))]
[JsonSerializable(typeof(TransferCompleted))]
[JsonSerializable(typeof(ValidatedTransfer))]
[JsonSerializable(typeof(ScreeningDecision))]
[JsonSerializable(typeof(CorrespondentRoute))]
[JsonSerializable(typeof(DebitPosted))]
[JsonSerializable(typeof(CreditPosted))]
[JsonSerializable(typeof(Settlement))]
internal sealed partial class BankingJsonContext : JsonSerializerContext;

/// <summary>
/// The core ledger, in memory.
/// </summary>
/// <remarks>
/// <para>
/// The capabilities depend on <see cref="ILedger"/>, not on this. Swapping in a real core
/// banking system changes this file and nothing else, which is the point of keeping
/// infrastructure out of the capability.
/// </para>
/// <para>
/// <strong>Two dictionaries, and the second one is the whole idempotency story.</strong>
/// <c>_entries</c> is keyed by the key the capability presented, so a second write under the
/// same key returns the first write's entry and moves nothing. That is what
/// <c>Idempotent = true</c> on <see cref="PostDebit"/> and <see cref="PostCredit"/> promises,
/// and it is what a real ledger's own deduplication would have to provide — the capability
/// passes the key downstream rather than deduplicating in process, because the process is
/// not the thing that survives.
/// </para>
/// </remarks>
internal sealed class InMemoryLedger : ILedger
{
    private readonly Dictionary<string, decimal> _balances =
        new(StringComparer.Ordinal)
        {
            ["GB33BUKB20201555555555"] = 500m,
            ["GB94BARC10201530093459"] = 25_000m,
            ["DE89370400440532013000"] = 0m,
            ["FR1420041010050500013M02606"] = 0m,
            ["US64SVBKUS6S3300958879"] = 0m,

            // Held, and closed. Crediting it is refused after the debit has been posted,
            // which is the one way to reach this sample's unwind from a live request.
            ["FR7630006000011234567890189"] = 0m,
        };

    private static readonly string[] Closed = ["FR7630006000011234567890189"];

    private readonly Dictionary<string, LedgerEntry> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _keys = [];
    private readonly Lock _sync = new();

    /// <summary>The keys this ledger has been asked to write under, in order.</summary>
    /// <remarks>
    /// Here so a test can assert that two writes of one transfer are two writes. A ledger
    /// that deduplicated a contra entry against the entry it offsets would report success
    /// and move nothing, and no balance assertion elsewhere would say which of the two
    /// keys was wrong.
    /// </remarks>
    public IReadOnlyList<string> Keys
    {
        get
        {
            lock (_sync)
            {
                return [.. _keys];
            }
        }
    }

    public ValueTask<decimal?> AvailableAsync(string iban, CancellationToken ct)
    {
        lock (_sync)
        {
            return ValueTask.FromResult<decimal?>(
                _balances.TryGetValue(iban, out var balance) ? balance : null);
        }
    }

    public ValueTask<bool> IsClosedAsync(string iban, CancellationToken ct) =>
        ValueTask.FromResult(Array.IndexOf(Closed, iban) >= 0);

    public ValueTask<LedgerEntry> PostAsync(
        string iban, decimal signedAmount, string currency, string key, CancellationToken ct)
    {
        lock (_sync)
        {
            _keys.Add(key);

            // Keyed on what the capability presented, so the same step replayed posts once.
            if (_entries.TryGetValue(key, out var existing))
            {
                return ValueTask.FromResult(existing);
            }

            _balances[iban] = _balances.GetValueOrDefault(iban) + signedAmount;

            var entry = new LedgerEntry(key, iban, signedAmount, currency);
            _entries[key] = entry;

            return ValueTask.FromResult(entry);
        }
    }
}

/// <summary>A screening provider that stops one well-known account. Enough for a sample.</summary>
internal sealed class InMemorySanctionsScreening : ISanctionsScreening
{
    private const string Listed = "IR580570022080010674200001";

    public ValueTask<string?> MatchAsync(string iban, CancellationToken ct) =>
        ValueTask.FromResult(string.Equals(iban, Listed, StringComparison.Ordinal) ? "OFAC-SDN" : null);
}

/// <summary>A correspondent directory covering the countries this sample's accounts are in.</summary>
internal sealed class InMemoryCorrespondentDirectory : ICorrespondentDirectory
{
    private readonly Dictionary<string, CorrespondentRoute> _routes = new(StringComparer.Ordinal)
    {
        ["DE"] = new CorrespondentRoute("DEUTDEFFXXX", "Deutsche Bank AG"),
        ["FR"] = new CorrespondentRoute("BNPAFRPPXXX", "BNP Paribas"),
        ["US"] = new CorrespondentRoute("CHASUS33XXX", "JPMorgan Chase"),
        ["GB"] = new CorrespondentRoute("BARCGB22XXX", "Barclays Bank"),
    };

    public ValueTask<CorrespondentRoute?> ForAsync(string country, CancellationToken ct) =>
        ValueTask.FromResult(_routes.GetValueOrDefault(country));
}

/// <summary>The payments register, in memory.</summary>
internal sealed class InMemorySettlementRegister : ISettlementRegister
{
    private readonly Dictionary<string, string> _records = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    /// <summary>How many transfers the register holds.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _records.Count;
            }
        }
    }

    public ValueTask RecordAsync(
        Settlement settlement, SettlementInstruction instruction, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        ArgumentNullException.ThrowIfNull(instruction);

        lock (_sync)
        {
            // Once per transfer id, so a replayed instance records the transfer it already
            // recorded rather than a second one.
            _records[settlement.TransferId] = string.Create(
                CultureInfo.InvariantCulture,
                $"{instruction.DebitEntryId}|{instruction.CreditEntryId}|{instruction.Amount}");
        }

        return ValueTask.CompletedTask;
    }
}
