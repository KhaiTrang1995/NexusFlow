# ADR-0077: A flow is dispatched in one of two modes, and the mode is a deployment choice

**Status:** Accepted
**Date:** 2026-08-14
**Deciders:** platform architecture, SRE

## Context

[ADR-0076](ADR-0076-a-host-is-chosen-against-a-capability-contract.md) published a host
capability contract and concluded, in one sentence, that **"Azure Functions hosts the edges,
never the runtime core."** That sentence was too strong, and this record says why and replaces
it.

0076 read the six hosted services in `src/FlowX.Hosting` — bus, change and stream sweeping at
**1 s**, recovery, timer and schedule at **10 s**, a lease renewed every **10 s** against a
**30 s** TTL — and treated them as a requirement of the runtime. They are not. They are the
*current implementation* of three requirements that a cloud can satisfy a different way:

| Requirement | In-process implementation today | What a broker offers instead |
|---|---|---|
| Notice work has arrived | `FlowBusService` polls every 1 s | The broker **pushes**. No poll at all |
| Wake a suspended flow at instant *T* | `FlowTimerService` scans every 10 s | A message scheduled for *T*. Exact, and no scan |
| Notice a node died | `FlowRecoveryService` scans every 10 s | The message lock lapses and the broker redelivers |

Two of 0076's three objections dissolve under that reading, and the third — that `MapFlow`
extends `IEndpointRouteBuilder`, which Azure Functions does not have — is a **missing
generator**, not a wall. The compiler already knows every route from `[HttpTrigger]`; nothing
stops a `FlowX.Functions` emitter producing `[Function]` classes from the same attributes.

**What does not dissolve is a cost, and it is not a hosting cost.** Externalising the sweeps
means a flow can no longer be one in-process loop holding a lease. Each step becomes its own
invocation: resume, fence, run one step, commit, schedule the next. The engine already has the
seam — `DurableExecution.ResumeAsync` fences then reads the frontier, and the step loop skips
what is committed (`FlowEngine`) — so this is reachable. But it changes the price per step:

| Profile | Step cost today | Step cost dispatched | Ratio |
|---|---|---|---|
| **Durable** | ~7.6 ms — dominated by the journal write | + ~20–50 ms broker round trip and invocation | ~4–5× |
| **Ephemeral** | **1.5 µs** p50, gated in CI | + ~20–50 ms | ~10⁴× |

That asymmetry is the whole decision. A durable flow already pays milliseconds per step, so a
broker hop is a multiple of something already slow, and a lead conversion does not care. An
ephemeral flow exists *because* it is an in-process microsecond loop; putting it on a broker
does not make it slower, it makes it pointless.

**Options considered.**

- *Keep 0076 as written — in-process only.* Rejected: it declines a valid architecture on a
  premise that is false, and it forecloses the deployment shape that suits a durable-only
  application best.
- *Replace the in-process model with the dispatched one.* Rejected: it would destroy the
  ephemeral profile, which is the platform's central performance claim
  ([ADR-0002](ADR-0002-compile-time-orchestration.md)) and is gated on every pull request.
- *Decide per host.* Rejected: the same flow must be able to run either way with no source
  change, or the manifest stops describing the application.
- *Two dispatch modes, chosen at deployment.* Chosen.

## Decision

**We will support two dispatch modes over one compiled plan, and the mode will be a property
of the deployment rather than of the flow.**

**`Hosted`** — what ships today. The engine runs the flow as one in-process loop, holds a
lease, and the six services sweep. Required for `ExecutionProfile.Ephemeral`; the default for
everything.

**`Dispatched`** — to be built. One invocation per step. No lease: the fencing token already
carried on every `StepCommit` is the mutual exclusion, and the broker's message lock is the
concurrency control. Suspension enqueues a message scheduled for the wake instant instead of
waiting for a scan. Available only to `ExecutionProfile.Durable`.

The same source compiles for both. `flowx.manifest.json` is unchanged — the mode is not a
property of the graph, so it is not published, for the reason
[ADR-0034](ADR-0034-the-manifest-publishes-a-schedules-address.md) gives about deployment
configuration.

**This replaces 0076's sentence "Azure Functions hosts the edges, never the runtime core."**
The corrected form: *Azure Functions hosts the edges in `Hosted` mode, and can host the whole
runtime in `Dispatched` mode once the three pieces below exist.* Everything else in 0076 — the
capability contract, scoring a platform rather than naming one, and the ranking of App
Service, Container Apps and AKS — stands unchanged.

Three pieces are required before `Dispatched` is real, and none exists today:

1. **`FlowX.Functions`** — a generator emitting `[Function]` bindings from the trigger
   attributes the compiler already reads.
2. **Dispatched execution in the engine** — resume, run one step, persist, schedule the next.
3. **A journal that suits it.** `IFlowJournal` is a plugin contract with a conformance suite,
   so a Cosmos DB implementation is well-defined work. It is worth doing for two reasons
   beyond preference: Cosmos is reached over HTTP, which removes the connection-pool ceiling
   that bounds replica count on PostgreSQL, and its **change feed is a first-class trigger**,
   which retires the outbox poller entirely when the step row and the event document are
   written in one transactional batch.

## Consequences

**Positive.**

- The platform stops having an opinion about serverless and starts having two shapes. An
  application that is entirely durable can run with nothing resident at all.
- The ephemeral profile is protected by making the constraint explicit rather than by
  refusing an architecture.
- `Dispatched` removes the fixed monthly floor 0076 accepted as a trade-off: with no sweeps
  and no leases, every role can scale to zero.
- The Cosmos path answers the connection ceiling in
  [28 §4.1](../28-Azure-Hosting.md#41-the-connection-ceiling--the-one-that-bites), which is
  otherwise the hard limit on replica count.
- Observability is unaffected either way. Instrumentation is `ActivitySource` and `Meter` from
  the base class library with **no OpenTelemetry package dependency**, so a span crossing an
  invocation boundary is a context-propagation question, not a re-instrumentation one.

**Negative / accepted trade-offs.**

- **Two execution paths to keep correct.** The conformance suite currently proves one. Every
  durability guarantee — exactly-once effects, compensation order, poll bounds — has to be
  proven twice or the second mode is a claim.
- **`Dispatched` is a design, not code.** Nothing here is built. This record exists so the
  option is not foreclosed, and it must not be read as a shipped feature.
- **Cost inverts.** `Hosted` bills instance-hours and amortises steps; `Dispatched` bills per
  invocation and per message. At B7's target rate of 5 000 commits/s that is 5 000 messages a
  second, and the cheaper model becomes the more expensive one somewhere below that.
- **Distributed tracing gets harder before it gets better.** One flow becomes *n* invocations,
  so the trace is only whole if the context is carried on the message. That is a requirement
  on the dispatched implementation, not a detail.
- **A third failure mode appears.** In `Hosted`, a lost lease is the recovery trigger. In
  `Dispatched`, a message that is delivered but whose step never commits relies on lock
  expiry — which is the same idea with a different owner, and a different set of ways to get
  it wrong.

**Revisit when:** `Dispatched` ships and its conformance run either passes or fails — a
passing run makes the second and third bullets above obsolete and this record should be
re-read against measurements rather than estimates; or the per-step overhead estimate of
20–50 ms is measured and proves materially wrong in either direction, since the whole
profile asymmetry rests on it; or `ExecutionProfile.Ephemeral` is retired, at which point one
mode is enough and the simpler runtime wins.
