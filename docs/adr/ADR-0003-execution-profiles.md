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
  [ADR-0015 commitment 2](ADR-0015-journal-schema-and-durable-execution.md)). The
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
  work on deploy; `Durable` on a query costs 1 000×. Mitigated at build time by
  **both** of the rules this bullet specified:
  [`FLOWX1017`](../diagnostics/FLOWX1017.md) (signals and timers require durable) and
  [`FLOWX1012`](../diagnostics/FLOWX1012.md) (compensation requires durable). *This
  bullet named the second one for two phases as specified-but-not-built, because its
  only fix — `Profile = Durable` — changed nothing while every profile ran in memory,
  and then, briefly after WP-52, refused to run at all for want of a journal to
  register. WP-53 and WP-55 gave a host one. It shipped at WP-60 as a **Warning**: it
  reports on flows that are not durable, which is the default, so an error would make
  the very trade this ADR ratified inexpressible and its remedy depends on a host
  registration no analyzer can see.* **And this bullet's claim that `flowx verify --cost`
  covered the gap in the meantime was wrong, which WP-60 found by reading the check.**
  `ProfileCostCheck` selects the flows whose manifest profile is `Durable` and reports the
  ones with no compensation, no signal and no timer. A compensable `Ephemeral` flow is
  never in the set it examines. The verb catches the *expensive* half of "a wrong profile"
  — durability bought for nothing — and until WP-60 the *lossy* half had no check at all,
  at build time or after it. The two are complementary rather than overlapping: one is a
  judgement about intent across a whole manifest that no analyzer can make, the other is a
  property of one source file that no manifest records.
- **The asymmetry was theoretical in one direction until WP-52 (2026-07-31), and
  is now partial.** *This bullet said `FlowX.Runtime` does not read
  `ExecutionProfile` and that `Durable` executes on the ephemeral path with no
  journal and no resumption.* The runtime reads the profile; a `Durable` flow
  journals one row per step boundary and resumes by replaying that journal into the
  same step loop; a `Durable` flow started with **no** journal is refused rather
  than run ephemerally. *This bullet then said everything around the seam was absent
  — no lease acquired, no recovery scan, no `IFlowJournal` outside an in-memory
  reference — and concluded that the second profile was "a runtime that has never met
  a database". All three clauses expired the same day: WP-55 acquires and renews a
  lease and sweeps for abandoned instances, WP-53 is a PostgreSQL journal and lease
  store that passes the conformance suite unmodified from another assembly, and
  `PostgresRecoveryIndex` connects the sweep to the store.* What is still absent is
  the transactional outbox, durable suspension, and a second store — and the one that
  keeps `Durable` short of being a performance claim, **any measurement at all**: B7
  and B8 have no harness. The second profile is a runtime that has met a database and
  has never been timed against one.
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
