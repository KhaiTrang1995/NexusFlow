# FlowX Samples

Nine directories, each named for a claim in the specification it is meant to prove
— not to demonstrate syntax. **Three of them contain an application.**

> [!WARNING]
> **Three of the nine have code.** This page used to open by saying *"every sample
> ships with tests, a benchmark where a budget applies, and a generated
> architecture diagram"*, and to list nine applications as though they existed.
> Eight of the nine directories were a `README.md` and nothing else, which
> [PLAN §6a](../PLAN.md#6a-p4p9--what-this-plan-does-not-yet-contain) has recorded
> as a finding since P1 closed. **`ecommerce` is the only one you can run today**,
> and `banking` and `workflow` joined it on 2026-07-31. The
> remaining six are specifications for samples, and each now says so in its own
> first screenful — with the code blocks that would not compile marked as such,
> rather than left for a reader to discover from the compiler.
>
> They are not deleted, because several of them are good specifications and one of
> them — [event-driven](event-driven/) — is the acceptance criterion for a whole
> phase. A README that documents features its sample does not have is worse than
> no sample; a README that says which parts are design is a design document, and
> those are worth keeping.

## The nine

| Sample | Proves | Code? | What blocks it |
|---|---|---|---|
| [ecommerce](ecommerce/) | A three-step ephemeral saga with compensation, served over HTTP *and* to an agent, plus the bus and change triggers on three durable flows beside it | **Yes** | Nothing. `dotnet run --project samples/ecommerce` serves an order and answers `POST /mcp` with no infrastructure at all. It is also the only NativeAOT-published assembly, which is what proves `FlowX.Http` and `FlowX.Mcp` publish that way |
| [banking](banking/) | A durable transfer saga: compensation in strict reverse order, `[Sensitive]` redaction reaching a real outbox row, every policy stage executing, and multi-tenancy at both shipped levels | **Yes** | Nothing. Needs PostgreSQL. *This cell used to read "no declared policy runs, the journal holds no principal or input"; every one of those is now false and its README retracts each in place* |
| [workflow](workflow/) | Multi-step orchestration exercising the whole shipped DSL: `Switch`, `Parallel`, `ForEach` containing `When`, `SubFlow`, `Fail`, compensation at six sites — plus a human wait, a timer and a cron schedule | **Yes** | Nothing. Needs PostgreSQL. *This cell used to say `OnTimeout`, `Delay` and `AwaitSignal` do not work.* All three do, and `offer.accept` and `offer.window.close` are where |
| [event-driven](event-driven/) | Transport portability: HTTP → broker → cron, zero logic changes (Q4, V2) | No | **No Kafka.** *This cell used to say `FlowX.Http` is the only transport plugin.* There are four, and `plugins/FlowX.Redis` serves a bus — `samples/ecommerce` declares both halves of one event chain over it. What is unbuilt is Kafka specifically, and the CI assertion this sample *is*. WP-72, P3 |
| [scheduler](scheduler/) | Cron with leader election, overlap and missed-fire policies | No | **The scheduler exists.** *This cell used to say it did not.* `[CronTrigger]` is served — `samples/workflow`'s `offer.window.close` fires once across three nodes over one PostgreSQL, and a missed firing happens late. What is left is this sample's own overlap and per-tenant options, and its README's larger claim |
| [polling](polling/) | Waiting costs one database row: 100 000 documents in flight, zero compute | No | **Durable suspension exists** — *this cell used to say it did not*, and `samples/workflow`'s parked instance is one row holding no thread and no lease. What does not exist is this sample's own DSL: `PollUntil`, `RaceUntil` and `Backoff.Exponential(from:, to:)` appear nowhere but on its page |
| [healthcare](healthcare/) | The same code at isolation L1 and L4, plus consent, PII redaction and erasure | No | **Isolation exists at L1 and L2.** *This cell used to read "no tenant isolation of any kind ... consumed by nothing".* A tenant is resolved at admission from validated claims, enforced by PostgreSQL row-level security or a schema per tenant, and bounded by five fairness mechanisms — `samples/banking` is the demonstration. L3 and L4, residency and erasure are what is left |
| [realtime-stream](realtime-stream/) | 250 000 rec/s/node with bounded memory under a slow sink (budget B13, principle P9) | No | **The stream engine exists.** *This cell used to say it did not, and that `Profile = Streaming` raised [FLOWX1028](../docs/diagnostics/FLOWX1028.md) whatever else the flow declared.* The rule narrowed: it fires on `Streaming` with no `[StreamTrigger]`, and a flow declaring both builds. What is left is the **number** — B13 is measured by no sample, benchmark or CI job |
| [ai-agent](ai-agent/) | Capabilities as agent tools with real authorisation and no parallel permission system | No | **The MCP surface exists.** *This cell used to read "nothing serves it — no agent can invoke anything".* `plugins/FlowX.Mcp` serves `initialize`, `tools/list` and `tools/call`, `samples/ecommerce` and `dotnet new flowx` both publish a tool, and the stance that refuses an agent is the one that refuses a request — there is no second permission system, which was this sample's whole claim. Its *other* claims — sampling, elicitation, a resource surface — are what is left |

**Read the "What blocks it" column as the sample's real content.** Six of these
nine still are, today, a statement of what the platform would need before the claim
in column two could be made — but for four of them the blocker named there is gone
and what is left is narrower than the sample. That is a useful thing for a
specification repository to hold, and a dishonest thing to present as a working
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
