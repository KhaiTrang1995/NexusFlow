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

**Enforced by.** Analyzer `FLOWX1001` (a flow must be `partial`, so its plan can
be generated into it) and `ManifestIsComplete`, which fails when a declared flow
is missing from the manifest or a step names a capability the manifest never
describes. *This paragraph named `FlowNamingRule`, a test that has never existed:
nothing asserts "every public entry point in the manifest resolves to a `Flow`",
and nothing rejects an HTTP endpoint declared outside a trigger attribute —
`samples/ecommerce` registers its endpoint by hand in `Program.cs` today.*

---

## P2 — Capability First

**Statement.** All business logic lives in capabilities. There are no `Manager`,
`Processor`, `Helper`, or `Util` types in application code.

**Consequence.** Every unit of business meaning has an identity, a contract, a
version, a policy set, and an owner. Logic without those is not business logic;
it is a private method.

**Enforced by.** `Version` and `Authorization` are `required` members of
`[Capability]`, so a capability without them is a C# compile error at the
attribute site — the strongest enforcement available, and stronger than the
analyzer this paragraph used to claim. `EveryPublicContractIsVersioned` then
fails the build when that version is not SemVer, and
`EveryCapabilityDeclaresAuthorization` when a shipped `ICapability<,>` carries no
stance at all.

*Two corrections. `FLOWX1002` is **not** a banned-suffix rule — it is "step type
is not a capability", raised when a `.Step<T>()` names a type that does not
implement `ICapability<,>`. Nothing rejects a `Manager`, `Processor`, `Helper` or
`Util` type name anywhere, in any assembly. And `CapabilityContractRule` has
never existed under that or any name; the two fitness functions above are the
tests that do the work it described.*

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
regression against the recorded baseline (`scripts/check-benchmark-budgets.py`),
and `EngineAllocationTests` fails the build on any non-zero allocation on the
linear, conditional and switch paths. *There is no benchmark called
`EphemeralDispatch`; the allocation assertion is a test, not a benchmark, which
is why it can fail a build at all.* Budgets in
[14-Performance](14-Performance.md) — where **B12 is currently failing** and
B4–B11 and B13 have no harness yet.

---

## P6 — AI Native

**Statement.** Every structural fact about the application is emitted as
machine-readable data, versioned and stable.

**Consequence.** `flowx.manifest.json` is a build output on par with the
assembly. Documentation, diagrams, impact analysis, test scaffolding and agent
tool descriptors are all *derived*, never hand-maintained.

**Enforced by.** `ManifestIsComplete` fails the build when a declared flow or
capability is absent from the emitted manifest, or when a step names one the
manifest never describes. The manifest schema is versioned and validated in CI
(`ManifestSchemaTests`).

*This paragraph named `flowx verify --complete`. The CLI has three verbs —
`graph`, `manifest`, `diff` ([22-CLI](22-CLI.md)) — and `verify` is not one of
them. `ManifestIsComplete` is the check that exists, and it is narrower than the
claim in one stated way: it does **not** check policies or events. A policy can
now be attached with `.WithPolicy(...)` and is written to the manifest, but
nothing in this repository declares one, so the completeness rule has never been
exercised against a policy. See
[05 §12](05-Architecture.md#12-architecture-fitness-functions).*

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

**Enforced by.** `flowx diff` compares the manifest's `events` by major schema
version and fails on an incompatible change (`CompareEvents` in
`ManifestDiff`) — that half is real and gated in CI's *Manifest compatibility*
job.

*The other half is not. There is no outbox and no publication: `.Emit<T>()`
compiles into the plan and into the manifest, and nothing publishes it. The
compiler says so out loud — [`FLOWX1024`](diagnostics/FLOWX1024.md) is raised on
every `Emit` step for exactly this reason, and `samples/ecommerce` suppresses it
under a dated debt entry rather than hiding it. "Transactional outbox semantics"
is a **P2** commitment; the integration test proving at-least-once publication
under process kill cannot be written before the thing it tests.*

---

## P9 — Streaming Native

**Statement.** Windowing, checkpointing and backpressure are runtime services,
not user code.

**Consequence.** A stream flow declares its window and delivery guarantee; the
Stream Engine owns offsets, watermarks and rate control.

**Enforced by.** Nothing yet. There is no Stream Engine, no `Streaming`
execution profile in the runtime, no windowing and no bounded channel — so
`BackpressureConformanceTest` has nothing to assert against and does not exist.
This is a **P7** exit criterion in [20-Roadmap](20-Roadmap.md). Stated here as an
unenforced principle rather than deleted, because it is the constraint the Stream
Engine must be designed against, not an afterthought to bolt on.

---

## P10 — Observable by Default

**Statement.** Traces, metrics and structured logs exist without user
instrumentation, and every execution is replayable.

**Consequence.** One span per flow, one per step, standard attribute names,
golden signals per capability, deterministic replay from the journal.

**Enforced by.** Nothing yet, in either half. **This is the principle with the
widest gap between what it claims and what exists, and both halves were audited
in P1:**

- **Telemetry.** `TelemetryConformanceTest` does not exist, and neither does the
  thing it would assert: there is no `ActivitySource`, no `Meter`, no `ILogger`
  and no exporter anywhere under `src/`. Not one of the span attributes or
  metric names frozen in [12-Observability](12-Observability.md) is emitted by
  any code path. "Traces, metrics and structured logs exist without user
  instrumentation" is true of the *design* — the compiled graph is what makes it
  derivable — and is not true of the runtime. **P5.**
- **Replay.** `ReplayDeterminismTest` does not exist, and cannot: there is no
  journal type in the solution, so there is nothing to replay *from*. Worse for
  the claim, `FlowX.Runtime` never reads `ExecutionProfile` at all — a flow
  declared `Durable` executes on exactly the same path as an `Ephemeral` one,
  with no checkpoint and no resume. The only thing the profile currently changes
  is a build-time validation (`ExecutionPlan` requires `AwaitSignal` to be
  `Durable`) and a field in the manifest. **P2.**

The determinism *analyzers* this principle leans on are in the same state:
`FLOWX1007`, `FLOWX1008` and `FLOWX1009` do not exist, and `FLOWX1011` — the one
rule of the five that ships — is a Warning rather than an Error precisely
because `Ephemeral` is the only profile the runtime executes. See
[06 §5](06-Execution-Engine.md#5-the-determinism-boundary) and risk R2 in
[05 §11](05-Architecture.md#11-risks-and-technical-debt), which carried the same
claim as a *mitigation* and has now been corrected to say so.

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

**Enforced by.** `EveryDiagnosticIsHelpful` in
`tests/FlowX.Compiler.Tests/CompilerFitnessTests.cs`: every `FLOWX####`
descriptor has a title, a message naming the offending symbol, a description
saying what to do instead, and a help URI that resolves to a committed page.
*The name in this paragraph was `DiagnosticQualityTest`, which does not exist;
the rule does, under the name above.* Onboarding study criterion V8 in
[01-Vision](01-Vision.md) — an unrun study, not a gate.

The other half of P12 is not enforced. `dotnet new flowx` has no template
package in this repository, and `flowx graph` is the only one of the three
promised commands that ships (`flowx dev`, `flowx new` do not — see
[22-CLI](22-CLI.md)).

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
| **P6 AI-native** vs **P11 secure-by-default** | The manifest contains structure, never secrets or data. Manifest emission is scanned for secret patterns in CI (`ManifestContainsNoSecrets`) | Structure is public; data is not |

---

## The rule about rules

> Every architectural rule stated in this documentation set exists as an
> executable fitness function in `tests/FlowX.Architecture.Tests` — **or is
> marked, at the point it is stated, as not yet enforced and named with the
> phase that makes it enforceable.**

The qualifier is not a softening. The unqualified version was the rule, and it
was false in both directions: rules were stated with no gate behind them, and
gates were named that had never been written. Six of the twelve principles above
carried an **Enforced by** clause naming something that does not exist. A reader
who saw a test name stopped looking for the rule, which is precisely the failure
the sentence was written to prevent.

A rule that only lives in a document is a rule that is already being violated
somewhere. A rule that names a test which does not exist is worse: it is a
violation nobody will look for. See
[architecture verification](05-Architecture.md#12-architecture-fitness-functions)
for the "Lives in" column that was added for the same reason, and
[21 §2.4](21-Quality-Gates.md#24-gates-named-here-but-not-yet-enforced) for the
gates that are blocked rather than overlooked.

---

**Next:** [04 — Core Concepts](04-Core-Concepts.md)
