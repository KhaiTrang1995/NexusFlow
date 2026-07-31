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
- Cost control has a real lever *in the design*: `flowx verify --cost` would flag
  durable flows with no compensation, no signals and no timers (a profile chosen
  by accident). **It does not exist** — `verify` is not a CLI verb; the CLI has
  `graph`, `manifest` and `diff` ([22-CLI](../22-CLI.md)).

**Negative / accepted trade-offs**
- **Two runtime paths to test.** The step loop is shared, but journaling,
  resumption and determinism only exist on one path. Mitigated by keeping
  resumption expressed as `ctx.ResumeFromStep` in the *same* loop, so there is no
  separate recovery code path to rot.
- **Determinism rules apply asymmetrically.** `FLOWX1007–1009` are to be errors
  in `Durable` flows and informational in `Ephemeral` ones. *None of the three
  exists yet* — they are a **P2** deliverable. The one determinism rule that does
  ship, [`FLOWX1011`](../diagnostics/FLOWX1011.md), follows the asymmetry this
  paragraph describes with one deliberate change: it is a **Warning** rather than
  Info in `Ephemeral`, because `Ephemeral` is the only profile the runtime
  executes and an Info diagnostic would never appear in any build anyone can run.
- **A wrong profile is a real bug class.** `Ephemeral` on a payment saga loses
  work on deploy; `Durable` on a query costs 1 000×. Mitigated today by
  [`FLOWX1017`](../diagnostics/FLOWX1017.md) alone (signals and timers require
  durable). *`FLOWX1012` — the compensable-plus-ephemeral warning — was specified
  alongside it and never built, so a compensable `Ephemeral` flow compiles in
  silence. `flowx verify --cost` does not exist either.*
- **The asymmetry is currently theoretical in one direction.** `FlowX.Runtime`
  does not read `ExecutionProfile`: `Durable` executes on the ephemeral path,
  with no journal and no resumption. The decision this ADR records still stands —
  it is what stops durability being made universal — but the second profile is a
  contract, not yet a runtime. The gap is now *reported*:
  [`FLOWX1028`](../diagnostics/FLOWX1028.md) warns on any flow declaring a profile
  the runtime does not implement, so the declaration can no longer be made in the
  belief that it is honoured. It is a warning rather than an error precisely to
  protect the declaration this ADR calls the most consequential a flow author
  makes — an error is repaired by writing `Ephemeral`, which erases the record P2
  must find — and it is deleted, not fixed, when the journal lands.
- Changing a flow's profile changes its operational characteristics
  significantly; it is a reviewable change, not a tuning knob.

**Revisit when:** journal commit cost approaches ephemeral cost (e.g. a durable
log primitive with sub-10 µs commits becomes commodity), at which point
always-durable becomes the simpler correct default.
