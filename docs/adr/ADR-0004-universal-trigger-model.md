# ADR-0004: Normalise every transport into one trigger abstraction

**Status:** Accepted
**Date:** 2026-07-30
**Deciders:** Platform architecture, Plugin team
**Amended by:** [ADR-0022](ADR-0022-http-shape-of-a-suspending-flow.md) ·
[ADR-0031](ADR-0031-an-occurrence-names-the-instance-it-starts.md) ·
[ADR-0032](ADR-0032-a-missed-schedule-fires-late.md)

## Context

Business logic today is written *inside* a transport's shape: a controller for
HTTP, a consumer for Kafka, a job for cron, a hosted service for background work.
Moving a use case between transports is therefore a rewrite, and the same
business operation exists in several incompatible forms across an estate.

Quality goal Q4 requires that moving a flow from HTTP to Kafka changes zero lines
of business logic.

Options considered:

- **A. Per-transport base classes** (`HttpFlow`, `KafkaFlow`). *Rejected:*
  reintroduces the coupling we are removing; a flow could not serve two
  transports at once.
- **B. Adapters written by users** — a thin controller that calls a flow.
  *Rejected:* works, but leaves the boilerplate we set out to delete and leaves
  each team to invent its own header/trace/tenant mapping.
- **C. One normalised `TriggerEnvelope`, with transports as plugins that produce
  it.** Chosen.

## Decision

We will normalise every activation mechanism into a single `TriggerEnvelope`
(kind, source, body, headers, occurred-at) produced by transport plugins, and
bind flows to transports through **attributes that the flow body cannot observe**
— because a transport is an integration detail, and the business operation is
what deserves to be stable.

Supporting rule: no flow or capability may reference a transport assembly
(`FLOWX1003`), and branching on `ctx.Trigger` is a warning.

## Consequences

**Positive**
- Q4 is met: one flow serves HTTP, Kafka, cron and an AI agent simultaneously.
  *That last claim is now known to be false for one of the four, and the correction is
  [ADR-0033](ADR-0033-a-scheduled-flows-input-is-its-occurrence.md) §4. A cron firing carries no
  body and the flow may not read a clock, so a scheduled flow's input contract is fixed by the
  platform — `Flow<ScheduledFire, TOut>` — while an HTTP endpoint binds a request body into
  whatever the flow declares. **One flow cannot serve both**, and `FLOWX1038` reports the
  attempt. What survives is the sentence this bullet was written to make: the flow **body** is
  unchanged across transports, and a capability never learns which one started it. What does not
  compose is two inbound contracts on one flow.*
- Header semantics (trace context, tenant, principal, idempotency key, deadline)
  are mapped once, in the platform, instead of once per team per transport.
- Admission control, quotas and authorisation apply uniformly to every ingress —
  including the agent surface, which is where inconsistency is most dangerous.
- New transports are plugins; `FlowX.Runtime` never changes (Q6).

**Negative / accepted trade-offs**
- **The abstraction genuinely leaks** (risk R3). Kafka rebalance callbacks, HTTP
  response streaming, MQTT QoS 2 and gRPC bidirectional streams do not fit the
  envelope. Accepted position: FlowX aims to make ~95 % of integrations uniform,
  not to make the remaining 5 % impossible. Transport-specific behaviour is
  configured on the plugin, *outside* the flow, and the documented non-goals are
  listed in [09 §12](../09-Trigger-Model.md#12-known-limits-of-the-abstraction).
- **Delivery guarantees differ per kind** (at-most-once for HTTP, at-least-once
  for buses). A flow that is correct under one may be wrong under another, so
  moving a flow across transports still requires thinking about idempotency —
  the *code* does not change, but the *review* is real.
- Some transport-native performance tricks (zero-copy body handling, native batch
  APIs) are harder to expose through a normalised envelope.

**Revisit when:** more than 30 % of production flows require a transport-specific
escape hatch — that would mean the abstraction is not paying for itself.

*One kind of flow was outside this decision for a day and is now back inside it. A flow that
suspends had no HTTP shape: the generated endpoint answers `200` with the flow's projected
output, and a suspended flow has none, so `samples/workflow`'s `offer.accept` declared no
`[HttpTrigger]` and mapped its routes by hand — this record's rejected option **B**, arrived
at by the transport having no answer rather than by anyone choosing it.
[ADR-0022](ADR-0022-http-shape-of-a-suspending-flow.md) gives it one — `202` with where to
continue the flow, and a generated signal endpoint — without touching the decision above: the
flow still declares an attribute the body cannot observe, and the transport still contributes
translation and nothing else.*
