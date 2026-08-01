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
/// <strong>What the policies below do at run time: one thing.</strong> See
/// <see cref="Policies"/> — the policy engine is P4, so the timeouts, the retry, the breaker,
/// the rate limit and the audits are carried into the plan and enforced by no code. The one
/// exception is the <c>CompensationRetry</c> on the two ledger legs: delete the
/// <c>.WithPolicy(...)</c> calls and only those two undos change, five attempts to one.
/// </para>
/// <para>
/// <strong>The compiler now says so, on all seven of them.</strong>
/// <a href="../../docs/diagnostics/FLOWX1032.md">FLOWX1032</a> reports every declared policy
/// this release does not apply, and this flow was its first finding: seven reports, one per
/// <c>.WithPolicy(...)</c>, naming <c>Audit</c> and <c>Timeout</c> on the ledger legs and not
/// their <c>CompensationRetry</c>. The paragraph above used to be the only thing standing
/// between a reader and the assumption that a declared timeout is an enforced one; it is now
/// the argument for a suppression rather than a promise nobody checks. The suppression's
/// reasoning is below and on <see cref="Policies"/>.
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

        // Deliberate, and argued rather than hidden: every .WithPolicy below declares
        // policies P4 will execute and this release does not, which is exactly what
        // FLOWX1032 reports and exactly what this sample exists to state out loud. Its page
        // asks for one of three answers; this is the first — the flow is survivable with
        // them unenforced. The PT60S deadline bounds the run whatever the step timeouts say;
        // the rate limit belongs in front of the process and a real deployment puts it
        // there; and the audits are a statement of what a financial reviewer should be able
        // to read, which the manifest delivers. Deleting the declarations to buy a green
        // build would delete the record P4 needs and change nothing about how this transfer
        // runs. See docs/diagnostics/FLOWX1032.md and the README's "What is declared and not
        // enforced".
#pragma warning disable FLOWX1032 // Deliberate: declared, unenforced, and argued in docs/diagnostics/FLOWX1032.md
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
#pragma warning restore FLOWX1032
    }
}
