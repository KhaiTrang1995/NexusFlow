using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace FlowX.Observability;

/// <summary>
/// The third pillar of
/// <a href="../../../docs/12-Observability.md">12-Observability</a> §4: the structured events the
/// flow boundary, the step boundary and the stores write, published through a
/// <see cref="DiagnosticListener"/> so that <c>FlowX.Abstractions</c> keeps its zero package
/// references.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A <see cref="DiagnosticSource"/> rather than an <c>ILogger</c>, and that is the whole
/// decision.</strong> §4 says the pillar was blocked on one:
/// <c>AbstractionsHasNoDependencies</c> (ADR-0009) forbids a package reference here,
/// <c>Microsoft.Extensions.Logging.Abstractions</c> <em>is</em> a package, and
/// <c>System.Diagnostics.DiagnosticSource</c> is in the <c>net10.0</c> shared framework — which
/// is exactly why <see cref="FlowXTelemetry"/>'s <see cref="ActivitySource"/> and
/// <see cref="System.Diagnostics.Metrics.Meter"/> were buildable and this was not. So the
/// platform emits the event and <c>FlowX.Logging</c> bridges it to <c>ILogger</c>. A host that
/// wants neither references neither, and the dependency is not inherited by every plugin and all
/// user code.
/// </para>
/// <para>
/// <strong>Every emit site is guarded, and the guard is in front of the argument.</strong>
/// <see cref="DiagnosticSource.Write"/> is cheap with no subscriber, but the payload handed to it
/// is not: a <see cref="FlowLogRecord"/> is an allocation and a <see cref="Guid"/> formatted into
/// an id is another. C# evaluates an argument before the call that would have discarded it, which
/// is the defect <c>TelemetryCostTests</c> already caught once on
/// <c>StartActivity($"step {i} {id}")</c>. Every method below therefore reads
/// <see cref="IsEnabledFor(string)"/> first and constructs nothing until it is true, and
/// <c>TelemetryCostTests</c> measures the zero.
/// </para>
/// <para>
/// <strong>The event names are frozen on the same terms as §2's attributes.</strong> A
/// subscriber filters by name, so a rename breaks every filter written against a deployment of
/// the previous version. <c>LogConformanceTests</c> compares literals on both sides, so changing
/// one is a deliberate act rather than a typo.
/// </para>
/// <para>
/// <strong>The message is a constant, and that is the cardinality rule for logs.</strong> §4
/// says "Structured only. Data as fields, never interpolated into the message" — which is the
/// same discipline §3 states for metric labels, one pillar over. An interpolated message makes
/// every occurrence its own distinct message, so an aggregator can no longer group by it and the
/// count of distinct messages grows with traffic. Every record below carries one of the
/// <c>…Message</c> constants by reference, and <c>LogConformanceTests</c> asserts reference
/// equality — which an interpolated string cannot satisfy.
/// </para>
/// </remarks>
public static class FlowXLog
{
    /// <summary>
    /// The <see cref="DiagnosticListener"/> name a subscriber attaches to, and the same string
    /// <see cref="FlowXTelemetry.SourceName"/> uses for spans and metrics.
    /// </summary>
    /// <remarks>
    /// One name across all three pillars, for the reason <see cref="FlowXTelemetry.SourceName"/>
    /// gives: an operator wires <c>"FlowX"</c> once, not once per pillar, and a second name would
    /// mean somebody who wired traces silently getting no logs.
    /// </remarks>
    public const string SourceName = "FlowX";

    // ---- Event names (12 §4). Frozen: a subscriber filters on these. ----

    /// <summary>The host admitted a flow and is about to execute it.</summary>
    public const string FlowStarted = "FlowX.Flow.Started";

    /// <summary>A flow reached an outcome — success, failure or suspension.</summary>
    public const string FlowCompleted = "FlowX.Flow.Completed";

    /// <summary>A step finished, whether it succeeded or failed.</summary>
    public const string StepCompleted = "FlowX.Step.Completed";

    /// <summary>A compensation ran.</summary>
    public const string StepCompensated = "FlowX.Step.Compensated";

    /// <summary>A store answered a journal call.</summary>
    public const string JournalCalled = "FlowX.Journal.Called";

    /// <summary>A store refused a journal call — a fenced-out write, or a missing instance.</summary>
    public const string JournalRefused = "FlowX.Journal.Refused";

    // ---- Messages. Constants, because §4 forbids data in the message. ----

    /// <summary>The message <see cref="FlowStarted"/> carries.</summary>
    public const string FlowStartedMessage = "Flow started";

    /// <summary>The message <see cref="FlowCompleted"/> carries on a successful flow.</summary>
    public const string FlowCompletedMessage = "Flow completed";

    /// <summary>The message <see cref="FlowCompleted"/> carries on a failed flow.</summary>
    public const string FlowFailedMessage = "Flow failed";

    /// <summary>The message <see cref="FlowCompleted"/> carries on a flow parked at a signal.</summary>
    public const string FlowSuspendedMessage = "Flow suspended";

    /// <summary>The message <see cref="StepCompleted"/> carries on a successful step.</summary>
    public const string StepCompletedMessage = "Step completed";

    /// <summary>The message <see cref="StepCompleted"/> carries on a failed step.</summary>
    public const string StepFailedMessage = "Step failed";

    /// <summary>The message <see cref="StepCompensated"/> carries.</summary>
    public const string StepCompensatedMessage = "Step compensated";

    /// <summary>The message <see cref="StepCompensated"/> carries when the undo itself failed.</summary>
    public const string StepCompensationFailedMessage = "Step compensation failed";

    /// <summary>The message <see cref="JournalCalled"/> carries.</summary>
    public const string JournalCalledMessage = "Journal call completed";

    /// <summary>The message <see cref="JournalRefused"/> carries.</summary>
    public const string JournalRefusedMessage = "Journal call refused";

    private static readonly DiagnosticListener Listener = new(SourceName);

    /// <summary>Whether anything at all has subscribed to FlowX log events.</summary>
    /// <remarks>
    /// The flow-level question, read once per execution rather than once per event, the way
    /// <c>StepTelemetry.IsEnabled</c> reads the span and metric listeners once per flow.
    /// </remarks>
    public static bool IsEnabled => Listener.IsEnabled();

    /// <summary>Whether anything has subscribed to one named event.</summary>
    /// <param name="eventName">One of the event-name constants on this type.</param>
    /// <remarks>
    /// The guard every emitter reads before it builds anything. A subscriber that filtered by
    /// name gets asked about the name, so a host logging only failures pays nothing for the
    /// successes.
    /// </remarks>
    public static bool IsEnabledFor(string eventName) => Listener.IsEnabled(eventName);

    /// <summary>
    /// Subscribes to every FlowX log event.
    /// </summary>
    /// <param name="onEvent">Receives the event's name and its record.</param>
    /// <returns>A subscription; disposing it stops delivery and restores the zero cost.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="onEvent"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <strong>Typed, so a subscriber does not re-implement the cast.</strong>
    /// <see cref="DiagnosticListener"/> is an
    /// <c>IObservable&lt;KeyValuePair&lt;string, object?&gt;&gt;</c>, so every subscriber would
    /// otherwise cast the payload itself and each one would decide independently what to do with
    /// a payload that is not a <see cref="FlowLogRecord"/>. This is the one place that decides:
    /// anything else is ignored rather than thrown at a listener that is not on the call stack.
    /// </para>
    /// <para>
    /// This is the seam <c>FlowX.Logging</c> attaches to. It is deliberately not the only one —
    /// a host that already consumes <see cref="DiagnosticListener.AllListeners"/> can subscribe
    /// to <see cref="SourceName"/> directly and never reference anything of FlowX's beyond this
    /// assembly.
    /// </para>
    /// </remarks>
    public static IDisposable Subscribe(Action<string, FlowLogRecord> onEvent)
    {
        ArgumentNullException.ThrowIfNull(onEvent);

        return Listener.Subscribe(new RecordObserver(onEvent));
    }

    /// <summary>Writes <see cref="FlowStarted"/> if anything is listening for it.</summary>
    /// <param name="flowId">The flow's business identity.</param>
    /// <param name="flowVersion">The flow's SemVer.</param>
    /// <param name="profile">The declared <c>ExecutionProfile</c>, by name.</param>
    /// <param name="tenantId">The resolved tenant, unbucketed — this is a record, not a series.</param>
    /// <param name="correlationId">The caller's correlation id.</param>
    public static void WriteFlowStarted(
        string flowId, string flowVersion, string profile, string? tenantId, string? correlationId)
    {
        if (!Listener.IsEnabled(FlowStarted))
        {
            return;
        }

        Emit(
            FlowStarted,
            new FlowLogRecord(FlowLogLevel.Information, FlowStartedMessage)
            {
                FlowId = flowId,
                FlowVersion = flowVersion,
                Profile = profile,
                TenantId = tenantId,
                CorrelationId = correlationId,
            }
            .WithCurrentTrace());
    }

    /// <summary>Writes <see cref="FlowCompleted"/> if anything is listening for it.</summary>
    /// <param name="flowId">The flow's business identity.</param>
    /// <param name="flowVersion">The flow's SemVer.</param>
    /// <param name="profile">The declared <c>ExecutionProfile</c>, by name.</param>
    /// <param name="instanceId">The journaled instance, or <c>null</c> on an ephemeral flow.</param>
    /// <param name="tenantId">The resolved tenant.</param>
    /// <param name="correlationId">The caller's correlation id.</param>
    /// <param name="outcome"><c>Success</c>, <c>Failure</c> or <c>Suspended</c>.</param>
    /// <param name="errorCode">The flow's error code, on failure only.</param>
    /// <param name="errorCategory">The flow's <c>ErrorCategory</c>, on failure only.</param>
    /// <remarks>
    /// The level follows the outcome rather than being passed in: a failed flow is a
    /// <see cref="FlowLogLevel.Warning"/> and everything else is
    /// <see cref="FlowLogLevel.Information"/>, decided in one place so two call sites cannot
    /// disagree about what a failure is worth.
    /// </remarks>
    public static void WriteFlowCompleted(
        string flowId,
        string flowVersion,
        string profile,
        Guid? instanceId,
        string? tenantId,
        string? correlationId,
        string outcome,
        string? errorCode,
        string? errorCategory)
    {
        if (!Listener.IsEnabled(FlowCompleted))
        {
            return;
        }

        var failed = errorCode is not null;

        Emit(
            FlowCompleted,
            new FlowLogRecord(
                failed ? FlowLogLevel.Warning : FlowLogLevel.Information,
                failed ? FlowFailedMessage
                    : string.Equals(outcome, SuspendedOutcome, StringComparison.Ordinal)
                        ? FlowSuspendedMessage
                        : FlowCompletedMessage)
            {
                FlowId = flowId,
                FlowVersion = flowVersion,
                Profile = profile,
                InstanceId = instanceId?.ToString(),
                TenantId = tenantId,
                CorrelationId = correlationId,
                Outcome = outcome,
                ErrorCode = errorCode,
                ErrorCategory = errorCategory,
            }
            .WithCurrentTrace());
    }

    /// <summary>Writes <see cref="StepCompleted"/> if anything is listening for it.</summary>
    /// <param name="flowId">The flow's business identity.</param>
    /// <param name="stepId">The step's index in the compiled plan.</param>
    /// <param name="capabilityId">The capability the step invoked, or the step's own identity.</param>
    /// <param name="capabilityVersion">The capability contract's SemVer, where there is one.</param>
    /// <param name="instanceId">The journaled instance, or <c>null</c> on an ephemeral flow.</param>
    /// <param name="tenantId">The resolved tenant.</param>
    /// <param name="outcome"><c>Success</c> or <c>Failure</c>.</param>
    /// <param name="errorCode">The step's error code, on failure only.</param>
    /// <param name="errorCategory">The step's <c>ErrorCategory</c>, on failure only.</param>
    /// <param name="payload">
    /// What the step produced, as the journal would record it. A
    /// <see cref="JournalPayload"/> and never a value — see <see cref="FlowLogRecord.Payload"/>.
    /// </param>
    public static void WriteStepCompleted(
        string flowId,
        int stepId,
        string capabilityId,
        string? capabilityVersion,
        string? instanceId,
        string? tenantId,
        string outcome,
        string? errorCode,
        string? errorCategory,
        JournalPayload? payload)
    {
        if (!Listener.IsEnabled(StepCompleted))
        {
            return;
        }

        var failed = errorCode is not null;

        Emit(
            StepCompleted,
            new FlowLogRecord(
                failed ? FlowLogLevel.Warning : FlowLogLevel.Debug,
                failed ? StepFailedMessage : StepCompletedMessage)
            {
                FlowId = flowId,
                StepId = stepId,
                CapabilityId = capabilityId,
                CapabilityVersion = capabilityVersion,
                InstanceId = instanceId,
                TenantId = tenantId,
                Outcome = outcome,
                ErrorCode = errorCode,
                ErrorCategory = errorCategory,
                Payload = payload ?? JournalPayload.Empty,
            }
            .WithCurrentTrace());
    }

    /// <summary>Writes <see cref="StepCompensated"/> if anything is listening for it.</summary>
    /// <param name="flowId">The flow's business identity.</param>
    /// <param name="stepId">The step's index in the compiled plan.</param>
    /// <param name="capabilityId">The compensation's identity.</param>
    /// <param name="instanceId">The journaled instance, or <c>null</c> on an ephemeral flow.</param>
    /// <param name="tenantId">The resolved tenant.</param>
    /// <param name="outcome"><c>Success</c> or <c>Failure</c>.</param>
    /// <param name="errorCode">The compensation's error code, on failure only.</param>
    /// <param name="errorCategory">The compensation's <c>ErrorCategory</c>, on failure only.</param>
    /// <remarks>
    /// An undo that failed is an <see cref="FlowLogLevel.Error"/> rather than a warning, and it
    /// is the only <c>Error</c> this type writes. §7 pages on
    /// <c>flowx_flow_compensation_failed_total</c> on any occurrence, and a record an operator
    /// is paged about should not arrive at the same level as an ordinary step failure the flow
    /// then compensated for successfully.
    /// </remarks>
    public static void WriteStepCompensated(
        string flowId,
        int stepId,
        string capabilityId,
        string? instanceId,
        string? tenantId,
        string outcome,
        string? errorCode,
        string? errorCategory)
    {
        if (!Listener.IsEnabled(StepCompensated))
        {
            return;
        }

        var failed = errorCode is not null;

        Emit(
            StepCompensated,
            new FlowLogRecord(
                failed ? FlowLogLevel.Error : FlowLogLevel.Debug,
                failed ? StepCompensationFailedMessage : StepCompensatedMessage)
            {
                FlowId = flowId,
                StepId = stepId,
                CapabilityId = capabilityId,
                InstanceId = instanceId,
                TenantId = tenantId,
                Outcome = outcome,
                ErrorCode = errorCode,
                ErrorCategory = errorCategory,
            }
            .WithCurrentTrace());
    }

    /// <summary>
    /// Writes <see cref="JournalCalled"/> or <see cref="JournalRefused"/> if anything is
    /// listening for it.
    /// </summary>
    /// <param name="operation">Which <c>IFlowJournal</c> call answered.</param>
    /// <param name="instanceId">The instance the call was about, where the call names one.</param>
    /// <param name="errorCode">The store's refusal code, or <c>null</c> when it answered.</param>
    /// <param name="errorCategory">The refusal's <c>ErrorCategory</c>, on refusal only.</param>
    /// <remarks>
    /// <strong>Two events rather than one with an outcome field</strong>, because a subscriber
    /// filters by name: a host that wants only the refusals — the fenced-out writes that say a
    /// lease moved — subscribes to one name and pays nothing per successful commit, which is
    /// every commit in a healthy process.
    /// </remarks>
    public static void WriteJournalCall(
        string operation, Guid? instanceId, string? errorCode, string? errorCategory)
    {
        var refused = errorCode is not null;
        var name = refused ? JournalRefused : JournalCalled;

        if (!Listener.IsEnabled(name))
        {
            return;
        }

        Emit(
            name,
            new FlowLogRecord(
                refused ? FlowLogLevel.Warning : FlowLogLevel.Debug,
                refused ? JournalRefusedMessage : JournalCalledMessage)
            {
                Operation = operation,
                InstanceId = instanceId?.ToString(),
                ErrorCode = errorCode,
                ErrorCategory = errorCategory,
            }
            .WithCurrentTrace());
    }

    /// <summary>The outcome name a flow parked at a signal reports.</summary>
    private const string SuspendedOutcome = "Suspended";

    /// <summary>
    /// The one call to <see cref="DiagnosticSource.Write"/> in the platform.
    /// </summary>
    /// <param name="name">The frozen event name.</param>
    /// <param name="record">The record, already stamped and already known to have a subscriber.</param>
    /// <remarks>
    /// <para>
    /// <strong>One write site, because it carries a trimming proof and a proof is worth
    /// stating once.</strong> <see cref="DiagnosticSource.Write"/> is annotated
    /// <c>RequiresUnreferencedCode</c> because the usual subscriber reflects over an
    /// <c>object</c> payload whose type the trimmer cannot see — which is a real hazard for the
    /// usual shape and not for this one. Constraint C2 makes every project here trim- and
    /// NativeAOT-analysed, so the annotation has to be answered rather than inherited.
    /// </para>
    /// <para>
    /// <strong>It is answered twice over.</strong> The payload is always exactly
    /// <see cref="FlowLogRecord"/> — a sealed type in this assembly, named statically at all
    /// five call sites — and <see cref="Subscribe"/> hands it to a subscriber already cast, so
    /// the delivery path this assembly offers reflects over nothing. For a subscriber that
    /// attaches to <see cref="DiagnosticListener.AllListeners"/> directly and does reflect, the
    /// <see cref="DynamicDependencyAttribute"/> below roots the properties it would read, so
    /// they survive trimming rather than being preserved by luck. That is what makes the
    /// suppression a statement about this code rather than a way of not hearing about it.
    /// </para>
    /// </remarks>
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(FlowLogRecord))]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification =
            "The payload is always the sealed FlowLogRecord, named statically at every call " +
            "site, and the DynamicDependency above roots its public properties for a " +
            "subscriber that reads them reflectively. Nothing here discovers a type.")]
    private static void Emit(string name, FlowLogRecord record) => Listener.Write(name, record);

    /// <summary>Adapts an <see cref="Action{T1,T2}"/> to the observer the listener wants.</summary>
    private sealed class RecordObserver(Action<string, FlowLogRecord> onEvent)
        : IObserver<KeyValuePair<string, object?>>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value is FlowLogRecord record)
            {
                onEvent(value.Key, record);
            }
        }
    }
}
