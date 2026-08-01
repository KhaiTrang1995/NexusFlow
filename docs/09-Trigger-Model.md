# 09 — Trigger Model

> **Status:** Accepted · **one transport is served; the rest are an attribute or nothing** ·
> **Audience:** application engineers, plugin authors
> **Answers:** how does one abstraction serve HTTP, brokers, cron, streams and agents?

> [!WARNING]
> **[ADR-0004](adr/ADR-0004-universal-trigger-model.md)'s "one trigger abstraction for
> every transport" is true of the *declaration* and, so far, of nothing else.** Five
> trigger attributes ship in `FlowX.Abstractions`, the compiler reads all five into
> `flowx.manifest.json`, and **one of them reaches a running transport.** There is no
> Trigger Engine: no type under `src/` or `plugins/` normalises, admits, dedupes or binds,
> and §11's `ITriggerSource` / `ITriggerSink` are declared nowhere.
>
> | Transport | What exists |
> |---|---|
> | **HTTP** ([§6](#6-http-trigger)) | **served, and generated.** `[HttpTrigger]` → `TriggerReader` → `EndpointEmitter` → `FlowXEndpoints.g.cs` → `plugins/FlowX.Http`. Route, body binding, `Idempotency-Key` enforcement when `Idempotent = true`, and RFC 7807 with `[Sensitive]` redaction are all real; `samples/ecommerce` and `samples/workflow` call the generated `app.MapFlowX()`. **A flow that suspends is served too, since WP-64** — `202` with where to continue it, and one generated delivery route per signal it waits for ([ADR-0022](adr/ADR-0022-http-shape-of-a-suspending-flow.md)); *this row used to say the signal endpoint was a design, and §6's own box has the correction*. **The OpenAPI operation is still not generated** — nothing in this repository writes an OpenAPI document, and `Version` is dropped by the reader rather than published, despite §6's *"Generated: … the OpenAPI operation"* and the same claim on `HttpTriggerAttribute` itself |
> | **Bus** ([§7](#7-bus-trigger)) | **attribute only.** `[KafkaTrigger]` compiles and publishes `kind`, `transport`, `topic` and `group`; `MaxInFlight` and `DeadLetter` reach no artifact. There is no `FlowX.Kafka` — `plugins/` holds `FlowX.Http`, `FlowX.Postgres` and `FlowX.Redis` — so nothing consumes a topic, commits an offset or dead-letters, and §7's sequence diagram is specification. **WP-72**, P3 |
> | **Schedule** ([§8](#8-schedule-trigger)) | **attribute only.** `[CronTrigger]` publishes `cron` and `timeZone`. `Overlap`, `MissedFire`, `Jitter` and `PerTenant` reach nothing at all: the manifest schema's `trigger` object has no property for them and no code reads them. Nothing fires a schedule, so "leader-elected, never double-fires" is a design — the lease store it names is real (`ILeaseStore`, both adapters), the scheduler that would take the lease is **WP-63** |
> | **Stream** ([§9](#9-stream-trigger)) | **attribute only, over an unbuilt profile.** `[StreamTrigger]` publishes its source; `Window`, `Lateness`, `Checkpoint` and `Parallelism` are dropped. `ExecutionProfile.Streaming` is an enum member no code branches on, and `.Window(…)` / `.Aggregate(…)` are not members of `IFlowBuilder<TIn, TOut>` — **§9's example does not compile.** Streaming is **P7** |
> | **Agent** ([§10](#10-agent-trigger)) | **attribute only.** `[AgentTrigger]` publishes `description` and `confirmation`. There is no MCP server, no tool descriptor and no JSON Schema generation; `MCP` occurs under `src/` only inside doc comments. §10's two properties are consequences of a surface nothing serves. **P8** — see [13-AI-Native](13-AI-Native.md), which states the same thing about `AgentTriggerAttribute` |
> | **Change**, **Cli**, **Manual** | **kinds with no attribute.** All three are `TriggerKind` members and values of the manifest schema's closed `kind` enum, so a third-party `TriggerAttribute` carrying `[TriggerKind]` can declare one and reach the manifest with it. `FlowX.Abstractions` ships nothing that does, and `TriggerKind.Cli`'s summary names `flowx run`, which is not one of the CLI's five verbs ([22-CLI](22-CLI.md)) |
> | **gRPC** | **does not exist, at any level.** [§4](#4-trigger-kinds-and-their-semantics) gives `Grpc` its own row with its own delivery, reply and ordering semantics. There is no `Grpc` member of `TriggerKind`, no such value in the schema's `kind` enum and no attribute; `TriggerKind.Http` folds *"REST, gRPC, GraphQL, webhook"* into one kind. Read that row as a ninth kind that was never declared rather than a declared one that is unimplemented — it is the reason this box is here |
>
> **§2's envelope is a type, not a value.** `TriggerEnvelope` and `TriggerHeaders` are
> declared exactly as printed and `FlowContext.Trigger` exposes one, but nothing outside
> `FlowX.Testing` and the test projects ever constructs one.
> `FlowExecutionContext.Trigger` is never assigned, so every running flow reads `default`
> — `Kind = Manual`, no source, no body, no headers. What the HTTP plugin actually
> produces is a `FlowInvocation` (correlation id, idempotency key, tenant, deadline), and
> that is what the engine reads. So the uniform-header claim holds for those four fields
> and for nothing else; `TraceParent` is neither populated nor read, which §2's own note
> already says.
>
> **§5's admission sequence is a diagram and four of its nine decisions.** Enforced today,
> on HTTP only: a missing `Idempotency-Key` is rejected when the trigger declares
> `Idempotent = true`; the tenant is resolved from validated claims and never from a
> header or body; the body binds and validates to a 400 RFC 7807. Enforced by nothing:
> payload size limits, tenant quota and rate limit — `RateLimit` is a `PolicySet` entry at
> stage `Admission` and [10-Policy-Framework](10-Policy-Framework.md) records that no
> policy runs on the forward path — idempotency *replay* (the key is required and
> propagated; no store holds a recorded result to return), and the
> `flowx_trigger_unbound_total` counter, which is one of the metrics
> [12-Observability](12-Observability.md) does not emit.
>
> **What is genuinely load-bearing** is the declaration path, and it is checked: `[TriggerKind]`
> is what the compiler can read out of a referenced assembly, `FLOWX1025` reports a trigger
> attribute that omits it, and `EveryTriggerKindHasAManifestNameTheSchemaAccepts` and
> `EveryTriggerAttributeTheAbstractionShipsHasAKnownShape` keep the enum, the reader and the
> schema from drifting apart. A third-party transport can therefore publish itself into the
> manifest today. It just has nothing to derive from at run time — see [§11](#11-writing-a-trigger-plugin).

---

## 1. The premise

Every activation mechanism reduces to the same three facts:

1. Something happened, at a point in time.
2. It carries a payload and some headers.
3. It expects a flow to run — with a delivery guarantee and possibly a reply.

FlowX normalises all of it into one envelope, then never lets the flow see it.

```mermaid
flowchart LR
    subgraph sources["Sources"]
        H["HTTP / gRPC / GraphQL"]
        B["Kafka / RabbitMQ / SB / MQTT / SQS"]
        C["Cron / interval / one-shot"]
        S["Streams / change feeds"]
        F["File watcher / blob"]
        A["AI agent (MCP)"]
        X["CLI / operator replay"]
    end
    H & B & C & S & F & A & X --> N["Trigger Engine<br/>normalise · admit · dedupe · bind"]
    N --> FL["Flow"]
    FL --> R{"Reply expected?"}
    R -- yes --> RESP["Transport-specific response<br/>HTTP 200 · gRPC status · agent result"]
    R -- no --> ACK["Ack / commit offset / mark complete"]
```

---

## 2. The envelope

```csharp
public readonly record struct TriggerEnvelope(
    TriggerKind Kind,
    string Source,                     // "POST /api/v1/orders" | "kafka:orders.requested[3]"
    ReadOnlyMemory<byte> Body,
    TriggerHeaders Headers,
    DateTimeOffset OccurredAt);

public readonly record struct TriggerHeaders(
    string CorrelationId,              // created if absent; always propagated
    string? TenantId = null,
    ClaimsPrincipal? Principal = null,
    string? IdempotencyKey = null,
    DateTimeOffset? Deadline = null,
    string? TraceParent = null);       // W3C traceparent, carried verbatim
```

Header propagation is uniform: an HTTP `traceparent`, a Kafka header, and an MQTT
user property all land in the same field, so a trace spans transports without
any user code.

*The block above previously printed a shape that never shipped:
`CorrelationId`/`TenantId` as wrapper types, an `ActivityContext TraceContext`,
and an `Extensions` dictionary. The real record takes strings, carries the
traceparent as an unparsed string, and has no extensions bag — a design choice
that keeps `FlowX.Abstractions` dependency-free, which
`AbstractionsHasNoDependencies` enforces. `TraceParent` is **carried, not
continued**: nothing reads it, because nothing starts an `Activity` (see
[12-Observability](12-Observability.md)).*

---

## 3. Declaring triggers

```csharp
[Flow("order.place", Profile = ExecutionProfile.Durable)]
[HttpTrigger("POST", "/api/v1/orders", Idempotent = true, Version = "v1")]
[KafkaTrigger("orders.requested", Group = "order-placement", StartFrom = Offset.Committed)]
[CronTrigger("0 2 * * *", TimeZone = "Europe/Berlin", Overlap = OverlapPolicy.Skip)]
[AgentTrigger(Description = "Place a customer order with payment and inventory reservation")]
public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult> { … }
```

Four transports, zero changes to the flow body. **This is quality goal Q4, and it
is the single most visible benefit of the model.**

---

## 4. Trigger kinds and their semantics

| Kind | Delivery | Reply | Ordering | Failure handling |
|---|---|---|---|---|
| `Http` | at-most-once | yes, synchronous | none | RFC 7807 + `Retry-After`; caller retries |
| `Grpc` | at-most-once | yes, sync or stream | per-stream | status code mapping |
| `Bus` | at-least-once | optional (reply-to) | per partition/key | retry → DLQ after N |
| `Stream` | at-least-once + checkpoint | no | per partition | pause → retry → poison topic |
| `Schedule` | at-least-once | no | none | missed-fire policy |
| `Change` | at-least-once | no | per key | retry → DLQ |
| `Agent` | at-most-once | yes | none | structured refusal or error |
| `Cli` / `Manual` | at-most-once | yes | none | surfaced to the operator |

---

## 5. Admission control

Before a flow is created, the Trigger Engine runs a fixed admission sequence.
This is the platform's outer defence, and it is the same for every transport.

```mermaid
flowchart TD
    E["Envelope arrives"] --> A1{"Flow exists and<br/>trigger is bound?"}
    A1 -- no --> R1["404 / DLQ · flowx_trigger_unbound_total"]
    A1 -- yes --> A2{"Payload within<br/>size limit?"}
    A2 -- no --> R2["413 / DLQ"]
    A2 -- yes --> A3{"Tenant resolved<br/>and active?"}
    A3 -- no --> R3["400 / DLQ · audit"]
    A3 -- yes --> A4{"Tenant quota<br/>and rate limit OK?"}
    A4 -- no --> R4["429 + Retry-After · backpressure upstream"]
    A4 -- yes --> A5{"Idempotency key<br/>seen before?"}
    A5 -- yes --> R5["Return recorded result · no re-execution"]
    A5 -- no --> A6{"Input binds and<br/>validates?"}
    A6 -- no --> R6["400 RFC7807 with field errors"]
    A6 -- yes --> OK["Create flow instance · execute"]
```

Admission happens **before** the flow instance exists, so a rejected request
costs no journal write, no context allocation and no capability resolution.

---

## 6. HTTP trigger

```csharp
[HttpTrigger("POST", "/api/v1/orders", Idempotent = true)]
```

Generated: the endpoint, the model binder, the OpenAPI operation, the RFC 7807
error mapping and the idempotency filter.

### API contract (generated, shown for review)

| Method & Path | Purpose | Auth | Idempotency | Success | Errors |
|---|---|---|---|---|---|
| `POST /api/v1/orders` | run `order.place` | Bearer (from capability stance) | `Idempotency-Key` **required** | 200 + result | 400 validation, 401, 403, 409 conflict, 429 quota, 503 unavailable |
| `GET /api/v1/orders/{id}` | run `order.get` | Bearer | n/a (safe) | 200 | 404 |
| `GET /api/v1/flows/{instanceId}` | instance status | operator scope | n/a | 200 | 404 |
| `POST {flow route}/{instanceId}/signals/{identity}` | deliver a signal to a waiting instance | Bearer | natural (the frontier) | 202 | 400 malformed body, 404 unknown instance or identity |

> [!IMPORTANT]
> **This row was a design and is now generated. Two things it said were wrong, and the
> route it printed was one of them.** *This box read "the signal row is a design, and
> nothing generates that endpoint" until WP-64 (2026-08-01).*
>
> **It is not `/api/v1/flows/{instanceId}/…`.** There is no flow-instance resource
> namespace — `GET /api/v1/flows/{instanceId}` above is still built by nothing — so the
> delivery route hangs off **the flow's own route**:
> `POST /api/v1/offers/{instanceId:guid}/signals/offer.countersigned` for
> `samples/workflow`'s `offer.accept`. One route is generated per signal the flow waits
> for, with the identity as a **literal segment**, which is what makes an identity nothing
> waits for a `404` from the router before any code runs
> ([ADR-0022](adr/ADR-0022-http-shape-of-a-suspending-flow.md)).
>
> **`409 not suspended` never existed and will not.** `FlowHost.SignalAsync` treats a
> delivery to an instance that is not waiting for that signal as **inert, not an error** —
> refusing would mean the host deciding what a flow is waiting for, and the journal already
> answers that. So a redelivery is `202` and changes nothing, which is what makes
> at-least-once transports safe. There is no `Idempotency-Key` rule on this route for the
> same reason.
>
> The run route gains a third answer with it: a flow that suspends is `202` with the
> instance and where to continue it, not `200` with an output it does not have.

```jsonc
// POST /api/v1/orders  → 200
{ "orderId": "01HV8…", "paymentReference": "pay_9f2…" }

// Any error — RFC 7807, always, on every transport that has a body
{ "type":  "https://errors.acme.com/inventory.out_of_stock",
  "title": "Out of stock",
  "status": 409,
  "detail": "SKU-1 has 0 available, 2 requested",
  "instance": "/api/v1/orders",
  "traceId": "00-4bf92f…-01",
  "flowInstanceId": "fi_01HV8…" }
```

Long-running durable flows return `202 Accepted` rather than holding the connection open.

```jsonc
// POST /api/v1/offers  → 202, when the flow stops at a wait
{ "instanceId": "019fbd86-b1be-7398-a9bb-b90a96c6774c",
  "status": "suspended",
  "awaiting": [
    { "signal": "offer.countersigned",
      "deliverTo": "/api/v1/offers/019fbd86-b1be-7398-a9bb-b90a96c6774c/signals/offer.countersigned" }
  ] }

// POST /api/v1/offers/{instanceId}/signals/offer.countersigned  → 202
{ "instanceId": "019fbd86-b1be-7398-a9bb-b90a96c6774c", "status": "completed" }
```

*This sentence used to end "with a `Location` header pointing at the **instance resource**".
There is no instance resource — `GET /api/v1/flows/{instanceId}` is in the table above and is
built by nothing — so `Location` points at the signal endpoint, which exists, and only when
the flow declares exactly one wait. A header that can name one of three addresses misleads two
callers out of three; `awaiting` in the body carries the whole set.*

`awaiting` is read off the compiled plan, so it is the same set of identities
`flowx.manifest.json` publishes as each `AwaitSignal` step's `signal`
([ADR-0021](adr/ADR-0021-manifest-publishes-the-wait.md)) and the same set the endpoint
generator emitted routes for: one declaration, three consumers.

---

## 7. Bus trigger

```csharp
[KafkaTrigger("orders.requested",
    Group = "order-placement",
    StartFrom = Offset.Committed,
    MaxInFlight = 32,
    DeadLetter = "orders.requested.dlq")]
```

```mermaid
sequenceDiagram
    autonumber
    participant K as Kafka
    participant P as FlowX.Kafka
    participant TE as Trigger Engine
    participant FE as Flow Engine
    participant DLQ as orders.requested.dlq

    K->>P: record (partition 3, offset 1042)
    P->>TE: envelope (key → tenant, headers → trace)
    TE->>FE: execute order.place
    alt success
        FE-->>P: completed
        P->>K: commit offset 1042
    else retryable failure
        FE-->>P: Error{Unavailable}
        P->>P: in-memory retry per policy (partition paused)
        P->>K: still uncommitted — redelivery on rebalance is safe
    else terminal failure (Validation / NotFound)
        FE-->>P: Error{Validation}
        P->>DLQ: publish with original headers + error + traceId
        P->>K: commit offset (poison message does not block the partition)
    end
```

Offsets are committed **after** flow completion. Combined with capability
idempotency this yields effectively-once processing. A terminal error is
dead-lettered rather than retried forever — head-of-line blocking is a bug, not
a durability strategy.

---

## 8. Schedule trigger

```csharp
[CronTrigger("0 2 * * *", TimeZone = "Europe/Berlin",
    Overlap = OverlapPolicy.Skip, MissedFire = MissedFirePolicy.RunOnce)]
```

| Option | Values | Meaning |
|---|---|---|
| `Overlap` | `Skip` \| `Queue` \| `Concurrent` | what happens when the previous run is still going |
| `MissedFire` | `Skip` \| `RunOnce` \| `RunAll` | behaviour after downtime |
| `TimeZone` | IANA id | DST-correct; a schedule without a zone is UTC |
| `Jitter` | duration | spreads load across replicas and tenants |

The Scheduler Engine is **leader-elected** using the same lease store as durable
flows. Two replicas never fire the same schedule; a dead leader is replaced
within the lease TTL. Schedules are per-tenant when the flow is tenant-scoped.

---

## 9. Stream trigger

```csharp
[Flow("telemetry.aggregate", Profile = ExecutionProfile.Streaming)]
[StreamTrigger("device.telemetry", Window = "tumbling:1m", Lateness = "10s",
    Checkpoint = "PT5S", Parallelism = 8)]
public sealed partial class AggregateTelemetryFlow : Flow<TelemetryBatch, Aggregate>
{
    protected override void Define(IFlowBuilder<TelemetryBatch, Aggregate> flow) => flow
        .Window(w => w.Tumbling(TimeSpan.FromMinutes(1)).AllowLateness(TimeSpan.FromSeconds(10)))
        .Aggregate<DeviceStats>((acc, r) => acc.Add(r))
        .Step<PersistAggregate>()
        .Emit<AggregateComputed>();
}
```

| Window | Semantics |
|---|---|
| `Tumbling(d)` | fixed, non-overlapping |
| `Sliding(size, advance)` | overlapping |
| `Session(gap)` | activity-bounded |
| `Global` | unbounded with explicit triggers |

Watermarks drive window closure; records later than `Lateness` are routed to a
side output rather than dropped silently. Backpressure per
[06 §10](06-Execution-Engine.md#10-backpressure-streaming-profile).

---

## 10. Agent trigger

```csharp
[AgentTrigger(Description = "Place a customer order with payment and inventory reservation",
              Confirmation = ConfirmationMode.RequiredForSideEffects)]
```

The compiler emits an MCP tool descriptor from the flow's input contract — the
same JSON Schema that drives OpenAPI. The agent surface is therefore **exactly**
the flow surface, with the same authorisation.

```mermaid
sequenceDiagram
    autonumber
    participant LLM as Agent
    participant MCP as FlowX.Ai (MCP server)
    participant TE as Trigger Engine
    participant PE as Policy Engine
    participant FE as Flow Engine
    participant U as Human

    LLM->>MCP: tools/call order.place {…}
    MCP->>TE: envelope (Kind=Agent, principal = agent identity)
    TE->>PE: admission + authorisation
    alt agent identity lacks the permission
        PE-->>MCP: Forbidden
        MCP-->>LLM: structured refusal {code, required permission}
    else side effects require confirmation
        MCP->>U: confirmation prompt with declared side effects
        U-->>MCP: approve
        MCP->>FE: execute
        FE-->>LLM: typed result
    end
```

Two properties matter here and both fall out of the model for free:

- **An agent cannot reach anything a human could not reach.** Authorisation is on
  the capability, not the transport (P11).
- **The agent sees declared side effects** (`SideEffects` in the capability
  attribute), so confirmation prompts are accurate rather than blanket. See
  [13-AI-Native](13-AI-Native.md) and [15-Security §7](15-Security.md).

---

## 11. Writing a trigger plugin

```csharp
public interface ITriggerSource
{
    TriggerKind Kind { get; }
    ValueTask StartAsync(ITriggerSink sink, CancellationToken ct);
    ValueTask StopAsync(CancellationToken ct);          // must drain, not drop
}

public interface ITriggerSink
{
    ValueTask<FlowResult> DispatchAsync(in TriggerEnvelope envelope, CancellationToken ct);
}
```

Every trigger plugin must pass `FlowX.Conformance.Tests`:

| Conformance test | Asserts |
|---|---|
| `PropagatesTraceContext` | W3C trace continues across the transport |
| `PropagatesTenantAndPrincipal` | identity survives normalisation |
| `HonoursBackpressure` | a slow sink slows the source, memory stays bounded |
| `DrainsOnShutdown` | no message lost or double-processed on SIGTERM |
| `DeadLettersTerminalErrors` | `Validation`/`NotFound` are not retried forever |
| `RespectsDeadline` | envelope deadline is enforced |
| `IsIdempotencyAware` | duplicate keys return the recorded result |

> [!IMPORTANT]
> **This box said `FlowX.Conformance.Tests` does not exist. That is now wrong,
> and what replaces it is narrower than it sounds.** `tests/FlowX.Conformance.Tests`
> exists (WP-51) and holds three suites — `JournalConformance`,
> `LeaseStoreConformance` and `RecoveryIndexConformance` — **none** of which is a
> trigger suite. **None of the
> seven tests above has been written**, `TriggerSourceConformance` does not
> exist, and the interfaces they would test against — `ITriggerSource`,
> `ITriggerSink` — are still not declared anywhere in `src/`. The two signatures
> printed above remain a design sketch, not a contract a plugin can compile
> against.
>
> What is true is that the *shape* now exists: a suite is an abstract class with
> a factory, a store author derives from it and inherits every assertion, and
> that mechanism is proved to reject a wrong implementation by name
> (`TheSuiteRejectsAStoreThatIsWrongTests`). Nothing real has met it — the only
> implementation held to it is an in-memory reference in the same project, the
> project is **not packable**, and no suite has ever run against a real store.
> A trigger plugin has nothing to derive from at all.
>
> Publishing a *trigger* suite is named as the mitigation for **both** risk R3
> and risk R8 in [05 §11](05-Architecture.md#11-risks-and-technical-debt), and
> that has not happened. It is a **P3** deliverable. `PluginsPassConformance`
> remains blocked, with what it is waiting for, in
> [21 §2.4](21-Quality-Gates.md#24-gates-named-here-but-not-yet-enforced).
>
> There is also nothing yet to compare: `plugins/FlowX.Http` is the only plugin,
> so "every plugin agrees on the minimum semantics" has one data point. What
> `FlowX.Http.Tests` does assert today is narrower and real — that HTTP
> normalises into a `TriggerEnvelope` (`HttpTriggerReaderTests`) and that every
> `ErrorCategory` maps to its documented status (`ProblemDetailsMapperTests`).
> Four of the seven rows above are additionally blocked on subsystems that do
> not exist at all: trace context (**P5**), backpressure (**P7**), deadline
> enforcement and idempotency (**P4**).
>
> Treat this table as the specification a P3 plugin author will be held to. Do
> not treat a plugin as conformant because nothing failed.

---

## 12. Known limits of the abstraction

Honest non-goals — see risk R3 in [05 §11](05-Architecture.md#11-risks-and-technical-debt):

| Transport feature | Status | Escape hatch |
|---|---|---|
| Kafka rebalance callbacks | not in the universal model | plugin options, outside the flow |
| HTTP response streaming (SSE, chunked) | v1.1 via `Flow<TIn, IAsyncEnumerable<T>>` | raw ASP.NET Core endpoint alongside |
| MQTT QoS 2 exactly-once | mapped to at-least-once + idempotency | plugin option |
| gRPC bidirectional streaming | v1.2 | raw gRPC service alongside |
| Broker-native transactions | not modelled | outbox pattern |

FlowX aims to make 95 % of integrations uniform, not to make the last 5 %
impossible. When you need the raw transport, use it — next to a FlowX flow, not
inside one.

---

**Next:** [10 — Policy Framework](10-Policy-Framework.md)
