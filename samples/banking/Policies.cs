using FlowX;

namespace Banking;

/// <summary>
/// The named policy sets this application's steps declare.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read this before reading anything else in the file: five of the eight
/// declarations below change what this flow does, and three do not.</strong> The policy
/// engine executes <see cref="PolicyStage.Resilience"/> — so every <c>Timeout</c> here is
/// armed, <see cref="ExternalRead"/>'s three attempts are made, and its breaker opens after
/// a sustained outage at the screening provider — and it executes
/// <see cref="PolicySet.CompensationRetry"/>, which it has since WP-57. What is still
/// executed by nothing is <see cref="Admission"/>'s <c>RateLimit</c> and
/// <c>Idempotency</c>, and the <c>Audit</c> on the three steps that declare one.
/// <a href="../../docs/adr/ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md">ADR-0025</a>
/// argues each of the three skips separately rather than as one concession.
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
/// So why declare the three that do nothing? Because a policy set is a <em>published</em>
/// statement of what this step needs, and it is published: each kind and its fixed
/// <see cref="PolicyStage"/> reach <c>flowx.manifest.json</c>, where a reviewer, a
/// <c>flowx diff</c> and an agent can all read them. Declaring the intent and saying
/// plainly that nothing enforces it is the honest position. Declaring it and letting a
/// reader assume it is enforced is the failure mode this repository exists to avoid, and a
/// banking sample would be the worst possible place to commit it.
/// </para>
/// <para>
/// <strong>Two corrections this file used to get wrong, both now checked by a compiler
/// rule.</strong>
/// </para>
/// <para>
/// <strong>First: the line was never "stages 1–6", and it is not a range of stages
/// now either.</strong> <see cref="LedgerPost"/>'s <c>Audit</c> is a
/// <see cref="PolicyStage.Consistency"/> policy — stage 7, the same stage as
/// <see cref="PolicySet.CompensationRetry"/>, which runs — so no line drawn by stage number
/// separates the two. The cut is a list of kinds, and "no financial audit record is written
/// by a policy" is still a true sentence about this bank.
/// </para>
/// <para>
/// <strong>Second: the prose was the only thing saying any of it.</strong>
/// <a href="../../docs/diagnostics/FLOWX1032.md">FLOWX1032</a> reports it at build time — on
/// four of this flow's seven <c>.WithPolicy(...)</c> calls, down from all seven — and
/// <c>ExecuteTransferFlow</c> carries two narrow argued suppressions rather than one over the
/// whole method. A paragraph can go stale; a build cannot.
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
    /// line of it is applied: this is the one set in the file that carries nothing FLOWX1032
    /// reports, so the three <c>.WithPolicy(Policies.ExternalRead)</c> calls in the flow need
    /// no suppression.
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
    /// Neither line counts anything: stage 1 and stage 3 are the two the policy engine did
    /// not implement. The honest summary is that they tell a reviewer what the endpoint is
    /// <em>supposed</em> to be bounded by, while a real deployment puts the limit in front of
    /// the process. This is the one set in the file that is entirely inert, and it is the
    /// reason <c>ExecuteTransferFlow</c> still carries a suppression on its first step.
    /// </para>
    /// </remarks>
    public static readonly PolicySet Admission = PolicySet.Named("transfer-admission")
        .RateLimit(permits: 20, TimeSpan.FromSeconds(1), RateLimitScope.Principal)
        .Idempotency(TimeSpan.FromHours(24));

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
    /// <em>error</em> rather than FLOWX1032's warning: P4 will execute a <c>Timeout</c>, and
    /// no release will ever give this step an undo. Reusing <see cref="LedgerPost"/> here —
    /// which is the tempting edit, since the two sets differ by one line — is the mistake the
    /// rule exists to catch, and it is why this set exists separately rather than being a
    /// second application of that one.
    /// </para>
    /// </remarks>
    public static readonly PolicySet SettlementRegister = PolicySet.Named("settlement-register")
        .Timeout(TimeSpan.FromSeconds(5))
        .Audit("financial");
}
