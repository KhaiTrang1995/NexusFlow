using System.Diagnostics;

namespace FlowX.Observability;

/// <summary>
/// One structured record as
/// <a href="../../../docs/12-Observability.md">12-Observability</a> §4 specifies it: a constant
/// message and the §2 correlating fields, each carried as a field rather than interpolated into
/// the message.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The field names are §2's, not new ones.</strong> §4 says so in as many words — "the
/// correlating fields … are the span attributes in §2" — so the properties below are named for
/// the <see cref="TelemetryNames"/> constants a bridge writes them under, and there is no second
/// vocabulary to keep in step with the first.
/// </para>
/// <para>
/// <strong><see cref="Payload"/> is a <see cref="JournalPayload"/> and there is no property that
/// is a value.</strong> This is the whole of the <c>[Sensitive]</c> guarantee and it is
/// structural rather than remembered, exactly as it is for the journal:
/// <see cref="JournalPayload"/> exposes no accessor for the object graph and its only exit is
/// <see cref="JournalPayload.ToJson"/>, which replaces every declared member with
/// <see cref="JournalPayload.Redacted"/>. A subscriber therefore cannot reach an unredacted
/// value from here, whatever it does — there is nothing to reach. An <c>object? Value</c>
/// property beside a redaction helper would be a control the first subscriber under deadline
/// pressure walks around, which is the argument <see cref="JournalPayload"/>'s own remarks make
/// about stores.
/// </para>
/// <para>
/// <strong>A class, and every property <c>init</c>.</strong> The record is built inside a
/// listener check and handed to <see cref="DiagnosticSource.Write"/> as an <c>object</c>, so a
/// struct would box on the way and buy nothing; immutability is what lets one record be
/// delivered to several subscribers without any of them being able to change what the next one
/// sees.
/// </para>
/// <para>
/// <strong>What is deliberately absent: <c>flowx.attempt</c>.</strong> §4's worked example shows
/// it, and §2's own table says it is "not emitted, and it would be a constant" — the number is
/// 1 by construction because no policy runs on the forward path, so a step is dispatched once.
/// Putting a literal 1 on every record would read as a retry count somebody had measured, which
/// is the fabrication §2 refused for the span attribute and refuses here for the same reason.
/// </para>
/// </remarks>
public sealed class FlowLogRecord
{
    /// <summary>Builds a record at a level, with a message.</summary>
    /// <param name="level">How serious the occurrence is.</param>
    /// <param name="message">
    /// One of <see cref="FlowXLog"/>'s message constants. §4 forbids data in the message, so
    /// this is never interpolated and <c>LogConformanceTests</c> asserts reference equality
    /// against the constant — which an interpolated string cannot satisfy.
    /// </param>
    public FlowLogRecord(FlowLogLevel level, string message)
    {
        Level = level;
        Message = message;
    }

    /// <summary>How serious the occurrence is.</summary>
    public FlowLogLevel Level { get; }

    /// <summary>The constant message. Never carries data — §4.</summary>
    public string Message { get; }

    /// <summary>The flow's business identity — <c>flowx.flow.id</c>.</summary>
    public string? FlowId { get; init; }

    /// <summary>The flow's SemVer — <c>flowx.flow.version</c>.</summary>
    public string? FlowVersion { get; init; }

    /// <summary>The declared <c>ExecutionProfile</c> — <c>flowx.flow.profile</c>.</summary>
    public string? Profile { get; init; }

    /// <summary>
    /// The journaled instance — <c>flowx.flow.instance_id</c>, or <c>null</c> on an ephemeral
    /// flow, which has no instance.
    /// </summary>
    /// <remarks>
    /// <strong>High cardinality, and that is correct here.</strong> §3's rule is that the
    /// instance id is <em>never</em> a metric label — "it belongs in traces and logs" — because a
    /// metric series carries a label for ever and a record is one occurrence. §7 makes the same
    /// distinction for <c>flowx_flow_compensation_failed_total</c>: the metric carries
    /// <c>flow</c> and <c>step</c>, and the alert carries the instance, the correlation and the
    /// tenant.
    /// </remarks>
    public string? InstanceId { get; init; }

    /// <summary>The step's index in the compiled plan — <c>flowx.step.id</c>.</summary>
    public int? StepId { get; init; }

    /// <summary>The capability the step invoked — <c>flowx.capability.id</c>.</summary>
    public string? CapabilityId { get; init; }

    /// <summary>The capability contract's SemVer — <c>flowx.capability.version</c>.</summary>
    public string? CapabilityVersion { get; init; }

    /// <summary>
    /// The resolved tenant — <c>flowx.tenant.id</c>, unbucketed.
    /// </summary>
    /// <remarks>
    /// Unbucketed for the reason <see cref="TelemetryNames.TenantId"/> gives about the span
    /// attribute: <see cref="FlowXTelemetry.TenantLabel"/> caps a metric label because a series
    /// is retained for ever, and a record is not a series.
    /// </remarks>
    public string? TenantId { get; init; }

    /// <summary>The caller's correlation id.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>How the flow, step or compensation ended.</summary>
    public string? Outcome { get; init; }

    /// <summary>The error code, on failure only — <c>flowx.error.code</c>.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>The <c>ErrorCategory</c>, on failure only — <c>flowx.error.category</c>.</summary>
    public string? ErrorCategory { get; init; }

    /// <summary>Which <c>IFlowJournal</c> call this record is about, on a store event.</summary>
    public string? Operation { get; init; }

    /// <summary>The ambient trace, from <see cref="Activity.Current"/> — <c>trace_id</c>.</summary>
    /// <remarks>
    /// §4: <c>trace_id</c> and <c>span_id</c> "come free from <c>Activity.Current</c> once a span
    /// exists, which it now does at both boundaries". Both are <c>null</c> when no trace exporter
    /// is attached, because with no listener <see cref="ActivitySource.StartActivity(string, ActivityKind)"/>
    /// returns <c>null</c> and there is no ambient activity to read — which is the honest answer
    /// rather than a fabricated id.
    /// </remarks>
    public string? TraceId { get; init; }

    /// <summary>The ambient span, from <see cref="Activity.Current"/> — <c>span_id</c>.</summary>
    public string? SpanId { get; init; }

    /// <summary>
    /// What the occurrence was about, wrapped in the type that redacts.
    /// </summary>
    /// <remarks>
    /// A <see cref="JournalPayload"/> and never a value. The payload arrives already carrying its
    /// flow's <c>SensitiveMembers</c> — it is the same object the journal would have been handed,
    /// obtained from <c>IStepDispatcher.DescribeStep</c> — so a member marked
    /// <c>[Sensitive]</c> leaves here as <see cref="JournalPayload.Redacted"/> through the one
    /// exit that exists. <see cref="JournalPayload.Empty"/> when the occurrence has no subject.
    /// </remarks>
    public JournalPayload Payload { get; init; } = JournalPayload.Empty;

    /// <summary>
    /// Returns this record stamped with the ambient trace and span ids.
    /// </summary>
    /// <remarks>
    /// Called by every emitter, after the listener check, so that reading
    /// <see cref="Activity.Current"/> — and formatting two ids out of it — costs nothing on a
    /// process with no subscriber. Returns <c>this</c> unchanged when no activity is current,
    /// so an untraced host allocates one record rather than two.
    /// </remarks>
    /// <returns>This record, or a copy carrying the ids.</returns>
    internal FlowLogRecord WithCurrentTrace()
    {
        if (Activity.Current is not { } activity)
        {
            return this;
        }

        return new FlowLogRecord(Level, Message)
        {
            FlowId = FlowId,
            FlowVersion = FlowVersion,
            Profile = Profile,
            InstanceId = InstanceId,
            StepId = StepId,
            CapabilityId = CapabilityId,
            CapabilityVersion = CapabilityVersion,
            TenantId = TenantId,
            CorrelationId = CorrelationId,
            Outcome = Outcome,
            ErrorCode = ErrorCode,
            ErrorCategory = ErrorCategory,
            Operation = Operation,
            Payload = Payload,
            TraceId = activity.TraceId.ToHexString(),
            SpanId = activity.SpanId.ToHexString(),
        };
    }
}

/// <summary>
/// How serious a <see cref="FlowLogRecord"/> is.
/// </summary>
/// <remarks>
/// <para>
/// <strong>FlowX's own enum, because <c>LogLevel</c> is in a package.</strong>
/// <c>Microsoft.Extensions.Logging.Abstractions</c> is exactly the dependency
/// <c>AbstractionsHasNoDependencies</c> forbids here and the reason §4 was unbuilt, so naming its
/// enum would reintroduce the whole problem for one type.
/// </para>
/// <para>
/// <strong>The numeric values are <c>LogLevel</c>'s, deliberately.</strong> The bridge in
/// <c>FlowX.Logging</c> converts one to the other, and equal values make that conversion a cast
/// whose correctness is checkable rather than a switch whose arms can silently disagree —
/// <c>FlowXLogBridgeTests</c> asserts the correspondence member by member.
/// </para>
/// </remarks>
public enum FlowLogLevel
{
    /// <summary>The most detailed records. Off in production.</summary>
    Trace = 0,

    /// <summary>Records for interactive investigation — a step that succeeded.</summary>
    Debug = 1,

    /// <summary>The normal flow of the application — a flow started, a flow completed.</summary>
    Information = 2,

    /// <summary>An abnormal or unexpected occurrence that did not stop the flow.</summary>
    Warning = 3,

    /// <summary>A failure the current execution could not recover from.</summary>
    Error = 4,

    /// <summary>An unrecoverable failure demanding immediate attention.</summary>
    Critical = 5,
}
