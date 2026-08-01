# 12 — Observability

> **Status:** Accepted as a specification · **not built** · **Audience:** SRE, application engineers
> **Answers:** what does FlowX emit, and how do you answer "why did instance 42 fail?"

> [!WARNING]
> **FlowX emits nothing today.** There is no `ActivitySource`, no `Meter`, no
> `ILogger` and no exporter anywhere under `src/` — not one span, metric or log
> record in this document is produced by any code path, and [§3](#3-metrics) now says
> per metric what each one would additionally need — three of the thirteen need more than
> an emitter. *This box also said "the journal every replay mode reads from does
> not exist"; since WP-53 it does — `plugins/FlowX.Postgres` stores one row per
> step boundary with its non-determinism capture, which is precisely what a
> replay reads. The missing thing is the verb, not the data.* ***It then said
> `flowx replay` is not a CLI verb and that [22-CLI](22-CLI.md) has four. Both expired at
> WP-64:*** the CLI has five, `flowx replay --mode inspect` renders an instance from the
> journal, and [§5](#5-flow-replay--the-differentiator) carries which of the four modes
> that is and which three still do not exist.
>
> This document is therefore a **frozen schema, not a description**. That is
> deliberate and it is why it is written in the present tense elsewhere: the
> attribute and metric names below are a contract that generated dashboards,
> alerts and an estate's worth of queries will depend on, and they are cheaper to
> agree before emission than after. Read every table as *"what will be emitted"*.
>
> Delivery is **P5** in [20-Roadmap](20-Roadmap.md), whose Must list is exactly
> "frozen span/metric schema · `TelemetryConformanceTest` · `flowx replay` all
> four modes · generated alerts and dashboards", gated behind the **P2** journal.
> `TelemetryConformanceTest` is named in four documents as an existing gate and
> exists in none of them.

---

## 1. The premise

The runtime executes a **known graph**. It therefore knows, without any user
instrumentation, what is running, what it depends on, what it emitted and how
long each part took. Observability in FlowX is not a library you add; it is a
consequence of the architecture.

```mermaid
flowchart LR
    G["Compiled graph"] --> T["Traces<br/>flow → step → capability"]
    G --> M["Metrics<br/>golden signals per node in the graph"]
    G --> L["Logs<br/>structured, graph-correlated"]
    G --> R["Replay<br/>causal reconstruction from the journal"]
    G --> V["Live topology<br/>manifest × runtime telemetry"]
```

---

## 2. Traces

One span per flow, one per step, one per policy decision that costs time.

```
span: flow order.place                                    [durable]  1.84s
├── span: step 0 order.validate                                       2ms
├── span: step 1 inventory.reserve                                   41ms
│   └── event: retry.attempt {attempt=1, delay_ms=213, error=inventory.locked}
├── span: step 2 payment.capture                                   1.61s
│   ├── event: policy.timeout.armed {duration_ms=2000}
│   ├── span: HTTP POST api.stripe.com/charges                     1.58s
│   └── event: policy.breaker.state {from=Closed, to=Closed, ratio=0.12}
├── span: step 3 emit order.placed                                    9ms
└── event: flow.completed {steps=4, compensations=0}
```

### Span attribute schema (normative)

| Attribute | Example | On |
|---|---|---|
| `flowx.flow.id` | `order.place` | flow, step |
| `flowx.flow.version` | `1.2.0` | flow |
| `flowx.flow.instance_id` | `fi_01HV8…` | flow, step |
| `flowx.flow.profile` | `Durable` | flow |
| `flowx.step.id` | `2` | step |
| `flowx.capability.id` | `payment.capture` | step |
| `flowx.capability.version` | `2.1.0` | step |
| `flowx.trigger.kind` | `Http` | flow |
| `flowx.trigger.source` | `POST /api/v1/orders` | flow |
| `flowx.tenant.id` | `acme` | flow, step |
| `flowx.error.code` | `payment.declined` | step (on failure) |
| `flowx.error.category` | `Conflict` | step (on failure) |
| `flowx.attempt` | `2` | step |

These names are frozen because dashboards and alerts across an entire estate
depend on them being identical in every service. `TelemetryConformanceTest` is
the gate that will assert them exactly — it is a **P5** deliverable and has not
been written, and until it exists "frozen" means agreed, not enforced.

**Trace context is continued, never restarted** — across HTTP, Kafka headers,
MQTT user properties, cron-originated flows (linked to the schedule's span) and
agent calls. A single trace shows the whole causal chain from the user's click
to the third downstream event.

---

## 3. Metrics

### Golden signals, automatically, per graph node

| Metric | Type | Labels | Today |
|---|---|---|---|
| `flowx_flow_duration_seconds` | histogram | `flow`, `profile`, `outcome`, `tenant` | no emitter; the subject runs |
| `flowx_flow_active` | gauge | `flow`, `state` | no emitter; and `state=Suspended` would be permanently zero — `FlowInstanceState.Suspended` is a value nothing sets (**WP-63**) |
| `flowx_flow_total` | counter | `flow`, `outcome` | no emitter; the subject runs |
| `flowx_step_duration_seconds` | histogram | `flow`, `step`, `capability`, `outcome` | no emitter; the subject runs |
| `flowx_capability_duration_seconds` | histogram | `capability`, `outcome` | no emitter; the subject runs |
| `flowx_capability_unhandled_total` | counter | `capability` — **a defect signal** | no emitter; `FlowErrors.Unhandled` is the fact it would count |
| `flowx_trigger_admitted_total` / `_rejected_total` | counter | `kind`, `reason`, `tenant` | no emitter **and nowhere to emit from.** There is no Trigger Engine; §5 of [09-Trigger-Model](09-Trigger-Model.md) is a diagram with four of its nine decisions enforced, all inside the HTTP endpoint. A `kind` label needs one admission point serving every transport, and there is one transport |
| `flowx_journal_commit_seconds` | histogram | `operation` | no emitter; the subject runs (`IFlowJournal`, both adapters) |
| `flowx_lease_lost_total` | counter | `reason` | no emitter; the subject runs (`DurableLease`, `FencingToken`) |
| `flowx_outbox_pending` | gauge | `type` | no emitter, **and nothing else missing** — unlike its sibling below. `SELECT count(*) … WHERE published_at IS NULL GROUP BY type` over `outbox_event` is exactly this gauge |
| `flowx_outbox_lag_seconds` | gauge | `type` | no emitter **and the fact is not recorded anywhere.** See below |
| `flowx_stream_lag_records` | gauge | `topic`, `partition` | no emitter **and no subject.** `ExecutionProfile.Streaming` is an enum member no code branches on, and no stream trigger reaches a transport (**P7**) |
| `flowx_flow_compensation_failed_total` | counter | `flow`, `step` — **always alert** | no emitter; the *seam* exists. `ICompensationAlertSink.CompensationExhausted` fires exactly once per exhausted compensation, and its own doc comment names this metric as the thing that is not built |

Plus every policy metric from [10 §9](10-Policy-Framework.md#9-observing-policies) —
none of which is emitted either, and for a further reason: [10](10-Policy-Framework.md)
records that no policy runs on the forward path, so a rate-limit rejection or a cache hit
is not merely uncounted, it does not occur.

> [!WARNING]
> **The fourth column reads "no emitter" thirteen times, and that is the whole table.**
> The box at the top of this document says FlowX emits nothing; this column says what each
> row would additionally need, because "not built" hides three different distances from
> being true and an operator reading a dashboard specification deserves to know which one
> a row is.
>
> - **Ten rows need only an emitter.** The thing being measured happens today, in code
>   this repository ships. When a `Meter` arrives, these come with it.
> - **`flowx_stream_lag_records` has no subject at all.** Nothing streams, so it cannot be
>   emitted by adding an emitter — it arrives with **P7**, not with **P5**.
> - **`flowx_trigger_*` has a subject and no seam.** HTTP does reject requests at
>   admission, but it is the only transport and the rejection happens inside the endpoint;
>   a counter labelled by `kind` presupposes the shared admission point **P3** introduces.
> - **`flowx_outbox_lag_seconds` needs a schema change**, and it is the row this section
>   overstated hardest.
>
> **`flowx_outbox_lag_seconds` is not merely unemitted — nothing in the store could
> compute it.** The outbox itself is real: [ADR-0018](adr/ADR-0018-outbox-publication-and-ordering.md)
> declares `IEventPublisher`, `FlowEngine` stages an event in the step's own transaction,
> and `PostgresOutboxPublisher` drains `outbox_event` at-least-once in per-`partition_key`
> order. But `outbox_event` carries exactly one timestamp — `published_at`, which is `NULL`
> for precisely the rows this gauge is about. Migration `0004` added `staged_seq`, which is
> an ordering sequence and deliberately not a clock: ADR-0018 considered ordering by a
> staging timestamp and rejected it, because *"a timestamp is not monotonic across nodes
> and ties are ordinary at commit granularity"*. So the age of a pending event is a fact the
> schema does not hold, and this gauge needs a column before it needs a meter. Its sibling
> `flowx_outbox_pending` needs no column: the rows are there and so is `type`.
>
> One thing the lag gauge would not tell you even then, and it is the reason a reader
> should not wait for it: `IEventPublisher`'s only implementation anywhere is a recording
> test double ([11 §5](11-Distributed-Runtime.md#5-the-transactional-outbox)). On a
> deployment with no broker wired, *pending* is not a backlog and *lag* is not a delay —
> both are the publisher being absent, which `flowx_outbox_pending` already says by growing
> without bound.
>
> The emitter is **P5** in [20-Roadmap](20-Roadmap.md), and `TelemetryConformanceTest` —
> named in four documents as an existing gate — is the thing that would stop this table
> drifting from what is emitted once anything is.

### Cardinality discipline

`tenant` is a label only where tenant-level SLOs exist, and is capped by a
configurable allow-list with an `other` bucket. `flow.instance_id` is **never** a
metric label — it belongs in traces and logs. Cardinality is a production
incident waiting to happen, so the platform enforces the discipline rather than
documenting it.

---

## 4. Logs

Structured only. Data as fields, never interpolated into the message.

```jsonc
{
  "timestamp": "2026-07-30T09:14:02.113Z",
  "level": "Warning",
  "message": "Step failed and will be retried",
  "flowx.flow.id": "order.place",
  "flowx.flow.instance_id": "fi_01HV8…",
  "flowx.step.id": 2,
  "flowx.capability.id": "payment.capture",
  "flowx.error.code": "payment.gateway_timeout",
  "flowx.attempt": 1,
  "flowx.tenant.id": "acme",
  "trace_id": "4bf92f3577b34da6a3ce929d0e0e4736",
  "span_id": "00f067aa0ba902b7"
}
```

Every log written inside a capability inherits this scope automatically — the
Capability Engine opens the logging scope before invoking. A capability that logs
`_logger.LogWarning("Payment failed for {OrderId}", id)` produces a record already
correlated to its flow, step, tenant and trace.

### Redaction

Fields marked `[Sensitive]` on a contract are redacted in logs, traces, the
journal **and** the replay view:

```csharp
public sealed record PaymentMethod([property: Sensitive] string Pan, string Brand);
```

Redaction is applied by the generated serialiser, so there is no code path that
can forget it. `SecretsNeverLeaveTheProcess` — *a CI test that does not exist,
and would need emitted telemetry fixtures there are none of* — is intended to
scan them for known secret patterns. What does run today is
`ManifestContainsNoSecrets`, which scans the emitted **manifests** for the shape
of a secret; that is a different artifact and a narrower claim.

> **Status: one path, not every path.** The compiler reads `[Sensitive]`, records the
> member in `flowx.manifest.json`, and emits the names as `Flow.SensitiveMembers`. The
> HTTP endpoint uses that list to replace matching structured error detail with
> `[redacted]` before the body is written — so a capability that attaches a secret to an
> `Error` does not send it to the caller.
>
> That is the **only** path this release serialises a capability-supplied value on.
> Logs, traces, the journal and the replay view do not exist yet, so the "no code path
> can forget it" claim above is still ahead of us. The value also still travels wherever
> your own code puts it. Tracked as **WP-12a** in [PLAN.md](../PLAN.md).

---

## 5. Flow replay — the differentiator

Because a durable flow's every step is journaled, execution is reconstructable.

```bash
flowx replay --instance fi_01HV8… --mode inspect
```

```
Flow order.place@1.2.0   instance fi_01HV8…   tenant acme
State: CompensationFailed        Duration: 4.2s        Trigger: kafka:orders.requested[3]@1042

  ✅ step 0  order.validate        2ms    → ValidatedOrder{id=…, total=EUR 19.98}
  ✅ step 1  inventory.reserve    41ms    → Reservation{sku=SKU-1, qty=2}   (attempt 2)
  ❌ step 2  payment.capture     1.61s    → Error{payment.gateway_timeout, Unavailable}
       attempts: 3 · backoff 213ms, 587ms · breaker Closed→Open
  🔄 step 1c inventory.release     —      → Error{inventory.unavailable}    (5 attempts)

  Non-deterministic values captured:
      clock@step0 = 2026-07-30T09:14:00.001Z
      id@step1    = res_01HV8…

  → Next action: flowx replay --instance fi_01HV8… --from 1c   (after inventory recovers)
```

| Mode | Behaviour | Use | Status |
|---|---|---|---|
| `--mode inspect` | render history, no execution | incident analysis | **built** — WP-64, [22 §9](22-CLI.md#9-flowx-replay---mode-inspect--reading-an-instance) |
| `--mode simulate` | re-execute with capabilities stubbed from the journal | verify a fix against real data | needs the engine |
| `--mode resume --from <step>` | continue the real instance | operator recovery | needs the engine, a lease and a fence |
| `--mode fork` | new instance seeded from this history | test a fix without touching production state | needs the engine and a journal *write* |

`simulate` is what makes post-incident work fast: you reproduce a production
failure locally, with the exact inputs, without touching any production system.

> **`inspect` is built and the other three are not, and the split is not about effort.**
> `inspect` renders and runs nothing, which is the only reason the CLI is allowed to read a
> journal at all: [ADR-0020](adr/ADR-0020-cli-reads-the-journal-as-rows.md) permits the verb
> *because* reading rows needs no engine, and it says in as many words that its argument
> does **not** reach the three modes that execute. Those are blocked on a decision — an
> out-of-process engine the CLI shells to, or the conclusion that they are not CLI verbs at
> all — and not merely on a phase.

### 5.1 Where the worked output above is aspirational

The rendering above is this page's specification and the built verb follows its shape. Four
things in it are **not** what the tool prints, and each is a place where this document
assumed the journal holds more than it does. They are recorded here, at the spec, rather
than only as departures noted at the implementation.

- **`Trigger: kafka:orders.requested[3]@1042`.** Nothing journals a trigger. `flow_instance`
  carries `correlation_id` and `trace_id`, and neither is a broker, topic, partition or
  offset. The built verb prints the correlation id and does not invent the rest.
- **`→ ValidatedOrder{id=…, total=EUR 19.98}`.** The journal stores a payload as JSON, not
  as a typed literal, and a tool that links no FlowX assembly has no contract types to
  render it through. It prints the stored document.
- **`attempts: 3 · backoff 213ms, 587ms · breaker Closed→Open`.** No column records a
  backoff or a breaker transition. What the journal has is one row per attempt, so the built
  verb shows the attempts themselves and says nothing about the policy that spaced them.
- **`→ Next action: flowx replay … --from 1c`.** That is a `resume`, which does not exist
  and is not reachable under ADR-0020. The built verb suggests no next action, because the
  only ones it could honestly suggest are the ones it cannot perform.
- **The captures under a forked step.** The capture is real; its *attribution* is not
  reliable, because a `Parallel`'s branches share one execution context and a capture taken
  at one branch's commit carries everything minted since the previous commit — including a
  sibling's, which
  `ReplayDeterminismTests.AForkAttributesOneBranchsCapturedIdToItsSiblingsRow` pins. The
  built verb joins the manifest's plan to learn which steps are branches and marks their
  captures; with no manifest it reports that the check could not run.

**And `flow_instance.input` is NULL on every row ever written** — `FlowHost` passes
`input: null` — so no replay mode currently has the "exact inputs" `simulate` is described
above as using. `inspect` renders that as `unknown` rather than as an empty payload, because
NULL does not distinguish a flow started with no input from one whose input was never
captured. The emoji markers are ASCII in the built verb too, for the reasons
[22 §9.2](22-CLI.md#92-two-departures-from-12-5) gives.

The `fi_01HV8…` instance id is illustrative in the same way: the journal keys on a UUID, so
that is what `--instance` takes. The verb says so by name when it is handed something else,
rather than reporting an unknown instance.

---

## 6. Live topology

```bash
flowx graph --live --format mermaid
```

The manifest supplies the structure; runtime metrics supply the colour.

```mermaid
flowchart LR
    T(["HTTP POST /api/v1/orders<br/>1.2k rpm"]) --> V["order.validate<br/>p99 3ms · err 0.0%"]
    V --> R["inventory.reserve<br/>p99 48ms · err 0.4%"]
    R --> P["payment.capture<br/>p99 1.9s · err 12% ⚠<br/>breaker OPEN"]
    P --> E[["order.placed<br/>1.05k/min · outbox lag 0.4s"]]
    R -. "compensation 12/min" .-> C["inventory.release"]

    style P fill:#c62828,color:#fff
    style C fill:#ef6c00,color:#fff
    style V fill:#2e7d32,color:#fff
    style R fill:#2e7d32,color:#fff
```

Studio renders the same data interactively, adds impact analysis ("what breaks if
`payment.capture` is down?" — answerable from the graph, not from tribal
knowledge) and links every node to its traces.

---

## 7. SLOs and alerting

Each quality goal in [05 §1.2](05-Architecture.md#12-quality-goals-measurable--arc42-12) maps to an SLO and an alert.

| SLO | Target | Alert on |
|---|---|---|
| Flow availability | 99.9 % non-`Internal` outcomes per flow | burn rate > 2 % of budget/hour |
| Flow latency | p99 within the flow's declared deadline | p99 > 80 % of deadline for 10 min |
| Journal health | p99 commit < 15 ms | p99 > 50 ms for 5 min |
| Outbox freshness | lag < 5 s | lag > 30 s for 2 min |
| Stream lag | < 10 s | lag > 60 s |
| **Compensation failure** | **0** | **any occurrence — page immediately** |
| Capability defects | 0 unhandled exceptions | any occurrence — ticket |
| Breaker state | closed | open > 5 min |

Alert rules are **generated from the manifest**, so a new flow arrives with its
dashboards and alerts already defined:

```bash
flowx generate alerts --format prometheus > alerts.yaml
flowx generate dashboard --format grafana > dashboard.json
```

Nobody has ever kept hand-written dashboards in sync with a growing service
estate. Generating them from the same artifact that defines the code is the only
approach that stays true.

---

## 8. Cost of observability

Principle P5 says performance is a design property, so telemetry must be free
when unobserved:

| Mechanism | Effect |
|---|---|
| `ActivitySource.HasListeners()` checked before span creation | zero cost with no exporter attached |
| Metrics use pre-resolved tag arrays from the plan | no per-call tag allocation |
| Log scopes are structs, pooled with the context | no per-step allocation |
| Sampling: head-based 1 %, plus tail-based 100 % on error | full fidelity where it matters |
| Journal is the replay source, not the trace backend | replay does not depend on trace retention |

Measured in `FlowX.Benchmarks`: telemetry adds **< 200 ns** per step with an
exporter attached, **0 ns and 0 allocations** without one.

---

## 9. What to look at first, by symptom

> [!WARNING]
> **Every first signal in this table is a metric nothing emits** — see
> [§3](#3-metrics), which says per row what each would need. This is a runbook for the
> platform once **P5** lands, not a procedure that works today; an operator who follows a
> row now finds no such series, which is indistinguishable from a healthy one. The
> right-hand column is more usable than the left: a trace exemplar, a publisher log and a
> capability's idempotency declaration are things a reader can go and look at, and
> `flowx replay --mode inspect` is a verb that exists.
>
> Two rows are worse than uncounted and are marked below: they would be misleading even
> with an emitter attached, because the subsystem they diagnose is not the one this
> repository has.

| Symptom | First signal | Then |
|---|---|---|
| Latency spike | `flowx_step_duration_seconds` by capability | trace exemplar for the slow step |
| Error spike | `flowx_flow_total{outcome=Failure}` by `error_code` | `flowx replay --mode inspect` on a failing instance |
| Stuck flows | `flowx_flow_active{state=Suspended\|Running}` growing | lease metrics; a step exceeding its budget — **but no instance is ever `Suspended`** (WP-63), so only the `Running` half of this can move |
| Events not arriving | `flowx_outbox_pending` — **and not `flowx_outbox_lag_seconds`,** which no schema can compute (§3) | publisher logs, broker health — **but the only `IEventPublisher` anywhere is a test double, so a growing `outbox_pending` on a real deployment means no publisher is wired rather than a broker in trouble** |
| Duplicated effects | `flowx_idempotency_replays_total` = 0 with retries > 0 | a capability declaring `Idempotent = true` but not honouring the key — **note that this counter is zero by construction today: no policy runs on the forward path ([10](10-Policy-Framework.md)), so nothing replays a recorded result** |
| One tenant slow | tenant-labelled histograms | quota and rate-limit rejections |
| Memory growth | `flowx_bulkhead_queue_depth`, stream channel depth | backpressure not reaching the source |

---

**Next:** [13 — AI-Native](13-AI-Native.md)
