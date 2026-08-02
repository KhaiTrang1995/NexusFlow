# FLOWX1049 — Stream lateness, checkpoint or parallelism cannot be read

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0
> **Applies to:** a `[StreamTrigger]` whose `Lateness` or `Checkpoint` is not a **non-negative**
> ISO-8601 duration — unparseable, empty or negative — or whose `Parallelism` is below one. A
> declaration that omits a property takes the attribute's default and is never reported.

## What it means

`StreamWindowSpec.Read` takes four arguments off the attribute. [FLOWX1042](FLOWX1042.md) judges
the first, because a window shape this engine does not implement is a different defect with a
different fix. This rule judges the other three.

`FlowStreamCatalog.Add` reads all four at composition time and **throws** on a value it cannot
use. So the failure mode without this rule is a green build, a green test run, and every replica
of the deployment refusing to start — over three values that were compile-time constants the
whole way. It is [FLOWX1045](FLOWX1045.md)'s shape, one transport over.

### `Window` takes the short form and these do not

```csharp
[StreamTrigger("device.telemetry", Window = "tumbling:1m", Lateness = "10s")]   // reported
[StreamTrigger("device.telemetry", Window = "tumbling:1m", Lateness = "PT10S")] // fine
```

`tumbling:1m` and `PT10S` sitting on one attribute looks like an inconsistency and is a
deliberate one: `Window`'s short form is what [09 §9](../09-Trigger-Model.md#9-stream-trigger)
prints and the two durations say ISO-8601 on the attribute. Accepting both spellings everywhere
would be the kindest-looking choice and the worst one — one value written two ways is two ways
for a manifest diff to report a change nobody made.

**This is not a hypothetical.** 09 §9 printed `Lateness = "10s"` from the day the page was
written until [ADR-0065](../adr/ADR-0065-a-window-is-declared-where-it-is-served.md), and nothing
between that page and a failed rollout read the value.

### Zero is refused for one of the three and accepted for the other two

```csharp
[StreamTrigger("t", Window = "tumbling:1m", Lateness = "PT0S")]   // fine — the stream is in order
[StreamTrigger("t", Window = "tumbling:1m", Checkpoint = "PT0S")] // fine — commit after every window
[StreamTrigger("t", Window = "tumbling:1m", Parallelism = 0)]     // reported
```

That is `StreamWindowSpec.Read`'s own boundary rather than a second opinion about it. A lateness
of nothing is the ordinary declaration for a stream already in order; a `Parallelism` of zero
reads as "the engine decides" and is a subscription that reads a stream and never runs a flow.

Unlike [FLOWX1045](FLOWX1045.md), which refuses a declared `Jitter = "PT0S"`: a spread of zero
asks for a spread and gets none, where a lateness of zero asks for no allowance and gets none.

## Example that triggers it

```csharp
[Flow("telemetry.aggregate", Version = "1.0.0", Profile = ExecutionProfile.Streaming)]
[StreamTrigger("device.telemetry", Window = "tumbling:1m", Lateness = "10s",
    Checkpoint = "PT5S", Parallelism = 8)]
public sealed partial class AggregateTelemetryFlow : Flow<StreamWindowBatch, DeviceStats>
```

`10s` is not ISO-8601. Neither is `00:00:10` or `PT10`.

## How to fix it

```csharp
[StreamTrigger("device.telemetry", Window = "tumbling:1m", Lateness = "PT10S",
    Checkpoint = "PT5S", Parallelism = 8)]
```

## What this rule does not judge

**The `Window`.** Its short form has no framework parser, so checking it here would mean a second
duration parser to keep in step with the runtime's. [FLOWX1042](FLOWX1042.md) judges the shape
family — `tumbling:` and nothing else — and `StreamWindowSpec.Read` judges the duration inside it
at startup.

**Whether the lateness is wide enough for the stream.** A producer whose clock is minutes behind
sends records this engine calls late, and the answer is either a wider `Lateness` or a repaired
producer. `IStreamSideOutput` carries the watermark alongside each late record precisely so an
operator can tell which — see `LateReading` in `samples/realtime-stream`.

## Why an error rather than a warning

Every reason this fires has a fix that produces a declaration the platform runs today. It is
[FLOWX1042](FLOWX1042.md)'s stance, and [FLOWX1045](FLOWX1045.md)'s.

## When to suppress

There is nothing a suppression buys. The registration is emitted either way and throws either
way; suppressing this moves the failure from a build that stops to a rollout that does not start.

## Related

- [ADR-0065](../adr/ADR-0065-a-window-is-declared-where-it-is-served.md) — why the window is
  declared on the trigger rather than in the flow's graph, and what 09 §9 said instead
- [ADR-0055](../adr/ADR-0055-a-window-names-the-instance-it-starts.md) — what the window's
  arguments are for
- [FLOWX1042](FLOWX1042.md) — the other rule a `[StreamTrigger]` can break, about the flow and
  the window's shape rather than about the remaining three arguments
- [FLOWX1045](FLOWX1045.md) — the same rule for a `[CronTrigger]`'s `Jitter`
- [09 §9](../09-Trigger-Model.md#9-stream-trigger) — what a stream declaration binds
- [samples/realtime-stream](../../samples/realtime-stream/) — a stream subscription that declares
  all four
