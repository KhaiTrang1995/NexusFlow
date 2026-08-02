using FlowX;

namespace Scheduler;

/// <summary>
/// The ledger as it stood at the end of the day the occurrence names.
/// </summary>
/// <param name="ClosedAt">
/// The instant the day closed, which is the occurrence and never a clock reading. Carried so
/// that every later step works from the same cut-off the first one used, on the first attempt
/// and on a resumed one.
/// </param>
/// <param name="Entries">What the ledger recorded, oldest first.</param>
public sealed record LedgerSnapshot(DateTimeOffset ClosedAt, IReadOnlyList<LedgerEntry> Entries);

/// <summary>One line in the ledger.</summary>
/// <param name="Reference">The payment reference the bank is expected to echo.</param>
/// <param name="Minor">The amount, in minor units, so nothing here is a floating-point number.</param>
/// <param name="BookedAt">When the entry was booked.</param>
public sealed record LedgerEntry(string Reference, long Minor, DateTimeOffset BookedAt);

/// <summary>What the bank says happened, for the same day.</summary>
/// <param name="ClosedAt">The occurrence, for <see cref="LedgerSnapshot.ClosedAt"/>'s reason.</param>
/// <param name="Lines">The statement lines, oldest first.</param>
public sealed record BankStatement(DateTimeOffset ClosedAt, IReadOnlyList<StatementLine> Lines);

/// <summary>One line on the bank statement.</summary>
/// <param name="Reference">
/// The reference the bank echoed, which is empty when it echoed nothing — the case the second
/// matcher exists for.
/// </param>
/// <param name="Minor">The amount, in minor units.</param>
/// <param name="ValuedAt">The value date.</param>
public sealed record StatementLine(string Reference, long Minor, DateTimeOffset ValuedAt);

/// <summary>Both sides of the reconciliation, as one matcher binds them.</summary>
/// <param name="Ledger">What we booked.</param>
/// <param name="Statement">What the bank says.</param>
/// <remarks>
/// A contract rather than two arguments, because a capability takes exactly one input and the
/// two loads that produce these run as separate steps. It is built by a mapping lambda on the
/// branch's step, which reads only the context — the rule <c>FLOWX1011</c> enforces.
/// </remarks>
public sealed record Reconcilable(LedgerSnapshot Ledger, BankStatement Statement);

/// <summary>What the exact-reference matcher found.</summary>
/// <param name="Matched">Ledger references the bank echoed exactly, with the amount agreeing.</param>
/// <remarks>
/// A distinct contract from <see cref="HeuristicMatches"/> and not a shared one, because the
/// state bag is keyed by type: two branches of a <c>Parallel</c> writing one contract would be
/// two threads writing one slot, and <c>FLOWX1013</c> refuses it at build time.
/// </remarks>
public sealed record ReferenceMatches(IReadOnlyList<string> Matched);

/// <summary>What the amount-and-date matcher found among the lines the bank did not reference.</summary>
/// <param name="Matched">Ledger references matched on amount and value date instead.</param>
public sealed record HeuristicMatches(IReadOnlyList<string> Matched);

/// <summary>Both matchers' answers, as the report step binds them.</summary>
/// <param name="Ledger">What we booked, for the totals.</param>
/// <param name="ByReference">The exact matches.</param>
/// <param name="ByAmountAndDate">The heuristic ones.</param>
public sealed record MatchSet(
    LedgerSnapshot Ledger, ReferenceMatches ByReference, HeuristicMatches ByAmountAndDate);

/// <summary>What one night's reconciliation concluded.</summary>
/// <param name="OccurrenceAt">
/// The instant the expression named, carried out of <see cref="ScheduledFire"/> and through
/// every step. It is what tells one night's run from the next, and what tells a run that
/// happened at 02:41 which day it was reconciling.
/// </param>
/// <param name="TenantId">
/// Whose books these are, or null on a deployment that serves one. The fan-out gives each
/// tenant its own instance, so this is a fact about the instance rather than a loop variable.
/// </param>
/// <param name="Booked">How many ledger entries were in scope.</param>
/// <param name="MatchedByReference">How many the bank referenced exactly.</param>
/// <param name="MatchedByAmountAndDate">How many were matched the harder way.</param>
/// <param name="Unmatched">How many are left for a human. The number the job exists to produce.</param>
public sealed record ReconciliationReport(
    DateTimeOffset OccurrenceAt,
    string? TenantId,
    int Booked,
    int MatchedByReference,
    int MatchedByAmountAndDate,
    int Unmatched);

/// <summary>
/// The event the flow stages when a night's reconciliation is done.
/// </summary>
/// <param name="OccurrenceAt">Which night.</param>
/// <param name="TenantId">Whose books.</param>
/// <param name="Unmatched">What is left over, which is what a subscriber acts on.</param>
/// <remarks>
/// Staged in the same transaction as the step row that produced it, so a subscriber never sees
/// a reconciliation that the journal does not also record — and never misses one it does.
/// </remarks>
public sealed record ReconciliationCompleted(
    DateTimeOffset OccurrenceAt, string? TenantId, int Unmatched);
