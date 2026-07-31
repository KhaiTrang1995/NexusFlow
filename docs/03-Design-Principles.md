# 03 — Design Principles

> **Status:** Accepted · **Audience:** contributors, architects
>
> A principle without an enforcement mechanism is a slogan. Every principle below
> names the artifact that makes it true and the gate that keeps it true.

---

## P1 — Flow First

**Statement.** The unit of design is the business flow, not the controller, the
service, or the handler.

**Consequence.** A use case is discoverable by name: `order.place` maps to exactly
one `Flow` type. Directory layout is by flow, not by pattern.

**Enforced by.** `FlowNamingRule` architecture test: every public entry point in
the manifest resolves to a `Flow`; no HTTP endpoint may be declared outside a
trigger attribute. Analyzer `FLOWX1001`.

---

## P2 — Capability First

**Statement.** All business logic lives in capabilities. There are no `Manager`,
`Processor`, `Helper`, or `Util` types in application code.

**Consequence.** Every unit of business meaning has an identity, a contract, a
version, a policy set, and an owner. Logic without those is not business logic;
it is a private method.

**Enforced by.** Analyzer `FLOWX1002` (banned type-name suffixes in
`*.Application` assemblies) plus `CapabilityContractRule` architecture test:
each `ICapability<,>` implementation has a `[Capability]` attribute with a
semantic version.

---

## P3 — Trigger Agnostic

**Statement.** A flow's code must contain no reference to its activation
mechanism.

**Consequence.** Changing HTTP → Kafka → cron is an attribute change. Business
tests never mention transport.

**Enforced by.** `FlowsAreTransportFree`: no type in a flow's transitive closure
may reference `FlowX.Http`, `FlowX.Kafka`, `Microsoft.AspNetCore.*`,
`Confluent.*`, or any plugin assembly — an IL walk over the flow and everything
it reaches, including the generated half. Analyzer `FLOWX1003` covers the
narrower case of a capability holding a transport dependency. *This paragraph
named `TriggerIsolationRule`, which never existed under that or any name until
WP-35.*

---

## P4 — Compile-Time Everything

**Statement.** Anything derivable at build time is computed at build time:
dispatch, policy composition, trigger binding, telemetry schema, manifest.

**Consequence.** No `Assembly.GetTypes()`, no `Activator.CreateInstance`, no
runtime handler scanning. NativeAOT works by construction.

**Trade-off (accepted).** Dynamic, user-authored flows loaded at run time are a
deliberate non-goal of v1. When that need is real it will be served by a
*separate* interpreted profile with its own, explicitly worse, performance
contract — never by degrading the compiled path. See
[ADR-0002](adr/ADR-0002-compile-time-orchestration.md).

**Enforced by.** `NoReflectionOnHotPath`, an architecture test scanning IL for
`System.Reflection`, `System.Runtime.Loader`, `Activator`, `AppDomain` and the C#
runtime binder across `FlowX.Abstractions`, `FlowX.Core` and `FlowX.Runtime`;
`PublishAot=true` smoke test in CI. *This paragraph named `NoReflectionRule`,
which never existed under that or any name until WP-35.*

---

## P5 — Performance by Design

**Statement.** Latency, throughput and allocation budgets are stated before
implementation and gated in CI.

**Consequence.** The ephemeral execution path allocates nothing per step beyond
user payloads. Contexts are pooled. Step state is a struct. Spans are only
created when a listener is attached.

**Enforced by.** `FlowX.Benchmarks` with BenchmarkDotNet; CI fails on > 5 %
regression against the recorded baseline, and on any non-zero allocation in the
`EphemeralDispatch` benchmark. Budgets in [14-Performance](14-Performance.md).

---

## P6 — AI Native

**Statement.** Every structural fact about the application is emitted as
machine-readable data, versioned and stable.

**Consequence.** `flowx.manifest.json` is a build output on par with the
assembly. Documentation, diagrams, impact analysis, test scaffolding and agent
tool descriptors are all *derived*, never hand-maintained.

**Enforced by.** `flowx verify --complete` fails when any flow, capability,
policy or event is absent from the manifest. Manifest schema is itself versioned
and validated in CI.

---

## P7 — Cloud Native

**Statement.** Runtime instances are stateless and horizontally scalable; all
durable state lives in pluggable stores.

**Consequence.** Scale-out is a replica count. Rolling updates never lose
in-flight flows because a flow's state is journaled, not in-memory.

**Enforced by.** `RuntimeHasNoMutableStatics`: every static field in
`FlowX.Runtime` is `readonly` or `const`, by IL scan. *This paragraph named
`StatelessRuntimeRule`, which never existed under that or any name until WP-35.*
The chaos test it also claims — kill a node mid-flow, assert completion on a
survivor — **does not exist and cannot yet**: there is no journal and no second
node. It is an exit criterion of P2 in [20-Roadmap](20-Roadmap.md).

---

## P8 — Event Native

**Statement.** Publishing and consuming events is part of the core model, not a
plugin concern.

**Consequence.** `.Emit<T>()` is a first-class flow step with transactional
outbox semantics. Event schemas appear in the manifest and are versioned like
capabilities.

**Enforced by.** Outbox integration test proving at-least-once publication under
process kill; `flowx diff` fails on incompatible event schema change.

---

## P9 — Streaming Native

**Statement.** Windowing, checkpointing and backpressure are runtime services,
not user code.

**Consequence.** A stream flow declares its window and delivery guarantee; the
Stream Engine owns offsets, watermarks and rate control.

**Enforced by.** Backpressure conformance test: a slow capability must reduce
consumption rate rather than grow an unbounded queue (bounded-channel assertion).

---

## P10 — Observable by Default

**Statement.** Traces, metrics and structured logs exist without user
instrumentation, and every execution is replayable.

**Consequence.** One span per flow, one per step, standard attribute names,
golden signals per capability, deterministic replay from the journal.

**Enforced by.** `TelemetryConformanceTest` asserting the exact span/metric
schema in [12-Observability](12-Observability.md); replay test asserting a
replayed durable flow produces byte-identical step outputs.

---

## P11 — Secure by Default

**Statement.** A capability with no declared authorisation policy is denied, not
allowed.

**Consequence.** Deny-by-default at the capability boundary; authorisation
survives transport changes; every decision is auditable.

**Trade-off (accepted).** This is friction on day one. It is the correct
friction: `[Capability(Authorization = Authorization.Public)]` is an explicit,
greppable, reviewable statement.

**Enforced by.** Analyzer `FLOWX1010` (error): capability without an
authorisation declaration fails the build. Threat model in
[15-Security](15-Security.md).

---

## P12 — Developer Happiness

**Statement.** The simple case must be simple, and the platform must explain
itself when it is unhappy.

**Consequence.** Two files for a use case. `dotnet new flowx` to start.
`flowx graph` to see. Every diagnostic carries a cause, a fix, and a doc link.

**Enforced by.** `DiagnosticQualityTest`: every `FLOWX*` diagnostic has a title,
a message with the offending symbol, a suggested fix, and a help URI. Onboarding
study criterion V8 in [01-Vision](01-Vision.md).

---

## Principles in tension — how we resolve them

Principles that never conflict are not principles. These are the real tensions
and their standing resolutions.

| Tension | Resolution | Rationale |
|---|---|---|
| **P4 compile-time** vs **P11 dynamic policy** | Policy *composition* is compile-time; policy *parameters* (limits, timeouts) are runtime-configurable | Shape is static, magnitude is operational |
| **P5 performance** vs **P10 observability** | Telemetry is compile-time-inlined and listener-gated; zero cost when no exporter is attached | Pay only when observed |
| **P2 capability-first** vs **KISS** | A capability is justified by a business name. Pure functions with no policy, no telemetry need and no reuse stay private methods | Avoid ceremony inflation |
| **DRY** vs **P3 trigger-agnostic** | Shared logic is promoted to a capability, never to a "shared base flow" | Inheritance between flows is banned (`FLOWX1005`) |
| **P7 cloud-native** vs **P5 performance** | Two execution profiles: `Ephemeral` (no journal) and `Durable` (journaled). The flow author chooses per flow | Not every flow needs to survive a crash |
| **P6 AI-native** vs **P15 security** | The manifest contains structure, never secrets or data. Manifest emission is scanned for secret patterns in CI | Structure is public; data is not |

---

## The rule about rules

> Every architectural rule stated in this documentation set exists as an
> executable fitness function in `tests/FlowX.Architecture.Tests`.

A rule that only lives in a document is a rule that is already being violated
somewhere. See [architecture verification](05-Architecture.md#12-architecture-fitness-functions).

---

**Next:** [04 — Core Concepts](04-Core-Concepts.md)
