# 03 — Design Principles

> **Status:** Accepted · **Audience:** contributors, architects
>
> A principle without an enforcement mechanism is a slogan. Every principle below
> names the artifact that makes it true and the gate that keeps it true — **or
> says plainly that there is no gate yet, and which phase brings one.** P9 and
> P10 are in the second group today, and the *Enforced by* clauses of most of the
> rest carry a correction. See [the rule about rules](#the-rule-about-rules).

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

**Enforced by.** `FlowX.Benchmarks` with BenchmarkDotNet;
`EngineAllocationTests` and `AllocationBudgetTests` fail the build on any
non-zero allocation on the linear, conditional and switch paths, and
`scripts/check-benchmark-budgets.py` fails CI when a p95 exceeds a ceiling
documented in [14-Performance](14-Performance.md). *There is no benchmark called
`EphemeralDispatch`; the allocation assertion is a test, not a benchmark, which
is why it can fail a build at all.* Budgets in
[14-Performance](14-Performance.md) — where **B12 is currently failing** and
B4–B11 and B13 have no harness yet.

*This paragraph also said **"CI fails on > 5 % regression against the recorded
baseline"**, and it does not. `check-benchmark-budgets.py` splits its checks in
two and says so in its own header: **blocking** on allocation counts and budget
ceilings, **advisory** on absolute and ratio drift against
`docs/benchmarks/baseline.json`. WP-3 asserted that ratios within a single run
are machine-independent and therefore tightly gateable; two runs of the identical
commit in the identical container then disagreed by up to 63 % on ratio and 159 %
on absolute time, because the ratio's denominator sits at ~10 ns, on the noise
floor. The claim was withdrawn, and the evidence that withdrew it is kept
([benchmarks §5](benchmarks/README.md)). Drift becomes a gate — the script's
`--strict` — once the baseline is re-recorded on dedicated hardware (**WP-11**).
What still catches a real regression is the ceiling, and its honest limit is
worth stating: a four-step flow measured at ~170 ns against a 5 000 ns budget
fails loudly on an order of magnitude and would not notice a 2×.*

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

*This paragraph named `flowx verify --complete`, which does not exist. The CLI has four verbs —
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
survivor — **does not exist and cannot yet**. *This sentence said "there is no journal
and no second node"; the first half expired at WP-52 (2026-07-31).* A `Durable` flow
journals its step boundaries and can be resumed — but no store persists them, nothing
acquires a lease and nothing scans for an abandoned instance, so a killed node's work is
still simply lost. It is an exit criterion of P2 in [20-Roadmap](20-Roadmap.md).

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

*The other half is now true inside the process and unproved outside it.* **`.Emit<T>()`
has transactional outbox semantics.** The generated dispatcher builds the event body
from the author's own expression, through the flow's serialiser context and its
`SensitiveMembers`; `FlowEngine.CommitStepAsync` stages it into `StepCommit.Outbox`;
`plugins/FlowX.Postgres` writes the step row and the event in one transaction, so a
refused commit discards both; and `PostgresOutboxPublisher` drains it at-least-once in
`partition_key` order, which
`ACrashBetweenPublishingAndMarkingDeliversTwiceRatherThanNever` and
`EmitReachesTheBrokerTests` hold it to end to end.

*This paragraph said no broker plugin exists and that `published` therefore meant `handed to
a publisher`. That expired at WP-56b:* `plugins/FlowX.Redis` implements `IEventPublisher` over
Redis Streams, one stream per `partition_key`, and `PublisherConformance` holds it and the
recording double to one contract
([ADR-0018](adr/ADR-0018-outbox-publication-and-ordering.md)). *One thing is still not true and
is stated rather than left to be discovered.* Two shapes of `.Emit`
still stage nothing — one on an `Ephemeral` flow, which keeps no transaction to stage
into, and one whose contract no source-generated `JsonSerializerContext` declares.
[`FLOWX1024`](diagnostics/FLOWX1024.md) reports exactly those two, with a fix in user
code for each, and `samples/ecommerce` is the first of them: it is deliberately
ephemeral, so it suppresses the warning under a dated debt entry rather than hiding it.*

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

- **Telemetry.** *This read: "`TelemetryConformanceTest` does not exist, and neither does the
  thing it would assert: there is no `ActivitySource`, no `Meter`, no `ILogger` and no exporter
  anywhere under `src/`. Not one of the span attributes or metric names frozen in
  12-Observability is emitted by any code path." Most of it expired at WP-90.* The gate exists
  (`tests/FlowX.Hosting.Tests/TelemetryConformanceTests.cs`), eleven of the thirteen metrics and
  ten of the thirteen span attributes are emitted, and
  [12 §2](12-Observability.md#2-traces) and [§3](12-Observability.md#3-metrics) say per row which
  are not and why. **There is still no `ILogger` and no exporter shipped in-box** — an exporter
  is an application's choice of SDK, and the logs are
  [12 §4](12-Observability.md#4-logs)'s unresolved dependency question. "Traces, metrics and structured logs exist without user
  instrumentation" is true of the *design* — the compiled graph is what makes it
  derivable — and is not true of the runtime. **P5.**
- **Replay.** *This line has been wrong three times and every correction is kept.* It
  said there is no journal type in the solution; WP-51 declared `IFlowJournal`. It then
  said no code path writes to a journal and that `FlowX.Runtime` never reads
  `ExecutionProfile`; **WP-52 (2026-07-31) made it read the profile**, and a `Durable`
  flow now journals a step boundary, captures `ctx.UtcNow`, `ctx.NewId()` and `Random`'s
  seed per step, and resumes through the same loop. It then said `ReplayDeterminismTest`
  does not exist, that nothing replays a capture back into execution, that there is no
  corpus, and that "the capture is written and never read, which is exactly the state in
  which a determinism leak leaves no trace". **WP-61 (2026-07-31) ended that state**:
  `ReplayDeterminismTests` replays a corpus of eight shapes against their own journals
  and compares them action for action and row for row, and
  `FlowExecutionContext.ReplayNondeterminism` is the read half of the capture it needed
  to do it. What is left is three measured gaps — an overlapping `Parallel`, a
  compensation's ambient reads, and the engine's own deadline check — each pinned by a
  test that goes red when it is closed. **Done at P2 · WP-61.**

The determinism *analyzers* this principle leans on **shipped at WP-58**.
*This paragraph said `FLOWX1007`, `FLOWX1008` and `FLOWX1009` "do not exist", and that
`FLOWX1011` was "the one rule of the five that ships" whose Warning severity rested on a
premise that had expired. All three were raised on 2026-07-31.* The stance was re-decided
**as a set** rather than one row at a time, and `Info` was rejected outright: it never
reaches a build log, and `Ephemeral` is the *default* profile, so an informational set
would do nothing in nearly every build — which is precisely the state that kept all four
ids unraised through two phases. They ship **Warning by default, and Error where the
compilation can prove the code is on a durable flow's replay path**, so `FLOWX1011`'s
deviation stopped being an exception and became the rule. `FLOWX1006` was the one of the
five still unwritten, and it was never a severity question — it waited on the generated
payload writer, which **WP-59** emitted. It ships an **error uniformly**: it reports only
on a `Durable` flow, so the condition the rest of the set escalates on is the condition it
fires on. See
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

The other half of P12 *was* unenforced, and this paragraph said so: that
`dotnet new flowx` had no template package in this repository. It has one.
`templates/FlowX.Templates` is that package, `templates/verify.sh` generates a
project from it and asserts that it builds with `TreatWarningsAsErrors`, emits
its own endpoint and serves a request, and CI runs that script on every push.
`flowx graph` is still the only one of the three promised commands that ships —
`flowx dev` and `flowx new` do not, see [22-CLI](22-CLI.md). The path from the
scaffold onwards is [24 Getting Started](24-Getting-Started.md), whose every code
block is compiled by a test.

---

## Principles in tension — how we resolve them

Principles that never conflict are not principles. These are the real tensions
and their standing resolutions.

| Tension | Resolution | Rationale | True today |
|---|---|---|---|
| **P4 compile-time** vs **P11 dynamic policy** | Policy *composition* is compile-time; policy *parameters* (limits, timeouts) are runtime-configurable | Shape is static, magnitude is operational | **no** — composition only |
| **P5 performance** vs **P10 observability** | Telemetry is compile-time-inlined and listener-gated; zero cost when no exporter is attached | Pay only when observed | **no** — no telemetry exists |
| **P2 capability-first** vs **KISS** | A capability is justified by a business name. Pure functions with no policy, no telemetry need and no reuse stay private methods | Avoid ceremony inflation | yes, as guidance |
| **DRY** vs **P3 trigger-agnostic** | Shared logic is promoted to a capability, never to a "shared base flow" | Inheritance between flows is banned (`FLOWX1005`) | yes — `FLOWX1005` ships |
| **P7 cloud-native** vs **P5 performance** | Two execution profiles: `Ephemeral` (no journal) and `Durable` (journaled). The flow author chooses per flow | Not every flow needs to survive a crash | **no** — the choice exists, the journal does not |
| **P6 AI-native** vs **P11 secure-by-default** | The manifest contains structure, never secrets or data. Manifest emission is scanned for secret patterns in CI (`ManifestContainsNoSecrets`) | Structure is public; data is not | yes |

**Three of these resolutions were written in the present tense about mechanisms
that do not exist**, which is the same defect as an `Enforced by` clause naming a
test that was never written — a reader takes the tension as settled and stops
looking. They are standing resolutions, and the fourth column says which are also
descriptions of the code.

- **Policy parameters are not runtime-configurable, and there are now magnitudes
  worth configuring.** `.WithPolicy(...)` composes at compile time and reaches the
  manifest, and `FlowX.Runtime` executes stage 4 — so a declared timeout, attempt
  count, failure ratio and concurrency bound are real numbers a deployment might
  want to move. There is still no configuration surface that reaches one: the
  last box of [10 §4](10-Policy-Framework.md#resolution-order--one-of-the-five-levels-exists)'s
  resolution diagram does not exist. The tension is no longer vacuous, which
  makes it a live resolution rather than a description of an absence
  ([10-Policy-Framework](10-Policy-Framework.md)).
- **Telemetry is listener-gated, and the gate is asserted.** *This entry said there were
  "no listeners and nothing to gate", because nothing under `src/` constructed an
  `ActivitySource`, a `Meter` or an `ILogger`. Two thirds of that expired at WP-90:*
  `FlowX.Abstractions` carries one `ActivitySource` and one `Meter`, both named `FlowX`, and
  the flow boundary, the step boundary, the journal, the lease, the outbox and both sweeps
  emit through them. "Zero cost when unobserved" is budget **B6** in
  [14 §1.1](14-Performance.md#11-platform-budgets-overhead-attributable-to-flowx-excluding-user-code-and-io),
  and its 0 B half is now asserted by `TelemetryCostTests` as a unit test rather than by a
  harness; the 0 ns half is still unmeasured, and so is B5. **The `ILogger` third has not
  expired** — there are no logs, and [12 §4](12-Observability.md#4-logs) records what that is
  blocked on. **P5**, as P10 above already states.
- ~~**`Durable` is not journaled.**~~ **Expired at WP-52 (2026-07-31), and this entry
  outlived it by a day.** It said *"the profile is a declaration the compiler validates and
  the manifest records; `FlowX.Runtime` never reads it, so a `Durable` flow executes the
  `Ephemeral` path with no checkpoint and no resume"* — every clause of which is now false.
  The runtime reads the profile, journals one row per step boundary, takes a fenced lease,
  resumes through the same step loop from a derived frontier, suspends at `.AwaitSignal<T>`
  and wakes on a timer. What is still true and worth keeping from the original tension: the
  ephemeral path pays **nothing** for any of it, which is budget **B2**'s hard zero and the
  reason the durable seam is gated on plan-level flags rather than on a runtime check.
  **P2**.

---

## The rule about rules

> Every architectural rule stated in this documentation set exists as an
> executable fitness function in `tests/FlowX.Architecture.Tests` — **or is
> marked, at the point it is stated, as not yet enforced and named with the
> phase that makes it enforceable.**

The qualifier is not a softening. The unqualified version was the rule, and it
was false in both directions: rules were stated with no gate behind them, and
gates were named that had never been written.

**This paragraph said "six of the twelve principles". Counted from the
corrections now standing above, it is ten.** P1 named `FlowNamingRule`, P2
`CapabilityContractRule`, P3 `TriggerIsolationRule`, P4 `NoReflectionRule`, P5 a
benchmark called `EphemeralDispatch` *and* a 5 % regression gate that is
advisory, P6 the command `flowx verify --complete`, P7 `StatelessRuntimeRule` and
a chaos test, P9 `BackpressureConformanceTest`, P10 `TelemetryConformanceTest`
and `ReplayDeterminismTest` (which WP-61 built, as `ReplayDeterminismTests`), and P12 `DiagnosticQualityTest`. Only P8 and P11
were sound as written. Three of the six tension resolutions were in the same
state. Undercounting the problem is the same class of error as the problem: a
reader who sees "six" assumes the other six were checked. A reader who saw a test
name stopped looking for the rule, which is precisely the failure the sentence
was written to prevent.

A rule that only lives in a document is a rule that is already being violated
somewhere. A rule that names a test which does not exist is worse: it is a
violation nobody will look for. See
[architecture verification](05-Architecture.md#12-architecture-fitness-functions)
for the "Lives in" column that was added for the same reason, and
[21 §2.4](21-Quality-Gates.md#24-gates-named-here-but-not-yet-enforced) for the
gates that are blocked rather than overlooked.

---

**Next:** [04 — Core Concepts](04-Core-Concepts.md)
