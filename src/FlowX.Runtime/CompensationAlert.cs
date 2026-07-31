namespace FlowX.Runtime;

/// <summary>
/// What the engine says when a compensation has exhausted its policy and given up.
/// </summary>
/// <param name="FlowId">The flow whose undo failed.</param>
/// <param name="FlowVersion">The version the instance is pinned to.</param>
/// <param name="InstanceId">
/// The journaled instance, or <c>null</c> for an ephemeral flow — which has no instance and
/// no replay, and whose alert is therefore a report rather than a task.
/// </param>
/// <param name="StepIndex">The step whose effect is still standing.</param>
/// <param name="CompensationId">The compensating capability that would not run.</param>
/// <param name="CorrelationId">Ties this to the request that started the flow.</param>
/// <param name="TenantId">Whose data is now inconsistent.</param>
/// <param name="Attempts">How many times the undo was dispatched before it was given up on.</param>
/// <param name="Error">The last failure, which is what has to be fixed before a replay.</param>
/// <remarks>
/// <para>
/// <strong>Every field is there because the runbook needs it.</strong>
/// <c>docs/11-Distributed-Runtime.md §8</c>'s recovery path is
/// <c>flowx replay --instance &lt;id&gt; --from &lt;step&gt;</c> after the downstream fault is
/// fixed — so an alert without the instance and the step is an alert nobody can act on, and
/// one without the error names no fault to fix.
/// </para>
/// <para>
/// A <c>readonly record struct</c>, passed by <c>in</c>: raising one allocates nothing, which
/// matters because the alert is raised from the failure path of a flow that is already
/// having a bad time.
/// </para>
/// </remarks>
public readonly record struct CompensationAlert(
    string FlowId,
    string FlowVersion,
    Guid? InstanceId,
    int StepIndex,
    string CompensationId,
    string CorrelationId,
    string? TenantId,
    int Attempts,
    Error Error);

/// <summary>
/// Where an exhausted compensation is reported to.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The seam, not the observability.</strong>
/// <c>docs/12-Observability.md</c> specifies <c>flowx_flow_compensation_failed_total</c> with
/// <c>flow</c> and <c>step</c> labels and "always alert" beside it, and
/// <c>docs/05-Architecture.md</c> adds a dead-letter record. Neither is built here: FlowX
/// ships no metrics or logging infrastructure yet, and inventing one inside the engine to
/// carry a single counter would be a worse decision than leaving a seam the observability
/// package attaches to. What ships is the one thing the engine is uniquely able to say — that
/// this undo has been given up on — and the guarantee that it is said exactly once per
/// exhausted compensation.
/// </para>
/// <para>
/// <strong>Synchronous and returning nothing, on purpose.</strong> This is called on the
/// failure path of a flow that is unwinding, and an <c>await</c> here would put a state
/// machine on it and let a slow pager hold a pooled context open. An implementation that
/// needs to do IO enqueues; it does not block.
/// </para>
/// <para>
/// A sink that throws is swallowed and the unwind continues. A broken pager must not leak the
/// inventory reservation the engine was in the middle of releasing — the alert is how the
/// operator hears about one failure, not a reason to create several more.
/// </para>
/// </remarks>
public interface ICompensationAlertSink
{
    /// <summary>Reports one compensation that failed and will not be attempted again.</summary>
    /// <param name="alert">What failed, where, and how many times it was asked.</param>
    void CompensationExhausted(in CompensationAlert alert);
}
