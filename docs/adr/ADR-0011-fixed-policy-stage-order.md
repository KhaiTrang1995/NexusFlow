# ADR-0011: Fix the policy stage order; do not allow user-composed pipelines

**Status:** Accepted
**Date:** 2026-07-30
**Deciders:** Platform architecture, Security

## Context

Frameworks with user-composed pipelines (ASP.NET Core middleware, MediatR
behaviours, MassTransit filters) give complete ordering freedom. Freedom here
reliably produces the same small set of production incidents:

- caching placed before authorisation → one tenant served another's data;
- retry placed outside idempotency → duplicate charges;
- rate limiting placed after authentication → an unauthenticated flood exhausts
  the token validator;
- timeout nested inside retry → 3 × 30 s attempts inside a 10 s SLA;
- validation placed after the side effect → corrupt data written, then rejected.

Every one of these is an *ordering* bug, and every one is silent until it is an
incident. They recur because ordering is expressed in code that reads fine
locally.

Options considered:

- **A. Full ordering freedom.** *Rejected:* the incidents above are not
  hypothetical; they are the modal production failure in pipeline-based systems.
- **B. Freedom with a linter warning.** *Rejected:* a warning that can be
  suppressed will be suppressed, usually at 2 a.m.
- **C. Fixed stage order with tie-breaking within a stage.** Chosen.
- **D. Fixed order with an opt-out attribute.** *Rejected for v1:* the opt-out
  becomes the norm once it exists; better to start strict and relax on evidence.

## Decision

We will fix the policy stage order at
**Admission → Identity → Integrity → Resilience → Efficiency → Execution →
Consistency**, allow ordering only *within* a stage, and make the ordering
non-configurable — because the correct order is knowable in advance, and the
incidents caused by getting it wrong are severe, silent and recurring.

## Consequences

**Positive**
- The five incident classes above become **unexpressible**, not merely
  discouraged.
- Reasoning about any capability's behaviour is uniform across the estate: you
  know what ran before it, everywhere.
- The policy graph in the manifest has a canonical shape, so it can be diffed,
  reviewed and reasoned about by tooling.
- Security review is tractable: authorisation is always before caching and always
  before execution, in every flow, without inspecting each one.

**Negative / accepted trade-offs**
- **Legitimate exceptions exist and are currently unserved** (risk R6). A
  plausible one: caching a cheap authorisation lookup itself, which the fixed
  order forbids. Current escape hatch: a capability may declare a
  `PolicyStage.Custom` handler that runs *within* its own stage.
- **Migration friction** for teams porting hand-tuned pipelines whose ordering
  encodes real, considered decisions.
- **The platform must be right.** If the fixed order is wrong for a whole class
  of applications, everyone suffers and nobody can locally fix it. This is why
  the revisit condition below is concrete rather than vague.

**Revisit when:** three documented, legitimate counterexamples are collected —
cases where the fixed order forces a materially worse design and
`PolicyStage.Custom` is insufficient. At that point, consider a narrowly scoped,
explicitly reviewed reordering attribute rather than general freedom.
