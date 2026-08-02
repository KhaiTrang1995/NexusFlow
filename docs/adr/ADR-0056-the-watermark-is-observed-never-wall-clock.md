# ADR-0056: The watermark is observed, never wall-clock

**Status:** Accepted
**Date:** 2026-08-02 (at P7's first work package)
**Deciders:** Runtime team

## Context

[09 §9](../09-Trigger-Model.md) says "watermarks drive window closure" and defines neither the
watermark nor what generates it. Every windowing engine has to choose, and the choice decides
what a replay produces.

Two generators are available:

- **A. Wall-clock, or wall-clock with an idle timeout.** The watermark advances as time passes,
  so a window closes whether or not more data arrives. This is what most stream engines do,
  because it makes a quiet stream still emit.
- **B. Observed event time.** The watermark is the highest event time seen, less the declared
  lateness, and moves only when a record moves it.

The constraint that decides it is [ADR-0055](ADR-0055-a-window-names-the-instance-it-starts.md):
window state is not journaled, so a restart must rebuild an identical window by replaying records
from the checkpoint. And it is [ADR-0015](ADR-0015-journal-schema-and-durable-execution.md)'s
commitment 4 restated one layer up — `FLOWX1007` makes an ambient `DateTime.UtcNow` an error
inside a journaled flow because a value taken ambiently is in none of the fields a replay
reconstructs.

A wall-clock watermark is exactly that value, taken by the engine instead of by the flow. Two
nodes replaying the same records at different moments would close different windows and derive
different instance ids, and the deduplication ADR-0055 rests on would not merely weaken — it
would not exist.

## Decision

**The watermark is `max(observed event time) − Lateness`, and it is monotonic. Nothing else moves
it.** A window `[s, e)` closes when the watermark reaches `e`; a record whose window has closed
goes to `IStreamSideOutput` rather than being dropped. `FlowXOptions.StreamScanInterval` is a read
cadence and closes nothing.

`StreamRecord.EventTime` is therefore required of a source, and a source that cannot supply one
must say so rather than substituting its own clock. `RedisStreamSource` reads it from a field and
refuses a record without it — including refusing to use the stream id, which encodes when the
entry was *written*.

## Consequences

**Positive**

- A window's contents, its bounds and its instance id are a function of the records alone. That
  is the whole precondition of ADR-0055, and `ReplayingFromTheCheckpointRebuildsTheSameWindows`
  is what asserts it.
- Late is a definition rather than a race: a record is late exactly when its window's upper bound
  is at or below the watermark, which two nodes replaying the same records agree on.
- No clock skew between nodes can close a window early, because no node's clock is consulted.

**Negative / accepted trade-offs**

- **A stream that goes quiet leaves its last window open, indefinitely.** No timer closes it and
  no configuration will; the next record past the boundary does. A user must not expect a
  windowed flow to fire at the end of a wall-clock minute — it fires when the data says the
  minute is over. On a low-rate or bursty stream, an aggregate can be arbitrarily late.
- **A stream that stops for good never emits its last window.** The records are not lost: the
  checkpoint stays behind them, so they are re-read for ever until something closes the window.
  That is visible as a subscription making no progress, which is the failure this platform
  prefers to a silent partial aggregate.
- **`Lateness` is the whole of the out-of-order tolerance.** A record more than `Lateness` behind
  the highest event time seen is late by definition, even if it was produced in order and delayed
  in transit.

**Revisit when:** a deployment needs an idle-close and can state what it wants a window computed
from a partial input to *mean* — at which point the answer is likely a declared idle timeout that
emits a distinguishable partial window rather than a watermark that lies about the data, because
the identity derivation cannot survive two different closures of one interval.
