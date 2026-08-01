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
/// <strong>What the policies below do at run time: most of it.</strong> See
/// <see cref="Policies"/>. The policy engine executes <c>PolicyStage.Resilience</c>, so the
/// three-second timeout and the three attempts on <c>ScreenSanctions</c> are real, the five-
/// second timeouts on the ledger legs and the settlement write are real, the breaker in front
/// of the screening provider opens, and the <c>CompensationRetry</c> on the two ledger legs
/// has run since WP-57. <c>ExecuteTransferFlowTests</c> settles a transfer whose screening
/// provider fails once, which is a transfer this sample refused a release ago.
/// </para>
/// <para>
/// <strong>Three declarations still do nothing, and the pragmas below say which.</strong>
/// <a href="../../docs/diagnostics/FLOWX1032.md">FLOWX1032</a> reported all seven of this
/// flow's <c>.WithPolicy(...)</c> calls when it was written; it now reports four of them,
/// naming <c>RateLimit</c> and <c>Idempotency</c> on the first step and <c>Audit</c> on the
/// three that declare one. The three <c>ExternalRead</c> calls in the <c>Switch</c> carry no
/// suppression at all, which is the visible half of the change: a policy that runs needs no
/// argument for why it does not.
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

        // The suppression below is narrower than it was, and the narrowing is the news.
        // FLOWX1032 used to report all seven of this flow's .WithPolicy(...) calls, so one
        // pragma wrapped the whole method. The policy engine now executes
        // PolicyStage.Resilience, so the three ExternalRead calls in the Switch below carry
        // no suppression at all: their Timeout, Retry and CircuitBreaker are applied. What
        // is left is RateLimit, Idempotency and Audit, and each is suppressed beside the
        // step that declares it rather than by one pragma over the lot.
        //
        // The rate limit and the idempotency window here are the first of FLOWX1032's three
        // answers — the flow is survivable with them unenforced. A real deployment puts the
        // limit in front of the process, and the endpoint is [HttpTrigger(Idempotent = true)]
        // so the caller's key already reaches the flow as ctx.IdempotencyKey; what does not
        // happen is a recorded result being replayed for a repeated key.
#pragma warning disable FLOWX1032 // RateLimit and Idempotency: stage 1 and stage 3 are not implemented.
        flow
            // Reads the debtor's balance and hands it forward. It does not judge it: the
            // judgement is the arm below, so that "we refuse transfers we cannot fund" is
            // a node in the published graph rather than an `if` inside a class.
            .Step<ValidateTransfer>().WithPolicy(Policies.Admission)
#pragma warning restore FLOWX1032

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
            //
            // The Timeout on these three steps is applied now and the Audit is not, which is
            // the whole of what the pragma covers. An unwritten financial audit record is a
            // real loss rather than a conservative default, and ADR-0025 §2.4 says so in
            // those words instead of filing it beside the two above.
#pragma warning disable FLOWX1032 // Audit: stage 7's audit record is not implemented.
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
#pragma warning restore FLOWX1032

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
