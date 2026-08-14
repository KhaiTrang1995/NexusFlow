namespace FlowX.Observability;

/// <summary>
/// The frozen span-attribute and metric names from
/// <a href="../../../docs/12-Observability.md">12-Observability</a> §2 and §3.
/// </summary>
/// <remarks>
/// <para>
/// <strong>These are a contract, not an implementation detail.</strong> §2 says the attribute
/// names are frozen "because dashboards and alerts across an entire estate depend on them
/// being identical in every service", and the same holds for the metric names. Changing a
/// value here breaks every query written against a deployment of the previous version, so it
/// is a breaking change in the sense constraint C7 means it — and
/// <c>TelemetryConformanceTests</c> is where it becomes a deliberate act rather than a typo.
/// </para>
/// <para>
/// <strong>Constants rather than an enum or a resource.</strong> They are interpolated into
/// nothing and compared against nothing at run time: an attribute name reaches
/// <see cref="System.Diagnostics.Activity.SetTag"/> and a metric name reaches an instrument
/// factory, both of which take a string. A constant is the shape that lets the conformance
/// test assert the literal and lets a caller name the same string without copying it.
/// </para>
/// <para>
/// <strong>Two of the thirteen attributes have no producer, and they are still named
/// here.</strong> <see cref="TriggerKind"/> and <see cref="TriggerSource"/> describe a fact
/// no code holds — see <see cref="FlowXTelemetry"/> — and naming them is what keeps the
/// frozen schema one list rather than "the ones we got to". The reason they are unemitted is
/// recorded on their rows in §2, next to the claim.
/// </para>
/// </remarks>
public static class TelemetryNames
{
    // ---- Span attributes (12 §2, "Span attribute schema (normative)") ----

    /// <summary>The flow's business identity, e.g. <c>order.place</c>. On the flow and step spans.</summary>
    public const string FlowId = "flowx.flow.id";

    /// <summary>The flow's SemVer, e.g. <c>1.2.0</c>. On the flow span.</summary>
    public const string FlowVersion = "flowx.flow.version";

    /// <summary>
    /// The journaled instance. On the flow and step spans, and <strong>never</strong> a metric
    /// label — see the cardinality note in <see cref="FlowXTelemetry"/>.
    /// </summary>
    public const string FlowInstanceId = "flowx.flow.instance_id";

    /// <summary>The declared <c>ExecutionProfile</c>, e.g. <c>Durable</c>. On the flow span.</summary>
    public const string FlowProfile = "flowx.flow.profile";

    /// <summary>The step's index in the compiled plan. On the step span.</summary>
    public const string StepId = "flowx.step.id";

    /// <summary>The capability the step invokes. On the step span.</summary>
    public const string CapabilityId = "flowx.capability.id";

    /// <summary>The capability contract's SemVer. On the step span.</summary>
    public const string CapabilityVersion = "flowx.capability.version";

    /// <summary>
    /// Which transport started the flow, e.g. <c>Http</c>. On the flow span.
    /// </summary>
    /// <remarks>
    /// <strong>Named and unemitted.</strong> <c>FlowInvocation</c> carries correlation, an
    /// idempotency key, a tenant and a deadline, and nothing that says what started the flow;
    /// there is no Trigger Engine to learn it from. This is the same gap that leaves
    /// <see cref="TriggerAdmittedTotal"/> without a producer, and it closes the same way.
    /// </remarks>
    public const string TriggerKind = "flowx.trigger.kind";

    /// <summary>
    /// The trigger's address, e.g. <c>POST /api/v1/orders</c>. On the flow span.
    /// </summary>
    /// <remarks>Unemitted, for the reason given on <see cref="TriggerKind"/>.</remarks>
    public const string TriggerSource = "flowx.trigger.source";

    /// <summary>The resolved tenant. On the flow and step spans.</summary>
    /// <remarks>
    /// Unbucketed here, unlike the metric label of the same name. A trace carries one
    /// instance and is retained for days; a metric series carries a tenant for ever, which is
    /// why <see cref="FlowXTelemetry.TenantLabel"/> exists and why it is not applied to spans.
    /// </remarks>
    public const string TenantId = "flowx.tenant.id";

    /// <summary>The step's error code, on failure only.</summary>
    public const string ErrorCode = "flowx.error.code";

    /// <summary>The step's <c>ErrorCategory</c>, on failure only.</summary>
    public const string ErrorCategory = "flowx.error.category";

    /// <summary>
    /// Which attempt at the step this span covers.
    /// </summary>
    /// <remarks>
    /// <strong>Named and unemitted.</strong> The attempt number is derived from committed
    /// journal history inside <c>FlowEngine.CommitStepAsync</c> and is not visible at the
    /// dispatch seam the step span is opened on. It is also 1 by construction today: no policy
    /// executes on the forward path, so a step is dispatched once and the only retries in the
    /// runtime are a compensation's. Emitting a literal 1 would put a number on a dashboard
    /// that can never move and would read as evidence that retries were being counted.
    /// </remarks>
    public const string Attempt = "flowx.attempt";

    // ---- Metrics (12 §3, "Golden signals, automatically, per graph node") ----

    /// <summary>Histogram, seconds. Labels: <c>flow</c>, <c>profile</c>, <c>outcome</c>, <c>tenant</c>.</summary>
    public const string FlowDurationSeconds = "flowx_flow_duration_seconds";

    /// <summary>Gauge. Labels: <c>flow</c>, <c>state</c>.</summary>
    public const string FlowActive = "flowx_flow_active";

    /// <summary>Counter. Labels: <c>flow</c>, <c>outcome</c>.</summary>
    public const string FlowTotal = "flowx_flow_total";

    /// <summary>Histogram, seconds. Labels: <c>flow</c>, <c>step</c>, <c>capability</c>, <c>outcome</c>.</summary>
    public const string StepDurationSeconds = "flowx_step_duration_seconds";

    /// <summary>Histogram, seconds. Labels: <c>capability</c>, <c>outcome</c>.</summary>
    public const string CapabilityDurationSeconds = "flowx_capability_duration_seconds";

    /// <summary>Counter. Label: <c>capability</c>. A defect signal — a capability that threw.</summary>
    public const string CapabilityUnhandledTotal = "flowx_capability_unhandled_total";

    /// <summary>
    /// Counter. Labels: <c>kind</c>, <c>reason</c>, <c>tenant</c>.
    /// </summary>
    /// <remarks>
    /// <strong>This row read "named and unemitted" until the admission seam it wanted
    /// existed.</strong> The claim was that a <c>kind</c> label presupposes one admission point
    /// serving every transport; that point is <c>FlowBusScan.AdmitAsync</c> and
    /// <c>FlowStreamScan.AdmitAsync</c>, which decide what becomes of one item without settling
    /// it, and which both a sweep and a push host reach. <c>reason</c> carries why the item was
    /// admitted — <c>started</c>, or <c>deduplicated</c> for an item an earlier delivery already
    /// journalled.
    /// </remarks>
    public const string TriggerAdmittedTotal = "flowx_trigger_admitted_total";

    /// <summary>
    /// Counter. Labels: <c>kind</c>, <c>reason</c>, <c>tenant</c>.
    /// </summary>
    /// <remarks>
    /// <strong>This row read "named and unemitted" until a rejection had a shape.</strong> The
    /// claim was that "the admission seam's other two answers are a requeue and a dead-letter,
    /// and neither is a rejection anybody has decided the shape of: a requeued item was not
    /// refused, it was left with the broker to offer again, and whether that belongs in the same
    /// series as a poison message is a decision this counter would be freezing rather than
    /// recording". <strong>Every clause of that is still true, and it is no longer the whole
    /// list.</strong> <c>FlowXOptions.MaxInFlightAdmissions</c> added a third answer that neither
    /// of those two describes: an item the node was offered and decided not to run, at a ceiling
    /// it set itself. <c>reason</c> carries why — <c>shed</c> is the one this repository
    /// produces — and an item this counts is not counted by
    /// <see cref="TriggerAdmittedTotal"/>, so the two sum to the offered load.
    /// </remarks>
    public const string TriggerRejectedTotal = "flowx_trigger_rejected_total";

    /// <summary>Histogram, seconds. Label: <c>operation</c>.</summary>
    public const string JournalCommitSeconds = "flowx_journal_commit_seconds";

    /// <summary>Counter. Label: <c>reason</c>.</summary>
    public const string LeaseLostTotal = "flowx_lease_lost_total";

    /// <summary>Gauge. Label: <c>type</c>.</summary>
    public const string OutboxPending = "flowx_outbox_pending";

    /// <summary>Gauge, seconds. Label: <c>type</c>.</summary>
    public const string OutboxLagSeconds = "flowx_outbox_lag_seconds";

    /// <summary>
    /// Gauge. Labels: <c>topic</c>, <c>partition</c>.
    /// </summary>
    /// <remarks>
    /// <strong>Named and unemitted, and the only one of the thirteen with no subject at
    /// all.</strong> <c>ExecutionProfile.Streaming</c> is an enum member no code branches on
    /// and no stream trigger reaches a transport, so this arrives with P7 rather than with an
    /// emitter.
    /// </remarks>
    public const string StreamLagRecords = "flowx_stream_lag_records";

    /// <summary>Counter. Labels: <c>flow</c>, <c>step</c>. Alert on any occurrence.</summary>
    public const string FlowCompensationFailedTotal = "flowx_flow_compensation_failed_total";

    // ---- Policy metrics (10 §9, "Observing policies") ----

    /// <summary>
    /// Counter. Labels: <c>policy</c>, <c>stage</c>, <c>capability</c>, <c>outcome</c>.
    /// </summary>
    /// <remarks>
    /// Every row of <c>docs/10 §9</c> is named below, frozen on the same terms as §3's above and
    /// for the same reason: an alert written against a breaker in one service must match the
    /// breaker in every other.
    /// <para>
    /// <strong>The list of omissions is now empty, which is exactly the condition
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0026-policy-metrics-name-only-what-executes.md">ADR-0026</a>
    /// set.</strong> That record kept the rate-limit, idempotency and cache rows unnamed rather
    /// than named-and-unemitted — the opposite of what was done for
    /// <see cref="TriggerAdmittedTotal"/> and <see cref="StreamLagRecords"/>, because those two
    /// describe a subject that exists and cannot be reached while a counter for an inert stage
    /// describes a decision no code makes. Stages 1, 3 and 5 execute, so each row's name is
    /// frozen here and the omission has become an addition. <c>Audit</c> gains no row of its
    /// own — §9 never gave it one — and reaches <see cref="PolicyInvocationsTotal"/> like every
    /// other policy that applies.
    /// </para>
    /// </remarks>
    public const string PolicyInvocationsTotal = "flowx_policy_invocations_total";

    /// <summary>Counter. Labels: <c>capability</c>, <c>attempt</c>, <c>error_code</c>.</summary>
    public const string RetryAttemptsTotal = "flowx_retry_attempts_total";

    /// <summary>Gauge, 0/1/2. Labels: <c>capability</c>, <c>key</c>.</summary>
    public const string CircuitState = "flowx_circuit_state";

    /// <summary>Gauge. Label: <c>capability</c>.</summary>
    public const string BulkheadQueueDepth = "flowx_bulkhead_queue_depth";

    /// <summary>
    /// Counter. Labels: <c>scope</c>, <c>tenant</c>.
    /// </summary>
    /// <remarks>
    /// <strong>The <c>scope</c> label is the decision <c>ADR-0026</c> said nobody had made.</strong>
    /// It carries the declared <c>RateLimitScope</c> by name — <c>Global</c>, <c>Tenant</c> or
    /// <c>Principal</c> — which is the value that decides what the exhausted budget belonged to
    /// and is therefore the one an operator needs to know which knob to turn. It is bounded by
    /// an enum with three members, so it is a label rather than a cardinality hazard.
    /// <c>tenant</c> is bucketed by <see cref="FlowXTelemetry.TenantLabel"/> like every other
    /// tenant label on a metric.
    /// </remarks>
    public const string RateLimitRejectedTotal = "flowx_ratelimit_rejected_total";

    /// <summary>Counter. Labels: <c>capability</c>, <c>scope</c>.</summary>
    /// <remarks>
    /// Counted on the replay only — a key presented for the first time is not a replay, and
    /// counting it would put every policed step on a dashboard whose number is meant to be the
    /// duplicate work that did <em>not</em> happen. An in-flight refusal is not a replay either:
    /// nothing was returned to the caller, so it is counted by
    /// <see cref="PolicyInvocationsTotal"/>'s <c>rejected</c> outcome instead.
    /// </remarks>
    public const string IdempotencyReplaysTotal = "flowx_idempotency_replays_total";

    /// <summary>Counter. Labels: <c>capability</c>, <c>scope</c>.</summary>
    /// <remarks>
    /// The half an operator sizes a cache from. Paired with <see cref="CacheMissesTotal"/>
    /// rather than shipped alone, for <see cref="PolicyInvocationsTotal"/>'s reason: a hit
    /// count with no miss count cannot express a hit <em>rate</em>, and "forty thousand hits"
    /// means something different against forty-one thousand calls than against four hundred
    /// thousand.
    /// </remarks>
    public const string CacheHitsTotal = "flowx_cache_hits_total";

    /// <summary>Counter. Labels: <c>capability</c>, <c>scope</c>.</summary>
    /// <remarks>
    /// Counts every consultation that dispatched, whichever reason it had: the key was absent,
    /// the entry had expired, or the store could not be reached. The three are not separated,
    /// because the decision they all produce is the same one — call the dependency — and a
    /// store that is down already shows up as an <see cref="ErrorCode"/> nowhere else.
    /// </remarks>
    public const string CacheMissesTotal = "flowx_cache_misses_total";

    // ---- Metric label names ----

    /// <summary>The flow's business identity.</summary>
    public const string FlowLabel = "flow";

    /// <summary>The step's index in the compiled plan.</summary>
    public const string StepLabel = "step";

    /// <summary>The capability the step invokes.</summary>
    public const string CapabilityLabel = "capability";

    /// <summary>How the flow or step ended.</summary>
    public const string OutcomeLabel = "outcome";

    /// <summary>The declared execution profile.</summary>
    public const string ProfileLabel = "profile";

    /// <summary>The instance state a gauge is counting.</summary>
    public const string StateLabel = "state";

    /// <summary>The bucketed tenant. See <see cref="FlowXTelemetry.TenantLabel"/>.</summary>
    public const string TenantLabel = "tenant";

    /// <summary>Which journal call was timed.</summary>
    public const string OperationLabel = "operation";

    /// <summary>Why a lease was lost, or why an admission seam admitted an item.</summary>
    public const string ReasonLabel = "reason";

    /// <summary>Which transport family an admitted item arrived through, e.g. <c>Bus</c>.</summary>
    /// <remarks>
    /// A <c>TriggerKind</c> member's name, so it is bounded by construction. The metric label,
    /// and deliberately not <see cref="TriggerKind"/> — §2's attributes are dotted and §3's
    /// labels are bare, the distinction <see cref="AttemptLabel"/> records.
    /// </remarks>
    public const string KindLabel = "kind";

    /// <summary>The event contract's type name.</summary>
    public const string TypeLabel = "type";

    /// <summary>The policy kind that made a decision, e.g. <c>CircuitBreaker</c>.</summary>
    public const string PolicyLabel = "policy";

    /// <summary>The <c>PolicyStage</c> that policy belongs to, by name.</summary>
    public const string StageLabel = "stage";

    /// <summary>Which attempt at a step is being made, counting from one.</summary>
    /// <remarks>
    /// The metric label, and deliberately not <see cref="Attempt"/>, which is the span
    /// attribute. §2's attribute is dotted and §3's labels are bare, and a shared constant
    /// would put <c>flowx.attempt</c> on a metric where every other label is a single word.
    /// </remarks>
    public const string AttemptLabel = "attempt";

    /// <summary>The error code an attempt failed with.</summary>
    /// <remarks>
    /// Bounded by the flow's error catalogue, which is closed at build time — so this is a
    /// label rather than a cardinality hazard, unlike a message or an instance id.
    /// </remarks>
    public const string ErrorCodeLabel = "error_code";

    /// <summary>The composite key a circuit breaker is tracked under.</summary>
    public const string KeyLabel = "key";

    /// <summary>
    /// What a rate limit's budget, an idempotency record or a cache entry is keyed within:
    /// <c>Tenant</c>, <c>Principal</c> or <c>Global</c>.
    /// </summary>
    /// <remarks>
    /// <c>docs/10 §9</c> froze this label on the rate-limit, idempotency and cache rows alike.
    /// It is the declared scope enum by name and never the resolved value — the tenant itself is
    /// <see cref="TenantLabel"/>, bucketed, and putting a principal in a metric label would be
    /// unbounded cardinality and an identity in a time series that is retained and shipped.
    /// </remarks>
    public const string ScopeLabel = "scope";
}
