# FLOWX1042 — Stream-triggered flow cannot be windowed

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0
> **Applies to:** a flow carrying `[StreamTrigger]`.

## What it means

The flow declares a stream trigger, and the generator emits no registration for it — so the
manifest publishes a stream subscription that this host will never read. Three things stop it,
and the message names which.

| Reason | Why it stops the registration |
|---|---|
| the input contract is not `StreamWindowBatch` | A closed window has an interval and its records to give, and nothing else. Each record's body arrives undeserialised, for [FLOWX1039](FLOWX1039.md)'s reason: turning it into a typed contract needs a `JsonTypeInfo` only generated code can name |
| the flow does not declare `Profile = ExecutionProfile.Streaming` | The checkpoint is committed *after* a window's flow has run, so a crash in between makes the engine rebuild that window from the source. What turns the rebuild into a refusal rather than a second aggregation is the derived instance id meeting the journal's primary key ([ADR-0055](../adr/ADR-0055-a-window-names-the-instance-it-starts.md)) — and only a journaled profile has one |
| the window is not `tumbling:<duration>` | Tumbling is the only shape this engine implements. A sliding window assigns one record to several windows and a session window's bounds move as records arrive, so in neither case is a window's identity a function of the event time alone — a window rebuilt after a crash would not derive the id that deduplicates it. A global window is never closed by a watermark, so the checkpoint would never advance |

## Example that triggers it

```csharp
[Flow("telemetry.aggregate", Version = "1.0.0", Profile = ExecutionProfile.Durable)]
//                                              ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^ reason 2
[StreamTrigger("device.telemetry", Window = "session:5m")]
//                                 ^^^^^^^^^^^^^^^^^^^^ reason 3
public sealed partial class AggregateTelemetryFlow : Flow<Reading, DeviceStats>
//                                                        ^^^^^^^ reason 1
```

## How to fix it

```csharp
[Flow("telemetry.aggregate", Version = "1.0.0", Profile = ExecutionProfile.Streaming)]
[StreamTrigger("device.telemetry", Window = "tumbling:1m", Lateness = "PT10S")]
public sealed partial class AggregateTelemetryFlow : Flow<StreamWindowBatch, DeviceStats>
{
    protected override void Define(IFlowBuilder<StreamWindowBatch, DeviceStats> flow) => flow
        .Step<AggregateReadings>()
        .Return(ctx => ctx.Get<DeviceStats>());
}
```

Deserialise each record's payload inside `AggregateReadings`, where a serialiser context is in
scope and the failure is a `Result`.

For a sliding or session window there is no fix in this release. The options are the ones
[FLOWX1028](FLOWX1028.md) lists for a platform gap: express the aggregation as tumbling windows if
it can be, or keep the windowing outside FlowX until the shape ships. Declaring a tumbling window
and re-windowing inside the flow is the one repair to avoid — the flow would be reading state
across invocations that nothing checkpoints.

## When to suppress

Never usefully. The generator emits no registration either way, so a suppression buys a manifest
publishing a stream subscription and a host that reads nothing.

## Why this is an error and FLOWX1028 is a warning

[FLOWX1028](FLOWX1028.md) is a warning because its only fix — a different profile — erases a
declaration the platform will one day honour, and there was nothing else the author could do.
Here every reason has a fix that produces a flow this engine runs today, and the alternative to
stopping the build is a deployment where a stream is never read and nothing says why.

---

**Back to:** [diagnostics index](README.md) ·
[ADR-0055](../adr/ADR-0055-a-window-names-the-instance-it-starts.md) ·
[ADR-0056](../adr/ADR-0056-the-watermark-is-observed-never-wall-clock.md) ·
[Trigger model §9](../09-Trigger-Model.md#9-stream-trigger)
