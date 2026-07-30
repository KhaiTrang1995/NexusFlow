# ADR-0001: Expose exactly two user primitives — Flow and Capability

**Status:** Accepted
**Date:** 2026-07-30
**Deciders:** Platform architecture

## Context

.NET application frameworks each introduce their own unit of work:
`IRequestHandler` (MediatR), `IConsumer` (MassTransit), `IJob` (Quartz/Hangfire),
`IHostedService`, `IWorkflow` + `Activity` (Temporal/Dapr). A typical enterprise
service uses three or four of them simultaneously. Each has a different
lifecycle, a different testing story, different failure semantics and different
telemetry. Nothing composes.

Quality goals affected: Q3 (static knowability), Q4 (transport portability),
plus success criteria V1 (use-case cost) and V8 (onboarding).

Options considered:

- **A. One primitive** — everything is a "step". *Rejected:* orchestration and
  work have genuinely different concerns; collapsing them either forbids
  composition or reintroduces it informally (a step that calls steps), which
  destroys the acyclic property the whole design relies on.
- **B. Two primitives — Flow (composition) and Capability (work).** Chosen.
- **C. Framework-per-concern with a shared telemetry convention** — keep
  `IRequestHandler`/`IConsumer`/`IJob`, unify only observability. *Rejected:* it
  makes the estate *look* uniform without making it uniform; the transport still
  dictates the code, so Q4 is unreachable.
- **D. Three primitives** (Flow, Capability, Saga). *Rejected:* a saga is a flow
  with compensation, which is a property of steps, not a new kind of thing.

## Decision

We will expose exactly two user-authored primitives — **Flow** (ordered,
compensable composition of steps) and **Capability** (one unit of business work
with a versioned contract) — because a single, closed vocabulary is the only way
one programming model can serve every transport while remaining statically
analysable.

A hard supporting rule: **a capability may never invoke another capability**
(analyzer `FLOWX1004`). All composition lives in flows.

## Consequences

**Positive**
- One lifecycle, one testing story, one failure model, one telemetry schema.
- The capability set is a *set*, not a graph; the only graph is the flow graph,
  which makes cycle detection, impact analysis and replay tractable.
- A use case becomes locatable: `order.place` → exactly one type.
- Agent tooling, docs, diagrams and OpenAPI all derive from one closed model.

**Negative / accepted trade-offs**
- Some legitimate shapes need an extra hop: shared logic used by two capabilities
  must become a private method, a domain service, or a step in a sub-flow. This
  is friction we accept in exchange for the acyclic guarantee.
- Teams migrating from MediatR must learn that a handler calling another handler
  is no longer allowed — the most common early complaint.
- Very small use cases pay a small ceremony cost (a flow wrapping a single
  capability). Mitigated by allowing a capability to be triggered directly.

**Revisit when:** a real production use case cannot be expressed as a flow of
capabilities without either duplicating logic or defeating the no-capability-calls-capability
rule — collect three such cases before reopening.
