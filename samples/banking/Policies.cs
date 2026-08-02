using FlowX;

namespace Banking;

/// <summary>
/// The named policy sets this application's steps declare.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read this before reading anything else in the file: every declaration below
/// changes what this flow does.</strong> The policy engine executes
/// <see cref="PolicyStage.Resilience"/> — so every <c>Timeout</c> here is armed,
/// <see cref="ExternalRead"/>'s three attempts are made, and its breaker opens after a
/// sustained outage at the screening provider — it executes
/// <see cref="PolicySet.CompensationRetry"/>, which it has since WP-57, it executes
/// stage 7's <c>Audit</c> (the three steps that declare one produce an immutable record
/// naming the step, the principal that authorised it and the redacted document it carried),
/// and it executes <see cref="Admission"/>'s stage-1 <c>RateLimit</c> against a store every
/// replica shares.
/// <a href="../../docs/adr/ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md">ADR-0025</a>
/// argued each skip separately rather than as one concession, and every one of its skips is
/// now closed;
/// <a href="../../docs/adr/ADR-0043-an-audit-record-is-the-journals-payload-redacted-twice.md">ADR-0043</a>
/// is the record that closed the last of them.
/// </para>
/// <para>
/// <strong>The one declaration this file does <em>not</em> carry is the reason to read
/// <see cref="Admission"/>.</strong> An <c>Idempotency(PT24H)</c> stood beside its rate limit
/// and was deleted rather than left unenforced: <c>ExecuteTransfer</c> marks two IBANs
/// <c>[Sensitive]</c>, so a recorded result would carry <c>[redacted]</c> where an account
/// number was, and <a href="../../docs/diagnostics/FLOWX1040.md">FLOWX1040</a> refuses it at
/// build time.
/// </para>
/// <para>
/// <strong>"No financial audit record is written by a policy" was this file's most important
/// true sentence, and it is now false.</strong> It is worth stating what replaced it, because
/// the replacement is narrower than "the bank is audited". Three steps are recorded — the two
/// ledger legs and the settlement write — and each record names one principal. A transfer that
/// crosses no wait therefore has three records naming the same starter; a transfer that did
/// would have records naming two people, and <c>AuditRecord.Authority</c> is what says which
/// was which. This flow has no wait, so the second case is
/// <c>AuditPolicyTests.TheTrailNamesTheStarterAndTheDelivererApart</c>'s rather than this
/// sample's.
/// </para>
/// <para>
/// <strong>The compensation retry reaches the engine from here.</strong> <c>FlowEngine</c>
/// reads <c>ExecutionPlan.HasCompensationPolicies</c> and retries a failing undo when a step
/// carries one. The source generator used to emit every plan node as
/// <c>StepNode.ForCapability(index, capability, compensation)</c> and never pass a
/// <c>PolicyChain</c>, so a plan built by the compiler always reported
/// <c>HasCompensationPolicies == false</c> and the retry was reachable only from a
/// hand-built <c>ExecutionPlan</c>. It now splits the set by what each policy wraps and
/// passes both halves. <c>ManifestTests.ThePlanCarriesTheDeclaredPolicyChain</c> is what
/// stops that sentence quietly becoming false without anyone noticing, in either direction.
/// </para>
/// <para>
/// This file used to end that paragraph with "so why declare the ones that do nothing?", and
/// the answer was that a policy set is a <em>published</em> statement of what a step needs:
/// each kind and its fixed <see cref="PolicyStage"/> reach <c>flowx.manifest.json</c>, where a
/// reviewer, a <c>flowx diff</c> and an agent can all read them, and declaring the intent while
/// saying plainly that nothing enforces it was the honest position. There is nothing left to be
/// honest about — every kind declared here is enforced — and the argument is kept because it is
/// the reason none of these declarations was ever deleted to silence a warning. Declaring a
/// control and letting a reader assume it is enforced is the failure mode this repository
/// exists to avoid, and a banking sample would be the worst possible place to commit it.
/// </para>
/// <para>
/// <strong>Two corrections this file used to get wrong, both now checked by a compiler
/// rule.</strong>
/// </para>
/// <para>
/// <strong>First: the line was never "stages 1–6", and it was never a range of stages at
/// all.</strong> <see cref="LedgerPost"/>'s <c>Audit</c> is a
/// <see cref="PolicyStage.Consistency"/> policy — stage 7, the same stage as
/// <see cref="PolicySet.CompensationRetry"/> — and both ran while
/// <see cref="Admission"/>'s stage-1 <c>RateLimit</c> did not. Each landing made the reverse
/// reading available and never made a stage-shaped one true. The question is closed now: every
/// stage a set can declare into is executed.
/// </para>
/// <para>
/// <strong>Second: the prose was the only thing saying any of it.</strong> <c>FLOWX1032</c>
/// said it at build time — on all seven of this flow's <c>.WithPolicy(...)</c> calls when it
/// was written, then four, then three, then none — and <c>ExecuteTransferFlow</c> carried an
/// argued suppression for each. Both are gone: the rule is
/// <a href="../../docs/diagnostics/README.md">deleted with the gap it described</a>, and so is
/// every pragma that pointed at it. A paragraph can go stale; a build cannot — which is why
/// <c>ReferenceSamplePolicyTests</c> now asserts that this sample reports nothing and
/// suppresses nothing.
/// </para>
/// <para>
/// <strong>Third: this file is one edit away from losing the one policy that runs, in three
/// different directions.</strong> Applying a second set to a ledger leg discards the first
/// (<a href="../../docs/diagnostics/FLOWX1034.md">FLOWX1034</a>); dropping the retry to one
/// attempt publishes a retry the engine will not perform
/// (<a href="../../docs/diagnostics/FLOWX1035.md">FLOWX1035</a>); and moving this file into a
/// referenced assembly makes every declaration in it unreadable to the compiler, including
/// that retry (<a href="../../docs/diagnostics/FLOWX1036.md">FLOWX1036</a>). All three
/// compiled in silence until those rules were written, and
/// <c>ReferenceSamplePolicyTests</c> makes each edit to this very file and asserts the
/// report.
/// </para>
/// </remarks>
public static class Policies
{
    /// <summary>
    /// A call to a system this bank does not own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three attempts inside a three-second cap, and a breaker so that retries against a
    /// provider that is already down do not turn an outage into a self-inflicted one. Every
    /// line of it is applied — as is every line of every other set here, which is why the
    /// seven <c>.WithPolicy(...)</c> calls in the flow need
    /// no suppression between them.
    /// </para>
    /// <para>
    /// FLOWX1019 reads the <c>Timeout</c> and the attempt count out of this chain and checks
    /// the product against the flow's <c>PT60S</c> deadline. That arithmetic now describes
    /// what happens rather than what would happen: the timeout bounds each attempt, because
    /// <a href="../../docs/adr/ADR-0024-stage-four-is-a-fixed-nesting.md">ADR-0024</a> puts
    /// the retry outside it.
    /// </para>
    /// </remarks>
    public static readonly PolicySet ExternalRead = PolicySet.Named("external-read")
        .Timeout(TimeSpan.FromSeconds(3))
        .Retry(attempts: 3, Backoff.ExponentialJitter(TimeSpan.FromMilliseconds(200)))
        .CircuitBreaker(failureRatio: 0.5, breakDuration: TimeSpan.FromSeconds(30));

    /// <summary>
    /// A write to the core ledger, and the undo of one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No <c>Retry</c>. The posting capabilities declare <c>Idempotent = true</c> and keep
    /// that promise, so FLOWX1014 would permit one — but a forward retry buys nothing here
    /// that the caller's own retry of an idempotent endpoint does not already buy, and it
    /// would double the deadline arithmetic for every leg.
    /// </para>
    /// <para>
    /// <c>CompensationRetry</c> is declared because a reversal is the one call in this
    /// application that must not be given up on: the alternative to a retried undo is money
    /// sitting in one account only. It wraps the <em>compensating</em> capability, which is
    /// why <c>ledger.reverse_debit</c> and <c>ledger.reverse_credit</c> both declare
    /// <c>Idempotent = true</c> — <c>PolicyChain.ForCompensation</c> refuses the pairing
    /// otherwise, and it is handed the reversal's descriptor rather than the posting's for
    /// exactly that reason. It was the one line in the file that ran; the Timeout above it
    /// now runs too, and only the Audit between them does not.
    /// </para>
    /// <para>
    /// <strong>Five, and not one.</strong> <c>CompensationPolicy.IsRetrying</c> is
    /// <c>Attempts &gt; 1</c>, so <c>attempts: 1</c> would leave
    /// <c>ExecutionPlan.HasCompensationPolicies</c> false, the engine would take
    /// <c>CompensationPolicy.None</c>, and both reversals would be dispatched exactly once —
    /// while <c>ManifestWriter</c> published the kind with no parameters, so this bank's
    /// contract would read as it does today.
    /// <a href="../../docs/diagnostics/FLOWX1035.md">FLOWX1035</a> reports that edit; before
    /// it, the number was the only thing standing between the manifest and a promise nothing
    /// keeps. Five is <see cref="PolicySet.CompensationDefault"/>'s count and
    /// <c>docs/06-Execution-Engine.md</c> §7 rule 2's, written out here because this set has
    /// a timeout and an audit to declare in the same breath and a step carries one set —
    /// applying the default <em>beside</em> this one would discard it, which is
    /// <a href="../../docs/diagnostics/FLOWX1034.md">FLOWX1034</a>.
    /// </para>
    /// <para>
    /// <strong>The <c>Audit</c>'s redact list is belt-and-braces here, and saying so is the
    /// point.</strong> <c>DebtorIban</c> and <c>CreditorIban</c> are already
    /// <c>[Sensitive]</c> on <see cref="ExecuteTransfer"/>, so the flow's own
    /// <c>SensitiveMembers</c> array already strips them from every payload — the record, the
    /// journal row and the emitted event alike. Naming them again costs nothing and removes
    /// nothing that was not already gone, and it survives a future edit that unmarked the
    /// contract. <see cref="SettlementRegister"/> is where the list does work no marker does.
    /// </para>
    /// </remarks>
    public static readonly PolicySet LedgerPost = PolicySet.Named("ledger-post")
        .Timeout(TimeSpan.FromSeconds(5))
        .Audit("financial", "DebtorIban", "CreditorIban")
        .CompensationRetry(attempts: 5);

    /// <summary>
    /// Admission control for the transfer endpoint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The README this sample replaced declared this as <c>[RateLimit(Permits = 20,
    /// Window = "PT1S", Scope = RateLimitScope.Principal)]</c> on the flow. <strong>There is
    /// no such attribute</strong>, and there is no flow-level policy surface at all: policies
    /// attach to steps, through <c>.WithPolicy(...)</c>. So it is declared on the first step,
    /// which is the closest thing to admission the DSL can express.
    /// </para>
    /// <para>
    /// <strong>Twenty principals a second is now enforced, across every replica.</strong> The
    /// limit is counted in a shared store — this application registers the Redis one — so the
    /// twenty is the deployment's and not each node's.
    /// <a href="../../docs/adr/ADR-0040-a-rate-limit-is-shared-or-it-is-not-a-rate-limit.md">ADR-0040</a>
    /// is why it could not ship as a process-local counter: a breaker that is per process is
    /// slower to protect and never wrong, and a limiter that is per process admits n × the
    /// declared rate across n nodes, with the factor being the replica count and nothing
    /// declaring it. The twenty-first principal in a second gets
    /// <c>policy.rate_limited</c> and the ledger is never reached.
    /// </para>
    /// <para>
    /// <strong>The <c>Idempotency</c> window that used to sit beside it is gone, and its
    /// absence is the most interesting thing in this file.</strong> It read
    /// <c>.Idempotency(TimeSpan.FromHours(24))</c>, and stage 3 now executes — so it would
    /// have run. It is removed because it cannot run <em>here</em>, and
    /// <a href="../../docs/diagnostics/FLOWX1040.md">FLOWX1040</a> is a build error that says
    /// so: <see cref="ExecuteTransfer"/> marks two IBANs <c>[Sensitive]</c>, so
    /// <c>ExecuteTransferFlow.SensitiveMembers</c> is non-empty, so every document this flow
    /// records has <c>[redacted]</c> where an account number was — including the state bag a
    /// replay would restore. A second caller presenting the same key would have been answered
    /// with a <c>ValidatedTransfer</c> whose <c>DebtorIban</c> was the literal string
    /// <c>[redacted]</c>, and <c>PostDebit</c> would have posted against it, and the flow
    /// would have returned <c>200</c>.
    /// </para>
    /// <para>
    /// That is worse than no idempotency at all, which is exactly what
    /// <a href="../../docs/adr/ADR-0042-a-recorded-result-is-replayed-only-when-recording-lost-nothing.md">ADR-0042</a>
    /// decides: without the window the step is simply dispatched again — the posting
    /// capabilities declare <c>Idempotent = true</c> and keep that promise — and the second
    /// caller gets the real answer for the second time. **The duplicate this window was
    /// reaching for is already held shut** by <c>FLOWX1014</c> and by the stable
    /// <c>ctx.IdempotencyKey</c> every attempt presents, which is
    /// <a href="../../docs/adr/ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md">ADR-0025</a>
    /// §2.2 and is unchanged by stage 3 landing.
    /// </para>
    /// </remarks>
    public static readonly PolicySet Admission = PolicySet.Named("transfer-admission")
        .RateLimit(permits: 20, TimeSpan.FromSeconds(1), RateLimitScope.Principal);

    /// <summary>The settlement register write.</summary>
    /// <remarks>
    /// <para>
    /// <strong>No <c>CompensationRetry</c>, and the omission is the point.</strong>
    /// <c>RecordSettlement</c> is not compensable — once the register holds the transfer
    /// there is nothing to take back — so a compensation retry here would wrap nothing.
    /// <c>FlowEmitter</c> drops such a declaration without a word and <c>ManifestWriter</c>
    /// publishes it anyway, which would leave this bank's published contract promising a
    /// retried undo for a settlement that has no undo.
    /// </para>
    /// <para>
    /// That is <a href="../../docs/diagnostics/FLOWX1033.md">FLOWX1033</a>, and it is an
    /// <em>error</em> rather than a warning: a <c>Timeout</c> is executed, and
    /// no release will ever give this step an undo. Reusing <see cref="LedgerPost"/> here —
    /// which is the tempting edit, since the two sets differ by one line — is the mistake the
    /// rule exists to catch, and it is why this set exists separately rather than being a
    /// second application of that one.
    /// </para>
    /// <para>
    /// <strong>This is the one <c>redact</c> list in the file that removes something no
    /// contract marks.</strong> <c>DebitEntryId</c> and <c>CreditEntryId</c> are the core
    /// ledger's own references. They are not sensitive in the <c>[Sensitive]</c> sense —
    /// they identify no person and they belong in the journal, where an operator resolving an
    /// incident needs them — but they are internal identifiers of a system this bank's external
    /// auditors do not have and cannot resolve, and a <c>financial</c> record retained for the
    /// statutory period is the wrong place to accumulate them. The transfer is identifiable
    /// from <c>AuditRecord.IdempotencyKey</c>, which is the key the caller supplied and the one
    /// both sides of an audit can agree on.
    /// </para>
    /// <para>
    /// It is also the assertion that keeps <c>redact</c> from being decorative: strip the two
    /// names from the call below and <c>TransferAuditTests</c> goes red, because the ledger
    /// references reappear in a record nothing else would have removed them from.
    /// </para>
    /// </remarks>
    public static readonly PolicySet SettlementRegister = PolicySet.Named("settlement-register")
        .Timeout(TimeSpan.FromSeconds(5))
        .Audit("financial", "DebitEntryId", "CreditEntryId");
}
