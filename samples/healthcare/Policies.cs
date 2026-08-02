using FlowX;

namespace Healthcare;

/// <summary>
/// The named policy sets this application's steps declare.
/// </summary>
/// <remarks>
/// <para>
/// Every declaration here is executed. Stage 1's <c>RateLimit</c> is counted in a shared
/// store, stage 4's <c>Timeout</c>, <c>Retry</c> and <c>CircuitBreaker</c> bound each dispatch,
/// stage 7's <c>Audit</c> writes an immutable record, and the <c>CompensationRetry</c> bounds
/// the undo. <c>samples/banking/Policies.cs</c> argues each of those at length and the argument
/// is not repeated here; what follows is only what is different about a clinic.
/// </para>
/// <para>
/// <strong>No set here declares an <c>Idempotency</c> window, and the reason is a build
/// error.</strong> <see cref="PatientIntake"/> marks two members <c>[Sensitive]</c>, so every
/// document this flow records carries <c>[redacted]</c> where the identifier and the name were
/// — including the state bag a replay would restore.
/// <a href="../../docs/diagnostics/FLOWX1040.md">FLOWX1040</a> refuses the window rather than
/// letting a second caller be answered with a patient whose name is the literal string
/// <c>[redacted]</c>. The duplicate the window would have been reaching for is held shut
/// elsewhere: <c>patient.deduplicate</c> answers with the same patient id for the same
/// identifier, and <c>records.store</c> files under the flow's idempotency key.
/// </para>
/// </remarks>
public static class Policies
{
    /// <summary>Admission control for the intake endpoint.</summary>
    /// <remarks>
    /// Per principal rather than per tenant. The tenant's own bound is declared on the host —
    /// <c>FlowXOptions.Fairness</c>, six mechanisms, applied before a lease is taken — and this
    /// one bounds a single clinician's terminal repeating a submission, which is a different
    /// failure with a different remedy.
    /// </remarks>
    public static readonly PolicySet Admission = PolicySet.Named("intake-admission")
        .RateLimit(permits: 20, TimeSpan.FromSeconds(1), RateLimitScope.Principal);

    /// <summary>
    /// The consent lookup: a call to a register this application does not own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Three attempts and a breaker, and the breaker is the interesting half.</strong>
    /// A consent register that is down must not become a reason to admit patients without
    /// checking: the breaker opens, the step fails, and the flow refuses the intake. That is
    /// the correct direction to fail in for a control whose whole purpose is to establish a
    /// lawful basis, and it is why there is no fallback, no cached verdict and no "assume
    /// consent while the register recovers" anywhere in this application.
    /// </para>
    /// <para>
    /// <strong>The <c>Audit</c> is what makes the decision survive the flow.</strong> The
    /// record names the capability, the principal that authorised it and a redacted document
    /// of what was asked and what came back — so "on what basis was this record processed" is
    /// answerable from the audit sink months later, without reading the journal and without
    /// the patient's identifier appearing in either. The redact list names nothing extra
    /// because <see cref="ConsentVerified"/> carries nothing that is not already the
    /// register's own reference.
    /// </para>
    /// </remarks>
    public static readonly PolicySet ConsentLookup = PolicySet.Named("consent-lookup")
        .Timeout(TimeSpan.FromSeconds(3))
        .Retry(attempts: 3, Backoff.ExponentialJitter(TimeSpan.FromMilliseconds(200)))
        .CircuitBreaker(failureRatio: 0.5, breakDuration: TimeSpan.FromSeconds(30))
        .Audit("clinical");

    /// <summary>The patient index lookup.</summary>
    /// <remarks>
    /// Audited under <c>clinical</c> with the national identifier named in the redact list,
    /// even though <see cref="PatientIntake"/> already marks it <c>[Sensitive]</c> and the
    /// flow's own <c>SensitiveMembers</c> already strips it from every payload. Naming it
    /// again removes nothing that was not already gone and costs nothing — and it survives a
    /// future edit that unmarks the contract, which is the only reason a belt-and-braces
    /// entry earns its place.
    /// </remarks>
    public static readonly PolicySet PatientLookup = PolicySet.Named("patient-lookup")
        .Timeout(TimeSpan.FromSeconds(3))
        .Audit("clinical", "NationalId");

    /// <summary>The clinical record write, and the undo of one.</summary>
    /// <remarks>
    /// <c>CompensationRetry</c> because a record left filed by a flow that did not finish is a
    /// clinical record of an admission that did not happen, and giving up on the withdrawal
    /// leaves it there. Five attempts, which is <see cref="PolicySet.CompensationDefault"/>'s
    /// count — written out here because this set has a timeout and an audit to declare in the
    /// same breath, and a step carries one set, so applying the default beside this one would
    /// discard it (<a href="../../docs/diagnostics/FLOWX1034.md">FLOWX1034</a>).
    /// </remarks>
    public static readonly PolicySet RecordWrite = PolicySet.Named("record-write")
        .Timeout(TimeSpan.FromSeconds(5))
        .Audit("clinical")
        .CompensationRetry(attempts: 5);
}
