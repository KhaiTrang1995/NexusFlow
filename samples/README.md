# FlowX Samples

Nine directories, each named for a claim in the specification it is meant to prove
— not to demonstrate syntax. **All nine contain an application.**

> [!NOTE]
> **All nine have code, and every one of them runs.** This page opened by saying
> *"three of the nine have code"* until 2026-08-02, and before that listed nine
> applications as though they existed while eight directories held a `README.md`
> and nothing else. `ecommerce` was the first, `banking` and `workflow` joined it
> on 2026-07-31, and the remaining six were built on 2026-08-02.
>
> Each of those six was a specification before it was a sample, and building it
> found claims the platform refuses rather than lacks — a leader election
> ([ADR-0031](../docs/adr/ADR-0031-an-occurrence-names-the-instance-it-starts.md)), a
> database per tenant
> ([ADR-0051](../docs/adr/ADR-0051-database-isolation-is-a-topology-not-a-runtime-level.md)),
> a fork over two suspending branches
> ([ADR-0058](../docs/adr/ADR-0058-a-poll-is-one-wait-not-a-race-between-two.md)). Those are named
> in the row and argued on the page, not quietly built around. The one number still
> unmeasured is `realtime-stream`'s throughput, which is deferred with the rest of
> the performance work.

## The nine

| Sample | Proves | Code? | What blocks it |
|---|---|---|---|
| [ecommerce](ecommerce/) | A three-step ephemeral saga with compensation, served over HTTP *and* to an agent, plus the bus and change triggers on three durable flows beside it | **Yes** | Nothing. `dotnet run --project samples/ecommerce` serves an order and answers `POST /mcp` with no infrastructure at all. It is also the only NativeAOT-published assembly, which is what proves `FlowX.Http` and `FlowX.Mcp` publish that way |
| [banking](banking/) | A durable transfer saga: compensation in strict reverse order, `[Sensitive]` redaction reaching a real outbox row, every policy stage executing, and multi-tenancy at both shipped levels | **Yes** | Nothing. Needs PostgreSQL. *This cell used to read "no declared policy runs, the journal holds no principal or input"; every one of those is now false and its README retracts each in place* |
| [workflow](workflow/) | Multi-step orchestration exercising the whole shipped DSL: `Switch`, `Parallel`, `ForEach` containing `When`, `SubFlow`, `Fail`, compensation at six sites — plus a human wait, a timer and a cron schedule | **Yes** | Nothing. Needs PostgreSQL. *This cell used to say `OnTimeout`, `Delay` and `AwaitSignal` do not work.* All three do, and `offer.accept` and `offer.window.close` are where |
| [event-driven](event-driven/) | Transport portability: HTTP → broker → change → cron, zero logic changes (Q4, V2) | **Yes** | Nothing. Needs PostgreSQL and Redis. One capability chain, four transports, each costing exactly one adapter step. **No Kafka**: no broker is reachable here and a plugin whose suite skips its own subject is a failing gate, so `[BusTrigger]` over Redis Streams is what ships and a Kafka plugin is an `IBusConsumer` that changes no flow ([ADR-0062](../docs/adr/ADR-0062-transport-portability-is-a-property-of-the-capability-chain.md)) |
| [scheduler](scheduler/) | Cron at fleet scale: overlap, jitter and missed-fire policies | **Yes** | Nothing. Needs PostgreSQL. `Overlap` and `Jitter` reached nothing before this sample and now execute; overlap is decided against the **journal**, because a lease is not held between a node dying and recovery taking its instance over. **There is no leader election** — [ADR-0031](../docs/adr/ADR-0031-an-occurrence-names-the-instance-it-starts.md) refuses it, and jitter is derived from the instance id rather than drawn at random, because *n* nodes drawing independently fire at min(*n*) ([ADR-0059](../docs/adr/ADR-0059-schedule-jitter-is-derived-from-the-firing.md)) |
| [polling](polling/) | Waiting costs one database row: no thread, no lease, no compute | **Yes** | Nothing. Needs PostgreSQL. `PollUntil` is one suspension point re-entered once per attempt — the attempt number is the step scope and the wake instant is the existing timer triple, so a parked document holds **no lease and no thread**. `RaceUntil` was refused as specified: a fork's branches share one context and one `wake_at` ([ADR-0058](../docs/adr/ADR-0058-a-poll-is-one-wait-not-a-race-between-two.md)) |
| [healthcare](healthcare/) | Consent, PII redaction and erasure by subject, at the isolation levels that exist | **Yes** | Nothing. Needs PostgreSQL. A `[Subject]` member is digested **inside** `JournalPayload` before the redaction pass, so a member can be both `[Sensitive]` and the erasure key without an accessor. **L3/L4 are refused, not missing** — [ADR-0051](../docs/adr/ADR-0051-database-isolation-is-a-topology-not-a-runtime-level.md) holds a database per tenant is a deployment topology; this runs at L1 and L2. A signed completion certificate and `flowx purge --subject` were both refused, with reasons on the page |
| [realtime-stream](realtime-stream/) | Bounded memory under a slow sink, over tumbling event-time windows | **Yes** | Nothing. Needs PostgreSQL and Redis. Peak resident records follow the declared channel capacity — asserted as correctness, and the assertion fails when backpressure stops consulting the channel. **The 250 000 rec/s number is not measured**: B13 is deferred with the rest of the performance work, so no benchmark claims it. `.Window(…)`/`.Aggregate(…)` are still not builder members; the window is declared on the trigger and the fold is a capability |
| [ai-agent](ai-agent/) | Capabilities as agent tools with real authorisation and no parallel permission system | **Yes** | Nothing. Serves real MCP JSON-RPC. Elicitation holds the flow until a human answers — **a decline never enters the flow**, which is a test that fails when the refusal is returned after the capability runs. Sampling borrows the caller's model; `resources/list` serves the manifest. Refused: naming a *magnitude* in a confirmation prompt, since computing it means running the flow the prompt gates |

**Read the "What blocks it" column as what the sample costs.** It held, for most of
this repository's life, a statement of what the platform would need before the claim
in column two could be made. Every one of those blockers is now gone or refused by a
recorded decision, so the column says what each sample needs to run and what its
page argues rather than what stops it. What it must never become is a working
example.

## Key documents, per sample

| Sample | Maps to |
|---|---|
| ecommerce | [01 §7 V1](../docs/01-Vision.md#7-measurable-success-criteria), [08](../docs/08-Flow-Definition.md) |
| banking | [15](../docs/15-Security.md), [10](../docs/10-Policy-Framework.md) |
| workflow | [06](../docs/06-Execution-Engine.md), [11](../docs/11-Distributed-Runtime.md) |
| event-driven | [09](../docs/09-Trigger-Model.md), [17](../docs/17-Plugin-System.md), quality goal Q4 |
| scheduler | [09 §8](../docs/09-Trigger-Model.md#8-schedule-trigger), [11 §3](../docs/11-Distributed-Runtime.md#3-leases-and-fencing) |
| polling | [06 §6](../docs/06-Execution-Engine.md#6-suspension-waiting-without-holding-resources), [FLOWX1017](../docs/diagnostics/FLOWX1017.md) |
| healthcare | [16](../docs/16-Multi-Tenant.md), [15 §9](../docs/15-Security.md#9-compliance-support) |
| realtime-stream | [09 §9](../docs/09-Trigger-Model.md#9-stream-trigger), [14](../docs/14-Performance.md), [FLOWX1028](../docs/diagnostics/FLOWX1028.md) |
| ai-agent | [13 §6](../docs/13-AI-Native.md#6-capabilities-as-agent-tools), [15 §7](../docs/15-Security.md#7-ai-and-agent-security) |

## Running a sample

```bash
dotnet run --project samples/ecommerce
dotnet test tests/Ecommerce.Tests
```

That is the whole of it, and it is the point of the one sample that exists: **no
infrastructure at all**. The in-memory inventory store and payment gateway are in
`Infrastructure.cs`, which is also why the flow is `Ephemeral` — the argument is
in [its README](ecommerce/README.md).

*This section used to read `flowx dev up` (Postgres + Redpanda + OTel collector +
Studio), then `flowx graph --live`. None of those exist: `flowx` has five verbs —
`graph`, `manifest`, `diff`, `verify` and `replay` ([22-CLI](../docs/22-CLI.md)) —
`graph` has no `--live`, and there is no `dev` verb, no Studio and no collector.
The count in that sentence was four until `replay --mode inspect` landed.*

The other two applications each need one server, and say so on their own pages:

```bash
FLOWX_POSTGRES_CONNECTION="Host=localhost;Port=5432;Database=postgres;Username=postgres" \
  dotnet run --project samples/banking     # and samples/workflow
```

## What every sample must contain

A sample that does not meet this bar is not merged — samples are how the
specification is proven, so they are held to the same standard as the runtime.
**`ecommerce` is the sample this list is applied to**, and three of its rows are not
met even there, which is stated rather than quietly dropped. The last row is met by
`ecommerce` alone: `banking` and `workflow` each need PostgreSQL, and both argue for
it on their own pages rather than being held to a bar written for a sample whose
whole point is that it needs nothing.

- [x] A `README.md` stating the claim it proves and the documents it maps to
- [ ] Flows and capabilities in the standard `<Feature>/` folder layout — *not met
  for `ecommerce`, deliberately: it is flat files, and the claim it proves is
  ≤ 3 files for a 4-step flow ([V1](../docs/01-Vision.md#7-measurable-success-criteria)).
  A folder layout is for a sample with more than one feature*
- [x] Unit tests for every capability; flow tests for every failure path
- [x] At least one **failure-path** test (compensation, timeout, or breaker)
- [ ] Generated architecture diagram committed as `docs/graph.md` — *not committed
  for `ecommerce`. `flowx graph` renders one from the manifest on demand; nothing
  writes it into the sample or checks it is current*
- [ ] A benchmark, where the sample maps to a budget in
  [14-Performance](../docs/14-Performance.md) — *`ecommerce` maps to B1 and B2,
  which `tests/FlowX.Benchmarks` gates in CI, but not from inside the sample*
- [x] No infrastructure required beyond `dotnet run`

**A `README.md` that describes a sample which does not exist fails this list at
every row.** That is what all eight of the code-less directories were doing; six
of them now open by saying so, and the two under construction are on their way to
not needing to.
