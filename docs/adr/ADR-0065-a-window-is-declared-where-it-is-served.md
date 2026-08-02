# ADR-0065: A window is declared where it is served, and a fold is a capability

**Status:** Accepted
**Date:** 2026-08-02
**Deciders:** Repository owner · Runtime team · Platform architecture
**Amends:** [ADR-0055](ADR-0055-a-window-names-the-instance-it-starts.md) — its *Revisit when*
clause only

## Context

[09 §9](../09-Trigger-Model.md#9-stream-trigger) has printed this since the page was written:

```csharp
flow.Window(w => w.Tumbling(TimeSpan.FromMinutes(1)).AllowLateness(TimeSpan.FromSeconds(10)))
    .Aggregate<DeviceStats>((acc, r) => acc.Add(r))
```

Neither method is a member of `IFlowBuilder<TIn, TOut>`, so the example does not compile and never
did. `ADR-0055` named it while deciding something else; `samples/realtime-stream` says so in a
remark; the shipped shape declares the window on `[StreamTrigger]` and folds inside a capability.

Two readings are available and only one can be right. Either the builder surface is the intended
design and the engine is behind it, in which case the methods should be built — or the trigger
declaration is the design and the page is wrong, in which case the page should be corrected and a
feature should not be built to make a stale document true.

## Decision

**`IFlowBuilder` gains no `.Window(…)` and no `.Aggregate(…)`. A window is declared on
`[StreamTrigger]`, where the subscription that serves it is declared; a fold over a closed
window's records is an ordinary `.Step<TCapability>()`. 09 §9's example is corrected to the shape
that runs.**

### 1. A window is not a node in the flow's graph, and a builder method says it is

`IFlowBuilder`'s own contract is one sentence: *every method here maps to a node in
`flowx.manifest.json`*. A window fails that on both sides.

A window closes **before** the instance exists. `FlowStreamScan` assigns records, waits for the
watermark, derives an id and only then calls `FlowHost.RunAsync` — so a `.Window(…)` node would
sit in a plan that is entered once the thing it describes has already happened. The step loop
would step over it, every time, for ever.

And publishing it is worse than not executing it. The manifest's `steps` array is what
`flowx diff` reads to decide whether a flow's *graph* changed; a widened window would be reported
as a graph change to consumers for whom nothing moved — the input contract is still
`StreamWindowBatch`, the output is still the aggregate, and a stream-triggered flow has no caller
to be told anything in the first place. That is exactly the class of change
[ADR-0034](ADR-0034-the-manifest-publishes-a-schedules-address.md) §2.2 tested `MissedFire`
against and rejected: *is this the string somebody else uses to reach the flow?* A window is not.
It is how this deployment cuts a stream it reads. `TriggerModel`'s remarks already name a stream
trigger's `Checkpoint` as the example of the tuning that stays out of a published contract, and
the width is the same kind of value as the interval.

The alternative — a builder method that deliberately writes no manifest node — is worse still,
because it breaks the one property that makes the manifest a complete description of the graph
([ADR-0005](ADR-0005-manifest-as-build-artifact.md)). A reader who has the manifest would have a
document that omits a declaration the author wrote.

### 2. Moving the declaration into the plan would break the derived id, in one of two ways

[ADR-0055](ADR-0055-a-window-names-the-instance-it-starts.md)'s decision 2 derives a closed
window's instance id from the flow's id and version, the subscription's source and group, and the
window's **two bounds**. The bounds come from the `StreamWindowSpec` in `FlowStreamCatalog`'s
registration, which the generator writes from the attribute. There is no third place the width is
written down, and that is what makes a rebuilt window derive the id the original did.

Adding a builder declaration gives the width a second home, and both ways of reconciling them are
defects:

- **The registration keeps reading the attribute.** Two declarations of one number, with nothing
  making them agree. A flow whose attribute says `tumbling:1m` and whose builder says five would
  cut windows on one width and derive ids from the other — and the failure is silent, because
  both values are individually legal and the journal refuses nothing.
- **The registration reads the plan.** Then `[StreamTrigger]` loses the property
  `FlowStreamCatalog.Add` needs before it can build a spec at all, and with it something the
  model has today: `[StreamTrigger]` is `AllowMultiple = true`, so one flow may read two streams,
  and a window declared on the *builder* is per flow. Two subscriptions would be forced to share
  one width — a narrowing of the trigger model paid for a spelling.

### 3. `.Aggregate((acc, r) => …)` is the `.Do(lambda)` the builder exists to refuse

`IFlowBuilder`'s doc comment refuses `.Do(lambda)` in as many words: *arbitrary inline code would
be invisible to the manifest, untestable in isolation and undetectable by determinism analysis.
If it is worth executing, it is worth naming — make it a capability.* An accumulator lambda is
business logic — it decides what a malformed record does to the window's answer, which of several
means is reported, whether an outlier is dropped — and none of that is visible to `FLOWX1007`,
`FLOWX1011` or a reviewer opening the manifest.

It is also inert. `ADR-0055` rejected journaling window state, so there is no incremental
accumulator for the engine to drive: the flow is handed `StreamWindowBatch.Records`, the whole
closed window, in one call. A fold over a list that is already present **is** a step over that
list. `.Aggregate` would be a second spelling of `.Step`, with a lambda where the name should be.

### 4. `ADR-0055`'s revisit condition is restated

That record's *Revisit when* names "`.Aggregate<T>((acc, r) => …)` as 09 §9 prints it" as the
event that would make journaling one accumulated value cheap enough to reconsider sliding and
session windows. Decision 3 makes that event unreachable as written, and the condition is
therefore restated below rather than left pointing at a surface that is not coming. What would
actually unlock it is an **incremental-aggregation capability contract** — a named
`ICapability` the engine folds per record, whose state is one serialisable value — because that
is the half of the idea that carries the reduction in resident state, and it carries it without
the lambda.

`FLOWX1042`'s tumbling-only rule and the derived-id property are untouched by any of this.

## Consequences

**Positive**

- The window stays a property of the subscription, declared once, read once, and reaching the
  registration and not the manifest — which is what `StreamGenerationTests`
  `.TheWindowReachesTheRegistrationAndNotTheManifest` already asserts. Nothing about `ADR-0034`'s
  line moves, and no schema field is added.
- 09 §9's example compiles, and `TriggerModelDocTests` compiles the bytes on the page rather than
  a paraphrase of them. The defect this record exists to close cannot recur silently.
- `FLOWX1049` was written on the way past: 09 §9 also printed `Lateness = "10s"`, which is the
  short form `Window` takes and the one spelling `Lateness` does not, and the value would have
  been refused by `FlowStreamCatalog.Add` at composition time. Three of `StreamWindowSpec.Read`'s
  four arguments are now read at build time.

**Negative / accepted trade-offs**

- **The declaration is split across two files and stays split.** The interval a flow aggregates
  over is on the attribute; what it computes is in a capability. A reader wanting both looks in
  two places, and the fluent single-expression reading 09 §9 promised is not available.
- **A fold cannot be declared inline, and for small folds that is more ceremony than the work.**
  Counting records in a window is a capability, a contract and a registration. The alternative is
  a lambda nothing can analyse, and this platform has chosen the ceremony everywhere else.
- **09 §9's window table still names three shapes that do not run.** They are now marked as
  refused, with the diagnostic that refuses them — the table is a statement of what windowing
  means rather than a promise this engine keeps, and pretending otherwise was the original
  defect.

**Revisit when:** an incremental-aggregation capability contract exists — a named `ICapability`
the engine folds per record, whose accumulated state is one serialisable value rather than a list
of records — at which point `StreamMaxResidentRecords` stops being the binding constraint,
journaling that one value per checkpoint becomes cheap, and `ADR-0055`'s option A is worth
re-running for the sliding and session shapes it refuses. A builder-level `.Window(…)` is
reopened by nothing short of a manifest that publishes a stream flow's window, which would need
`ADR-0034`'s line moved first.
