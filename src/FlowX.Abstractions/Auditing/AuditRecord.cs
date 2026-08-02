namespace FlowX;

/// <summary>
/// How the identity on an audited step reached the engine — which is the half of
/// <c>docs/10-Policy-Framework.md §3</c>'s <c>Audit</c> row that
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0028-identity-arrives-on-the-invocation.md">ADR-0028</a>
/// made necessary and nothing recorded.
/// </summary>
/// <remarks>
/// <para>
/// <strong>ADR-0028 §3's first negative, spelled as an enum.</strong> *"A durable flow's
/// authorisation is discontinuous across a wait. The steps before are decided against the
/// starter, the steps after against the deliverer, and these may be different people with
/// different grants … an auditor reconstructing 'who authorised this transfer' must read two
/// events. Nothing yet writes those events."* This is what those events say. An auditor does
/// not have to reconstruct the discontinuity from the shape of the flow: every audited step
/// carries the principal that ran it <em>and</em> which of the two roles that principal was
/// playing, so the transition from <see cref="Starter"/> to <see cref="Deliverer"/> is a fact
/// in the trail rather than an inference about it.
/// </para>
/// <para>
/// <strong>The four members are exhaustive over what the engine can actually observe</strong>,
/// and each is derived from something already on the invocation rather than from anything new:
/// whether the execution rehydrated a journal frontier (a resumption), whether
/// <c>FlowInvocation.IsContinuation</c> is set (a sweep, which by construction nothing
/// reachable by asking can set), and whether the principal is authenticated — the
/// <c>principal?.Identity?.IsAuthenticated == true</c> question ADR-0028 §2.1 makes the
/// difference between a control that holds and one that fails open.
/// </para>
/// </remarks>
public enum AuditAuthority
{
    /// <summary>
    /// Nobody authenticated. The invocation carried no principal, or one whose identity is not
    /// authenticated.
    /// </summary>
    /// <remarks>
    /// Recorded rather than left blank. A step that ran for an anonymous caller is a fact worth
    /// having in a financial trail — most often because the capability's stance is
    /// <c>Public</c> or <c>Internal</c>, which <see cref="AuditRecord.Stance"/> says — and an
    /// absent field would read as a record somebody forgot to fill in.
    /// </remarks>
    Anonymous = 0,

    /// <summary>
    /// The principal that started the instance. Every step before the first wait.
    /// </summary>
    Starter = 1,

    /// <summary>
    /// The principal that delivered the signal this execution resumed on. Every step after a
    /// wait.
    /// </summary>
    /// <remarks>
    /// The other half of ADR-0028 §2.2: the journal row carries no claims, deliberately —
    /// persisting them "would authorise a payment on Friday with a grant proved on Monday" —
    /// so <c>FlowHost.SignalAsync</c> takes the deliverer's principal and the steps after the
    /// wait are decided against whoever is delivering now. A record carrying this value is the
    /// second of the two answers to "who authorised this transfer".
    /// </remarks>
    Deliverer = 2,

    /// <summary>
    /// The platform continued an instance it had already admitted: a timer sweep or a recovery
    /// scan, with no caller and no claims to ask about.
    /// </summary>
    /// <remarks>
    /// ADR-0028 §2.3's <c>IsContinuation</c>, which is set in exactly one place and only where
    /// there is neither a signal nor a principal. **It is not a bypass**, and recording it as
    /// its own value rather than folding it into <see cref="Anonymous"/> is what keeps that
    /// checkable: an auditor can count the steps that ran with no caller and confirm that every
    /// one of them was a continuation rather than a request nobody signed.
    /// </remarks>
    Platform = 3,
}

/// <summary>
/// One immutable record of one audited step: what ran, on whose authority, and what it carried.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The schema <c>PLAN §6a</c> names as one of the two decisions P4 had to invent, and
/// it was declined twice for a reason this type has to answer rather than inherit</strong> —
/// *"an audit record the engine can write carries no payload, which makes <c>redact</c>
/// vacuous"*. It is answered by <see cref="Payload"/>: the record carries the same document
/// the journal would have written for the step, and the <c>redact</c> list an author passes to
/// <c>PolicySet.Audit(category, redact)</c> is applied to it. <c>redact</c> names members that
/// are stripped out of a record that would otherwise have carried them, which is the only thing
/// it could have meant and the thing it could not mean while the record carried nothing.
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0043-an-audit-record-is-the-journals-payload-redacted-twice.md">ADR-0043</a>
/// is the record.
/// </para>
/// <para>
/// <strong>There is no second exit for a value, and that is structural rather than
/// remembered.</strong> <see cref="Payload"/> is a <see cref="JournalPayload"/>, whose only way
/// out is <see cref="JournalPayload.ToJson"/>, which redacts — the control WP-59 was careful
/// not to open a second door on. A sink receives this type and can read the document; it has no
/// route to the object graph, because this type never held one. The audit redaction is not a
/// second implementation of the pass: it is the same pass, given a longer list of names.
/// </para>
/// <para>
/// <strong>A record is written only for a step that succeeded.</strong> <c>Audit</c> is a
/// <see cref="PolicyStage.Consistency"/> policy — stage 7, after execution — and
/// <c>docs/10 §2</c>'s table gives the reason in the row above it: "compensation registered
/// before the step succeeds → compensating something that never happened". An audit of a step
/// that failed would be the same mistake with a different noun. A failed step is reported as an
/// <see cref="Error"/> to the caller and, if it carried a policy, as a measurement.
/// </para>
/// </remarks>
public sealed record AuditRecord
{
    /// <summary>The category the author declared, e.g. <c>financial</c>.</summary>
    /// <remarks>
    /// Verbatim from <c>PolicySet.Audit(category, …)</c>. FlowX defines no vocabulary for it:
    /// which categories exist is a property of the regime an application is audited under, and
    /// a fixed enum here would be FlowX inventing a compliance taxonomy it cannot maintain.
    /// </remarks>
    public required string Category { get; init; }

    /// <summary>The flow, as the manifest publishes it.</summary>
    public required string FlowId { get; init; }

    /// <summary>The flow's declared version.</summary>
    public required string FlowVersion { get; init; }

    /// <summary>
    /// The durable instance, or <c>null</c> for an <see cref="ExecutionProfile.Ephemeral"/> flow.
    /// </summary>
    /// <remarks>
    /// What ties the records of one transfer together, and therefore what makes "who authorised
    /// this transfer" a query rather than a search. An ephemeral flow has no instance and its
    /// records are tied together by <see cref="CorrelationId"/> alone.
    /// </remarks>
    public string? InstanceId { get; init; }

    /// <summary>The correlation id the trigger carried.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>The idempotency key the invocation ran under.</summary>
    /// <remarks>
    /// Stable across every attempt of a retried step (<c>docs/10 §5</c>), so two records of one
    /// step's two attempts are recognisable as such rather than as two transfers.
    /// </remarks>
    public required string IdempotencyKey { get; init; }

    /// <summary>The tenant, or <c>null</c> when the trigger resolved none.</summary>
    public string? TenantId { get; init; }

    /// <summary>Where in the plan the step sits.</summary>
    public required int StepIndex { get; init; }

    /// <summary>The capability the step invoked.</summary>
    public required string CapabilityId { get; init; }

    /// <summary>The capability's declared version, or <c>null</c> when it declared none.</summary>
    public string? CapabilityVersion { get; init; }

    /// <summary>When the record was made — after the step succeeded, before the next one began.</summary>
    public required DateTimeOffset RecordedAt { get; init; }

    /// <summary>Whose authority the step ran under.</summary>
    public required AuditAuthority Authority { get; init; }

    /// <summary>
    /// The principal's name, or <c>null</c> when there was no authenticated one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ClaimsPrincipal.Identity.Name</c> and nothing else. The whole principal is
    /// deliberately not carried: an audit record is retained for years and a
    /// <see cref="System.Security.Claims.ClaimsPrincipal"/> holds every claim an issuer put in
    /// the token, which is credentials at rest for the life of the record — the objection
    /// ADR-0028 §2.2 raises against persisting claims on the journal row, and it does not stop
    /// being true because the table has a different name.
    /// </para>
    /// <para>
    /// What a permission decision actually turned on is <see cref="Permission"/>, which is the
    /// grant the stance named rather than the set the caller held. Between the two, an auditor
    /// can say who acted and what they had to have; neither is a copy of the token.
    /// </para>
    /// </remarks>
    public string? Principal { get; init; }

    /// <summary>
    /// The capability's declared authorisation stance, or <c>null</c> when it declared none.
    /// </summary>
    /// <remarks>
    /// What makes <see cref="AuditAuthority.Anonymous"/> readable. A step that ran for nobody
    /// under <see cref="Authorization.Public"/> is a public step working; the same record under
    /// <see cref="Authorization.Permission"/> could not exist, because
    /// <c>StepAuthorization.Decide</c> would have refused the step and no record would have
    /// been written.
    /// </remarks>
    public Authorization? Stance { get; init; }

    /// <summary>The permission the stance named, or <c>null</c> when it names none.</summary>
    public string? Permission { get; init; }

    /// <summary>
    /// What the step carried, redacted: the flow's <c>[Sensitive]</c> members and the policy's
    /// <c>redact</c> list, replaced by <see cref="JournalPayload.Redacted"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A composed document with a <c>request</c> member and a <c>result</c> member — the step's
    /// input and its output — built by <c>JournalPayload.OfState</c>, which is the same
    /// composition the state-bag snapshot goes through and therefore the same single redaction
    /// pass. Matching is by member name at every depth, so a marked member one level down
    /// inside <c>request</c> is stripped exactly as one at the root is.
    /// </para>
    /// <para>
    /// <see cref="JournalPayload.Empty"/> when the flow is not <c>Durable</c> or when the
    /// dispatcher describes nothing: the payload is the journal's payload, and a flow that
    /// journals nothing has none to lend. The record is still written — what ran and on whose
    /// authority does not depend on the profile — and it says so by carrying an empty payload
    /// rather than by not existing.
    /// </para>
    /// </remarks>
    public required JournalPayload Payload { get; init; }
}

/// <summary>
/// Where an <see cref="AuditRecord"/> goes. The seam behind <c>docs/10 §3</c>'s
/// "immutable audit record".
/// </summary>
/// <remarks>
/// <para>
/// <strong>A seam rather than a store, and the asymmetry with <see cref="IResultCache"/> is
/// deliberate.</strong> A cache has a contract worth holding two implementations to — a hit
/// must be a hit, a TTL must expire, a miss must be a miss — and
/// <c>ResultCacheConformance</c> holds them to it. An audit sink has one obligation, "receive
/// this and do not lose it", and where it is kept is a decision about a compliance regime
/// rather than about FlowX: a table, an append-only log, a SIEM. Inventing a conformance suite
/// for it would be inventing the obligations to conform to.
/// </para>
/// <para>
/// <strong>A sink that throws fails the flow, and that is the point.</strong> Every other
/// plugin failure in this engine degrades — an unreachable cache dispatches, an unreachable
/// breaker store does not exist — because the alternative is worse behaviour. Here the
/// alternative is a step that happened with no record that it happened, which is precisely the
/// state the deleted <c>FLOWX1032</c>'s third remedy refused to ship: *"do not ship this flow on this
/// release, if the step genuinely cannot run without the policy — a regulated write whose audit
/// record is the reason it is allowed to happen"*. A record that may be dropped is not an audit
/// trail, so the engine lets the failure reach the flow.
/// </para>
/// </remarks>
public interface IAuditSink
{
    /// <summary>Records one audited step.</summary>
    /// <param name="record">What happened.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken);
}
