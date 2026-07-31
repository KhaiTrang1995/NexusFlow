# ADR-0003: Choose durability per flow via execution profiles

**Status:** Accepted
**Date:** 2026-07-30
**Deciders:** Platform architecture, Runtime team

## Context

Durable execution engines (Temporal, Dapr Workflow, Azure Durable Functions)
journal every step of every workflow. That yields excellent crash semantics and
costs roughly a millisecond of storage latency per step, plus storage volume,
plus operational surface — on *every* execution, including a three-step read
query that nobody would ever want to resume.

Measured orders of magnitude: an in-memory step boundary is ~1 µs; a journaled
step commit is ~1–15 ms. That is a factor of 1 000–10 000.

Most business applications are a mixture: a handful of flows genuinely need
crash-safe resumption (payments, onboarding, fulfilment), and the large majority
do not (queries, validation, CRUD, projections).

Quality goals in tension: Q1 (latency) versus Q2 (durable correctness).

Options considered:

- **A. Always durable.** *Rejected:* fails Q1 by three orders of magnitude for
  the majority of flows; makes FlowX unusable as a general application platform.
- **B. Never durable.** *Rejected:* fails Q2; forces users back to a second
  framework for sagas, which defeats the whole premise (ADR-0001).
- **C. Durability decided per *invocation*.** *Rejected:* the same flow behaving
  differently per call makes replay, testing and reasoning incoherent.
- **D. Durability declared per *flow*, at design time.** Chosen.

## Decision

We will make durability a **per-flow declaration** — `ExecutionProfile.Ephemeral`
(default), `Durable`, or `Streaming` — because the cost/reliability trade-off is
a property of the business operation, is known at design time, and is the single
most consequential decision a flow author makes.

The default is `Ephemeral`: you opt *into* cost, never out of it.

## Consequences

**Positive**
- Q1 and Q2 are both met, each where it applies.
- The trade-off becomes explicit, reviewable and greppable: `Profile = Durable`
  appears in the code, the manifest, the diagram and the cost report.
- One programming model still covers both worlds — a flow's body is identical
  under either profile.
- Cost control has a real lever, and **it exists now**: `flowx verify --cost` flags
  durable flows with no compensation, no signals and no timers — a profile chosen by
  accident. It is this bullet's rule unembellished, reads only the manifest, and exits
  non-zero on a finding ([22-CLI](../22-CLI.md)). *This bullet said "it does not exist"
  from the day the record was written until the verb was built.*

**Negative / accepted trade-offs**
- **Two runtime paths to test.** The step loop is shared, but journaling,
  resumption and determinism only exist on one path. Mitigated by keeping
  resumption in the *same* loop, so there is no separate recovery code path to rot.
  *This bullet named `ctx.ResumeFromStep` as the mechanism; the implementation does
  not use a scalar cursor, because one cannot describe a half-completed `Parallel`
  fork. WP-52 derives the position from committed journal rows and re-enters the
  same `ExecuteAsync` at index 0 —
  [ADR-0015 commitment 2](ADR-0015-journal-schema-and-durable-execution.md). The
  mitigation this bullet claims held; the mechanism it named did not survive.*
- **Determinism rules apply asymmetrically, and *informational* did not survive.**
  This bullet said `FLOWX1007–1009` would be errors in `Durable` flows and
  informational in `Ephemeral` ones. **They shipped at WP-58 as Warning by default
  and Error where the compilation can prove the code is on a durable flow's replay
  path. Info was rejected outright**, and the reasoning supersedes this clause:
  Info never reaches a build log, `Ephemeral` is the *default* profile, so an Info
  set would do nothing in nearly every build — which is precisely the state all
  four ids were already in, and what kept them unraised through two phases.
  `FLOWX1011` deviated from this clause first and was right to; making the
  deviation the rule is the honest way to record that, and it stops 1011 being an
  exception. Escalation is a proof rather than a guess: a capability has no
  profile of its own, so it escalates only when a `Durable` flow *in this
  compilation* names it as a step.
- **A wrong profile is a real bug class.** `Ephemeral` on a payment saga loses
  work on deploy; `Durable` on a query costs 1 000×. Mitigated today by
  [`FLOWX1017`](../diagnostics/FLOWX1017.md) alone (signals and timers require
  durable). *`FLOWX1012` — the compensable-plus-ephemeral warning — was specified
  alongside it and is still not built, so a compensable `Ephemeral` flow compiles in
  silence. `flowx verify --cost` **does now**, and flags exactly the accident
  `FLOWX1012` would have caught at build time — from the manifest rather than the
  source, and after the build rather than during it. Its second blocker is gone:
  the fix `FLOWX1012` would recommend, `Profile = Durable`, changed nothing while
  every profile ran in memory, and since WP-52 it changes something. WP-60.*
- **The asymmetry was theoretical in one direction until WP-52 (2026-07-31), and
  is now partial.** *This bullet said `FlowX.Runtime` does not read
  `ExecutionProfile` and that `Durable` executes on the ephemeral path with no
  journal and no resumption.* The runtime reads the profile; a `Durable` flow
  journals one row per step boundary and resumes by replaying that journal into the
  same step loop; a `Durable` flow started with **no** journal is refused rather
  than run ephemerally. What is still absent is everything around the seam — no
  lease is acquired, no recovery scan exists, and no store implements
  `IFlowJournal` outside an in-memory reference in the conformance tests. So the
  second profile is a runtime that has never met a database, which is a different
  claim from "a contract, not yet a runtime" and a weaker one than "durable".
  [`FLOWX1028`](../diagnostics/FLOWX1028.md) was **narrowed to `Streaming`**, not
  deleted: `Streaming` still has no engine, and deleting the rule would have handed
  it the silence `Durable` had. It stays a warning rather than an error for the
  same reason as before — an error is repaired by writing `Ephemeral`, which erases
  the record P7 must find.
- Changing a flow's profile changes its operational characteristics
  significantly; it is a reviewable change, not a tuning knob.

**Revisit when:** journal commit cost approaches ephemeral cost (e.g. a durable
log primitive with sub-10 µs commits becomes commodity), at which point
always-durable becomes the simpler correct default.
