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
    /// <strong>Named and unemitted.</strong> A <c>kind</c> label presupposes one admission
    /// point serving every transport, and there is one transport whose admission decisions
    /// happen inside the generated HTTP endpoint. That shared point is P3's.
    /// </remarks>
    public const string TriggerAdmittedTotal = "flowx_trigger_admitted_total";

    /// <summary>Counter. Unemitted, for the reason on <see cref="TriggerAdmittedTotal"/>.</summary>
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
    /// The four rows of <c>docs/10 §9</c> below are frozen on the same terms as §3's above, and
    /// for the same reason: an alert written against a breaker in one service must match the
    /// breaker in every other. The three §9 rows that are <em>not</em> named here —
    /// <c>flowx_ratelimit_rejected_total</c>, the cache hit/miss pair and
    /// <c>flowx_idempotency_replays_total</c> — are omitted rather than named-and-unemitted,
    /// which is the opposite of what was done for <see cref="TriggerAdmittedTotal"/> and
    /// <see cref="StreamLagRecords"/>. The difference is that those two describe a subject that
    /// exists and cannot be reached; a rate-limit rejection counter describes a decision no code
    /// makes, so there is no name to freeze until <c>PolicyStage.Admission</c> is executed and
    /// the shape of its <c>scope</c> label is a decision somebody has made.
    /// </remarks>
    public const string PolicyInvocationsTotal = "flowx_policy_invocations_total";

    /// <summary>Counter. Labels: <c>capability</c>, <c>attempt</c>, <c>error_code</c>.</summary>
    public const string RetryAttemptsTotal = "flowx_retry_attempts_total";

    /// <summary>Gauge, 0/1/2. Labels: <c>capability</c>, <c>key</c>.</summary>
    public const string CircuitState = "flowx_circuit_state";

    /// <summary>Gauge. Label: <c>capability</c>.</summary>
    public const string BulkheadQueueDepth = "flowx_bulkhead_queue_depth";

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

    /// <summary>Why a lease was lost.</summary>
    public const string ReasonLabel = "reason";

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
}
