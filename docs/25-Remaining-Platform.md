# 25 — The remaining platform: priority, design, and what each one costs

What is left to build, in the order the gaps argue for. Every item here is
**declared and inert** or **absent** in
[CHECKLIST §5e2](../CHECKLIST.md#5e2-platform-subsystems--what-runs-what-is-declared-what-is-absent).

The recurring shape is worth naming once, because three of the six have it: a
contract is declared, published and diffed, and **the wire is cut at the last
inch**. Authorisation was found that way (`FlowExecutionContext.Principal`
returned `null` unconditionally behind a complete abstraction). Tenancy is the
same today. That is what these designs are checked against — not "is there a
type", but "does anything read it at run time".

## Priority

| # | Feature | State | Why here |
|---|---|---|---|
| 1 | **Four policy kinds** — `RateLimit`, `Idempotency`, `Cache`, `Audit` | declared, inert | The only item that **deletes a diagnostic**. `FLOWX1032` exists to tell a user their declaration does nothing; every release shipping it ships an admission |
| 2 | **Multi-tenancy** | plumbed, unenforced | The only *correctness* gap left: nothing stops one tenant's flow reading another's rows. A data-isolation bug is not a missing feature |
| 3 | ~~**Logs**~~ | **built** | Was "absent, blocked on one decision, not effort". The decision was taken as designed in [§2](#2-logs--the-design): `FlowXLog` emits through `DiagnosticSource` and `src/FlowX.Logging` bridges to `ILogger` |
| 4 | **AI surface (MCP)** | declared, unbound | `AgentTrigger` exists and nothing serves it. [13](13-AI-Native.md) specifies the descriptor and the `tools/call` sequence in full |
| 5 | **`Stream` and `Change` triggers** | declared, unbound | Two of eight kinds. `Change` is CDC over the outbox, which already exists |
| 6 | **Stream engine** | absent | **Not next.** Nothing defines the checkpoint format, watermark generation or how window state is journaled — implementing it means inventing it |
| 7 | **Studio** | absent | **Not next.** Sixteen one-line mentions and no design |

## 1. Multi-tenancy — the design

### Context (C4 L1)

```mermaid
flowchart LR
    Caller["Caller<br/><i>HTTP · bus · cron</i>"]
    subgraph FlowX
      R["Trigger reader"]
      TR["ITenantResolver<br/><b>new</b>"]
      E["Flow engine"]
      S["Store plugin"]
    end
    DB[("PostgreSQL<br/><i>RLS per tenant</i>")]

    Caller -->|"envelope + claims"| R
    R -->|"TriggerEnvelope"| TR
    TR -->|"TenantId, validated"| E
    E -->|"writes carry tenant_id"| S
    S -->|"SET LOCAL flowx.tenant"| DB
```

**The seam that does not exist is `ITenantResolver`.** Everything else in that
diagram is built: `TriggerEnvelope.TenantId`, `FlowContext.TenantId`,
`JournalWrites.TenantId`, `flow_instance.tenant_id` and its index have been
there since migration `0001`, and `FlowTelemetry` already tags spans with it.

### Class view

```mermaid
classDiagram
    class ITenantResolver {
        <<interface>>
        +Resolve(TriggerEnvelope) TenantResolution
    }
    class TenantResolution {
        +string? TenantId
        +bool Refused
        +string Reason
    }
    class ClaimTenantResolver {
        +Resolve(TriggerEnvelope) TenantResolution
    }
    class TenantIsolation {
        <<enumeration>>
        None
        Row
        Schema
        Database
    }
    class FlowInvocation {
        +string? TenantId
        +ClaimsPrincipal? Principal
    }
    class PostgresFlowJournal {
        -ApplyTenantScope(NpgsqlConnection, string?)
    }

    ITenantResolver <|.. ClaimTenantResolver
    ITenantResolver ..> TenantResolution
    ClaimTenantResolver ..> FlowInvocation : populates TenantId
    PostgresFlowJournal ..> TenantIsolation : reads
```

`TenantResolution` carries a **refusal**, not a null. A null tenant on a
multi-tenant deployment is the bug this feature exists to prevent, so the
resolver has to be able to say *no* and be distinguishable from *not
configured*.

### Runtime — happy path

```mermaid
sequenceDiagram
    participant C as Caller
    participant H as FlowHost
    participant TR as ITenantResolver
    participant E as FlowEngine
    participant J as Journal

    C->>H: invoke(envelope, claims)
    H->>TR: Resolve(envelope)
    TR-->>H: TenantId "acme"
    H->>E: ExecuteAsync(invocation{TenantId})
    E->>J: BeginAsync(tenant_id = "acme")
    J->>J: SET LOCAL flowx.tenant = 'acme'
    Note over J: RLS policy restricts every row to the setting
    J-->>E: instance opened
    E-->>H: Result
```

### Runtime — refusal, which is the case that matters

```mermaid
sequenceDiagram
    participant C as Caller
    participant H as FlowHost
    participant TR as ITenantResolver
    participant E as FlowEngine

    C->>H: invoke(envelope, no tenant claim)
    H->>TR: Resolve(envelope)
    TR-->>H: Refused("no tenant claim, and this deployment is multi-tenant")
    H--xE: never reached
    H-->>C: Result failure · ErrorCategory.Forbidden
    Note over H,C: A refusal is a Result, never an exception (ADR-0007)
```

**Deliberately not designed here:** journal partitioning.
[11 §6](11-Distributed-Runtime.md) names sharding as a lever and stops, so
building it would be invention. `Row` isolation via RLS is what the
documentation actually specifies.

### Acceptance

- A flow started with tenant *A*'s claims cannot read, resume or recover an
  instance of tenant *B* — asserted against real PostgreSQL, both directions.
- A deployment declaring multi-tenancy refuses an untenanted call rather than
  defaulting it.
- `Ephemeral` and single-tenant deployments pay nothing: budget **B2** stays a
  hard zero, gated by the existing allocation tests.

## 2. Logs — the design ✅ built

> [!NOTE]
> **This one is built, and it was built as designed.** `FlowXLog` publishes through a
> `DiagnosticListener` named `FlowX`; the flow boundary, the step boundary and the stores write
> the records; `src/FlowX.Logging` is the separate project that bridges them to `ILogger`;
> `AbstractionsHasNoDependencies` never had to move. The design below is kept in the present
> tense because it describes what shipped rather than what was intended.
>
> Two things the sketch did not say, both found in the building.
> **`DiagnosticSource.Write` is annotated `RequiresUnreferencedCode`**, because the usual
> subscriber reflects over an `object` payload — which constraint C2 makes a build error here,
> not a warning. It is answered where it is raised: one write site, one payload type named
> statically, and a `DynamicDependency` that roots the properties a reflecting subscriber would
> read. **And the scope is not the sink**: `[Sensitive]` is safe because a record carries a
> `JournalPayload`, but correlating a *capability's own* `ILogger` needs `BeginScope` called from
> `FlowX.Runtime`, which is the one thing this shape forbids. [12 §4](12-Observability.md#4-logs)
> marks that clause as still specification.

```mermaid
flowchart LR
    E["FlowEngine · FlowHost · stores"]
    S["FlowXLog<br/><i>DiagnosticSource</i>"]
    B["FlowX.Logging bridge<br/><b>new project</b>"]
    L["ILogger"]

    E -->|"structured event"| S
    S -.->|"opt-in subscription"| B
    B --> L
```

The whole feature is **one decision**:
`AbstractionsHasNoDependencies` forbids a package reference, and
`Microsoft.Extensions.Logging.Abstractions` is a package while
`System.Diagnostics.DiagnosticSource` is in the shared framework — which is why
traces and metrics were buildable and logs were not.

So the abstraction emits through `DiagnosticSource`, and a **separate**
`FlowX.Logging` project bridges it to `ILogger`. A host that wants neither
references neither, and the zero-listener cost stays the property **B6**
already asserts.

`[Sensitive]` redaction is structural and must stay so: a log event carries a
`JournalPayload`, never a value, so there is no accessor to leak through.

## 3. AI surface (MCP) — the design

```mermaid
sequenceDiagram
    participant A as Agent
    participant M as MCP endpoint
    participant H as FlowHost
    participant F as Flow

    A->>M: tools/list
    M->>M: read flowx.manifest.json
    M-->>A: descriptors from the manifest, not from reflection
    A->>M: tools/call(order.place, args)
    M->>H: invoke with the agent's claims
    H->>F: ExecuteAsync
    alt authorised
        F-->>A: Result
    else refused
        H-->>A: Forbidden, same stance the HTTP path enforces
    end
```

The manifest is already a complete, byte-pinned description of every flow, its
input contract and its authorisation stance — so `tools/list` is a projection
of an existing artifact rather than a second source of truth. **The stance is
the same one HTTP enforces**: an agent gets no separate authorisation path.

## 4. `Stream` and `Change` triggers

`Change` is the cheaper of the two and is nearly free: the outbox already
stages every event in the step's transaction and `PostgresOutboxPublisher`
already drains it, so a CDC trigger is a second consumer of a table that
exists. `Stream` needs the engine in item 6 and is blocked behind it.

## What this document is not

It is not a work-package breakdown, and it does not reserve numbers. Package
detail is deferred to each phase gate, per [PLAN §6](../PLAN.md) — this exists
so the next three features are designed before they are written, and so the
two that are **not** next say why in one place.
