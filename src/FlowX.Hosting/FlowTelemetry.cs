using System.Diagnostics;
using FlowX.Observability;
using FlowX.Runtime;

namespace FlowX.Hosting;

/// <summary>
/// The flow boundary of
/// <a href="../../../docs/12-Observability.md">12-Observability</a> §2 and §3: one span and two
/// measurements per execution, opened where the host admits work and closed where it reports an
/// outcome.
/// </summary>
/// <remarks>
/// <para>
/// <strong>In the host rather than the engine, for the reason the lease is.</strong>
/// <see cref="FlowHost"/> already owns lifecycle — it is what decides whether this process is
/// willing to start a flow at all — and an execution the host refused while draining has no
/// duration to report and no outcome to count. Opening the span here means a refusal is not a
/// flow that failed, which is a distinction <see cref="FlowExecutionResult.Rejected"/> makes
/// and which the metrics would otherwise lose.
/// </para>
/// <para>
/// <strong>A struct, and <c>default</c> means "nothing is listening".</strong> The scope is
/// held across the engine call, so it becomes a field of the host's async state machine; a
/// class would put an allocation on every execution to carry four references that are null
/// whenever no exporter is attached. <see cref="Start"/> returns <c>default</c> without reading
/// a clock or touching an instrument when nothing is listening, which is budget B6 at this
/// boundary.
/// </para>
/// </remarks>
internal readonly struct FlowScope
{
    private readonly Activity? _span;
    private readonly ExecutionPlan? _plan;
    private readonly string? _tenantLabel;
    private readonly string? _tenantId;
    private readonly string? _correlationId;
    private readonly long _startedAt;

    private FlowScope(
        Activity? span,
        ExecutionPlan plan,
        string tenantLabel,
        string? tenantId,
        string? correlationId,
        long startedAt)
    {
        _span = span;
        _plan = plan;
        _tenantLabel = tenantLabel;
        _tenantId = tenantId;
        _correlationId = correlationId;
        _startedAt = startedAt;
    }

    /// <summary>
    /// Whether anything is listening for FlowX spans, FlowX flow metrics or FlowX log events.
    /// </summary>
    /// <remarks>
    /// The log subscriber is one more reason to open a scope, not a fourth kind of scope. A host
    /// that exports no traces and no metrics but does bridge logs
    /// (<a href="../../../docs/12-Observability.md">12-Observability</a> §4) is a supported
    /// configuration, and before this read it got a <c>default</c> scope whose
    /// <see cref="Complete"/> returned immediately.
    /// </remarks>
    internal static bool IsEnabled =>
        FlowXTelemetry.Source.HasListeners()
        || FlowXMetrics.FlowDuration.Enabled
        || FlowXMetrics.FlowTotal.Enabled
        || FlowXLog.IsEnabled;

    /// <summary>The span this execution runs under, or <c>null</c> when nothing is listening.</summary>
    internal Activity? Span => _span;

    /// <summary>
    /// Opens the flow span, or returns a scope that does nothing.
    /// </summary>
    /// <param name="plan">The compiled flow.</param>
    /// <param name="invocation">Correlation, tenant and the caller's remaining budget.</param>
    internal static FlowScope Start(ExecutionPlan plan, in FlowInvocation invocation)
    {
        if (!IsEnabled)
        {
            return default;
        }

        // HasListeners before the interpolation. IsEnabled above is true when a *meter* is
        // attached and no trace exporter is, and in that state StartActivity returns null
        // while its interpolated argument is still built and thrown away — one string per
        // flow, for nobody. StepTelemetry.StartSpan makes the same check for the same reason,
        // where it is one string per step.
        //
        // Then StartActivity before the tags, never the reverse: the attributes below are set
        // on a span that exists rather than gathered into a collection first.
        var span = FlowXTelemetry.Source.HasListeners()
            ? FlowXTelemetry.Source.StartActivity($"flow {plan.Flow.Id}", ActivityKind.Internal)
            : null;

        if (span is not null)
        {
            span.SetTag(TelemetryNames.FlowId, plan.Flow.Id);
            span.SetTag(TelemetryNames.FlowVersion, plan.Flow.Version);
            span.SetTag(TelemetryNames.FlowProfile, plan.Flow.Profile.ToString());

            if (invocation.TenantId is { } tenant)
            {
                span.SetTag(TelemetryNames.TenantId, tenant);
            }
        }

        // After the span, so the record carries the trace and span ids §4's example shows. They
        // come from Activity.Current, which is this span — so a host that bridges logs and also
        // exports traces gets a record joined to the trace, and one that exports no traces gets
        // nulls rather than a fabricated id.
        FlowXLog.WriteFlowStarted(
            plan.Flow.Id,
            plan.Flow.Version,
            plan.Flow.Profile.ToString(),
            invocation.TenantId,
            invocation.CorrelationId);

        return new FlowScope(
            span,
            plan,
            FlowXTelemetry.TenantLabel(invocation.TenantId),
            invocation.TenantId,
            invocation.CorrelationId,
            Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Closes the span and records the flow's duration and outcome.
    /// </summary>
    /// <param name="result">How the execution ended.</param>
    /// <remarks>
    /// <para>
    /// <strong>Three outcomes, because a suspended flow is neither of the other two.</strong>
    /// A flow parked at a signal has not failed and has not finished; counting it as either
    /// would either invent an error rate or claim a completion whose <c>.Return(...)</c> never
    /// ran. Its duration is recorded too, and that is deliberate: how long a flow ran before it
    /// parked is the number an operator wants when a wait is the thing that has gone wrong.
    /// </para>
    /// <para>
    /// The instance id is taken from the result rather than threaded in, because the host mints
    /// it inside <c>OpenAsync</c> after the span is already open and the result is the one place
    /// both journaled and ephemeral executions agree about whether there is one.
    /// </para>
    /// </remarks>
    internal void Complete(in FlowExecutionResult result)
    {
        if (_plan is not { } plan)
        {
            return;
        }

        var outcome = result switch
        {
            { IsSuspended: true } => "Suspended",
            { IsSuccess: true } => "Success",
            _ => "Failure",
        };

        if (_span is { } span)
        {
            if (result.InstanceId is { } instanceId)
            {
                span.SetTag(TelemetryNames.FlowInstanceId, instanceId.ToString());
            }

            if (result.Error is { } error)
            {
                span.SetTag(TelemetryNames.ErrorCode, error.Code);
                span.SetTag(TelemetryNames.ErrorCategory, error.Category.ToString());
                span.SetStatus(ActivityStatusCode.Error, error.Message);
            }

            span.Dispose();
        }

        // After the tags and after the span is closed, but the ids are read from the record's own
        // WithCurrentTrace — which sees the parent once this span is disposed. Written here rather
        // than before the Dispose so that a subscriber cannot observe a flow as completed while
        // the span it belongs to is still open.
        FlowXLog.WriteFlowCompleted(
            plan.Flow.Id,
            plan.Flow.Version,
            plan.Flow.Profile.ToString(),
            result.InstanceId,
            _tenantId,
            _correlationId,
            outcome,
            result.Error?.Code,
            result.Error?.Category.ToString());

        if (FlowXMetrics.FlowDuration.Enabled)
        {
            FlowXMetrics.FlowDuration.Record(
                Stopwatch.GetElapsedTime(_startedAt).TotalSeconds,
                new KeyValuePair<string, object?>(TelemetryNames.FlowLabel, plan.Flow.Id),
                new KeyValuePair<string, object?>(TelemetryNames.ProfileLabel, plan.Flow.Profile.ToString()),
                new KeyValuePair<string, object?>(TelemetryNames.OutcomeLabel, outcome),
                new KeyValuePair<string, object?>(TelemetryNames.TenantLabel, _tenantLabel));
        }

        if (FlowXMetrics.FlowTotal.Enabled)
        {
            FlowXMetrics.FlowTotal.Add(
                1,
                new KeyValuePair<string, object?>(TelemetryNames.FlowLabel, plan.Flow.Id),
                new KeyValuePair<string, object?>(TelemetryNames.OutcomeLabel, outcome));
        }
    }
}

/// <summary>
/// Counts compensations that exhausted their policy, against
/// <see cref="TelemetryNames.FlowCompensationFailedTotal"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the seam <see cref="ICompensationAlertSink"/> was left for.</strong> Its own
/// documentation says so in as many words: it names this metric as "the thing that is not
/// built", and says the engine ships the one fact it is uniquely able to state — that an undo
/// has been given up on — rather than inventing a metrics stack to carry a single counter. This
/// is the counter, attached from outside the engine exactly as that note anticipated.
/// </para>
/// <para>
/// <strong>Labelled <c>flow</c> and <c>step</c>, and nothing else.</strong> The alert carries an
/// instance id, a correlation id and a tenant, and none of them may become a metric label:
/// §3's cardinality rule is that <c>flowx.flow.instance_id</c> is never one, and the other two
/// are unbounded for the same reason. They are on the alert, which is a record per occurrence,
/// and §7 pages on the occurrence rather than on a rate.
/// </para>
/// </remarks>
public sealed class CompensationFailureCounter : ICompensationAlertSink
{
    /// <inheritdoc />
    public void CompensationExhausted(in CompensationAlert alert)
    {
        if (!FlowXMetrics.CompensationFailed.Enabled)
        {
            return;
        }

        FlowXMetrics.CompensationFailed.Add(
            1,
            new KeyValuePair<string, object?>(TelemetryNames.FlowLabel, alert.FlowId),
            new KeyValuePair<string, object?>(
                TelemetryNames.StepLabel,
                alert.StepIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }
}
