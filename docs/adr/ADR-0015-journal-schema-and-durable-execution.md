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
>
> **Re-decided at WP-52, and still Proposed — a decision, not an oversight.** That
> condition is now literally met: the suite exists, and since WP-52 the runtime writes
> through it. It is met by an **in-memory reference** — a dictionary in
> `tests/FlowX.Conformance.Tests` that satisfies the assertions. What this record commits
> to is *storage*: a primary key an append-only table must reject a duplicate of, one
> transaction spanning the step row, the instance update and the outbox rows, a fencing
> token validated on every write, a retention table, an expand/contract migration. A
> dictionary has no transaction, no unique constraint, no index and no migration, so it
> cannot disagree with any of them. It proves the schema is *expressible*; ADR-0006's
> lesson is about what happens when that is mistaken for *implementable*.
>
> Two of this record's own clauses were found wrong by its first implementation and are
> [amended below](#amendments-the-first-implementation-forced-wp-52). A record still moving
> under contact is what "Proposed" describes. **First real contact is
> [WP-53](../../PLAN.md#wp-53--postgres-journal-and-lease-store), against Postgres, and
> that is where the status is decided.**

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
>
> **This was true from WP-40 until WP-52 (2026-07-31), and is the gap this record was
> written to close.** It is kept as the problem statement, not corrected into a claim about
> today: the runtime now reads the profile, journals a `Durable` flow's step boundaries and
> resumes through the same loop. What it still does not do is
> [below](#what-wp-52-landed-and-what-it-did-not).

Found at WP-40 while auditing risk R2, made audible at WP-42 by
[`FLOWX1028`](../diagnostics/FLOWX1028.md), and pinned executably by
`RuntimeDoesNotReadTheExecutionProfile` in `tests/FlowX.Architecture.Tests`, which is
written to **fail** the day the runtime reads a profile and whose failure message lists
what to take down. **This ADR is the design that trips that test.** It tripped it at
WP-52; the test is deleted, and
[the last section](#what-lands-with-this-and-what-is-deleted) is the take-down list it
named.

**What P1 shipped that the P2 design in [11 §2](../11-Distributed-Runtime.md#2-the-journal)
predates.** That schema was drawn when a flow was a straight line. It is no longer:

| P1 shipped | What it does to a journal key |
|---|---|
| `ForEach` (WP-29) — one range of the flat step array **re-entered per element** | `(instance, step)` is no longer unique. A 500-element loop writes step 7 five hundred times |
| `Parallel` (WP-24) — several indices in flight on several threads between a fork and its join | `flow_instance.resume_from_step int` cannot say "branch A done, branch B at step 12" |
| `SubFlow` (WP-33) — the engine recurses into a **second plan** with its own dispatcher, context and compensation stack | one instance is no longer one plan; `Detached` children outlive the parent's step |
| `Switch`, `When` (WP-15, WP-20) | control flow is data-dependent, so a resume position cannot be a step count. *This cell also said the journal "must record the branch taken, not only the steps run"; it does not, and the Decision below never gave it a field to — [amended at WP-52](#amendments-the-first-implementation-forced-wp-52)* |
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

*WP-52 discharged the premise: the runtime reads the profile, so `FLOWX1007`–`FLOWX1009`
and `FLOWX1012` are no longer blocked on severity or on a fix that changes nothing. They
are unwritten, which is a smaller and more ordinary thing to be. WP-58 and WP-60.*

## Amendments the first implementation forced (WP-52)

Three, listed rather than folded into the text above. A record quietly corrected teaches
nobody what it got wrong, and two of these were wrong from the day it was written.

**1. The journal does not record the branch taken — 2026-07-31.** The context table's
`Switch`/`When` row said it must. The Decision one section down commits to exactly one row
per step boundary, and the shipped `StepCommit` has no field for a branch outcome, so this
record contradicted itself before anything implemented it. WP-52 resolves it **in favour of
the Decision**: flow predicates and selectors are pure by
[`FLOWX1011`](../diagnostics/FLOWX1011.md), so replaying one against the restored state bag
reproduces the arm it chose. The journal records what *ran*; the branch is derived — the
same argument as commitment 2, applied to control flow instead of position. A branch field
would have made one fact storable in two places, and the stored copy would be the one
nothing checks.

The obligation this moves onto `FLOWX1011` is stated rather than implied: replay
correctness now rests on that rule's coverage, and the rule ships as a **Warning** under
`Ephemeral` and says nothing about capability bodies at all. A selector reading something
the rule cannot see picks a different arm on replay and no journal row would catch it.
That is risk [R2](../05-Architecture.md#11-risks-and-technical-debt) arriving through the
door this amendment opens, and `ReplayDeterminismTest` (WP-61) is what closes it.

**2. A `Durable` flow with no journal is refused — 2026-07-31.** This record never said
what happens, which left the likeliest wiring mistake in P2 undefined. WP-52 defines it:
the engine returns `flow.durability_not_configured` before the first step — an `Internal`
error, a *rejection* rather than a failure, because no step ran and there is nothing to
compensate. The alternative, running it ephemerally, is precisely the defect
[`FLOWX1028`](../diagnostics/FLOWX1028.md) existed to name, and doing it one layer lower
now that the diagnostic's `Durable` half is deleted would leave nothing anywhere to say so.
The mirror is refused too — a journal supplied for a flow that did not declare `Durable` is
`flow.profile_is_not_durable` — because the profile is the declaration.

> **The consequence, plainly: until WP-55 wires lease acquisition into a host, a `Durable`
> flow is rejected at its first invocation** unless the caller builds a `DurableExecution`
> itself. The seam is reachable from a test and from a host that opts in; it is not
> reachable from an HTTP trigger. This is not a regression — `Durable` bought nothing
> before — but it is a louder nothing, and a flow that used to run now does not.

**3. When the fence rises — 2026-07-31.** WP-51 found this record silent on *when* a
lease's fencing token becomes the instance's fence, and a journal that learned tokens only
from writes accepts a stale one in the window between a new owner acquiring the lease and
its first commit. `IFlowJournal.FenceAsync` closed the gap, pinned by
`JournalConformance.TheFenceRisesOnAcquisitionNotOnTheFirstWrite`. The amendment was
recorded as owed at WP-51 and is discharged here: **the fence rises on acquisition**,
before any history is read.

## What WP-52 landed, and what it did not

The seam exists. **Nothing has run against a store**, and no sentence in this record should
be read as saying durable execution works end to end.

| In, at WP-52 | Not in |
|---|---|
| The runtime reads `ExecutionProfile`; a `Durable` flow commits one row per `(instance, scope, step, attempt)`, and a failed attempt gets a row too | Lease **acquisition** and renewal. The token is passed in; nothing acquires or renews it — WP-55 |
| **Resumption**: the frontier is read and the same `ExecuteAsync` is re-entered at index 0, skipping steps a successful row covers | The **recovery scan**. Nothing looks for an abandoned instance to hand to that resume — WP-55 |
| A composed sub-flow gets its own `flow_instance` row, per commitment 3 | **Postgres** (WP-53) and **Redis** (WP-54). The only implementation of `IFlowJournal` anywhere is the in-memory reference in `tests/FlowX.Conformance.Tests` |
| Non-determinism captured per step, `Random`'s seed journaled, per commitment 4 | The transactional **outbox**. `.Emit<T>()` still publishes nothing ([`FLOWX1024`](../diagnostics/FLOWX1024.md)) — WP-56 |
| `[Sensitive]` members cannot reach the journal in the clear: payloads enter only through `JournalPayload`, which requires a generated `JsonTypeInfo` | **`AwaitSignal`** and durable suspension ([`FLOWX1017`](../diagnostics/FLOWX1017.md)) — WP-63. A durable flow still runs to completion inside one invocation |
| B2 re-measured at **0 B** on the ephemeral path; `Durable` costs 192 B per step, recorded as a ceiling | The generated payload writer and `FLOWX1006` (WP-59); B7 and B8, which still have no harness (WP-50) |

**Two fidelity limits, stated rather than papered over.**

- **Non-determinism attribution inside a fork is best-effort.** One pooled context is
  shared by every branch of a `Parallel`, so an id minted by a sibling between a step
  finishing and its commit lands on that step's row. The writes are serialised, so it is
  safe, and it is not yet wrong in a way any reader can act on, because nothing replays a
  capture back into execution. Exactness needs a per-branch context — which is what WP-61
  has to buy before it can trust a capture.
- **A skipped sub-flow's compensations are not rebuilt on resume.** The parent records a
  composition as one entry bound to the *child's* context, and that context died with the
  node. A resumed parent that skips a completed sub-flow and later fails will not undo the
  child's work. Named rather than approximated — a compensation stack that is silently
  short is the failure a saga exists to prevent — and rebuilding it from the child's own
  instance rows is **WP-57**'s package.

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

**Worked row by row at WP-54 (2026-07-31).** The **State** column records what actually
happened to each, because a take-down list with no verdict beside it is the same kind of
scaffold it was written to remove.

| Deleted or narrowed | Why it goes | State |
|---|---|---|
| `tests/FlowX.Architecture.Tests/ExecutionProfileHonestyTests.cs` — both `RuntimeDoesNotReadTheExecutionProfile` and its guard `TheTreesTheReminderWatchesExist` | The statement it asserts becomes false. It is deleted, never skipped or narrowed to a subdirectory | **done** — WP-52. Observed failing first; the file is gone |
| `src/FlowX.Compiler/Analysis/ExecutionProfileAnalyzer.cs` — **narrowed to `Streaming`**, not deleted | `Durable` becomes implemented; `Streaming` does not until P7. Deleting it outright would hand `Streaming` the silence `Durable` had. [FLOWX1028's own deletion table](../diagnostics/FLOWX1028.md#when-this-rule-is-deleted) says the same | **done** — WP-52 |
| `FLOWX1028`'s `Durable` half: its message, its descriptor text in `FlowXDiagnostics.cs`, its row in `AnalyzerReleases.Unshipped.md`, its tests, and the `Durable` column of `docs/diagnostics/FLOWX1028.md` | Same reason. The page keeps its `Streaming` half and its deletion table | **done** — WP-52 |
| The `[!WARNING]` box in [06 §4](../06-Execution-Engine.md#4-execution-profiles--the-central-trade-off) — "only the `Ephemeral` column describes something that runs" | The middle column starts describing something that runs. The `Streaming` sentence stays | **done** — WP-54. Replaced, not removed: the middle column now runs *partly*, and the box says which cells are still design |
| The closing note in [06 §5](../06-Execution-Engine.md#5-the-determinism-boundary) — "`ReplayDeterminismTest` does not exist" and "`FlowX.Runtime` never reads `ExecutionProfile`" — plus the four **no — P2** rows in its table | The test exists at WP-61; the rows are raised at WP-58 and WP-59. The severity paragraph is re-decided **as a set**, including `FLOWX1011`'s deliberate Warning deviation | **half.** The profile sentence is gone (WP-52). `ReplayDeterminismTest` still does not exist and the four rows are still **no** — WP-58, WP-59, WP-61. What changed is the *reason*: they are no longer blocked on severity |
| The header `[!WARNING]` in [11-Distributed-Runtime](../11-Distributed-Runtime.md) — "nothing in this document is implemented" | Section by section, as each lands. It is not removed wholesale on the first commit | **partial, as designed.** §2 (the journal) and §3 (resume through the same loop) have an implementation; §4–§8 do not. The box now says so per section |
| The `[!WARNING]` in [ADR-0006](ADR-0006-journal-and-leases.md) — "Accepted, not implemented" — and its literature-derived "measured ceiling", replaced by B7's real number | The record stops being a prediction | **half.** "No `IFlowJournal` anywhere in `src/`" and "the runtime does not read `ExecutionProfile`" are false and are corrected. The **measured ceiling stays a literature figure**: B7 has no harness (WP-50) and no store has been benchmarked |
| [ADR-0003](ADR-0003-execution-profiles.md)'s negative bullet "The asymmetry is currently theoretical in one direction", and the `FLOWX1012` sentence in the bullet above it | Both describe the gap this ADR closes | **done** — WP-54. The asymmetry bullet is rewritten; `FLOWX1012`'s sentence keeps "never built" and loses "its fix would change nothing" |
| Risk **R2** in [05 §11](../05-Architecture.md#11-risks-and-technical-debt) | It stops being *unreachable* and becomes live-and-mitigated, with WP-61 as the mitigation actually named | **half, and the worse half.** R2 is now **live**; it is *not* mitigated. All three named mitigations are still unbuilt, and amendment 1 above added a fourth dependency on `FLOWX1011`'s coverage |
| The blocked row for `CrossTenantAccessIsDenied` in [CHECKLIST §4](../../CHECKLIST.md) loses **half** its blocker | "there is no journal, so there is no audit event to assert" ceases to be true. It stays blocked on P4's policy execution, and the row must say so rather than being ticked | **done** — WP-54. Half struck, row still `[ ]`, blocked on P4 (policy execution) and P3 ("every trigger kind") |
| `JournalBenchmarks` absent from [14 §8](../14-Performance.md#8-benchmark-suite-and-ci-gating), and the "not written — no journal, no second node" chaos row in [21 §7](../21-Quality-Gates.md) | WP-50 writes the harness *before* the journal, so these two are the **first** entries removed, not the last | **not yet, and the prediction inverted.** WP-50 has not started, so both entries stand — but their stated reason ("there is no journal") has stopped being true. They are the *last* entries removed, not the first, and the reason is corrected in both files rather than the state |

**One item is deliberately not on this list.** `flowx.manifest.json` keeps publishing
`"profile": "Durable"` exactly as it does today. The field was always the declaration
faithfully recorded; what was untrue was the silence around it. Nothing about the manifest
changes when the runtime starts honouring it, which is the property that lets `flowx diff`
stay quiet across the release that lands P2.

---

**Back to:** [ADR index](README.md) · [06 — Execution Engine](../06-Execution-Engine.md) ·
[11 — Distributed Runtime](../11-Distributed-Runtime.md) · [PLAN §5](../../PLAN.md#5-p2--durable-execution)
