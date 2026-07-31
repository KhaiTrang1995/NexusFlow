# ADR-0015: Journal the step boundary and resume through the same step loop

**Status:** Proposed
**Date:** 2026-07-31
**Deciders:** Runtime team, Platform architecture

> **Proposed, not Accepted, and deliberately.** [ADR-0006](ADR-0006-journal-and-leases.md)
> was accepted before anything met it, and its own warning box now records the price:
> every consequence in it is "a prediction about a system that has not been written",
> including a "measured ceiling" that was never measured here. This record is one layer
> more concrete — it fixes a table shape and a primary key — so the same risk is larger,
> not smaller. It becomes **Accepted** when the conformance suite in
> [PLAN §5, WP-51](../../PLAN.md#5-p2--durable-execution) exists to hold an implementation
> to it, and not before.

## Context

[ADR-0006](ADR-0006-journal-and-leases.md) chose the two primitives — an append-only
journal and fenced leases — and [ADR-0003](ADR-0003-execution-profiles.md) chose to make
durability a per-flow declaration. Neither says **what a journal record is**, **when it is
written**, or **how the engine that exists today gets from the ephemeral path to a durable
one**. That gap is the whole of P2, and it is why the following is true:

> **`FlowX.Runtime` never reads `ExecutionProfile`.** A flow declaring
> `Profile = ExecutionProfile.Durable` runs the ephemeral path — same step loop, same
> pooled context, no journal, no lease, no resume. The profile reaches an `ExecutionPlan`
> validation and the `profile` field of `flowx.manifest.json`, and stops.

Found at WP-40 while auditing risk R2, made audible at WP-42 by
[`FLOWX1028`](../diagnostics/FLOWX1028.md), and pinned executably by
`RuntimeDoesNotReadTheExecutionProfile` in `tests/FlowX.Architecture.Tests`, which is
written to **fail** the day the runtime reads a profile and whose failure message lists
what to take down. **This ADR is the design that trips that test.**
[the last section](#what-lands-with-this-and-what-is-deleted) is the take-down list.

**What P1 shipped that the P2 design in [11 §2](../11-Distributed-Runtime.md#2-the-journal)
predates.** That schema was drawn when a flow was a straight line. It is no longer:

| P1 shipped | What it does to a journal key |
|---|---|
| `ForEach` (WP-29) — one range of the flat step array **re-entered per element** | `(instance, step)` is no longer unique. A 500-element loop writes step 7 five hundred times |
| `Parallel` (WP-24) — several indices in flight on several threads between a fork and its join | `flow_instance.resume_from_step int` cannot say "branch A done, branch B at step 12" |
| `SubFlow` (WP-33) — the engine recurses into a **second plan** with its own dispatcher, context and compensation stack | one instance is no longer one plan; `Detached` children outlive the parent's step |
| `Switch`, `When` (WP-15, WP-20) | control flow is data-dependent, so the journal must record the branch taken, not only the steps run |
| B2 is a **hard zero** for the ephemeral path | a durable seam that costs the ephemeral loop one allocation charges every flow for a feature it does not use |

`CompensationStack` already met the first row and answered it: its duplicate check moved
from `index` to `(index, scope)` at WP-29, because keying on index alone threw on the
second element. **The journal is not allowed to be less precise than the compensation
stack that has to undo it.**

### Options considered

- **A. A second engine for durable flows**, beside `FlowEngine`. *Rejected:* ADR-0003
  already names "two runtime paths to test" as an accepted cost and mitigates it by
  keeping resumption in the *same* loop. Two loops diverge, and the ephemeral one — the
  one every test and every sample exercises — gets the features first. This is the
  failure `RuntimeDoesNotReadTheExecutionProfile` would be re-written to describe rather
  than deleted.
- **B. Checkpoint at an interval** (every N steps, or on suspension only). *Rejected:*
  resume granularity becomes coarser than the effect boundary, so recovery re-executes
  steps whose effects already happened. That converts a correctness property into a
  tuning parameter.
- **C. Journal every `ctx.Set`** — event-source the state bag. *Rejected:* it makes the
  bag's write pattern a public, versioned contract, and it puts a store round trip inside
  a code path B2 measures at zero.
- **D. Snapshot the state bag per step, with no per-step outcome rows.** *Rejected:* it
  loses attempt history and the non-determinism capture, so the replay contract in
  [06 §5](../06-Execution-Engine.md#5-the-determinism-boundary) becomes unprovable — and
  that contract is risk R2's only mitigation.
- **E. Journal the step boundary; derive the resume position by reading the journal.**
  Chosen.

## Decision

We will implement `Durable` as **the ephemeral step loop with a journaled step boundary**:
exactly one append-only row per `(instance_id, scope, step_id, attempt)`, committed in the
same transaction as the state-bag update and any outbox rows and guarded by the lease's
fencing token — and **resumption is that same loop re-entered from a cursor derived by
replaying the journal**, not a recovery path of its own.

Five schema commitments follow from that sentence. Each is a change to
[11 §2](../11-Distributed-Runtime.md#2-the-journal) as drawn, and each exists because
something P1 shipped makes the drawn version wrong.

**1. `flow_step`'s primary key gains `scope`:** `(instance_id, scope, step_id, attempt)`.
`scope` is the `IterationScope` chain rendered as text — the empty string for the flow
body, `7` for the eighth element of a `ForEach`, `7/2` for an element of a loop nested
inside it. Without it a re-entered range overwrites its own history, which an append-only
table cannot do and must instead reject.

**2. `flow_instance.resume_from_step` stops being the resume position.** The position is
**derived** by replaying committed `flow_step` rows against the compiled plan; the column
survives only as a denormalised hint for operator queries and is never read by the engine.
A scalar cursor cannot describe a half-completed fork, and a fork is a shape the DSL
shipped in P1. Deriving is the only variant that survives `Parallel` — and it is also what
makes resume *provable*: the plan is compile-time, so "which steps have committed" is
enough to reconstruct where execution is, without trusting a field a crashed node wrote.

**3. A `SubFlow` child is its own `flow_instance` row**, carrying `parent_instance_id` and
the parent's `(scope, step_id)`. Its steps are **not** spliced into the parent's history.
WP-33 already refused splicing at compile time — it would make the parent's manifest claim
the child's capabilities and discard the child's deadline and profile — and splicing in the
journal would reintroduce exactly that untruth one layer down. It is also the only shape in
which a `Detached` child, which outlives the step that started it, has anywhere to live.

**4. `flow_step.nondeterministic` is the capture envelope**, written on first use and
replayed thereafter, for `ctx.UtcNow`, `ctx.NewId()` and `ctx.Random`. This is where a
known defect gets fixed rather than documented: `FlowExecutionContext.Random` is built as
`new Random()`, whose seed nothing can read back, so the replay guarantee its own remarks
describe cannot hold today ([CHECKLIST §2](../../CHECKLIST.md)). The seed becomes a
journaled value, which is the only construction under which those remarks are true.

**5. Payloads are written through the generated `System.Text.Json` context**
[ADR-0008](ADR-0008-serialization-and-schema.md) chose. Membership in that context is
precisely what `FLOWX1006` was reserved to check, which is why that diagnostic is blocked
on P2 and not on anyone's analysis.

### The sequencing argument this decision carries

**Four reserved diagnostics are blocked on severity, not on analysis, and the journal
makes them nearly free.** `PredicatePurityAnalyzer` already performs the analysis
`FLOWX1007`–`FLOWX1009` need; extending it from flow delegates to capability bodies is
mechanical, and WP-25 already restructured it into a table of constructs so that the next
one is a row rather than a code path. What blocks them is that ADR-0003 makes them
*informational* under `Ephemeral`, `Ephemeral` is the only profile that runs, and an Info
diagnostic never reaches a build log — so they would ship doing nothing anywhere.
`FLOWX1012` is blocked the same way from the other end: the check is one predicate, and
its only available fix, `Profile = Durable`, changes nothing while there is no journal.
A rule whose fix is a lie is worse than an unraised id.

The day the runtime reads the profile, all four stop being blocked at once, and
`FLOWX1006` follows from commitment 5 above. **That is an argument about ordering, not a
bonus:** the journal seam should land early in P2 rather than after the store adapters,
because five of P2's diagnostics deliverables become cheap on the day it does, and
[06 §5](../06-Execution-Engine.md#5-the-determinism-boundary) asks for the severity stance
to be revisited **as a set** rather than one row at a time.

## Consequences

**Positive**

- One loop, one set of semantics. Compensation ordering, deadline handling, `ForEach`
  scoping and sub-flow recursion are not re-implemented for durability and cannot drift
  from the versions the existing 132 runtime tests pin.
- Resume is derivable rather than remembered, so a crashed node's last write cannot
  mislead recovery. Correctness rests on committed rows and a fencing token, both of
  which ADR-0006 already argued.
- The journal is the replay source, the audit source and the operator's view of a stuck
  instance — one mechanism, three consumers, as ADR-0006 intended.
- Five reserved diagnostic ids stop being reserved, and risk R2 becomes *reachable*
  rather than unreachable, which is a precondition for it ever being mitigated.
- `AwaitSignal`, `Delay` and `SubFlowMode.AwaitCompletion` gain the durable suspension
  point they are refused for the absence of today
  ([FLOWX1026](../diagnostics/FLOWX1026.md), [FLOWX1017](../diagnostics/FLOWX1017.md)).

**Negative / accepted trade-offs**

- **The ephemeral hot path grows a branch it does not need.** Not adding a second engine
  is not free: the seam must be gated on a plan-level flag — the precedent is
  `ExecutionPlan.HasParallel`, which is why a linear flow pays nothing for the fork lock
  — or B2's hard zero is spent on a feature most flows never use. This is a *measured*
  obligation, not a stylistic one: WP-52's exit criterion re-runs the allocation tests and
  the number must still be 0 B.
- **Deriving the resume position means reading history.** Budget B8 (rehydration p99
  ≤ 8 ms) is now a budget on a read whose size grows with how much the instance has done —
  a 500-element `ForEach` is 500 rows before the first resumed step. The mitigation is the
  state-bag snapshot on the instance row, which bounds the scan to rows committed after
  it; the accepted cost is that the snapshot is a second thing to keep correct.
- **`scope` multiplies rows.** A loop over 10 000 elements writes 10 000 rows for one step
  index, and [11 §2](../11-Distributed-Runtime.md#2-the-journal)'s retention table — which
  is written per instance — did not anticipate a per-instance volume that tracks *data
  size*. Retention becomes a real capacity question for loop-heavy flows, not a default.
- **Child instances complicate retention.** A `Detached` sub-flow can outlive its parent,
  so "completed instance + steps: 30 days" can archive a parent while a child is still
  running. The parent link must be nullable on read, and an orphan must be legible rather
  than a foreign-key error.
- **The journal is a new sink for `[Sensitive]` values, and it lands three phases before
  the work that redacts sinks.** Redaction is generated for exactly one sink today — the
  RFC 7807 body — and `RedactionCannotBeBypassed` is blocked until the rest exist. A
  `flow_step.result` or `state_bag` written verbatim puts a marked member into a table
  retained for 30 to 180 days, which is strictly worse than the log line the attribute was
  written for. **Commitment 5's payload writer must consult `SensitiveMembers`**, which the
  generator already emits on every flow's partial class; nothing new has to be discovered
  to do it, only remembered. This is an accepted trade-off only in the sense that the
  *gate* arrives late; the behaviour does not get to.
- **Nothing here makes a non-idempotent effect safe.** A process that dies after the
  effect and before the commit re-executes the step on resume. ADR-0006's honest statement
  stands unchanged and is not weakened by a finer key.
- **The journal is still the shared bottleneck** (risk R5), and this decision adds to the
  transaction rather than removing from it: one `flow_step` insert, one `flow_instance`
  update, N outbox inserts, in one transaction, per step. B7 is the number that says
  whether that is affordable, and today no benchmark measures it.

**Revisit when:** the derived resume frontier's read cost breaches B8 on a real flow shape
— a long `ForEach` or a deep `SubFlow` tree are the two candidates and both are ordinary —
or when journal payloads need a shape the generated STJ context cannot express, at which
point commitment 5 and ADR-0008 are the pair to re-open together.

## What lands with this, and what is deleted

`RuntimeDoesNotReadTheExecutionProfile` is a **deletion reminder that is meant to fail**.
Its message names most of this list; the list is repeated here because the ADR is where the
change is decided, and a scaffold that outlives what it describes is noise — noise is what
teaches people to suppress a catalogue.

| Deleted or narrowed | Why it goes |
|---|---|
| `tests/FlowX.Architecture.Tests/ExecutionProfileHonestyTests.cs` — both `RuntimeDoesNotReadTheExecutionProfile` and its guard `TheTreesTheReminderWatchesExist` | The statement it asserts becomes false. It is deleted, never skipped or narrowed to a subdirectory |
| `src/FlowX.Compiler/Analysis/ExecutionProfileAnalyzer.cs` — **narrowed to `Streaming`**, not deleted | `Durable` becomes implemented; `Streaming` does not until P7. Deleting it outright would hand `Streaming` the silence `Durable` had. [FLOWX1028's own deletion table](../diagnostics/FLOWX1028.md#when-this-rule-is-deleted) says the same |
| `FLOWX1028`'s `Durable` half: its message, its descriptor text in `FlowXDiagnostics.cs`, its row in `AnalyzerReleases.Unshipped.md`, its tests, and the `Durable` column of `docs/diagnostics/FLOWX1028.md` | Same reason. The page keeps its `Streaming` half and its deletion table |
| The `[!WARNING]` box in [06 §4](../06-Execution-Engine.md#4-execution-profiles--the-central-trade-off) — "only the `Ephemeral` column describes something that runs" | The middle column starts describing something that runs. The `Streaming` sentence stays |
| The closing note in [06 §5](../06-Execution-Engine.md#5-the-determinism-boundary) — "`ReplayDeterminismTest` does not exist" and "`FlowX.Runtime` never reads `ExecutionProfile`" — plus the four **no — P2** rows in its table | The test exists at WP-61; the rows are raised at WP-58 and WP-59. The severity paragraph is re-decided **as a set**, including `FLOWX1011`'s deliberate Warning deviation |
| The header `[!WARNING]` in [11-Distributed-Runtime](../11-Distributed-Runtime.md) — "nothing in this document is implemented" | Section by section, as each lands. It is not removed wholesale on the first commit |
| The `[!WARNING]` in [ADR-0006](ADR-0006-journal-and-leases.md) — "Accepted, not implemented" — and its literature-derived "measured ceiling", replaced by B7's real number | The record stops being a prediction |
| [ADR-0003](ADR-0003-execution-profiles.md)'s negative bullet "The asymmetry is currently theoretical in one direction", and the `FLOWX1012` sentence in the bullet above it | Both describe the gap this ADR closes |
| Risk **R2** in [05 §11](../05-Architecture.md#11-risks-and-technical-debt) | It stops being *unreachable* and becomes live-and-mitigated, with WP-61 as the mitigation actually named |
| The blocked row for `CrossTenantAccessIsDenied` in [CHECKLIST §4](../../CHECKLIST.md) loses **half** its blocker | "there is no journal, so there is no audit event to assert" ceases to be true. It stays blocked on P4's policy execution, and the row must say so rather than being ticked |
| `JournalBenchmarks` absent from [14 §8](../14-Performance.md#8-benchmark-suite-and-ci-gating), and the "not written — no journal, no second node" chaos row in [21 §7](../21-Quality-Gates.md) | WP-50 writes the harness *before* the journal, so these two are the **first** entries removed, not the last |

**One item is deliberately not on this list.** `flowx.manifest.json` keeps publishing
`"profile": "Durable"` exactly as it does today. The field was always the declaration
faithfully recorded; what was untrue was the silence around it. Nothing about the manifest
changes when the runtime starts honouring it, which is the property that lets `flowx diff`
stay quiet across the release that lands P2.

---

**Back to:** [ADR index](README.md) · [06 — Execution Engine](../06-Execution-Engine.md) ·
[11 — Distributed Runtime](../11-Distributed-Runtime.md) · [PLAN §5](../../PLAN.md#5-p2--durable-execution)
