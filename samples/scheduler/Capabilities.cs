using FlowX;

namespace Scheduler;

/// <summary>
/// Reads the ledger for the day the occurrence closed.
/// </summary>
/// <remarks>
/// <strong>It binds <see cref="ScheduledFire"/> and never a clock.</strong> The capability is
/// the layer allowed to reach the outside world, and the one piece of the outside world it may
/// not reach is the current time (<c>FLOWX1007</c>). The instant it works from is data,
/// journalled on <c>flow_instance.input</c>, so a firing at 02:41 reads the same day a firing
/// at 02:00 would have, and a resumed instance reads the same day the first attempt did.
/// </remarks>
[Capability("reconciliation.ledger.read", Version = "1.0.0",
    Authorization = Authorization.Internal, Idempotent = true)]
public sealed class LoadLedgerSnapshot : ICapability<ScheduledFire, LedgerSnapshot>
{
    private readonly ILedger _ledger;

    /// <summary>Creates the capability over the ledger.</summary>
    /// <param name="ledger">Where the day's entries come from.</param>
    public LoadLedgerSnapshot(ILedger ledger) => _ledger = ledger;

    /// <inheritdoc />
    public ValueTask<Result<LedgerSnapshot>> ExecuteAsync(
        ScheduledFire input, CapabilityContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        return ValueTask.FromResult(Result.Ok(new LedgerSnapshot(
            input.OccurrenceAt,
            _ledger.EntriesFor(ctx.TenantId, input.OccurrenceAt))));
    }
}

/// <summary>
/// Reads the bank statement for the same day.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The slow step, and the one the sample's overlap demonstration runs through.</strong>
/// It carries <see cref="Policies.ExternalRead"/>, so it is bounded, retried on
/// <see cref="ErrorCategory.Unavailable"/>, and taken out of circuit when the bank is down —
/// and it is the step that can make a night's run outlast the next night's occurrence.
/// </para>
/// <para>
/// <c>Idempotent = true</c> is what makes the retry legal at all: <c>FLOWX1014</c> refuses a
/// retry on a capability that does not declare it, and reading a statement twice is genuinely
/// free.
/// </para>
/// </remarks>
[Capability("reconciliation.statement.read", Version = "1.0.0",
    Authorization = Authorization.Internal, Idempotent = true)]
public sealed class LoadBankStatement : ICapability<ScheduledFire, BankStatement>
{
    private readonly IBank _bank;

    /// <summary>Creates the capability over the bank.</summary>
    /// <param name="bank">Where the statement comes from.</param>
    public LoadBankStatement(IBank bank) => _bank = bank;

    /// <inheritdoc />
    public async ValueTask<Result<BankStatement>> ExecuteAsync(
        ScheduledFire input, CapabilityContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var lines = await _bank
            .StatementAsync(ctx.TenantId, input.OccurrenceAt, ct)
            .ConfigureAwait(false);

        return lines.IsFailure
            ? Result.Fail<BankStatement>(lines.Error)
            : Result.Ok(new BankStatement(input.OccurrenceAt, lines.Value));
    }
}

/// <summary>
/// Matches ledger entries the bank echoed by reference, which is the cheap and certain half.
/// </summary>
/// <remarks>
/// One branch of the fork. It writes <see cref="ReferenceMatches"/> and its sibling writes
/// <see cref="HeuristicMatches"/>; two branches writing one contract would be two threads
/// writing one slot of the state bag, and <c>FLOWX1013</c> refuses that at build time.
/// </remarks>
[Capability("reconciliation.match.reference", Version = "1.0.0",
    Authorization = Authorization.Internal, Idempotent = true)]
public sealed class MatchByReference : ICapability<Reconcilable, ReferenceMatches>
{
    /// <inheritdoc />
    public ValueTask<Result<ReferenceMatches>> ExecuteAsync(
        Reconcilable input, CapabilityContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var referenced = input.Statement.Lines
            .Where(static line => line.Reference.Length > 0)
            .ToLookup(static line => line.Reference, StringComparer.Ordinal);

        var matched = input.Ledger.Entries
            .Where(entry => referenced[entry.Reference].Any(line => line.Minor == entry.Minor))
            .Select(static entry => entry.Reference)
            .ToList();

        return ValueTask.FromResult(Result.Ok(new ReferenceMatches(matched)));
    }
}

/// <summary>
/// Matches what is left on amount and value date, which is the expensive and fallible half.
/// </summary>
/// <remarks>
/// Runs beside <see cref="MatchByReference"/> rather than after it, and the merge is
/// <c>AllSettled</c> — so a matcher that fails leaves the other's answer readable and the
/// report says how much of the night's work was done. <c>AllMustSucceed</c> would throw away a
/// correct exact-reference match because a heuristic went wrong.
/// </remarks>
[Capability("reconciliation.match.heuristic", Version = "1.0.0",
    Authorization = Authorization.Internal, Idempotent = true)]
public sealed class MatchByAmountAndDate : ICapability<Reconcilable, HeuristicMatches>
{
    /// <inheritdoc />
    public ValueTask<Result<HeuristicMatches>> ExecuteAsync(
        Reconcilable input, CapabilityContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var unreferenced = input.Statement.Lines
            .Where(static line => line.Reference.Length == 0)
            .ToList();

        var matched = input.Ledger.Entries
            .Where(entry => unreferenced.Any(line =>
                line.Minor == entry.Minor && line.ValuedAt.Date == entry.BookedAt.Date))
            .Select(static entry => entry.Reference)
            .ToList();

        return ValueTask.FromResult(Result.Ok(new HeuristicMatches(matched)));
    }
}

/// <summary>
/// Turns both matchers' answers into the one number the job exists to produce.
/// </summary>
/// <remarks>
/// The tenant on the report comes from the context rather than from a parameter, because the
/// fan-out gave this instance its tenant at admission and the platform attested it
/// (<c>docs/adr/ADR-0054-a-platform-trigger-attests-its-tenant.md</c>). A capability that took
/// it as data would be trusting a value some earlier step put in the bag.
/// </remarks>
[Capability("reconciliation.report", Version = "1.0.0",
    Authorization = Authorization.Internal, Idempotent = true)]
public sealed class ProduceReport : ICapability<MatchSet, ReconciliationReport>
{
    private readonly IReportDesk _desk;

    /// <summary>Creates the capability over the report desk.</summary>
    /// <param name="desk">Where a finished report is filed.</param>
    public ProduceReport(IReportDesk desk) => _desk = desk;

    /// <inheritdoc />
    public ValueTask<Result<ReconciliationReport>> ExecuteAsync(
        MatchSet input, CapabilityContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var matched = new HashSet<string>(input.ByReference.Matched, StringComparer.Ordinal);

        matched.UnionWith(input.ByAmountAndDate.Matched);

        var report = new ReconciliationReport(
            input.Ledger.ClosedAt,
            ctx.TenantId,
            input.Ledger.Entries.Count,
            input.ByReference.Matched.Count,
            input.ByAmountAndDate.Matched.Count,
            input.Ledger.Entries.Count(entry => !matched.Contains(entry.Reference)));

        _desk.File(report);

        return ValueTask.FromResult(Result.Ok(report));
    }
}
