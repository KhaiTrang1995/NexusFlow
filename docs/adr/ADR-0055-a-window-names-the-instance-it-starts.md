# ADR-0055: A window names the instance it starts, and window state is not journaled

**Status:** Accepted
**Date:** 2026-08-02 (at P7's first work package)
**Deciders:** Runtime team, Platform architecture

## Context

`ExecutionProfile.Streaming` had no engine, and P7's blocker was never the code. It was that
nothing defined **what a checkpoint is**. [06 §10](../06-Execution-Engine.md) drew a bounded
channel; [09 §9](../09-Trigger-Model.md) tabulated four window shapes and printed a DSL that does
not compile; `[StreamTrigger]` declared `Checkpoint = "PT5S"` and reached nothing. Between those
there is no statement of what a stream engine promises when a node dies mid-window.

A windowing engine accumulates. A tumbling one-minute window over a busy stream is a growing set
of records that exists only in the reading process, and the question every such engine has to
answer is where that set lives when the process does not. The three available answers:

- **A. Journal the accumulating state.** Every record, or every incremental aggregate, written
  through `IFlowJournal`. *Rejected:* it is a schema for a value that changes on every record,
  and [ADR-0015](ADR-0015-journal-schema-and-durable-execution.md)'s commitment 1 — one
  append-only row per `(instance, scope, step, attempt)` — has no shape that fits it. A window is
  not a step boundary. Adding a second write path to the journal for a value the source already
  holds would also charge a stream 1–15 ms per *record* against ADR-0003's per-flow bargain.
- **B. Checkpoint the source position of the last record read, and lose the open windows.**
  *Rejected:* it is at-most-once for every record in an open window. A one-minute window that had
  accumulated fifty seconds when the node died would emit an aggregate over the ten seconds a new
  node happened to see, silently — which is the failure mode that makes an aggregation wrong in a
  way nothing downstream can detect.
- **C. Checkpoint conservatively and rebuild from the source.** Chosen.

## Decision

**A stream subscription's checkpoint is the source position of the last record in the longest
prefix of admitted records whose windows have all closed and whose flows reached a recorded
outcome. Window state is not journaled at all.**

Four things follow, and each is a line of code rather than a paragraph:

1. **A restart re-reads from the checkpoint and rebuilds every open window identically**, because
   window assignment is a pure function of a record's event time and a declared width, aligned to
   the epoch rather than to the first record seen. `StreamWindowAssigner` has no clock, no store
   and no I/O, which is what makes this a property rather than a hope —
   `ReplayingFromTheCheckpointRebuildsTheSameWindows` is the assertion.

2. **A closed window derives the id of the instance it starts**, from the flow's id and version,
   the subscription's source and group, and the window's two bounds. This is
   [ADR-0031](ADR-0031-an-occurrence-names-the-instance-it-starts.md)'s derivation for the fourth
   time — after a schedule's occurrence, a delivery ([ADR-0035](ADR-0035-a-delivery-names-the-instance-it-starts.md))
   and a change ([ADR-0049](ADR-0049-a-change-names-the-instance-it-starts.md)) — under its own
   scope term. A window whose flow already committed is refused by `flow_instance`'s primary key
   rather than aggregated a second time.

3. **`Streaming` is journaled**, exactly as `Durable` is. Decision 2 is inert without it: an
   ephemeral instance has no primary key to refuse the rebuild. `ExecutionProfiles.IsJournaled`
   is the one place the runtime asks, replacing five comparisons against `Durable` that all meant
   "does this journal?".

4. **The checkpoint is committed after the windows' flows have run**, never before —
   [ADR-0048](ADR-0048-a-change-feed-advances-a-cursor.md)'s order, for its reason. The prefix is
   computed in *arrival* order, never by comparing two positions, because a position is opaque
   above the plugin.

## Consequences

**Positive**

- No window schema, no window table, no second write path into the journal. The stream engine is
  one table of one opaque string per subscription (`0011_stream_checkpoint.sql`) and a state
  machine with no I/O in it.
- The checkpoint is safe by construction rather than by tuning: it cannot move past a record
  belonging to an open window, because that record is in the ledger and unsettled.
- One `FlowEngine.ExecuteAsync`. `FlowStreamScan` is a driver that builds a `StreamWindowBatch`
  and calls `FlowHost.RunAsync` with a derived id, which is what a change scan and a schedule
  sweep already do. ADR-0003 paid to avoid a second runtime; nothing here spends it.

**Negative / accepted trade-offs**

- **A node death costs re-reading, and it is not free.** Every record between the checkpoint and
  the crash is read again, and every open window is rebuilt from scratch. For a wide window over
  a fast stream that is real work, and it is bounded by the window width rather than by anything
  configurable.
- **A late record may reach the side output twice.** It belongs to a window that has already
  closed, so there is no instance id for the journal to refuse; `IStreamSideOutput` must be
  idempotent, and its own remarks say so.
- **The source must retain from the checkpoint.** A stream trimmed past it has lost records that
  no window can be computed without, and the subscription **stops** with `stream.trimmed` rather
  than resuming from the oldest retained record. Resuming would emit windows computed from part
  of their input, silently and for ever. This makes source retention an operational requirement
  of running a stream flow, and there is nothing in this platform that enforces it.
- **One subscription is read by one node at a time**, under a lease. A window's records are in
  the reader's memory, so two readers would each hold half of every window and both would emit a
  partial aggregate under the same derived id. A deployment scales by adding subscriptions or by
  partitioning the stream, not by adding nodes — weaker than
  [ADR-0037](ADR-0037-the-consumer-offers-per-key-order.md)'s per-partition concurrency, and the
  same cost [ADR-0048](ADR-0048-a-change-feed-advances-a-cursor.md) accepted for a cursor.
- **Memory is bounded by refusing.** Open windows may hold at most
  `FlowXOptions.StreamMaxResidentRecords`; beyond that the subscription stops with
  `stream.window_overflow`. Eviction was the alternative and is worse for decision B's reason.
- **Tumbling windows only.** A sliding window assigns one record to several windows and a session
  window's bounds move as records arrive, so in neither case is the window's identity a function
  of the event time alone — decision 2 does not hold, and a rebuilt window would not deduplicate.
  A global window is never closed by a watermark, so the checkpoint would never advance.
  `FLOWX1042` reports all three at build time and `FlowStreamCatalog.Add` refuses them at
  startup. **This is the largest gap between what 09 §9's table promises and what runs.**

**Revisit when:** an incremental-aggregation contract exists in the DSL — `.Aggregate<T>((acc, r)
=> …)` as 09 §9 prints it — at which point a window's state is one value rather than a list of
records, `StreamMaxResidentRecords` stops being the binding constraint, and journaling that one
value per checkpoint becomes cheap enough to reconsider option A for the sliding and session
shapes this record refuses.
