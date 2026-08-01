using System.Diagnostics;
using FlowX.Observability;

namespace FlowX.Runtime;

/// <summary>
/// The step boundary of
/// <a href="../../../docs/12-Observability.md">12-Observability</a> §2 and §3, instrumented as
/// an <see cref="IStepDispatcher"/> decorator.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A decorator, so <see cref="FlowEngine"/> is not edited at all.</strong> The step
/// boundary is a call the engine makes rather than a place inside it: every capability
/// invocation, every emit and every satisfied wait passes through
/// <see cref="IStepDispatcher.ExecuteAsync"/>, and every undo through
/// <see cref="IStepDispatcher.CompensateAsync"/>. Wrapping the contract observes all of them
/// without a line in the loop — which keeps the engine's allocation budget a fact about the
/// engine rather than a fact about the engine plus whatever telemetry currently does, and
/// leaves the loop free for the policy engine to change underneath.
/// </para>
/// <para>
/// <strong>What it therefore does not see.</strong> A sub-flow's child dispatcher arrives
/// through <see cref="IStepDispatcher.BeginSubFlow"/> and is forwarded unwrapped, so a composed
/// child's steps produce no step spans of their own; the composition itself does, as one step
/// of the parent. Wrapping the child would need this type to know the child's plan and the
/// child's instance, which is the engine's knowledge and not the seam's.
/// </para>
/// <para>
/// <strong>Nothing is constructed when nothing is listening.</strong> <see cref="Wrap"/>
/// returns the caller's own dispatcher unless a listener or a meter is attached, so budget B6
/// holds at the seam rather than inside it: with no exporter there is no decorator, no span
/// attempt and no tag list, and the engine dispatches to exactly the object it was given.
/// <c>TelemetryCostTests</c> asserts both halves.
/// </para>
/// </remarks>
public sealed class StepTelemetry : IStepDispatcher
{
    /// <summary>
    /// Metric label values for small step indices, so a label costs no allocation and no box.
    /// </summary>
    /// <remarks>
    /// §8 asks for "pre-resolved tag arrays from the plan". This is the same idea one level
    /// down and without a per-plan cache to invalidate: a step index is an <see cref="int"/>,
    /// an <c>int</c> becomes a metric label by boxing, and a flow with more than sixty-four
    /// steps is rare enough to pay for its own string.
    /// </remarks>
    private static readonly string[] StepLabels = BuildStepLabels(64);

    private readonly IStepDispatcher _inner;
    private readonly ExecutionPlan _plan;
    private readonly string? _tenantId;
    private readonly string? _instanceId;

    private StepTelemetry(
        IStepDispatcher inner,
        ExecutionPlan plan,
        string? tenantId,
        string? instanceId)
    {
        _inner = inner;
        _plan = plan;
        _tenantId = tenantId;
        _instanceId = instanceId;
    }

    /// <summary>
    /// Whether anything is listening for FlowX spans or FlowX step metrics.
    /// </summary>
    /// <remarks>
    /// Read once per flow rather than once per step. A listener attached mid-flow is picked up
    /// by the next invocation, which is the same freshness an exporter added at run time gets
    /// from any other library, and it is what keeps the cost of being unobserved one virtual
    /// call at the flow boundary instead of four property reads per step.
    /// </remarks>
    public static bool IsEnabled =>
        FlowXTelemetry.Source.HasListeners()
        || FlowXMetrics.StepDuration.Enabled
        || FlowXMetrics.CapabilityDuration.Enabled
        || FlowXMetrics.CapabilityUnhandled.Enabled;

    /// <summary>
    /// Wraps <paramref name="inner"/> so its steps are observed, or returns it unchanged when
    /// nothing is listening.
    /// </summary>
    /// <param name="plan">The compiled flow, which is where the step's identity comes from.</param>
    /// <param name="invocation">The tenant this execution runs for.</param>
    /// <param name="inner">The dispatcher the engine would otherwise be handed.</param>
    /// <param name="instanceId">
    /// The journaled instance, or <c>null</c> for an ephemeral flow — which has no instance,
    /// so its step spans carry no <c>flowx.flow.instance_id</c> rather than an invented one.
    /// </param>
    /// <returns>
    /// <paramref name="inner"/> itself when <see cref="IsEnabled"/> is false. Reference
    /// equality is the assertion <c>TelemetryCostTests</c> makes, because it is the only
    /// evidence that costs nothing to produce.
    /// </returns>
    public static IStepDispatcher Wrap(
        ExecutionPlan plan,
        in FlowInvocation invocation,
        IStepDispatcher inner,
        Guid? instanceId = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(inner);

        return IsEnabled
            ? new StepTelemetry(inner, plan, invocation.TenantId, instanceId?.ToString())
            : inner;
    }

    /// <inheritdoc />
    public async ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
    {
        var step = _plan.Graph.Steps[stepIndex];
        var identity = step.Identity;

        using var span = StartSpan("step", stepIndex, step, identity);

        var startedAt = Stopwatch.GetTimestamp();

        StepOutcome outcome;

        try
        {
            outcome = await _inner.ExecuteAsync(stepIndex, ctx, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A capability that throws is a defect (ADR-0007), and §7 gives it an SLO of zero
            // with a ticket on any occurrence. Counted here and re-thrown unchanged, so the
            // engine still turns it into FlowErrors.Unhandled and still compensates: this
            // observes the defect, it does not change what happens to it.
            if (FlowXMetrics.CapabilityUnhandled.Enabled)
            {
                FlowXMetrics.CapabilityUnhandled.Add(
                    1, new KeyValuePair<string, object?>(TelemetryNames.CapabilityLabel, identity));
            }

            Fail(span, exception);
            Record(startedAt, stepIndex, step, identity, "Failure");

            throw;
        }

        var outcomeLabel = outcome.IsSuccess ? "Success" : "Failure";

        if (span is not null && outcome.Error is { } error)
        {
            span.SetTag(TelemetryNames.ErrorCode, error.Code);
            span.SetTag(TelemetryNames.ErrorCategory, error.Category.ToString());
            span.SetStatus(ActivityStatusCode.Error, error.Message);
        }

        Record(startedAt, stepIndex, step, identity, outcomeLabel);

        return outcome;
    }

    /// <inheritdoc />
    /// <remarks>
    /// An undo is a step boundary too, and the one an operator is most likely to be reading a
    /// trace for. It is spanned under its own name so that a compensation is not silently
    /// aggregated into the forward step's histogram — <c>step 1 inventory.reserve</c> taking
    /// 41 ms and <c>compensate 1 inventory.release</c> taking five attempts are different
    /// facts about the same index.
    /// </remarks>
    public async ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
    {
        var step = _plan.Graph.Steps[stepIndex];
        var identity = step.CompensationIdentity;

        using var span = StartSpan("compensate", stepIndex, step, identity);

        try
        {
            var outcome = await _inner.CompensateAsync(stepIndex, ctx, ct).ConfigureAwait(false);

            if (span is not null && outcome.Error is { } error)
            {
                span.SetTag(TelemetryNames.ErrorCode, error.Code);
                span.SetTag(TelemetryNames.ErrorCategory, error.Category.ToString());
                span.SetStatus(ActivityStatusCode.Error, error.Message);
            }

            return outcome;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fail(span, exception);
            throw;
        }
    }

    /// <inheritdoc />
    public bool Evaluate(int stepIndex, FlowContext ctx) => _inner.Evaluate(stepIndex, ctx);

    /// <inheritdoc />
    public int Select(int stepIndex, FlowContext ctx) => _inner.Select(stepIndex, ctx);

    /// <inheritdoc />
    public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
        _inner.BeginIteration(stepIndex, ctx);

    /// <inheritdoc />
    public FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
        _inner.EnterIteration(stepIndex, source, iteration, ctx);

    /// <inheritdoc />
    public SubFlowSource BeginSubFlow(int stepIndex, FlowContext ctx) => _inner.BeginSubFlow(stepIndex, ctx);

    /// <inheritdoc />
    public void EnterSubFlow(int stepIndex, in SubFlowSource source, FlowContext child) =>
        _inner.EnterSubFlow(stepIndex, source, child);

    /// <inheritdoc />
    public StepJournalEntry DescribeStep(int stepIndex, FlowContext ctx) => _inner.DescribeStep(stepIndex, ctx);

    /// <inheritdoc />
    public JournalPayload DescribeInput(object? input) => _inner.DescribeInput(input);

    /// <inheritdoc />
    public void RestoreState(FlowContext ctx, string stateBagJson) => _inner.RestoreState(ctx, stateBagJson);

    private static string[] BuildStepLabels(int count)
    {
        var labels = new string[count];

        for (var i = 0; i < count; i++)
        {
            labels[i] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return labels;
    }

    private static string StepLabel(int stepIndex) =>
        (uint)stepIndex < (uint)StepLabels.Length
            ? StepLabels[stepIndex]
            : stepIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void Fail(Activity? span, Exception exception)
    {
        if (span is null)
        {
            return;
        }

        span.SetTag(TelemetryNames.ErrorCode, "capability.unhandled");
        span.SetTag(TelemetryNames.ErrorCategory, nameof(FlowX.ErrorCategory.Internal));
        span.SetStatus(ActivityStatusCode.Error, exception.Message);
    }

    private Activity? StartSpan(string verb, int stepIndex, StepNode step, string identity)
    {
        // HasListeners before the interpolation, and this order is budget B6 rather than a
        // micro-optimisation. StartActivity returns null with no listener — but its *argument*
        // is evaluated first, so `StartActivity($"step {i} {id}")` allocates the name on every
        // step of every flow and then throws it away. Measured at 68 B per step before this
        // check existed, which is what TelemetryCostTests caught.
        if (!FlowXTelemetry.Source.HasListeners())
        {
            return null;
        }

        // The same reasoning one level down: the span is started first and the attributes are
        // set on the result, never built into a collection and passed in. A sampler that drops
        // this activity still returns null here.
        var span = FlowXTelemetry.Source.StartActivity(
            $"{verb} {stepIndex} {identity}", ActivityKind.Internal);

        if (span is null)
        {
            return null;
        }

        span.SetTag(TelemetryNames.FlowId, _plan.Flow.Id);
        span.SetTag(TelemetryNames.StepId, stepIndex);
        span.SetTag(TelemetryNames.CapabilityId, identity);

        if (step.Capability is { } capability)
        {
            span.SetTag(TelemetryNames.CapabilityVersion, capability.Version);
        }

        if (_instanceId is not null)
        {
            span.SetTag(TelemetryNames.FlowInstanceId, _instanceId);
        }

        if (_tenantId is not null)
        {
            span.SetTag(TelemetryNames.TenantId, _tenantId);
        }

        return span;
    }

    private void Record(long startedAt, int stepIndex, StepNode step, string identity, string outcome)
    {
        var elapsed = Stopwatch.GetElapsedTime(startedAt).TotalSeconds;

        if (FlowXMetrics.StepDuration.Enabled)
        {
            FlowXMetrics.StepDuration.Record(
                elapsed,
                new KeyValuePair<string, object?>(TelemetryNames.FlowLabel, _plan.Flow.Id),
                new KeyValuePair<string, object?>(TelemetryNames.StepLabel, StepLabel(stepIndex)),
                new KeyValuePair<string, object?>(TelemetryNames.CapabilityLabel, identity),
                new KeyValuePair<string, object?>(TelemetryNames.OutcomeLabel, outcome));
        }

        // Only a real capability. An emit, a satisfied wait and a declared failure are step
        // boundaries with no dependency behind them, and putting them in the capability
        // histogram would make "p99 by capability" include time no capability spent.
        if (step.Capability is not null && FlowXMetrics.CapabilityDuration.Enabled)
        {
            FlowXMetrics.CapabilityDuration.Record(
                elapsed,
                new KeyValuePair<string, object?>(TelemetryNames.CapabilityLabel, identity),
                new KeyValuePair<string, object?>(TelemetryNames.OutcomeLabel, outcome));
        }
    }
}
