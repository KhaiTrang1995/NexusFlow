using FlowX;

namespace Banking;

/// <summary>
/// The named policy sets this application's steps declare.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read this before reading anything else in the file: with one exception, these
/// declarations change nothing about how the flow runs.</strong> There is no policy engine
/// — <c>docs/10-Policy-Framework.md</c> is P4 — so no timeout is applied, nothing is retried
/// on the forward path, no circuit opens, no rate limit is counted and no audit record is
/// written by a policy. They reach the compiled plan and the manifest; they are executed by
/// nothing.
/// </para>
/// <para>
/// <strong>The exception is <see cref="PolicySet.CompensationRetry"/> (WP-57), and it now
/// reaches the engine from here.</strong> <c>FlowEngine</c> reads
/// <c>ExecutionPlan.HasCompensationPolicies</c> and retries a failing undo when a step
/// carries one. The source generator used to emit every plan node as
/// <c>StepNode.ForCapability(index, capability, compensation)</c> and never pass a
/// <c>PolicyChain</c>, so a plan built by the compiler always reported
/// <c>HasCompensationPolicies == false</c> and the retry was reachable only from a
/// hand-built <c>ExecutionPlan</c>. It now splits the set by what each policy wraps and
/// passes both halves. <c>ManifestTests.ThePlanCarriesTheDeclaredPolicyChain</c> is what
/// stops that sentence quietly becoming false without anyone noticing, in either direction.
/// </para>
/// <para>
/// So why declare the rest at all? Because a policy set is a <em>published</em> statement of
/// what this step needs, and it is published: each kind and its fixed
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
/// <strong>First: the line is not "stages 1–6".</strong> <see cref="LedgerPost"/>'s
/// <c>Audit</c> is a <see cref="PolicyStage.Consistency"/> policy — stage 7, the same stage
/// as <see cref="PolicySet.CompensationRetry"/> — and it is inert all the same, because
/// <c>PolicyChain.ForStep</c> moves only the compensation retry onto the undo's chain and
/// <c>CompensationPolicy.From</c> reads only that kind. The cut is by <em>what a policy
/// wraps</em>, not by which stage it runs in. So "no financial audit record is written by a
/// policy" is a true sentence about this bank, and the paragraph above was one stage away
/// from implying otherwise.
/// </para>
/// <para>
/// <strong>Second: the prose was the only thing saying any of it.</strong>
/// <a href="../../docs/diagnostics/FLOWX1032.md">FLOWX1032</a> now reports it at build time,
/// on all seven of this flow's <c>.WithPolicy(...)</c> calls, and
/// <c>ExecuteTransferFlow</c> carries an argued suppression rather than a quiet one. A
/// paragraph can go stale; a build cannot.
/// </para>
/// </remarks>
public static class Policies
{
    /// <summary>
    /// A call to a system this bank does not own.
    /// </summary>
    /// <remarks>
    /// Three attempts inside a three-second cap, and a breaker so that retries against a
    /// provider that is already down do not turn an outage into a self-inflicted one.
    /// FLOWX1019 reads the <c>Timeout</c> and the attempt count out of this chain and checks
    /// the total against the flow's <c>PT60S</c> deadline, which is the one thing in this
    /// file that has an effect today: get the arithmetic wrong and the build says so.
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
    /// exactly that reason. This is the one line in the file that runs.
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
    /// It counts nothing. A rate limit that exists only in a manifest protects no ledger,
    /// and the honest summary of this line is that it tells a reviewer what the endpoint
    /// is <em>supposed</em> to be limited to while a real deployment puts the limit in front
    /// of the process.
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
