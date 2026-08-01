using System.Diagnostics.Metrics;
using System.Globalization;

namespace FlowX.Observability;

/// <summary>
/// The instruments behind <a href="../../../docs/10-Policy-Framework.md">10-Policy-Framework</a>
/// §9's table, for the rows whose policy this repository actually executes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Four instruments, not seven, and the absences are the same discipline
/// <see cref="FlowXMetrics"/> applies.</strong> §9 lists a rate-limit counter, a cache
/// hit/miss pair and an idempotency replay counter. Their policies are not executed, so the
/// instruments are not created: an instrument that exists and is never written to publishes an
/// empty series, and an empty series is indistinguishable from a healthy one. A dashboard
/// showing <c>flowx_ratelimit_rejected_total == 0</c> would be evidence that no request was
/// ever refused, when the truth is that nothing ever refuses.
/// </para>
/// <para>
/// <strong>Separate from <see cref="FlowXMetrics"/> because the tables are separate.</strong>
/// <c>docs/12 §3</c> is the golden-signal schema every flow emits; §9 is the resilience schema
/// a <em>declared policy</em> emits. They share
/// <see cref="FlowXTelemetry.Meter"/> — one meter name is the whole point of that type — and
/// nothing else. Splitting them keeps "which document froze this name" answerable from the
/// class it lives on.
/// </para>
/// <para>
/// <strong>Every call site checks <see cref="Instrument.Enabled"/> first</strong>, which is
/// budget B6 and is a property of the caller rather than of the instrument: recording is cheap
/// with no listener, but building the tags is not, because a non-<see cref="string"/> label
/// boxes on its way into a <see cref="KeyValuePair{TKey,TValue}"/>. Every helper here takes
/// its labels as strings already, and the ones derived from an <see cref="int"/> come out of
/// <see cref="AttemptLabels"/> rather than a fresh <c>ToString</c>.
/// </para>
/// <para>
/// <strong>B2 is untouched, structurally rather than carefully.</strong> Nothing here is
/// reachable unless <c>ExecutionPlan.HasStepPolicies</c> is true and the step's resolved
/// <c>StepPolicy.IsActive</c> is true — the gate
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0023-policy-stages-hook-through-the-plan.md">ADR-0023</a>
/// already put in front of the whole policy path. An ephemeral flow that declares no policy
/// does not reach a guard here; it does not reach this file.
/// </para>
/// </remarks>
public static class PolicyMetrics
{
    /// <summary>The breaker is passing calls through. <c>docs/10 §9</c>'s gauge value 0.</summary>
    public const int CircuitClosed = 0;

    /// <summary>The breaker is admitting one probe to see whether the dependency recovered.</summary>
    public const int CircuitHalfOpen = 1;

    /// <summary>The breaker is refusing. The value an alert fires on.</summary>
    public const int CircuitOpen = 2;

    /// <summary>The policy applied and did not interfere.</summary>
    public const string OkOutcome = "ok";

    /// <summary>The policy refused the call outright — a bulkhead with no permit left.</summary>
    public const string RejectedOutcome = "rejected";

    /// <summary>The call ran out of the budget the policy granted it.</summary>
    public const string TimedOutOutcome = "timed_out";

    /// <summary>A breaker refused because it is open.</summary>
    public const string OpenOutcome = "open";

    /// <summary>A retry used every attempt it was allowed and the step still failed.</summary>
    public const string ExhaustedOutcome = "exhausted";

    /// <summary>A cache was consulted and held nothing usable, so the step was dispatched.</summary>
    /// <remarks>
    /// Distinct from <see cref="OkOutcome"/> on <c>flowx_policy_invocations_total</c> because a
    /// cache that applied cleanly and a cache that saved nothing are different facts, and the
    /// counter's whole purpose is that a rate has a denominator. The dedicated
    /// <see cref="CacheHits"/> / <see cref="CacheMisses"/> pair carries the same split at the
    /// resolution <c>docs/10 §9</c> froze; this label is what keeps the shared counter honest
    /// for an operator who graphs it by <c>policy</c>.
    /// </remarks>
    public const string MissedOutcome = "missed";

    /// <summary>An audit record was written for a step that succeeded.</summary>
    /// <remarks>
    /// The only outcome an <c>Audit</c> reaches the counter with. A record that could not be
    /// written fails the flow rather than being counted — see <see cref="IAuditSink"/> — so
    /// there is no "failed" partner here, and a fall in this series against
    /// <c>flowx_flow_total</c> is what an operator watches instead.
    /// </remarks>
    public const string RecordedOutcome = "recorded";

    /// <summary>
    /// Metric label values for small attempt numbers, so a label costs no allocation and no box.
    /// </summary>
    /// <remarks>
    /// The same trick, and the same reason, as <c>StepTelemetry.StepLabels</c>: an attempt
    /// number is an <see cref="int"/>, an <c>int</c> becomes a metric label by boxing, and a
    /// policy declaring more than thirty-two attempts is rare enough to pay for its own string.
    /// <c>FLOWX1017</c> refuses more than ten in the DSL, so the array is already generous.
    /// </remarks>
    private static readonly string[] AttemptLabels = BuildAttemptLabels(32);

    /// <summary>
    /// Policy applications, by what the policy did. Labels: policy, stage, capability, outcome.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Both halves, deliberately.</strong> A counter that only incremented on refusal
    /// would have no denominator, and "this breaker refused 40 calls" is a different fact
    /// depending on whether it saw 45 or 45,000. The <c>outcome</c> label is what makes the
    /// refusal rate computable from one series.
    /// </para>
    /// <para>
    /// The <c>stage</c> label is constant per policy kind today — all four executed kinds are
    /// <c>Resilience</c> — and is emitted anyway, because §9 froze the label and a stage that
    /// lands later must not change the shape of a series an operator already graphs.
    /// </para>
    /// </remarks>
    public static Counter<long> Invocations { get; } = FlowXTelemetry.Meter.CreateCounter<long>(
        TelemetryNames.PolicyInvocationsTotal,
        unit: null,
        "Applications of a declared policy to a step, by what the policy decided.");

    /// <summary>
    /// Attempts a retry spent beyond the first. Labels: capability, attempt, error_code.
    /// </summary>
    /// <remarks>
    /// <strong>The first dispatch is not counted.</strong> Counting it would make every step
    /// that ever ran appear on a retry dashboard, and the number an operator wants is the
    /// excess: how much traffic this dependency is receiving that it did not earn. The
    /// <c>attempt</c> label separates "many steps retried once" — a flaky dependency — from
    /// "one step retried ten times", which is a dependency that is down.
    /// </remarks>
    public static Counter<long> RetryAttempts { get; } = FlowXTelemetry.Meter.CreateCounter<long>(
        TelemetryNames.RetryAttemptsTotal,
        unit: null,
        "Dispatches of a step beyond its first, made because a Retry policy allowed another.");

    /// <summary>
    /// What a breaker is doing: 0 closed, 1 half-open, 2 open. Labels: capability, key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A synchronous <see cref="Gauge{T}"/> rather than an observable one.</strong> A
    /// breaker's state changes at an instant the engine knows about and is otherwise
    /// unobservable; an observable gauge would need a callback holding every live engine, which
    /// is a static registry that outlives the engines in it. Recording on transition publishes
    /// the same series with no lifetime to manage, and the transitions are rare — a breaker
    /// that changed state often enough for this to be a cost would be an incident in itself.
    /// </para>
    /// <para>
    /// <strong><c>capability</c> and <c>key</c> carry the same value today, and both are
    /// emitted.</strong> <c>docs/10 §6</c> describes a composite breaker key —
    /// <c>Capability | Downstream | Tenant | Partition</c> — of which only <c>Capability</c> is
    /// expressible, so <c>CircuitBreakerState</c> is keyed by capability alone. Dropping the
    /// duplicate label would mean every dashboard written against §9 has to be re-written on
    /// the day the key widens; emitting it costs one string that is already in hand.
    /// </para>
    /// </remarks>
    public static Gauge<int> CircuitState { get; } = FlowXTelemetry.Meter.CreateGauge<int>(
        TelemetryNames.CircuitState,
        unit: null,
        "Circuit breaker state: 0 closed, 1 half-open, 2 open. Recorded when it changes.");

    /// <summary>How many callers are waiting for a bulkhead permit. Label: capability.</summary>
    /// <remarks>
    /// The queue, not the pool. A full pool is a bulkhead doing its job; callers stacking up
    /// behind it is the pool being too small for the load, and it is the leading indicator of
    /// the refusals <see cref="Invocations"/> counts once they start.
    /// </remarks>
    public static Gauge<int> BulkheadQueueDepth { get; } = FlowXTelemetry.Meter.CreateGauge<int>(
        TelemetryNames.BulkheadQueueDepth,
        unit: null,
        "Callers waiting for a bulkhead permit, excluding those already holding one.");

    /// <summary>
    /// Consultations of a declared cache that were served from it. Labels: capability, scope.
    /// </summary>
    /// <remarks>
    /// <strong>Created because stage 5 executes, which is the condition
    /// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0026-policy-metrics-name-only-what-executes.md">ADR-0026</a>
    /// set.</strong> That record left this row and its partner unnamed while nothing consulted
    /// a cache — "an instrument that exists and is never written to publishes an empty series,
    /// and an empty series is indistinguishable from a healthy one". Something consults one
    /// now, so the series carries information and the omission becomes an addition.
    /// </remarks>
    public static Counter<long> CacheHits { get; } = FlowXTelemetry.Meter.CreateCounter<long>(
        TelemetryNames.CacheHitsTotal,
        unit: null,
        "Steps served from a declared Cache without dispatching the capability.");

    /// <summary>
    /// Consultations of a declared cache that dispatched anyway. Labels: capability, scope.
    /// </summary>
    /// <remarks>
    /// The denominator. A hit count alone cannot answer "what fraction of calls to this
    /// dependency are we still making", which is the only question a cache is sized from.
    /// </remarks>
    public static Counter<long> CacheMisses { get; } = FlowXTelemetry.Meter.CreateCounter<long>(
        TelemetryNames.CacheMissesTotal,
        unit: null,
        "Consultations of a declared Cache that found nothing usable and dispatched.");

    /// <summary>Whether anything is listening for any policy metric.</summary>
    /// <remarks>
    /// Read at the one place a policy path begins, so a policed step with no exporter pays a
    /// single property read rather than one per instrument per decision.
    /// </remarks>
    public static bool IsEnabled =>
        Invocations.Enabled || RetryAttempts.Enabled ||
        CircuitState.Enabled || BulkheadQueueDepth.Enabled ||
        CacheHits.Enabled || CacheMisses.Enabled;

    /// <summary>Counts one application of a policy to a step.</summary>
    /// <param name="policy">The descriptor kind, e.g. <c>Timeout</c>.</param>
    /// <param name="stage">The <c>PolicyStage</c> it belongs to, by name.</param>
    /// <param name="capability">The capability the step invokes.</param>
    /// <param name="outcome">What the policy decided.</param>
    public static void Applied(string policy, string stage, string capability, string outcome)
    {
        if (!Invocations.Enabled)
        {
            return;
        }

        Invocations.Add(
            1,
            new KeyValuePair<string, object?>(TelemetryNames.PolicyLabel, policy),
            new KeyValuePair<string, object?>(TelemetryNames.StageLabel, stage),
            new KeyValuePair<string, object?>(TelemetryNames.CapabilityLabel, capability),
            new KeyValuePair<string, object?>(TelemetryNames.OutcomeLabel, outcome));
    }

    /// <summary>Counts one dispatch made because a retry allowed another attempt.</summary>
    /// <param name="capability">The capability being retried.</param>
    /// <param name="attempt">The attempt about to be made, counting from one. Never one.</param>
    /// <param name="errorCode">The code the previous attempt failed with.</param>
    public static void Retried(string capability, int attempt, string errorCode)
    {
        if (!RetryAttempts.Enabled)
        {
            return;
        }

        RetryAttempts.Add(
            1,
            new KeyValuePair<string, object?>(TelemetryNames.CapabilityLabel, capability),
            new KeyValuePair<string, object?>(TelemetryNames.AttemptLabel, AttemptLabel(attempt)),
            new KeyValuePair<string, object?>(TelemetryNames.ErrorCodeLabel, errorCode));
    }

    /// <summary>Publishes a breaker's state, which has just changed.</summary>
    /// <param name="capability">The capability the breaker guards.</param>
    /// <param name="state">
    /// <see cref="CircuitClosed"/>, <see cref="CircuitHalfOpen"/> or <see cref="CircuitOpen"/>.
    /// </param>
    public static void CircuitChanged(string capability, int state)
    {
        if (!CircuitState.Enabled)
        {
            return;
        }

        CircuitState.Record(
            state,
            new KeyValuePair<string, object?>(TelemetryNames.CapabilityLabel, capability),
            new KeyValuePair<string, object?>(TelemetryNames.KeyLabel, capability));
    }

    /// <summary>Publishes how many callers are queued for a bulkhead permit.</summary>
    /// <param name="capability">The capability the pool isolates.</param>
    /// <param name="waiting">Callers waiting, excluding those holding a permit.</param>
    public static void BulkheadQueued(string capability, int waiting)
    {
        if (!BulkheadQueueDepth.Enabled)
        {
            return;
        }

        BulkheadQueueDepth.Record(
            waiting,
            new KeyValuePair<string, object?>(TelemetryNames.CapabilityLabel, capability));
    }

    /// <summary>Counts one step served out of a declared cache.</summary>
    /// <param name="capability">The capability that was not dispatched.</param>
    /// <param name="scope">The declared <c>CacheScope</c>, by name.</param>
    public static void CacheHit(string capability, string scope)
    {
        if (!CacheHits.Enabled)
        {
            return;
        }

        CacheHits.Add(
            1,
            new KeyValuePair<string, object?>(TelemetryNames.CapabilityLabel, capability),
            new KeyValuePair<string, object?>(TelemetryNames.ScopeLabel, scope));
    }

    /// <summary>Counts one consultation of a declared cache that dispatched anyway.</summary>
    /// <param name="capability">The capability that was dispatched.</param>
    /// <param name="scope">The declared <c>CacheScope</c>, by name.</param>
    public static void CacheMiss(string capability, string scope)
    {
        if (!CacheMisses.Enabled)
        {
            return;
        }

        CacheMisses.Add(
            1,
            new KeyValuePair<string, object?>(TelemetryNames.CapabilityLabel, capability),
            new KeyValuePair<string, object?>(TelemetryNames.ScopeLabel, scope));
    }

    /// <summary>The label for an attempt number, without allocating for the common ones.</summary>
    private static string AttemptLabel(int attempt) =>
        (uint)attempt < (uint)AttemptLabels.Length
            ? AttemptLabels[attempt]
            : attempt.ToString(CultureInfo.InvariantCulture);

    private static string[] BuildAttemptLabels(int count)
    {
        var labels = new string[count];

        for (var i = 0; i < count; i++)
        {
            labels[i] = i.ToString(CultureInfo.InvariantCulture);
        }

        return labels;
    }
}
