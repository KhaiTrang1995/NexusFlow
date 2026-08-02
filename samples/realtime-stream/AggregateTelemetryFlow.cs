using FlowX;

namespace RealtimeStream;

/// <summary>
/// Aggregates a minute of device telemetry, <strong>started by nothing but a window
/// closing</strong>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing in this file manages an offset, a watermark or a checkpoint, and that is the
/// claim the sample exists to show.</strong> The <c>[StreamTrigger]</c> below is read once: it
/// produces the <c>triggers</c> block of <c>flowx.manifest.json</c> and the registration
/// <c>AddFlowXStreamSubscriptions()</c> makes, and from then on <c>FlowStreamScan</c> reads the
/// stream under a bounded channel, assigns each record to a window from its event time, closes a
/// window when the observed watermark passes its upper bound, starts this flow once per closed
/// window, and commits the position of the longest settled prefix. There is no hosted service in
/// this project, no consumer loop, and no mention of Redis anywhere below this attribute.
/// </para>
/// <para>
/// <strong>The input is <see cref="StreamWindowBatch"/>, and it could not be anything
/// else.</strong> <c>FLOWX1042</c> reports a stream-triggered flow whose input contract is
/// something narrower, and the reason is not tidiness: the flow's input is journaled on
/// <c>flow_instance.input</c> and read back verbatim on a resume, so the interval has to be
/// <em>in</em> it. A flow that took a bare list of readings would have to ask a clock which
/// minute it was aggregating, and a resumed instance would ask on a different minute and commit
/// a different answer — which is exactly what <c>FLOWX1007</c> and <c>FLOWX1011</c> forbid.
/// </para>
/// <para>
/// <strong>The aggregation is a capability, not a builder call.</strong>
/// <c>samples/realtime-stream/README.md</c> was written against a <c>.Window(...)</c> /
/// <c>.Aggregate(...)</c> surface that <c>IFlowBuilder</c> does not have. The shipped shape is
/// the one below: the window is <em>declared</em> on the trigger, because it is a property of
/// what the flow's output means and travels with the registration; the fold over the window's
/// records is <see cref="FoldReadings"/>, because it is a business rule and business rules live
/// in capabilities. Nothing is lost by the split — a builder-level <c>.Aggregate(...)</c> would
/// have to take a lambda, and a lambda in a flow definition is code the manifest cannot publish
/// and a reviewer cannot find.
/// </para>
/// <para>
/// <strong><c>Streaming</c> is load-bearing, in the way <c>Durable</c> is on
/// <c>offer.window.close</c>.</strong> The checkpoint is committed <em>after</em> a window's flow
/// has run, so every crash in between re-reads that window's records and rebuilds it — and what
/// turns the second run into a refusal rather than a second aggregate is the derived instance id
/// meeting <c>flow_instance</c>'s primary key. An <c>Ephemeral</c> flow journals nothing, so the
/// id would be inert; <c>FlowStreamCatalog.Add</c> refuses the registration and <c>FLOWX1042</c>
/// refuses the source
/// (<a href="../../docs/adr/ADR-0055-a-window-names-the-instance-it-starts.md">ADR-0055</a>).
/// </para>
/// <para>
/// <strong>Lateness is <c>PT10S</c> and not <c>10s</c>.</strong> The README wrote the latter;
/// <see cref="StreamTriggerAttribute.Lateness"/> is an ISO-8601 duration and
/// <c>StreamWindowSpec.Read</c> refuses anything else at startup. <c>Window</c> is the one that
/// takes the short form, because <c>tumbling:1m</c> is what docs/09 §9 prints and accepting both
/// spellings everywhere would give a manifest diff two ways to report a change nobody made.
/// </para>
/// </remarks>
[Flow("telemetry.aggregate", Version = "1.0.0", Profile = ExecutionProfile.Streaming, Owner = "platform")]
[StreamTrigger(
    "device.telemetry",
    Window = "tumbling:1m",
    Lateness = "PT10S",
    Checkpoint = "PT5S",
    Parallelism = 8)]
[FlowDeadline("PT60S")]
public sealed partial class AggregateTelemetryFlow : Flow<StreamWindowBatch, DeviceStats>
{
    /// <inheritdoc />
    protected override void Define(IFlowBuilder<StreamWindowBatch, DeviceStats> flow)
    {
        // CA1062, answered the way every other flow in this repository answers it.
        ArgumentNullException.ThrowIfNull(flow);

        flow
            // Deserialise and fold, in that order and in one step, because they are one
            // decision: what a malformed record does to the window's answer. It is counted and
            // the rest of the window is aggregated — see FoldReadings for why that is the
            // choice rather than refusing the window.
            .Step<FoldReadings>()

            // A read over the fold's output, so it is safe to retry and declares as much.
            // Nothing here calls out of the process.
            .Step<DetectAnomalies>()

            // The slow one, and deliberately the last. Everything upstream of it is fast, so
            // when the store is slower than the stream this is the step the backpressure is
            // measured against — the engine stops asking Redis for records rather than growing
            // a queue in front of it. BoundedMemoryTests is that property, asserted.
            .Step<PersistAggregate, AggregateToPersist>(ctx => new AggregateToPersist(
                ctx.Get<DeviceStats>(),
                ctx.Get<AnomalyVerdict>().IsAnomalous,
                ctx.Get<AnomalyVerdict>().Reason))
                .WithPolicy(Policies.BulkWrite)

            // Staged in this step's own transaction, because a Streaming flow is journaled
            // exactly as a Durable one is and so has a transaction to stage into. That is the
            // condition FLOWX1024 checks, and it is why this line needs no suppression.
            .Emit<AggregateComputed>(ctx => new AggregateComputed(
                ctx.Input.Source,
                ctx.Input.WindowStart,
                ctx.Input.WindowEnd,
                ctx.Get<DeviceStats>().Readings,
                ctx.Get<DeviceStats>().MeanCelsius,
                ctx.Get<AnomalyVerdict>().IsAnomalous))

            // The window's statistics are the flow's answer. Nothing reads it over a transport —
            // a stream-triggered flow has no caller — but it is what the journal records as the
            // instance's output, which is what a replay of a rebuilt window is compared against.
            .Return(ctx => ctx.Get<DeviceStats>());
    }
}
