# 01 — Vision

> **Status:** Accepted · **Audience:** everyone · **Reading time:** 8 min

## 1. The observation

A modern enterprise use case — *"place an order"* — is roughly forty lines of
business meaning. In a typical .NET codebase in 2026 it costs:

| Artifact | Files | Reason it exists |
|---|---|---|
| Controller / minimal endpoint | 1 | HTTP transport |
| Request DTO + validator | 2 | HTTP contract |
| Command + handler | 2 | in-process dispatch |
| Application service | 1 | orchestration |
| Domain service | 1 | business rules |
| Integration event + publisher | 2 | asynchronous transport |
| Consumer + consumer definition | 2 | message transport |
| Background worker + hosted service | 2 | deferred work |
| Retry / resilience configuration | 1 | reliability |
| Saga / state machine + persistence | 2 | consistency |
| Telemetry wiring | 1 | operations |
| **Total** | **≈ 17** | **13 of which are infrastructure** |

Thirteen of seventeen files exist because of *how the code is invoked and
transported*, not because of *what the business does*. That ratio is the problem
FlowX exists to fix.

## 2. Why the ratio keeps getting worse

Each concern was solved independently and well:

- MediatR solved in-process decoupling.
- MassTransit and NServiceBus solved messaging.
- Hangfire and Quartz solved scheduling.
- Polly solved resilience.
- Temporal and Dapr solved durable execution.
- OpenTelemetry solved observability.

None of them share a **model**. Every one introduces its own unit of work
(`IRequest`, `IConsumer`, `IJob`, `IPolicy`, `IWorkflow`, `Activity`), its own
lifecycle, its own testing story, its own failure semantics. Composing five of
them does not give you a platform; it gives you five platforms in a trench coat.

The result is measurable and familiar:

- **Business logic is not locatable.** No file, folder, or type answers "what
  happens when an order is placed".
- **The architecture is not knowable.** The dependency graph exists only in the
  heads of the people who wrote it, and in the runtime after DI resolution.
- **Change is expensive.** Moving a use case from HTTP to Kafka is a rewrite, not
  a configuration change.
- **AI cannot help much.** An LLM given this codebase must reverse-engineer
  intent from transport plumbing, because intent was never expressed directly.

## 3. The reframe

FlowX starts from a different primitive.

> A business application is a **directed graph of capabilities**, activated by
> **triggers**, governed by **policies**, executed by a **runtime**.

Everything else — HTTP routing, Kafka partition assignment, cron scheduling,
retry backoff, saga compensation, distributed tracing — is a property *of the
graph*, not a hand-written layer *around* it.

If that graph exists as a real, compiled artifact, then a very large amount of
work that engineers do by hand today becomes derivable:

| Derived from the graph | Replaces |
|---|---|
| HTTP endpoints + OpenAPI | controllers, DTO wiring, Swagger annotations |
| Message consumers + topology | consumer classes, bus configuration |
| Retry / breaker / timeout wiring | hand-placed Polly pipelines |
| Saga compensation ordering | hand-written state machines |
| Distributed trace spans + metrics | manual instrumentation |
| Architecture diagrams | out-of-date Visio files |
| Impact analysis on change | tribal knowledge |
| Test scaffolding | boilerplate |

That is the vision: **the architecture stops being documentation and becomes a
compiled artifact that other tools — including AI — can execute against.**

## 4. What FlowX is

> **FlowX is a Universal Application Platform for building AI-native,
> cloud-native, event-driven, high-performance business applications using
> compile-time orchestration.**

Three phrases carry the weight:

**Universal** — one programming model spans request/response APIs, event-driven
integration, stream processing, scheduled work, background jobs, long-running
sagas and AI agents. Not five SDKs; one.

**Compile-time orchestration** — the flow graph, the policy composition, the
trigger bindings and the telemetry are resolved by a Roslyn source generator
during build. The runtime executes a pre-computed plan. No reflection, no runtime
scanning, no service-locator, no surprises at 3 a.m.

**AI-native** — the compiler emits `flowx.manifest.json`, a complete, versioned,
machine-readable description of the application's capability graph. Tools,
humans, and language models consume the same artifact.

## 5. What FlowX is not

Being explicit about non-goals is what keeps a platform from becoming a swamp.

| Not | Because |
|---|---|
| A replacement for ASP.NET Core | FlowX hosts *on* it. Kestrel, auth, middleware stay. |
| A replacement for your database or broker | Those are infrastructure adapters, deliberately. |
| A low-code tool | Studio visualises and scaffolds; C# remains the source of truth. |
| A distributed transaction coordinator | Consistency is saga-based and explicit; no 2PC. |
| A general workflow engine for humans-in-the-loop BPM | Human tasks are a plugin, not the core model. |
| An actor framework | Orleans/Akka model *state affinity*; FlowX models *business intent*. They compose. |
| A service mesh | Network policy stays in the mesh. FlowX governs application semantics. |

## 6. Who it is for

| Persona | Pain today | What FlowX gives |
|---|---|---|
| **Application engineer** | 17 files per use case | 2 files: a flow and its capabilities |
| **Staff / principal architect** | architecture drifts silently from the diagram | fitness functions + manifest diffing in CI |
| **SRE / platform engineer** | every service is instrumented differently | uniform golden signals, replay, topology |
| **Security engineer** | authorisation logic scattered across layers | one policy graph, auditable per capability |
| **Data / integration engineer** | integration is bespoke per team | capabilities are addressable and versioned |
| **AI engineer** | agents need a safe, typed action surface | capabilities *are* tools, with policy attached |

## 7. Measurable success criteria

A vision that cannot fail is marketing. FlowX succeeds only if:

| # | Criterion | Target | Verified by |
|---|---|---|---|
| V1 | Use case cost | ≤ 3 files, ≤ 60 lines for a 4-step flow | sample audit in `samples/ecommerce` |
| V2 | Transport portability | moving a flow from HTTP to a broker, an outbox feed or a schedule = one adapter step; the capability chain below it unchanged | `TransportPortabilityTests` over `samples/event-driven` |
| V3 | Ephemeral dispatch overhead | p99 ≤ 5 µs, ≤ 1 allocation per step at steady state | `FlowX.Benchmarks`, CI-gated |
| V4 | Durable checkpoint latency | p99 ≤ 15 ms at 5 000 flows/s/node (Postgres journal) | load test in `FlowX.Runtime.Tests` |
| V5 | Cold start | ≤ 200 ms, NativeAOT-compatible | startup benchmark |
| V6 | Build overhead | generator allocation ≤ 800,000 bytes per flow and ≤ 160,000 per capability ([ADR-0014](adr/ADR-0014-derived-error-catalogue-vs-build-budget.md), 2026-08-10, replacing "≤ 8 % versus the same code without FlowX") | `check-generator-cost.py` |
| V7 | Architecture knowability | 100 % of flows, capabilities, policies and events present in the manifest | `ManifestIsComplete` |
| V8 | Onboarding | a mid-level engineer ships a correct flow within 2 hours of first contact | onboarding study, n ≥ 10 |

> **What is actually gated today, criterion by criterion.** V3 and V6 are the
> two of the four that this section calls CI-enforced and that a CI job
> measures. **V3 passes** with a wide margin ([P0.md](benchmarks/P0.md): 172.3 ns
> against a 5 000 ns budget, 0 B). **V6 passes** against the criterion
> [ADR-0014](adr/ADR-0014-derived-error-catalogue-vs-build-budget.md) re-expressed it in on
> 2026-08-10 — 148,562 bytes per capability against a 160,000 ceiling — and
> `check-generator-cost.py` fails the run when a ceiling is breached. It was failing the
> ratio that criterion replaced, at +46.6 % against ≤ 8 %
> ([B12-scale.md](benchmarks/B12-scale.md)), and that measurement stays true of what it
> measured.
>
> **V4 has no harness, and V5 now has one.** *This lead read "V4 and V5 have no harness"
> until the rig named below existed.* *This sentence said "there is no journal to checkpoint
> into"; since WP-53 there is one, with a PostgreSQL store. What V4 lacks is the load test
> (WP-50), not the journal.* (V4,
> P2) and no start-up benchmark or AOT-published image to time (V5, P9); the
> AOT job proves the binary links and serves a request, and does not measure
> 200 ms. *The V5 half of that expired on 2026-08-14: `tests/FlowX.ColdStart.Bench` publishes
> the sample with the AOT job's own command and times thirty cold starts to the first served
> flow response — **63.0 ms at p50, 102.2 ms at p99 against the 200 ms ceiling**, on a shared
> container ([V5-cold-start.md](benchmarks/V5-cold-start.md)). What stays true is the clause
> before it: the AOT job still does not measure 200 ms, and neither does any other job — V5 is
> measured and ungated.* V1 is a review, V2 has its transports and its sample and is now
> measured by
> `TransportPortabilityTests` — *this sentence read "V2 needs a second transport (P3) and a
> sample that is currently one `README.md`"; the sample is four flows over one billing chain,
> and its target is stated over that chain rather than over a flow class
> ([ADR-0062](adr/ADR-0062-transport-portability-is-a-property-of-the-capability-chain.md))* —
> V7's gate is `ManifestIsComplete` — *not
> `flowx verify --complete`, which is not a CLI verb. `verify --cost` is; the CLI has four, see
> [22-CLI](22-CLI.md)* — and even that does not check the "policies and events"
> half of the criterion. V8 has not been run.
>
> So of eight criteria: **two met and gated, one failing and gated, one measured and
> ungated, four not yet measurable.** *That third count was "five not yet measurable" until
> V5 was measured; measured-and-ungated is its own state and collapsing it into either
> neighbour is how a scorecard starts lying.* That is the expected shape with P0 complete
> and P1 in progress out of ten phases, and it is worth writing down so the table is not read as a
> scorecard.

## 8. The ten-year framing

| Platform | Made this a commodity |
|---|---|
| Kubernetes | container scheduling |
| Kafka | durable event streams |
| ASP.NET Core | web hosting |
| Terraform | infrastructure state |
| **FlowX** | **business flow execution** |

The claim is deliberately narrow. FlowX does not aspire to be an operating
system for everything. It aspires to make **the execution of business intent** a
solved, portable, observable, machine-readable commodity — the same way
Kubernetes made "run this container somewhere" a solved problem.

---

**Next:** [02 — Manifesto](02-Manifesto.md) · **Design doc:** [05 — Architecture](05-Architecture.md)
