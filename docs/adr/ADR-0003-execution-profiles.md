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
- Cost control has a real lever: `flowx verify --cost` flags durable flows with
  no compensation, no signals and no timers (a profile chosen by accident).

**Negative / accepted trade-offs**
- **Two runtime paths to test.** The step loop is shared, but journaling,
  resumption and determinism only exist on one path. Mitigated by keeping
  resumption expressed as `ctx.ResumeFromStep` in the *same* loop, so there is no
  separate recovery code path to rot.
- **Determinism rules apply asymmetrically.** `FLOWX1007–1009` are errors in
  `Durable` flows and informational in `Ephemeral` ones. This is initially
  surprising; the analyzer message explains why.
- **A wrong profile is a real bug class.** `Ephemeral` on a payment saga loses
  work on deploy; `Durable` on a query costs 1 000×. Mitigated by
  `FLOWX1012` (compensable + ephemeral warning), `FLOWX1017` (signals/timers
  require durable) and `flowx verify --cost`.
- Changing a flow's profile changes its operational characteristics
  significantly; it is a reviewable change, not a tuning knob.

**Revisit when:** journal commit cost approaches ephemeral cost (e.g. a durable
log primitive with sub-10 µs commits becomes commodity), at which point
always-durable becomes the simpler correct default.
