using FlowX;

namespace Banking;

/// <summary>Errors this application can produce.</summary>
/// <remarks>
/// <para>
/// Declared in one place so the codes are greppable and so two capabilities cannot invent
/// two spellings of the same condition. Each one reaches the manifest, the generated
/// OpenAPI responses and the RFC 7807 <c>type</c> URI.
/// </para>
/// <para>
/// <strong>No message here interpolates an account number.</strong> An <c>Error</c> travels
/// to the caller as a Problem Details body, and the endpoint's redaction covers structured
/// detail attached with <c>.With(...)</c> — not the free-text <c>detail</c> string. So the
/// account is kept out of the message rather than trusted to be stripped from it.
/// </para>
/// </remarks>
public static class TransferErrors
{
    /// <summary>The amount is not a positive quantity of money.</summary>
    public static Error InvalidAmount(decimal amount) =>
        new Error(
            "transfer.invalid_amount",
            $"The amount must be positive; got {amount}.",
            ErrorCategory.Validation)
            .With("amount", amount);

    /// <summary>The debtor account is not held here.</summary>
    /// <remarks>
    /// Carries no detail at all. Which account was not found is exactly the fact an
    /// unauthenticated prober would use this endpoint to enumerate, and the caller who
    /// legitimately owns the account already knows which one they sent.
    /// </remarks>
    public static Error AccountUnknown() =>
        new Error(
            "ledger.account_unknown",
            "The debtor account is not held at this bank.",
            ErrorCategory.NotFound);

    /// <summary>The creditor's account has been closed and cannot be credited.</summary>
    /// <remarks>
    /// <c>Conflict</c>, and it is the failure that makes this sample's saga do its job at run
    /// time: the debit has already been posted when the credit is refused, so the flow unwinds
    /// and the contra entry puts the money back. Every other failure path in the flow happens
    /// before the first ledger write.
    /// </remarks>
    public static Error CreditorAccountClosed() =>
        new Error(
            "ledger.account_closed",
            "The beneficiary account has been closed and cannot be credited.",
            ErrorCategory.Conflict);

    /// <summary>The counterparty is on a sanctions list.</summary>
    public static Error SanctionsHit(string list) =>
        new Error(
            "compliance.sanctions_hit",
            "The counterparty matched a sanctions list; the transfer cannot be made.",
            ErrorCategory.Forbidden)
            .With("list", list);

    /// <summary>No correspondent bank serves the creditor's country.</summary>
    public static Error NoCorrespondent(string country) =>
        new Error(
            "correspondent.not_found",
            $"No correspondent bank is configured for '{country}'.",
            ErrorCategory.NotFound)
            .With("country", country);

    /// <summary>
    /// The debtor cannot cover the transfer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>static readonly</c> field rather than a factory, because it is the argument to
    /// <c>.Fail(...)</c> in the flow and that is a <em>declaration</em>: the compiler copies
    /// the expression into a field of the generated partial class, evaluated once at type
    /// initialisation. A factory taking the shortfall would be evaluated at the same moment
    /// — before any transfer exists — so the number in it would be a lie.
    /// </para>
    /// <para>
    /// <c>Conflict</c>, so the endpoint answers <c>409</c>. The request is well formed
    /// (which would be <c>400</c>) and the account exists (which would be <c>404</c>); it
    /// conflicts with the balance as it stands, and a caller who pays money in may retry the
    /// identical request successfully. ADR-0007's point exactly: this is an outcome, not an
    /// exception.
    /// </para>
    /// </remarks>
    public static readonly Error InsufficientFunds = new(
        "transfer.insufficient_funds",
        "The debtor account does not have enough available balance for this transfer.",
        ErrorCategory.Conflict);
}

/// <summary>Checks the transfer is well formed and reads the debtor's available balance.</summary>
/// <remarks>
/// A read. No side effects, safe to retry, and — because the class references no transport
/// type and calls no other capability — testable by constructing it and calling the method.
/// </remarks>
[Capability("transfer.validate", Version = "1.0.0",
    Authorization = Authorization.Authenticated,
    Idempotent = true)]
public sealed class ValidateTransfer : ICapability<ExecuteTransfer, ValidatedTransfer>
{
    private readonly ILedger _ledger;

    /// <summary>Creates the capability.</summary>
    public ValidateTransfer(ILedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        _ledger = ledger;
    }

    /// <inheritdoc />
    public async ValueTask<Result<ValidatedTransfer>> ExecuteAsync(
        ExecuteTransfer input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Amount <= 0m)
        {
            return TransferErrors.InvalidAmount(input.Amount);
        }

        var available = await _ledger.AvailableAsync(input.DebtorIban, ct).ConfigureAwait(false);

        if (available is null)
        {
            return TransferErrors.AccountUnknown();
        }

        // The balance is reported, never judged. Whether a short one ends the transfer is
        // the flow's arm to take, so that the decision is in the graph, the manifest and
        // the rendered diagram instead of buried in an `if` nobody can see from outside.
        return new ValidatedTransfer(
            input.DebtorIban,
            input.CreditorIban,
            input.Amount,
            input.Currency,
            input.Channel,
            available.Value);
    }
}

/// <summary>Screens the counterparty against a sanctions list.</summary>
/// <remarks>
/// The one call this application makes to a system it does not own, which is why it is the
/// step carrying <c>Policies.ExternalRead</c> — and why the README is explicit that the
/// policy is a declaration rather than a behaviour in this release.
/// </remarks>
[Capability("compliance.screen_sanctions", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "compliance:screen",
    Idempotent = true,
    SideEffects = ["sanctions-provider"])]
public sealed class ScreenSanctions : ICapability<ValidatedTransfer, ScreeningDecision>
{
    private readonly ISanctionsScreening _screening;

    /// <summary>Creates the capability.</summary>
    public ScreenSanctions(ISanctionsScreening screening)
    {
        ArgumentNullException.ThrowIfNull(screening);
        _screening = screening;
    }

    /// <inheritdoc />
    public async ValueTask<Result<ScreeningDecision>> ExecuteAsync(
        ValidatedTransfer input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var list = await _screening.MatchAsync(input.CreditorIban, ct).ConfigureAwait(false);

        return list is null
            ? new ScreeningDecision(LedgerKeys.For(ctx))
            : TransferErrors.SanctionsHit(list);
    }
}

/// <summary>Finds the correspondent bank a cross-border transfer routes through.</summary>
[Capability("correspondent.resolve", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "correspondent:read",
    Idempotent = true)]
public sealed class ResolveCorrespondent : ICapability<ValidatedTransfer, CorrespondentRoute>
{
    private readonly ICorrespondentDirectory _directory;

    /// <summary>Creates the capability.</summary>
    public ResolveCorrespondent(ICorrespondentDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _directory = directory;
    }

    /// <inheritdoc />
    public async ValueTask<Result<CorrespondentRoute>> ExecuteAsync(
        ValidatedTransfer input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        // The first two characters of an IBAN are its ISO 3166 country. Not an account
        // number, and the only part of one this capability needs.
        var country = input.CreditorIban.Length >= 2
            ? input.CreditorIban[..2]
            : input.CreditorIban;

        var route = await _directory.ForAsync(country, ct).ConfigureAwait(false);

        return route is null ? TransferErrors.NoCorrespondent(country) : route;
    }
}

/// <summary>Takes the money out of the debtor's account.</summary>
/// <remarks>
/// <para>
/// <strong>Idempotent, and the declaration is load-bearing in both directions.</strong> It
/// is what allows a <c>Retry</c> to be attached at all (FLOWX1014 refuses one otherwise),
/// and it is a promise this class has to keep — which it keeps by presenting the ledger a
/// key derived from <see cref="CapabilityContext.IdempotencyKey"/> and
/// <see cref="CapabilityContext.CapabilityId"/>, both of which are stable across a retry
/// and across a replay. A key built from <c>Guid.NewGuid()</c> or the clock would debit
/// twice on the second attempt and nothing in the platform would notice.
/// </para>
/// </remarks>
[Capability("ledger.post_debit", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "ledger:post",
    Idempotent = true,
    SideEffects = ["core-ledger"])]
public sealed class PostDebit : ICapability<DebitInstruction, DebitPosted>
{
    private readonly ILedger _ledger;

    /// <summary>Creates the capability.</summary>
    public PostDebit(ILedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        _ledger = ledger;
    }

    /// <inheritdoc />
    public async ValueTask<Result<DebitPosted>> ExecuteAsync(
        DebitInstruction input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var entry = await _ledger
            .PostAsync(input.DebtorIban, -input.Amount, input.Currency, LedgerKeys.For(ctx), ct)
            .ConfigureAwait(false);

        return new DebitPosted(entry.EntryId, input.Amount);
    }
}

/// <summary>Puts the money into the creditor's account.</summary>
[Capability("ledger.post_credit", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "ledger:post",
    Idempotent = true,
    SideEffects = ["core-ledger"])]
public sealed class PostCredit : ICapability<CreditInstruction, CreditPosted>
{
    private readonly ILedger _ledger;

    /// <summary>Creates the capability.</summary>
    public PostCredit(ILedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        _ledger = ledger;
    }

    /// <inheritdoc />
    public async ValueTask<Result<CreditPosted>> ExecuteAsync(
        CreditInstruction input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        // The receiving side gets to say no, and it says no after the debit has already been
        // posted. That is the whole reason this flow is a saga rather than a script.
        if (await _ledger.IsClosedAsync(input.CreditorIban, ct).ConfigureAwait(false))
        {
            return TransferErrors.CreditorAccountClosed();
        }

        var entry = await _ledger
            .PostAsync(input.CreditorIban, input.Amount, input.Currency, LedgerKeys.For(ctx), ct)
            .ConfigureAwait(false);

        return new CreditPosted(entry.EntryId, input.Amount);
    }
}

/// <summary>Backs a posted debit out with a contra entry. The business inverse of <see cref="PostDebit"/>.</summary>
/// <remarks>
/// <para>
/// <c>Internal</c>: an unwind is reachable only from the engine, never from a caller. A
/// reversal anybody could invoke would be a way to move money by asking for the undo of a
/// transfer that succeeded.
/// </para>
/// <para>
/// <strong>It consumes a <see cref="DebitInstruction"/> — the input of the step it undoes,
/// not that step's output — and that is a fact about the generated dispatcher rather than a
/// preference.</strong> <c>PostDebit</c> is declared with an input mapping, and the
/// generated <c>CompensateAsync</c> re-evaluates that same mapping to build the
/// compensation's argument. A <c>ReverseDebit</c> written against <c>DebitPosted</c>
/// compiles here and fails inside generated code with a <c>CS1503</c>, which is how this
/// was found. The mapping is pure (FLOWX1011), so re-evaluating it during the unwind
/// produces the instruction the step actually ran with.
/// </para>
/// <para>
/// The contra entry is keyed like every other write in this application, so an unwind that
/// runs twice — a retried compensation, or a recovered instance reaching an undo a dead node
/// already ran — posts one contra entry and not two.
/// </para>
/// </remarks>
[Capability("ledger.reverse_debit", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["core-ledger"])]
public sealed class ReverseDebit : ICapability<DebitInstruction, DebitReversed>
{
    private readonly ILedger _ledger;

    /// <summary>Creates the capability.</summary>
    public ReverseDebit(ILedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        _ledger = ledger;
    }

    /// <inheritdoc />
    public async ValueTask<Result<DebitReversed>> ExecuteAsync(
        DebitInstruction input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var entry = await _ledger
            .PostAsync(
                input.DebtorIban,
                input.Amount,
                input.Currency,
                LedgerKeys.ForUndo(ctx, "ledger.reverse_debit"),
                ct)
            .ConfigureAwait(false);

        return new DebitReversed(entry.EntryId);
    }
}

/// <summary>Backs a posted credit out with a contra entry. The business inverse of <see cref="PostCredit"/>.</summary>
[Capability("ledger.reverse_credit", Version = "1.0.0",
    Authorization = Authorization.Internal,
    Idempotent = true,
    SideEffects = ["core-ledger"])]
public sealed class ReverseCredit : ICapability<CreditInstruction, CreditReversed>
{
    private readonly ILedger _ledger;

    /// <summary>Creates the capability.</summary>
    public ReverseCredit(ILedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        _ledger = ledger;
    }

    /// <inheritdoc />
    public async ValueTask<Result<CreditReversed>> ExecuteAsync(
        CreditInstruction input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var entry = await _ledger
            .PostAsync(
                input.CreditorIban,
                -input.Amount,
                input.Currency,
                LedgerKeys.ForUndo(ctx, "ledger.reverse_credit"),
                ct)
            .ConfigureAwait(false);

        return new CreditReversed(entry.EntryId);
    }
}

/// <summary>Records a settled transfer in the payments register.</summary>
/// <remarks>
/// <para>
/// The last step, and deliberately not compensable: once the register has the transfer,
/// there is nothing to take back — the money is where it was asked to go. What makes it
/// interesting is that it can still <em>fail</em>, and a failure here is what unwinds both
/// ledger legs. That is the only arrangement in which strict reverse order is observable,
/// which is why <c>ExecuteTransferFlowTests</c> fails this step rather than a legs.
/// </para>
/// <para>
/// The timestamp comes from <see cref="CapabilityContext.UtcNow"/> and never from
/// <see cref="DateTimeOffset.UtcNow"/>. Under a durable profile that is FLOWX1007, an
/// error rather than a warning: a replayed instance must record the instant the transfer
/// actually settled, not the instant it was replayed.
/// </para>
/// </remarks>
[Capability("settlement.record", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "settlement:write",
    Idempotent = true,
    SideEffects = ["payments-register"])]
public sealed class RecordSettlement : ICapability<SettlementInstruction, Settlement>
{
    private readonly ISettlementRegister _register;

    /// <summary>Creates the capability.</summary>
    public RecordSettlement(ISettlementRegister register)
    {
        ArgumentNullException.ThrowIfNull(register);
        _register = register;
    }

    /// <inheritdoc />
    public async ValueTask<Result<Settlement>> ExecuteAsync(
        SettlementInstruction input,
        CapabilityContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);

        var settlement = new Settlement(input.TransferId, ctx.UtcNow);

        await _register.RecordAsync(settlement, input, ct).ConfigureAwait(false);

        return settlement;
    }
}

/// <summary>
/// The key a ledger write is deduplicated on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The flow's key alone is not enough, and that is the whole point of this type.</strong>
/// <see cref="CapabilityContext.IdempotencyKey"/> is one value for the entire instance — it
/// is what the caller sent in <c>Idempotency-Key</c> — so a debit and a credit that used it
/// unqualified would present the ledger with the same key twice, and the credit would be
/// deduplicated away as a repeat of the debit. The transfer would take money out and put
/// none in.
/// </para>
/// <para>
/// <see cref="CapabilityContext.CapabilityId"/> is the identity of the step being executed.
/// It changes per step and is fixed by the compiled plan, so it is stable across a retry
/// and across a replay of the same instance — which is what makes the combination a key the
/// ledger can be asked twice with.
/// </para>
/// <para>
/// The sample README once described this key as <c>instanceId:stepId</c>. There is no step
/// id on <see cref="CapabilityContext"/> and <see cref="CapabilityContext.FlowInstanceId"/>
/// is null outside a durable flow, so that description named two things a capability cannot
/// read. This is what it can.
/// </para>
/// </remarks>
internal static class LedgerKeys
{
    /// <summary>The key for the write the given step is about to make.</summary>
    public static string For(CapabilityContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        return ctx.IdempotencyKey + ":" + ctx.CapabilityId;
    }

    /// <summary>
    /// The key for the write an <em>undo</em> is about to make.
    /// </summary>
    /// <param name="ctx">The context the compensation is running under.</param>
    /// <param name="capabilityId">
    /// The compensating capability's own declared id, written out because it cannot be read
    /// from <paramref name="ctx"/>.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>A compensation cannot use <see cref="For"/>, and finding out why cost this
    /// sample two red tests.</strong> During an unwind the engine calls
    /// <c>FlowExecutionContext.EnterStep(entry.Step)</c> with the step being undone, and that
    /// method reads <c>step.Capability.Id</c> — the <em>forward</em> capability. So inside
    /// <see cref="ReverseDebit"/>, <c>ctx.CapabilityId</c> is <c>ledger.post_debit</c>, and a
    /// key built from it is byte-for-byte the key the debit was written under.
    /// </para>
    /// <para>
    /// A ledger that deduplicates on that key then treats the contra entry as a repeat of the
    /// debit, returns the debit's own entry, and moves nothing. The compensation reports
    /// success, the engine reports <c>CompensationOutcome.Succeeded</c>, and the money stays
    /// out of the debtor's account. Both of this sample's unwind tests failed exactly that
    /// way — <c>Compensated</c> was right and the balance was 380 instead of 500 — which is
    /// the most dangerous shape a bug can have: correct-looking telemetry over a real loss.
    /// </para>
    /// <para>
    /// Writing the id out is therefore not duplication for its own sake. It is the only
    /// value in scope that identifies the capability doing the writing.
    /// <c>CapabilityTests.AnUndoKeyedOnTheContextWouldCollideWithTheWriteItUndoes</c> pins
    /// the hazard so that a future context member does not quietly make this comment wrong.
    /// </para>
    /// </remarks>
    public static string ForUndo(CapabilityContext ctx, string capabilityId)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        return ctx.IdempotencyKey + ":" + capabilityId;
    }
}

/// <summary>The core ledger this application reads and writes.</summary>
public interface ILedger
{
    /// <summary>The account's available balance, or <c>null</c> when it is not held here.</summary>
    ValueTask<decimal?> AvailableAsync(string iban, CancellationToken ct);

    /// <summary>
    /// Moves money, once per <paramref name="key"/>.
    /// </summary>
    /// <param name="iban">The account to move.</param>
    /// <param name="signedAmount">Negative for a debit, positive for a credit.</param>
    /// <param name="currency">ISO 4217 code.</param>
    /// <param name="key">
    /// What the write is deduplicated on. A second call with the same key must return the
    /// first call's entry and move nothing — that promise is what
    /// <c>Idempotent = true</c> on the posting capabilities means.
    /// </param>
    /// <param name="ct">Cancels the call.</param>
    ValueTask<LedgerEntry> PostAsync(
        string iban, decimal signedAmount, string currency, string key, CancellationToken ct);

    /// <summary>
    /// Whether the account has been closed and can no longer be credited.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="AvailableAsync"/> returning null. "This bank does not hold
    /// that account" and "it holds it and will not take money into it" are different facts,
    /// and only the second one can be discovered after the debit has already been posted.
    /// </remarks>
    ValueTask<bool> IsClosedAsync(string iban, CancellationToken ct);
}

/// <summary>The sanctions screening provider.</summary>
public interface ISanctionsScreening
{
    /// <summary>The list the account matched, or <c>null</c> when it matched none.</summary>
    ValueTask<string?> MatchAsync(string iban, CancellationToken ct);
}

/// <summary>The correspondent banking directory.</summary>
public interface ICorrespondentDirectory
{
    /// <summary>The correspondent serving a country, or <c>null</c> when none is configured.</summary>
    ValueTask<CorrespondentRoute?> ForAsync(string country, CancellationToken ct);
}

/// <summary>The payments register a settled transfer is recorded in.</summary>
public interface ISettlementRegister
{
    /// <summary>Records a settlement, once per transfer id.</summary>
    ValueTask RecordAsync(Settlement settlement, SettlementInstruction instruction, CancellationToken ct);
}
