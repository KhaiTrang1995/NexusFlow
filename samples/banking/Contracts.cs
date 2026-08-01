using FlowX;

namespace Banking;

/// <summary>How the money leaves this bank, which is what decides who has to look at it.</summary>
/// <remarks>
/// The flow switches on this. It is a property of the transfer rather than of the
/// transport, which is why it is on the contract and not read from a header — a header
/// would make the same transfer mean different things over HTTP and over a queue.
/// </remarks>
public enum TransferChannel
{
    /// <summary>Both accounts are held here. The money never leaves the bank.</summary>
    Book = 0,

    /// <summary>Euro payment to another bank in the single euro payments area.</summary>
    Sepa = 1,

    /// <summary>Cross-border, through a correspondent bank.</summary>
    Swift = 2,
}

/// <summary>What a caller asks for.</summary>
/// <param name="DebtorIban">The account the money leaves. <strong>Sensitive.</strong></param>
/// <param name="CreditorIban">The account the money arrives at. <strong>Sensitive.</strong></param>
/// <param name="Amount">How much, in <paramref name="Currency"/>. Must be positive.</param>
/// <param name="Currency">ISO 4217 code.</param>
/// <param name="Channel">How the money leaves the bank.</param>
/// <remarks>
/// The two account numbers carry <c>[Sensitive]</c>, and that is the declaration the whole
/// redaction story in this sample hangs on: the compiler collects them into
/// <c>ExecuteTransferFlow.SensitiveMembers</c>, and every <see cref="JournalPayload"/> the
/// generated dispatcher builds is given that array. See
/// <see cref="TransferCompleted"/> for the one place it does visible work.
/// </remarks>
public sealed record ExecuteTransfer(
    [property: Sensitive] string DebtorIban,
    [property: Sensitive] string CreditorIban,
    decimal Amount,
    string Currency,
    TransferChannel Channel);

/// <summary>A transfer that is well formed, with the debtor's balance as the ledger reported it.</summary>
/// <remarks>
/// <see cref="AvailableBalance"/> is carried rather than re-read because the decision that
/// consumes it is a flow arm, and a flow arm may read only the context, the input and prior
/// step results (FLOWX1011). A predicate that called the ledger would take a different
/// branch on replay than it took the first time.
/// </remarks>
public sealed record ValidatedTransfer(
    string DebtorIban,
    string CreditorIban,
    decimal Amount,
    string Currency,
    TransferChannel Channel,
    decimal AvailableBalance);

/// <summary>The sanctions screening that cleared the counterparty.</summary>
/// <param name="Reference">
/// What the screening provider recorded the decision under. Derived from the flow's
/// idempotency key, so a replay quotes the same decision rather than asking again.
/// </param>
public sealed record ScreeningDecision(string Reference);

/// <summary>The correspondent bank a cross-border transfer is routed through.</summary>
public sealed record CorrespondentRoute(string Bic, string Name);

/// <summary>Take this much out of this account.</summary>
public sealed record DebitInstruction([property: Sensitive] string DebtorIban, decimal Amount, string Currency);

/// <summary>Put this much into this account.</summary>
public sealed record CreditInstruction([property: Sensitive] string CreditorIban, decimal Amount, string Currency);

/// <summary>A debit that is in the ledger.</summary>
public sealed record DebitPosted(string EntryId, decimal Amount);

/// <summary>A credit that is in the ledger.</summary>
public sealed record CreditPosted(string EntryId, decimal Amount);

/// <summary>The contra entry that backed a debit out.</summary>
/// <remarks>
/// A ledger is append-only, so an undo is a second entry rather than the removal of the
/// first. <see cref="EntryId"/> names the contra entry, not the entry it offsets.
/// </remarks>
public sealed record DebitReversed(string EntryId);

/// <summary>The contra entry that backed a credit out.</summary>
public sealed record CreditReversed(string EntryId);

/// <summary>Record this settled transfer in the payments register.</summary>
/// <remarks>
/// The one step whose input is built from more than one prior result, which is what
/// <c>.Step&lt;TCapability, TStepIn&gt;(map)</c> exists for: nothing in the flow produces a
/// <see cref="SettlementInstruction"/>, because it is the join of the validated transfer
/// and both ledger legs.
/// </remarks>
public sealed record SettlementInstruction(
    string TransferId,
    string DebitEntryId,
    string CreditEntryId,
    decimal Amount,
    string Currency);

/// <summary>A transfer the payments register has accepted.</summary>
public sealed record Settlement(string TransferId, DateTimeOffset RecordedAt);

/// <summary>What the caller gets back.</summary>
/// <remarks>
/// There is no status field. A settled transfer is a <c>200</c> carrying the two ledger
/// references; anything else is an RFC 7807 body whose <c>code</c> says which business
/// outcome it was. A <c>"status": "Settled"</c> that could only ever hold one value would
/// be a field pretending to carry information.
/// </remarks>
public sealed record TransferResult(
    string TransferId,
    string DebitEntryId,
    string CreditEntryId,
    decimal Amount,
    string Currency);

/// <summary>Published once both legs are in the ledger.</summary>
/// <remarks>
/// <para>
/// <strong>The account numbers are redacted out of this before it leaves the process, and
/// the mechanism is worth stating exactly.</strong> The generated <c>DescribeStep</c> wraps
/// this body in a <see cref="JournalPayload"/> carrying
/// <c>ExecuteTransferFlow.SensitiveMembers</c> — which is read off
/// <see cref="ExecuteTransfer"/> and <see cref="TransferResult"/>, the flow's own input and
/// output contracts, and <em>not</em> off this record. Redaction then matches by property
/// name, case-insensitively, at every depth. So these two members are stripped because they
/// are spelled the same as the marked members of the flow's input.
/// </para>
/// <para>
/// The <c>[Sensitive]</c> markers below therefore reach nothing at run time; they are here
/// so a reader of this file is not left to reconstruct that chain, and
/// <c>TransferJournalTests.TheEmittedEventCarriesNoAccountNumber</c> is what makes the
/// coupling break loudly if a member is ever renamed on one side only.
/// </para>
/// </remarks>
public sealed record TransferCompleted(
    string TransferId,
    [property: Sensitive] string DebtorIban,
    [property: Sensitive] string CreditorIban,
    decimal Amount,
    string Currency);

/// <summary>One line in the ledger.</summary>
/// <param name="EntryId">The ledger's own reference. Stable for a given idempotency key.</param>
/// <param name="Iban">The account it moved.</param>
/// <param name="SignedAmount">Negative for a debit, positive for a credit.</param>
/// <param name="Currency">ISO 4217 code.</param>
public sealed record LedgerEntry(string EntryId, string Iban, decimal SignedAmount, string Currency);
