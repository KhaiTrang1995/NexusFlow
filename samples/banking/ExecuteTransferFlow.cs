using FlowX;

namespace Banking;

/// <summary>
/// Moves money between two accounts: validate, screen, debit, credit, record, announce.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole application's control flow. It expresses <em>order, condition and
/// recovery</em> and nothing else — there is no business rule in this method, and no
/// mention of HTTP, which is why moving this behind a queue would change the attribute
/// above it and nothing below it.
/// </para>
/// <para>
/// <strong><c>Durable</c>, and unlike the ecommerce sample that is not a decoration.</strong>
/// Every step boundary is committed to a journal before the next one starts, so a node that
/// dies between the debit and the credit leaves a row saying the debit happened and a
/// pending unwind another node can pick up. On <c>Ephemeral</c> the compensation stack lives
/// in the memory of the process that took the request, and a crash there leaves money in one
/// account with nothing left to move it back. <c>FLOWX1012</c> says exactly that, and it is
/// the reason this flow is not ephemeral rather than a rule to suppress. The cost is real
/// and is stated in the README: this sample does not start without PostgreSQL.
/// </para>
/// <para>
/// <strong>The <c>[HttpTrigger]</c> below <em>is</em> the endpoint.</strong> One reading of
/// it produces both the <c>triggers</c> block of <c>flowx.manifest.json</c> and the route
/// <c>app.MapFlowX()</c> registers, so the address this flow publishes and the address it
/// answers on are one string rather than two kept in step.
/// </para>
/// <para>
/// <strong>What the policies below do at run time: nothing.</strong> See
/// <see cref="Policies"/> — the policy engine is P4, and even the one policy the engine does
/// read (<c>CompensationRetry</c>) never reaches a generated plan. The declarations are
/// published in the manifest and enforced by no code. Every step here would behave
/// identically with the <c>.WithPolicy(...)</c> calls deleted.
/// </para>
/// </remarks>
[Flow("transfer.execute", Version = "1.0.0", Profile = ExecutionProfile.Durable, Owner = "payments")]
[FlowDeadline("PT60S")]
[HttpTrigger("POST", "/api/v1/transfers", Idempotent = true)]
public sealed partial class ExecuteTransferFlow : Flow<ExecuteTransfer, TransferResult>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<ExecuteTransfer, TransferResult> flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        flow
            // Reads the debtor's balance and hands it forward. It does not judge it: the
            // judgement is the arm below, so that "we refuse transfers we cannot fund" is
            // a node in the published graph rather than an `if` inside a class.
            .Step<ValidateTransfer>().WithPolicy(Policies.Admission)

            // A business outcome, not an exception (ADR-0007). The engine treats this
            // exactly as it treats a capability that returned Result.Fail — the failure
            // path, and the completed compensable steps unwind. Here there are none yet,
            // which is the point of rejecting before the first ledger write rather than
            // after it.
            .When(
                ctx => ctx.Get<ValidatedTransfer>().Amount > ctx.Get<ValidatedTransfer>().AvailableBalance,
                shortOfFunds => shortOfFunds.Fail(TransferErrors.InsufficientFunds))

            // Who has to look at this transfer is a property of where the money goes.
            // There is deliberately no Default: a Book transfer matches no case and
            // continues after the switch, because money that stays inside the bank is
            // screened by nobody. ISwitchBuilder documents that as the rule, and stating
            // it as `.Default(b => { })` would be noise.
            .Switch(ctx => ctx.Get<ValidatedTransfer>().Channel)
                .Case(
                    TransferChannel.Sepa,
                    sepa => sepa.Step<ScreenSanctions>().WithPolicy(Policies.ExternalRead))
                .Case(
                    TransferChannel.Swift,
                    swift => swift
                        .Step<ScreenSanctions>().WithPolicy(Policies.ExternalRead)
                        .Step<ResolveCorrespondent>().WithPolicy(Policies.ExternalRead))

            // The two legs. Each is compensable and each names its own inverse; the input
            // is mapped because a DebitInstruction is not something an earlier step
            // returned — it is one half of the validated transfer, and saying so here is
            // what keeps the capability's contract down to one account and one amount.
            .Step<PostDebit, DebitInstruction>(ctx => new DebitInstruction(
                ctx.Get<ValidatedTransfer>().DebtorIban,
                ctx.Get<ValidatedTransfer>().Amount,
                ctx.Get<ValidatedTransfer>().Currency))
                .CompensateWith<ReverseDebit>()
                .WithPolicy(Policies.LedgerPost)

            .Step<PostCredit, CreditInstruction>(ctx => new CreditInstruction(
                ctx.Get<ValidatedTransfer>().CreditorIban,
                ctx.Get<ValidatedTransfer>().Amount,
                ctx.Get<ValidatedTransfer>().Currency))
                .CompensateWith<ReverseCredit>()
                .WithPolicy(Policies.LedgerPost)

            // Not compensable: once the register holds the transfer there is nothing to
            // take back. It can still fail, and a failure here is what unwinds both legs —
            // credit first, then debit.
            .Step<RecordSettlement, SettlementInstruction>(ctx => new SettlementInstruction(
                ctx.IdempotencyKey,
                ctx.Get<DebitPosted>().EntryId,
                ctx.Get<CreditPosted>().EntryId,
                ctx.Get<ValidatedTransfer>().Amount,
                ctx.Get<ValidatedTransfer>().Currency))
                .WithPolicy(Policies.SettlementRegister)

            // Staged into the outbox by the same transaction that commits the step, so the
            // event and the state it announces are one write. The account numbers in the
            // body are replaced with [redacted] on the way in, because the flow's input
            // contract marked them [Sensitive] and a JournalPayload has no exit but ToJson.
            .Emit<TransferCompleted>(ctx => new TransferCompleted(
                ctx.IdempotencyKey,
                ctx.Input.DebtorIban,
                ctx.Input.CreditorIban,
                ctx.Input.Amount,
                ctx.Input.Currency))

            // The transfer id is the caller's own idempotency key. Derived rather than
            // minted, so the answer to a replayed request is the answer to the first one.
            .Return(ctx => new TransferResult(
                ctx.IdempotencyKey,
                ctx.Get<DebitPosted>().EntryId,
                ctx.Get<CreditPosted>().EntryId,
                ctx.Get<ValidatedTransfer>().Amount,
                ctx.Get<ValidatedTransfer>().Currency));
    }
}
