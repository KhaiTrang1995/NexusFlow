using System.Diagnostics.Metrics;

namespace FlowX.Observability;

/// <summary>
/// The instruments behind <a href="../../../docs/12-Observability.md">12-Observability</a> §3's
/// table, for the twelve rows that have a subject this repository ships.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Twelve of thirteen, and the absence is structural.</strong>
/// <see cref="TelemetryNames.StreamLagRecords"/> has no subject — nothing streams — so it is not
/// created here: an instrument that exists and is never written to publishes an empty series, and
/// an empty series is indistinguishable from a healthy one, which is the exact failure §9's
/// warning box describes. <em>This paragraph read "eleven of thirteen" while
/// <see cref="TelemetryNames.TriggerAdmittedTotal"/> had "a subject and no seam". The seam is
/// <c>AdmitAsync</c>.</em> <em>It then read "twelve of thirteen, and the absences are structural"
/// and named <see cref="TelemetryNames.TriggerRejectedTotal"/> as having "no agreed one". That
/// expired at WP-144: the ceiling is what a rejection now is, and it is neither of the two
/// answers that made the subject unagreed — see <see cref="TriggerRejected"/>.</em>
/// </para>
/// <para>
/// <strong>Every call site checks <see cref="Instrument.Enabled"/> first.</strong> That is
/// budget B6 — "telemetry with no listener costs 0 ns and 0 B per step" — and it is a property
/// of the <em>caller</em>, not of the instrument: <c>Record(value, tags)</c> is cheap with no
/// listener, but building the tags to pass it is not, because a label value that is not already
/// a <see cref="string"/> boxes on the way into a <see cref="KeyValuePair{TKey,TValue}"/>. The
/// guard is what stops a step paying for a box nobody reads.
/// </para>
/// <para>
/// <strong>Durations are seconds, as doubles.</strong> The names say <c>_seconds</c> and the
/// SLOs in §7 are stated in seconds and milliseconds; a histogram recorded in milliseconds
/// under a name ending <c>_seconds</c> is the kind of unit mismatch that survives for years
/// because every dashboard is wrong by the same factor.
/// </para>
/// </remarks>
public static class FlowXMetrics
{
    private const string Seconds = "s";

    /// <summary>How long a flow took, end to end. Labels: flow, profile, outcome, tenant.</summary>
    public static Histogram<double> FlowDuration { get; } = FlowXTelemetry.Meter.CreateHistogram<double>(
        TelemetryNames.FlowDurationSeconds,
        Seconds,
        "Wall-clock duration of one flow execution, from admission to outcome.");

    /// <summary>How many flows have ended, by outcome. Labels: flow, outcome.</summary>
    public static Counter<long> FlowTotal { get; } = FlowXTelemetry.Meter.CreateCounter<long>(
        TelemetryNames.FlowTotal,
        unit: null,
        "Flow executions that reached an outcome.");

    /// <summary>How long one step took. Labels: flow, step, capability, outcome.</summary>
    public static Histogram<double> StepDuration { get; } = FlowXTelemetry.Meter.CreateHistogram<double>(
        TelemetryNames.StepDurationSeconds,
        Seconds,
        "Wall-clock duration of one step dispatch.");

    /// <summary>
    /// How long one capability took. Labels: capability, outcome.
    /// </summary>
    /// <remarks>
    /// Deliberately not the same series as <see cref="StepDuration"/> with a label dropped. §9
    /// diagnoses a latency spike "by capability" and a capability used by six flows is one
    /// dependency with one p99; aggregating the step histogram over <c>flow</c> would answer a
    /// different question, because the same capability at two steps of one flow contributes
    /// twice.
    /// </remarks>
    public static Histogram<double> CapabilityDuration { get; } = FlowXTelemetry.Meter.CreateHistogram<double>(
        TelemetryNames.CapabilityDurationSeconds,
        Seconds,
        "Wall-clock duration of one capability invocation.");

    /// <summary>
    /// Capabilities that threw instead of returning an error. Label: capability.
    /// </summary>
    /// <remarks>
    /// A defect signal, and §7 gives it an SLO of zero with a ticket on any occurrence.
    /// Counted at the dispatch seam and re-thrown, so the engine still converts it into
    /// <c>FlowErrors.Unhandled</c> exactly as it did before anything counted it.
    /// </remarks>
    public static Counter<long> CapabilityUnhandled { get; } = FlowXTelemetry.Meter.CreateCounter<long>(
        TelemetryNames.CapabilityUnhandledTotal,
        unit: null,
        "Capability invocations that threw. Expected failures are values (ADR-0007), so any of these is a defect.");

    /// <summary>
    /// Items an admission seam let into the runtime. Labels: kind, reason, tenant.
    /// </summary>
    /// <remarks>
    /// Written by <c>FlowBusScan.AdmitAsync</c> and <c>FlowStreamScan.AdmitAsync</c>, which is
    /// what makes <c>kind</c> a label rather than a wish: a sweep and a push host reach the same
    /// decision through the same call, so one series covers both routes into a transport. An
    /// item the seam requeued or dead-lettered is not counted here — it was not admitted.
    /// </remarks>
    public static Counter<long> TriggerAdmitted { get; } = FlowXTelemetry.Meter.CreateCounter<long>(
        TelemetryNames.TriggerAdmittedTotal,
        unit: null,
        "Items an admission seam started a flow for, or found an earlier delivery had already journalled.");

    /// <summary>
    /// Items an admission seam refused to let into the runtime. Labels: kind, reason, tenant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The subject is the ceiling, and it is the one this counter was waiting for.</strong>
    /// The row was left without an instrument because the seam's other two answers are a requeue
    /// and a dead-letter, and neither is a rejection: a requeued item was not refused, it was left
    /// with the broker to offer again, and a poison message is a different event. A shed is
    /// neither — the item was offered, the node decided against running it, and it says so — so
    /// <c>reason</c> carries <c>shed</c> and the series counts exactly the decisions
    /// <c>FlowXOptions.MaxInFlightAdmissions</c> causes.
    /// </para>
    /// <para>
    /// <strong>Its sum with <see cref="TriggerAdmitted"/> is the offered load, and that is what
    /// makes it worth having.</strong> A shed item does not touch
    /// <see cref="TriggerAdmitted"/> — putting a refusal under a counter named <c>admitted</c> is
    /// the mislabelling that outlives everyone who could correct it — so the two series add up
    /// rather than overlapping, and a burst is legible as the shed half of one number.
    /// </para>
    /// </remarks>
    public static Counter<long> TriggerRejected { get; } = FlowXTelemetry.Meter.CreateCounter<long>(
        TelemetryNames.TriggerRejectedTotal,
        unit: null,
        "Items an admission seam refused rather than running, because the node was at its in-flight ceiling.");

    /// <summary>How long a journal call took. Label: operation.</summary>
    public static Histogram<double> JournalCommit { get; } = FlowXTelemetry.Meter.CreateHistogram<double>(
        TelemetryNames.JournalCommitSeconds,
        Seconds,
        "Wall-clock duration of one IFlowJournal call, whichever store backs it.");

    /// <summary>Leases this node stopped owning while still executing. Label: reason.</summary>
    public static Counter<long> LeaseLost { get; } = FlowXTelemetry.Meter.CreateCounter<long>(
        TelemetryNames.LeaseLostTotal,
        unit: null,
        "Renewals that were refused or could not be made before the lease would have lapsed.");

    /// <summary>
    /// Compensations that exhausted their policy. Labels: flow, step. Alert on any occurrence.
    /// </summary>
    /// <remarks>
    /// §7 pages immediately on this one, and it is the only row in that table with a target of
    /// exactly zero and no burn-rate window: two systems now disagree about the same business
    /// fact, and nothing automatic resolves it.
    /// </remarks>
    public static Counter<long> CompensationFailed { get; } = FlowXTelemetry.Meter.CreateCounter<long>(
        TelemetryNames.FlowCompensationFailedTotal,
        unit: null,
        "Compensations that failed every attempt their policy allowed. An effect is still standing.");
}
