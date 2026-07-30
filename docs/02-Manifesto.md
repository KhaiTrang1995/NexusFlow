# 02 — The FlowX Manifesto

> **Status:** Accepted · **Audience:** everyone · This document is normative for
> every design decision in the platform. When a decision conflicts with the
> manifesto, either the decision changes or the manifesto is amended by ADR.

---

## We have observed

Modern enterprise software has become complex in a way that does not serve
anyone.

Engineers spend more time wiring infrastructure than expressing business intent.
The concepts we work with daily — controllers, mediators, commands, handlers,
services, events, consumers, workers, schedulers, pipelines, retry policies,
brokers, stream processors — each solved a real problem, independently and well.
Together they force us to think about **transport before meaning**.

As a system grows, its business logic dissolves into technical layers.
Complexity rises while business visibility falls. Eventually nobody can answer
the simplest possible question about the system:

> *"What happens when a customer places an order?"*

That question should have a one-file answer. In most codebases it has a
three-day answer.

---

## Therefore we believe

### 1. Business intent is the source of truth. Infrastructure is a detail.

A flow describes what the business does. Where it was triggered from, which
broker delivered the message, and which database persisted the result are
adapters — replaceable, configurable, and never allowed into the flow's
vocabulary.

### 2. Everything is a Flow. Everything is a Capability.

There is exactly one unit of orchestration and exactly one unit of work. Not
seven. A platform that offers `IRequestHandler`, `IConsumer`, `IJob`,
`IWorkflow` and `IHostedService` has not unified anything; it has renamed the
same idea five times and made all five incompatible.

### 3. The trigger is not part of the design.

HTTP, gRPC, GraphQL, Kafka, RabbitMQ, Service Bus, MQTT, cron, polling,
webhooks, file watchers, CLI, database change feeds, and AI agents are the same
thing wearing different clothes: *something happened, run this flow*. A flow
must never know which one it was.

### 4. What can be decided at compile time must not be decided at run time.

Dependency graphs, dispatch tables, policy composition, trigger bindings and
telemetry structure are all knowable during build. Deciding them at run time
buys flexibility nobody asked for and costs latency, allocations, cold-start
time, AOT compatibility and — worst — *knowability*.

### 5. Performance is a design property, not a later phase.

A platform that is slow by construction cannot be optimised into being fast. We
publish budgets, we benchmark them in CI, and we fail the build when we regress.
Zero allocations on the steady-state path is a requirement, not a stretch goal.

### 6. Architecture must be machine-readable or it will rot.

Every FlowX build emits a complete manifest of the application's graph. Diagrams
are generated from it. Impact analysis queries it. AI reads it. Nothing is
described in two places, because the second place is always the stale one.

### 7. The failure path is part of the design.

Timeouts, retries, circuit breakers, compensation and idempotency are declared
alongside the happy path, in the same artifact, and are visible in the same
diagram. A design that shows only success is not a design.

### 8. Consistency is explicit, never magical.

FlowX will not pretend that distributed systems have transactions. Sagas and
compensation are first-class and visible. Anyone reading a flow can see exactly
what happens when step 3 of 5 fails.

### 9. Observability is not an add-on.

Traces, metrics, logs and causal replay exist because the runtime executes a
known graph — not because someone remembered to add an `Activity.StartActivity`.
An unobservable system is an unfinished system.

### 10. Security is a property of a capability, not of a URL.

Authorisation belongs to the business operation, so it survives when the
transport changes. A capability that requires a permission requires it whether
it was reached over HTTP, over Kafka, or by an AI agent.

### 11. Extension is the default; forking is a failure.

Every subsystem — triggers, policies, journals, serialisers, telemetry
exporters, AI providers — sits behind a published contract with a compatibility
policy. If a user must fork FlowX to do something reasonable, that is our bug.

### 12. Developer happiness is a hard requirement.

If a simple thing is not simple, the design is wrong — regardless of how elegant
it is internally. Two files for a use case. One command to run it. One command
to see it. Error messages that say what to do next.

---

## What we refuse

- **Reflection-based dispatch on the hot path.** It costs latency, allocations,
  AOT support and static knowability. There is no acceptable amount.
- **Hidden control flow.** No convention that changes execution order invisibly.
  If it affects behaviour, it appears in the graph.
- **Configuration that contradicts code.** The compiled artifact wins, always.
  Configuration selects adapters; it never redefines business meaning.
- **Exceptions as business control flow.** Expected outcomes are values
  (`Result<T>`). Exceptions signal defects and infrastructure faults only.
- **Silent breaking changes.** Contract changes are detected by the compiler and
  fail the build. `flowx diff` is a quality gate, not a report.
- **Vendor lock-in inside the programming model.** Kafka, Azure, AWS, Postgres
  and OpenAI are plugins. Removing one must not touch a single flow.
- **Speculative generality.** No abstraction ships with fewer than two real
  implementations and a documented reason they diverge.

---

## The equation

```
Application = Trigger + Flow + Capability + Policy + Runtime
```

Everything in this repository exists to make that equation true, fast, and
verifiable.

---

## Our promise to the reader of the code

Six months from now, an engineer who has never met you opens your service. They
run one command:

```bash
flowx graph --format mermaid
```

and they see, accurately and completely, what your application does — every
flow, every capability, every policy, every event, every failure path. Not what
it did when the diagram was drawn. What it does *now*.

That is the entire point.

---

**Next:** [03 — Design Principles](03-Design-Principles.md)
