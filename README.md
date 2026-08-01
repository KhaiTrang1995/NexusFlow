<div align="center">

# FlowX

**The Universal Application Platform**

*Write Business. Compile Intelligence. Run Everywhere.*

[![Status](https://img.shields.io/badge/status-design--phase-blue)](docs/20-Roadmap.md)
[![License](https://img.shields.io/badge/license-Apache--2.0-green)](LICENSE)
[![Spec](https://img.shields.io/badge/spec-arc42%20%2B%20C4%20%2B%20ADR-informational)](docs/05-Architecture.md)
[![Quality gate](https://img.shields.io/badge/quality-SonarQube%20clean-brightgreen)](docs/21-Quality-Gates.md)
[![OWASP](https://img.shields.io/badge/OWASP-Top%2010%20mapped-red)](docs/21-Quality-Gates.md#3-owasp-top-10-mapping)

![FlowX — the universal application platform. Universal triggers (HTTP, gRPC, GraphQL, Kafka, RabbitMQ, Azure Service Bus, MQTT, cron, polling, webhook, SignalR, file watcher, AI agent, CLI) feed a runtime platform of eight engines: trigger, flow, capability, policy, event, stream, scheduler and plugin. Those rest on five core abstractions — flow, capability, context, policy and event — which reach infrastructure through pluggable connectors, and deploy to Docker, Kubernetes, serverless, bare metal, multi-cloud and multi-region.](docs/assets/flowx-overview.png)

</div>

---

## What FlowX is

FlowX is a **universal application runtime** for building AI-native, cloud-native,
event-driven, high-performance business applications using **compile-time
orchestration**.

> Applications are not collections of services.
> Applications are **networks of business capabilities connected by executable flows**.

FlowX turns that sentence into a runtime.

## The core equation

```
Application  =  Trigger  +  Flow  +  Capability  +  Policy  +  Runtime
```

There is no Controller, no Mediator, no Handler, no Consumer, no Scheduler.
Those are not architectural concepts — they are **transport details**, and FlowX
models all of them as one thing: a **Trigger**.

## What it looks like

```csharp
// A capability: one unit of business meaning. Transport-agnostic. Testable alone.
[Capability("inventory.reserve", Version = "1.0")]
public sealed class ReserveInventory : ICapability<ReserveRequest, Reservation>
{
    private readonly IInventoryStore _store;
    public ReserveInventory(IInventoryStore store) => _store = store;

    public async ValueTask<Result<Reservation>> ExecuteAsync(
        ReserveRequest input, CapabilityContext ctx, CancellationToken ct)
    {
        var ok = await _store.TryReserveAsync(input.Sku, input.Quantity, ct);
        return ok
            ? Result.Ok(new Reservation(input.Sku, input.Quantity))
            : Result.Fail<Reservation>(InventoryErrors.OutOfStock(input.Sku));
    }
}
```

```csharp
// A flow: business intent, composed at compile time. Knows nothing about HTTP or Kafka.
[Flow("order.place", Profile = ExecutionProfile.Durable)]
[HttpTrigger("POST", "/api/v1/orders", Idempotent = true)]
[KafkaTrigger("orders.requested", Group = "order-placement")]
public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
{
    protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) => flow
        .Step<ValidateOrder>()
        .Step<ReserveInventory>().CompensateWith<ReleaseInventory>()
        .Step<CapturePayment>().WithPolicy(Policies.PaymentGateway)
        .Emit<OrderPlaced>()
        .Return(ctx => new OrderPlacedResult(ctx.Get<OrderId>()));
}
```

That is the whole application. The HTTP endpoint, the Kafka consumer, the retry
policy, the saga compensation, the OpenTelemetry spans, the OpenAPI document, the
architecture diagram and the machine-readable manifest are **generated at compile
time** from those two files.

## Architecture at a glance

![FlowX runtime architecture in six layers. Layer 1, the front door, adapts every
ingress — API gateway, webhook receiver, message brokers, IoT hub, file watcher,
scheduler, polling — into one shape. Layer 2, the runtime core, holds the trigger,
flow, capability, policy, event, stream, scheduler and plugin engines. Layer 3 is
the programming model itself: flow, capability, context, policy, event and
compensation. Layer 4 adapts infrastructure through plugins; layer 5 is the data
and state layer covering operational data, event store, cache and object storage;
layer 6 is deployment. Security, observability, resilience, governance,
multi-tenancy and versioning cut across every layer.](docs/assets/flowx-runtime-architecture.png)

The load-bearing idea is layer 3. Everything above it is an adapter and everything
below it is a detail — which is why the same flow runs behind HTTP, Kafka or a cron
schedule with no change to its body, and why the whole graph can be emitted as a
machine-readable manifest at build time.

The specification behind this picture, in diffable Mermaid, is
[05-Architecture](docs/05-Architecture.md). Where the two disagree, the
specification wins.

## Why this is not "another MediatR"

FlowX does not sit where MediatR sits. It sits one layer above.

```
ASP.NET Core / Kafka / gRPC / Cron        ← transport
            ▼
          FlowX                            ← programming model + runtime
            ▼
   Business Capabilities                   ← your code
            ▼
       Infrastructure                      ← databases, brokers, clouds
```

| Concern | MediatR | MassTransit | Temporal / Dapr Workflow | **FlowX** |
|---|---|---|---|---|
| Dispatch | runtime reflection | runtime | remote worker | **compile-time, zero-reflection** |
| Durability | none | none | always-on (heavy) | **per-flow profile: ephemeral *or* durable** |
| Triggers | in-proc only | message bus | signals/schedules | **one model for HTTP, bus, cron, stream, AI agent** |
| Policies | manual pipeline behaviors | pipe config | code | **declarative policy graph, compile-composed** |
| Machine-readable architecture | no | no | partial | **`flowx.manifest.json` — first-class artifact** |
| Cost when you don't need it | low | medium | very high | **pay-per-profile** |

The differentiator is not "faster mediator". It is: **one programming model whose
architecture is a compiled, queryable artifact** — see [13-AI-Native](docs/13-AI-Native.md).

## Documentation

Start here, in order:

| # | Document | What it answers |
|---|---|---|
| 01 | [Vision](docs/01-Vision.md) | What problem justifies a new platform |
| 02 | [Manifesto](docs/02-Manifesto.md) | What FlowX believes |
| 03 | [Design Principles](docs/03-Design-Principles.md) | The 12 principles, each with its enforcement mechanism |
| 04 | [Core Concepts](docs/04-Core-Concepts.md) | Trigger, Flow, Capability, Policy, Context, Manifest |
| 05 | [Architecture](docs/05-Architecture.md) | **arc42 + C4 — the main design document** |
| 06 | [Execution Engine](docs/06-Execution-Engine.md) | How a flow actually runs; determinism; replay |
| 07 | [Capability Model](docs/07-Capability-Model.md) | Contracts, versioning, compensation, testing |
| 08 | [Flow Definition](docs/08-Flow-Definition.md) | The DSL, control flow, the compiled graph |
| 09 | [Trigger Model](docs/09-Trigger-Model.md) | Universal ingress: HTTP, bus, cron, stream, agent |
| 10 | [Policy Framework](docs/10-Policy-Framework.md) | Retry, timeout, breaker, authz, idempotency, cache |
| 11 | [Distributed Runtime](docs/11-Distributed-Runtime.md) | Partitioning, journal, leases, exactly-once |
| 12 | [Observability](docs/12-Observability.md) | Traces, metrics, flow replay, live topology |
| 13 | [AI-Native](docs/13-AI-Native.md) | The manifest, the knowledge graph, agent surface |
| 14 | [Performance](docs/14-Performance.md) | Budgets, benchmarks, allocation discipline |
| 15 | [Security](docs/15-Security.md) | Zero-trust, STRIDE per boundary, supply chain |
| 16 | [Multi-Tenancy](docs/16-Multi-Tenant.md) | Isolation levels, noisy neighbours, data residency |
| 17 | [Plugin System](docs/17-Plugin-System.md) | Extension contracts and compatibility rules |
| 18 | [Cloud-Native](docs/18-Cloud-Native.md) | Kubernetes, KEDA, rollout strategies |
| 19 | [SDK](docs/19-SDK.md) | Developer surface, CLI, testing kit |
| 20 | [Roadmap](docs/20-Roadmap.md) | Risk-first delivery plan, from walking skeleton to v1 |
| 21 | [Quality Gates](docs/21-Quality-Gates.md) | SonarQube thresholds, OWASP Top 10 mapping, SAST/DAST, debt policy |
| — | [ADR index](docs/adr/README.md) | Every significant decision, with its trade-off |
| — | [Samples](samples/README.md) | Nine reference applications — **one has code today**; the index says which and what blocks the rest |

**Working documents** — these change as the build progresses:

| Document | What it answers |
|---|---|
| [PLAN.md](PLAN.md) | Work packages WP-0…WP-11, each with a mechanically checkable exit criterion |
| [CHECKLIST.md](CHECKLIST.md) | **Where the project actually is right now** — updated with every change |

## Repository layout (target)

```
src/
├── FlowX.Abstractions/        # contracts only — zero dependencies
├── FlowX.Core/                # flow graph, context, result, policy model
├── FlowX.Compiler/            # Roslyn source generators + analyzers
├── FlowX.Runtime/             # engines: flow, capability, policy, event
├── FlowX.Runtime.Durable/     # journal, replay, leases
├── FlowX.Hosting/             # composition root, options, health
├── FlowX.Cli/                 # flowx new | graph | diff | replay | verify
└── plugins/
    ├── FlowX.Http/  FlowX.Kafka/  FlowX.Cron/  FlowX.Stream/  FlowX.Ai/  ...
tests/
├── FlowX.Architecture.Tests/  # fitness functions — written first
├── FlowX.Compiler.Tests/      # generator snapshot tests
├── FlowX.Runtime.Tests/
└── FlowX.Benchmarks/          # budgets from docs/14-Performance.md, gated in CI
docs/                          # this documentation set
samples/                       # nine reference applications
```

## Status

FlowX is at the **start of P0 — the walking skeleton**. The specification is
complete; the contract surface (`FlowX.Abstractions`) is written but has not yet
been compiled, and no runtime exists.

The specification is the contract: code that contradicts it is a bug in the code,
or an ADR that has not been written yet.

[CHECKLIST.md](CHECKLIST.md) carries the honest current state, including what is
blocked and why. [PLAN.md](PLAN.md) is what to build next, and
[20-Roadmap](docs/20-Roadmap.md) is the phase plan it sits inside.

P0 exists to attempt to **falsify** the platform's central bet: that a source
generator can emit an execution plan reaching ≤ 5 µs p99 with zero allocations.
If it cannot, [ADR-0002](docs/adr/ADR-0002-compile-time-orchestration.md)) is
wrong and the thesis is revisited before anything else is built. That is the
point of doing it first.

## License

Apache License 2.0 — see [LICENSE](LICENSE). Rationale in
[ADR-0012](docs/adr/ADR-0012-apache-2-license.md)).
