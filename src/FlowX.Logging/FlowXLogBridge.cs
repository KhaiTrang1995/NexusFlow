using FlowX.Observability;
using Microsoft.Extensions.Logging;

namespace FlowX.Logging;

/// <summary>
/// Writes the structured events <see cref="FlowXLog"/> publishes to an <see cref="ILogger"/>,
/// as <a href="../../../docs/12-Observability.md">12-Observability</a> §4 specifies them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Its own project, and that is the decision §4 was blocked on.</strong>
/// <c>AbstractionsHasNoDependencies</c> (ADR-0009) forbids a package reference in
/// <c>FlowX.Abstractions</c> — "it is referenced by every plugin and by all user code; a
/// dependency here is inherited by everyone" — and
/// <c>Microsoft.Extensions.Logging.Abstractions</c> is a package, while
/// <c>System.Diagnostics.DiagnosticSource</c> is in the shared framework. §4 stated the choice
/// as "either that dependency is accepted, or logging lives above <c>FlowX.Abstractions</c>".
/// This is the second answer, and it is strictly better than the first because it is not a
/// compromise: the platform emits the events unconditionally, this project is the only thing
/// that knows what an <c>ILogger</c> is, and a host that wants neither references neither.
/// </para>
/// <para>
/// <strong>The field names are §2's frozen attribute names.</strong> A record arrives as a
/// <see cref="FlowLogRecord"/> whose properties are named for them, and leaves as a structured
/// state whose keys are the <see cref="TelemetryNames"/> constants themselves — so a query
/// written against a span attribute finds the same key on a log record, which is the whole
/// reason §4 reuses §2's vocabulary rather than inventing one.
/// </para>
/// <para>
/// <strong>The message stays a constant.</strong> It is passed as the message template with no
/// placeholders in it, so a sink groups by it and the count of distinct messages stays bounded
/// by the event set — §4's "Data as fields, never interpolated into the message", which is the
/// logging half of the cardinality discipline §3 states for metric labels.
/// </para>
/// </remarks>
public sealed class FlowXLogBridge : IDisposable
{
    private readonly ILoggerFactory _factory;
    private readonly IDisposable _subscription;
    private readonly Dictionary<string, ILogger> _loggers = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private FlowXLogBridge(ILoggerFactory factory)
    {
        _factory = factory;
        _subscription = FlowXLog.Subscribe(Write);
    }

    /// <summary>
    /// Starts bridging FlowX's events onto loggers from <paramref name="factory"/>.
    /// </summary>
    /// <param name="factory">Where the category loggers come from.</param>
    /// <returns>
    /// The bridge. Disposing it unsubscribes, after which
    /// <see cref="FlowXLog.IsEnabled"/> is false again and the platform is back to costing
    /// nothing — the property <c>TelemetryCostTests</c> asserts.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
    /// <remarks>
    /// A static factory rather than a constructor, because subscribing is the thing that happens
    /// and an object that subscribes in its constructor publishes itself to another thread
    /// before it is fully built.
    /// </remarks>
    public static FlowXLogBridge Attach(ILoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return new FlowXLogBridge(factory);
    }

    /// <summary>Stops bridging. The platform's events go back to having no subscriber.</summary>
    public void Dispose() => _subscription.Dispose();

    /// <summary>
    /// The logger category an event is written under.
    /// </summary>
    /// <param name="eventName">The frozen event name.</param>
    /// <returns>The event name with its last segment removed, e.g. <c>FlowX.Step</c>.</returns>
    /// <remarks>
    /// <strong>The event's area, not the event.</strong> A category is what an operator raises
    /// and lowers a level on, so <c>FlowX.Step</c> is the useful granularity — "stop telling me
    /// about individual steps" is a real request and "stop telling me about step completions but
    /// keep the failures" is served by the level, which is already derived from the outcome. It
    /// is also a bounded set of three, which keeps a per-category logger cache bounded.
    /// </remarks>
    public static string CategoryFor(string eventName)
    {
        ArgumentNullException.ThrowIfNull(eventName);

        var lastDot = eventName.LastIndexOf('.');

        return lastDot > 0 ? eventName[..lastDot] : eventName;
    }

    /// <summary>Maps FlowX's level onto the logging abstraction's.</summary>
    /// <param name="level">The record's level.</param>
    /// <returns>The equivalent <see cref="LogLevel"/>.</returns>
    /// <remarks>
    /// A cast, because <see cref="FlowLogLevel"/>'s members are declared with
    /// <see cref="LogLevel"/>'s numeric values on purpose. Spelled as a method rather than
    /// scattered casts so that <c>FlowXLogBridgeTests</c> has one thing to assert the
    /// correspondence of, member by member — a switch would have arms that can silently
    /// disagree, and a bare cast would have no name to test.
    /// </remarks>
    public static LogLevel ToLogLevel(FlowLogLevel level) => (LogLevel)level;

    private void Write(string eventName, FlowLogRecord record)
    {
        var logger = LoggerFor(eventName);
        var level = ToLogLevel(record.Level);

        if (!logger.IsEnabled(level))
        {
            return;
        }

        logger.Log(level, default, Fields(record), null, static (state, _) => state.Message);
    }

    /// <summary>
    /// The record as the structured state a sink enumerates, keyed by §2's frozen names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the fields the record actually carries. A flow event has no step and a store event
    /// has no flow, and writing the absent ones as nulls would put a key on every record that a
    /// sink then indexes and an operator then has to filter out.
    /// </para>
    /// <para>
    /// <strong><see cref="FlowLogRecord.Payload"/> leaves through
    /// <see cref="JournalPayload.ToJson"/> and there is no other way out of it.</strong> That
    /// call is what applies the flow's <c>SensitiveMembers</c>, so a member declared
    /// <c>[Sensitive]</c> reaches the sink as <see cref="JournalPayload.Redacted"/>. The bridge
    /// could not have leaked it even carelessly — the type it is handed exposes no accessor for
    /// the value — which is the same structural argument that keeps a store out of the object
    /// graph, applied to the third pillar.
    /// </para>
    /// </remarks>
    private static FlowLogState Fields(FlowLogRecord record)
    {
        var fields = new List<KeyValuePair<string, object?>>(12);

        Add(fields, TelemetryNames.FlowId, record.FlowId);
        Add(fields, TelemetryNames.FlowVersion, record.FlowVersion);
        Add(fields, TelemetryNames.FlowProfile, record.Profile);
        Add(fields, TelemetryNames.FlowInstanceId, record.InstanceId);
        Add(fields, TelemetryNames.CapabilityId, record.CapabilityId);
        Add(fields, TelemetryNames.CapabilityVersion, record.CapabilityVersion);
        Add(fields, TelemetryNames.TenantId, record.TenantId);
        Add(fields, TelemetryNames.ErrorCode, record.ErrorCode);
        Add(fields, TelemetryNames.ErrorCategory, record.ErrorCategory);
        Add(fields, CorrelationIdField, record.CorrelationId);
        Add(fields, OutcomeField, record.Outcome);
        Add(fields, OperationField, record.Operation);
        Add(fields, TraceIdField, record.TraceId);
        Add(fields, SpanIdField, record.SpanId);

        if (record.StepId is { } stepId)
        {
            fields.Add(new KeyValuePair<string, object?>(TelemetryNames.StepId, stepId));
        }

        if (!record.Payload.IsEmpty)
        {
            fields.Add(new KeyValuePair<string, object?>(PayloadField, record.Payload.ToJson()));
        }

        return new FlowLogState(record.Message, fields);
    }

    private static void Add(List<KeyValuePair<string, object?>> fields, string name, string? value)
    {
        if (value is not null)
        {
            fields.Add(new KeyValuePair<string, object?>(name, value));
        }
    }

    private ILogger LoggerFor(string eventName)
    {
        lock (_gate)
        {
            if (_loggers.TryGetValue(eventName, out var cached))
            {
                return cached;
            }

            var logger = _factory.CreateLogger(CategoryFor(eventName));
            _loggers[eventName] = logger;

            return logger;
        }
    }

    /// <summary>The caller's correlation id.</summary>
    /// <remarks>
    /// Not in <see cref="TelemetryNames"/> because §2 puts no correlation id on a span: a trace
    /// already <em>is</em> the correlation, so the attribute would be a second answer to the
    /// question <c>trace_id</c> answers. A log record is not a trace and carries it, which is
    /// the same distinction §7 draws when it puts a correlation id on an alert and refuses it as
    /// a metric label.
    /// </remarks>
    public const string CorrelationIdField = "flowx.correlation.id";

    /// <summary>How the flow, step or compensation ended.</summary>
    public const string OutcomeField = "flowx.outcome";

    /// <summary>Which <c>IFlowJournal</c> call a store record is about.</summary>
    public const string OperationField = "flowx.journal.operation";

    /// <summary>The ambient trace. §4 names this one <c>trace_id</c>, undotted.</summary>
    public const string TraceIdField = "trace_id";

    /// <summary>The ambient span. §4 names this one <c>span_id</c>, undotted.</summary>
    public const string SpanIdField = "span_id";

    /// <summary>The redacted payload, as <see cref="JournalPayload.ToJson"/> writes it.</summary>
    public const string PayloadField = "flowx.payload";
}
