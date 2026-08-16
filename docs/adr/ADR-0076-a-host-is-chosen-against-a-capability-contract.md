# ADR-0076: A host is chosen against a capability contract, so the sweeping half decides the platform

**Status:** Accepted
**Date:** 2026-08-14
**Deciders:** platform architecture, SRE

## Context

A deployment question arrived in the usual shape: *"host it on Azure, serverless first —
Functions for compute, Static Web Apps for the client, Service Bus, Event Hubs, PostgreSQL,
Blob Storage."* Every item on that list is reasonable. One of them is not available to this
runtime, and the reason is a property of FlowX rather than a shortcoming of the service.

**FlowX is two workloads with opposite hosting needs, and the split is in the source.**

The *reactive* half — HTTP triggers, bus deliveries, the agent surface — is request-shaped.
Something calls, it answers, it can be idle in between. That half is what people picture when
they say serverless.

The *sweeping* half is not called by anything. It looks. `src/FlowX.Hosting` registers six
hosted services, and their defaults in `FlowXOptions` are the constraint:

| Service | Interval |
|---|---|
| `FlowBusService`, `FlowChangeService`, `FlowStreamService` | **1 s** |
| `FlowRecoveryService`, `FlowTimerService`, `FlowScheduleService` | **10 s** |
| Lease renewal | **10 s**, against `LeaseTtl` of **30 s** |

A durable flow is durable because something is sweeping. Recovery is how a dead node's work
is picked up; the timer scan is how a suspended flow wakes; the change feed is how an event
staged in the outbox ever leaves it. Take the sweeps away and the runtime does not degrade
gracefully — it stops being durable while continuing to accept work, which is the worst
available failure.

**Options considered.**

- *Pick one platform and standardise on it.* Rejected: the repository ships an
  orchestrator-neutral operations specification ([18](../18-Cloud-Native.md)) precisely
  because deployments differ. Naming a platform in an ADR would make that document a lie the
  first time somebody ran it on App Service.
- *Host everything on Azure Functions, as asked.* Rejected on two structural grounds below.
- *Re-architect the sweeps into platform timers* — replace the hosted services with
  minute-granularity triggers supplied by the host. Rejected: it moves a runtime guarantee
  into deployment configuration, and it changes every latency the runtime promises by an
  order of magnitude. Worse, it would be invisible: the flows still run, just late.
- *Write a capability contract and score platforms against it.* Chosen.

**Why Functions specifically cannot carry the sweeping half.** Two of the failures are
structural, not a matter of tier or price:

1. **Timer resolution.** A timer-triggered Function is practical at one minute. Three of the
   six sweeps default to one *second*. Premium's always-ready instances do not change this.
2. **The generated HTTP surface.** `FlowX.Http` generates
   `MapFlow<TRequest,TResponse>(this IEndpointRouteBuilder …)`. Functions has no
   `IEndpointRouteBuilder`, so every generated route becomes a hand-written `[Function]`
   carrying an `HttpTrigger`. For `samples/crm` that is **79 routes** of glue replacing code
   the compiler writes today — and glue that can drift from the manifest, which is the one
   artifact the whole platform exists to keep true.

A third cost is real but not structural: only `samples/ecommerce` publishes NativeAOT, because
Npgsql blocks it. Anything touching the journal has a JIT-sized cold start, which is exactly
what a consumption plan makes you pay for repeatedly.

## Decision

**We will publish a host capability contract — H1 to H8 in
[28 §1](../28-Azure-Hosting.md#1-the-host-capability-contract) — and choose a host by scoring
it, rather than by naming a platform.** The contract is derived from the source and each
requirement cites what produces it.

Two consequences follow immediately and are recorded here rather than left to be rediscovered:

- **Azure Functions hosts the edges, never the runtime core.** Webhook ingress,
  blob-triggered imports, business jobs at minute granularity, notification fan-out — work
  that holds no lease and needs no generated route.
- **App Service with Always On, Container Apps, and AKS all satisfy the full contract.** They
  are ranked by fit, not by capability: Container Apps is the recommended default because it
  is the only one of the three that satisfies **H8** — scaling on a signal that is not HTTP,
  via a KEDA scaler over `flow_instance` — while still billing per second and scaling to zero
  in non-production. App Service is the smallest step from `dotnet run`. AKS is right when
  you already run AKS.

The recommended shape is therefore **hybrid, and that is not a compromise**: each half of the
runtime on the platform whose shape it matches.

## Consequences

**Positive.**

- The hosting question has an answer that survives a new platform. A service that did not
  exist when this was written is scored against the same eight rows.
- The contract is falsifiable. Each row names the code that produces it, so a change to a
  scan interval or to how endpoints are generated makes a row visibly wrong rather than
  quietly stale.
- "Serverless" stops being a yes/no argument. Container Apps is serverless *and* satisfies
  the contract, which is the finding that dissolves the original tension.
- The failure mode it prevents is the expensive one: a deployment where flows still run, so
  everything looks healthy, but timers fire a minute late and abandoned instances wait a
  minute to be recovered.

**Negative / accepted trade-offs.**

- **Two roles cannot scale to zero.** The worker and scheduler roles keep `minReplicas ≥ 1`,
  so there is a fixed monthly floor. This is correctness bought with money: a scheduler that
  sleeps misses firings, and a worker that sleeps leaves events in the outbox.
- **The contract is a maintenance obligation.** Eight rows that cite source are eight rows
  that can rot. Nothing gates them today.
- **We are declining a request as specified.** The answer to "host it on Functions" is "the
  edges, yes; the core, no", and that costs a conversation.
- **H2 is asserted from defaults, not from a measurement.** The intervals are configurable. A
  deployment that genuinely tolerates minute-granularity timers, has no durable flows and no
  change feed is a case the contract calls unsupported when it is merely unusual.

**Revisit when:** Azure Functions offers a hosting mode that keeps a process resident between
invocations at consumption pricing **and** supports `IEndpointRouteBuilder` endpoint routing —
both, since either alone leaves one of the two structural failures standing; or the sweeping
services gain a push-based mechanism that removes the sub-minute poll (PostgreSQL
`LISTEN/NOTIFY` for the change feed is the obvious candidate, since the feed is a cursor poll
over `pg_current_xact_id()` today), at which point H2 drops to the recovery and timer scans
alone and the whole matrix is worth re-running.
