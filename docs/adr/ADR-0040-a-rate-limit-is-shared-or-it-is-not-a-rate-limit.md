# ADR-0040: A rate limit is enforced against a shared store, or it is not enforced at all

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Repository owner · Platform architecture
**Amends:** [ADR-0025](ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md) §2.1

> **A rate limiter was started once before and deliberately abandoned.** This record is the
> argument that made the abandonment right, restated as the condition under which stage 1 may
> ship — and the statement that the condition is now met.

---

## 1. Context

[ADR-0025](ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md) §2.1 argued that
skipping stage 1 was safe *for stage 4*: an unenforced limit admits more traffic than declared,
which cannot corrupt anything and cannot make a retry, a breaker or a bulkhead wrong. That
argument is about the **skip**. It says nothing about what may be shipped in its place, and the
obvious implementation is worse than the skip it replaces.

### 1.1 The asymmetry between a breaker and a limiter

`CircuitBreakerState` is per process, and
[10 §6](../10-Policy-Framework.md#6-circuit-breaker-scope) says so and calls it "the
conservative direction — every node discovers an outage for itself". That is true of a breaker
and it is **false of a limiter**, and the difference is the direction each errs in when it is
not shared:

| Policy | Not shared across n nodes | Direction of the error |
|---|---|---|
| `CircuitBreaker` | each node needs its own evidence before it opens | **slower to protect** — n × `minimumThroughput` failed calls before the fleet is closed, and every one of them is a call the dependency was going to receive anyway |
| `Bulkhead` | each node bounds its own concurrency | **per node by construction** — a thread pool is a process's own resource and there is nothing to share |
| `RateLimit` | each node admits the full declared rate | **anti-conservative** — a `RateLimit(20, PT1S)` on a fleet of five admits 100 per second, and the declaration reads as 20 |

A process-local breaker is a breaker that arrives late. A process-local limiter is a limiter
that is **wrong by a factor nobody wrote down and nothing reports** — the factor is the replica
count, which changes on every scale event without any declaration changing.

### 1.2 Why that is the half-executing policy ADR-0025 refuses

ADR-0025's rejected option 2 is *"ship a partial version of each stage — an idempotency
in-flight guard with no replay, an audit record with no payload. Rejected: a half-executing
policy is worse than an unexecuted one, because the declaration then looks satisfied."*

A process-local limiter behind `RateLimit(permits, window)` is exactly that shape, and it is the
worst instance of it in the catalogue, because the gap between the declaration and the behaviour
is not visible in the code, the manifest, the metric or the incident. `FLOWX1032` would have
stopped reporting the declaration — that is what narrowing the rule means — so the one signal an
author had that the limit was not real would be removed at the same moment the limit stopped
being real by a different, quieter amount.

### 1.3 Rejected options

* **Ship a process-local token bucket and document the multiplier.** Rejected: the multiplier is
  the replica count, which no document can state, because a deployment changes it without
  changing any source this repository holds. A note saying "divide your declared permits by your
  replica count" is a note that is wrong on the first autoscale.
* **Keep stage 1 skipped until a limiter is unnecessary.** Rejected: this is ADR-0025's own
  position and it has a cost that record names — *"a retry now amplifies load that stage 1 was
  declared to bound. A step declaring both a `RateLimit` and a `Retry(3)` gets the retry and not
  the limit, so the declared worst case is three times what the author wrote down."* Stage 4
  shipping made the gap wider, not narrower.
* **Put the limit in the HTTP endpoint.** Rejected as the *whole* answer, and kept as advice:
  a limiter at the transport is real and is what
  [FLOWX1032](../diagnostics/FLOWX1032.md)'s remedy 2 recommends, but it bounds an address
  rather than a step, and `PolicySet.RateLimit` attaches to a step. One transport's admission
  control cannot bound a capability three flows and two transports reach.
* **A limiter interface with an in-memory default registered by the host.** Rejected for
  `ICompensationAlertSink`'s inverse reason. A default sink that counts is degraded rather than
  wrong; a default limiter that admits everything is a policy that reads as enforced and is not,
  in every deployment that forgot to replace it.

---

## 2. Decision

**`PolicyStage.Admission` executes, and it executes against `IRateLimiterStore` — a plugin
contract in `FlowX.Abstractions` backed by an implementation that is shared across the fleet.
A step declaring a `RateLimit` with no store registered is refused, never admitted.**

### 2.1 The seam is a store, per ADR-0009

`IRateLimiterStore` declares one operation — `TryAcquireAsync(key, permits, window, ct)` — and
lives in `FlowX.Abstractions`, which is
[ADR-0009](ADR-0009-plugin-contracts.md)'s rule and is enforced by
`DependencyRuleTests`. Two implementations back it: `RedisRateLimiterStore` and
`PostgresRateLimiterStore`. Both are held to `RateLimiterConformance`, which is this
repository's way of holding two implementations to one contract —
`JournalConformance`, `LeaseStoreConformance` and `PublisherConformance` before it.

**The suite's load-bearing assertion is that two store clients share one budget.** A limiter
that passes every other assertion in the file and fails that one is precisely the limiter this
record refuses, and it is the assertion that a single-client suite cannot make. It is written
with two independently constructed stores over the same server, which is the same relationship
two processes have and the only one a test process can create.

### 2.2 A missing store is a refusal

`FlowEngine` takes an optional `IRateLimiterStore`, as it takes an optional
`ICompensationAlertSink`. When a step's resolved `StepPolicy` declares a rate limit and no store
was supplied, the step fails with `policy.ratelimit_unavailable` rather than being dispatched.

**Fail-closed, and this is the whole of what makes the policy honest.** The alternative — admit
when the limiter is absent — reintroduces §1.2's defect through the configuration rather than
through the algorithm: the declaration reads as enforced, and whether it is depends on a
registration nobody can see from the flow. A refusal is loud, is a `Result` failure like every
other policy refusal ([ADR-0029](ADR-0029-a-refusal-is-a-result-failure.md)), and is fixed by
one registration.

The same argument makes a store *error* a refusal. A limiter that cannot reach Redis does not
know whether the caller is inside the budget, and "admit on doubt" is the behaviour that turns a
dependency outage into an unbounded flood of the dependency that is already down.

### 2.3 What the key is, and what it is not

The key is the capability id, narrowed by the declared `RateLimitScope`:

| Scope | Key |
|---|---|
| `Global` | capability id |
| `Tenant` (the default) | capability id + `ctx.TenantId` |
| `Principal` | capability id + the principal's name |

Capability id rather than step index, so two flows calling one dependency share the bound —
which is `BulkheadGate`'s decision and is made here for the same reason: the thing being
protected is the dependency, not the graph node.

A `Tenant`-scoped limit on an invocation with no tenant, and a `Principal`-scoped limit on an
anonymous one, key on the absence explicitly rather than collapsing into `Global`. Collapsing
would put every un-tenanted caller into one bucket sized for one tenant, which is a global limit
wearing a per-tenant declaration.

---

## 3. Consequences

**Positive:**

* **`RateLimit(20, PT1S)` means twenty per second across the deployment**, which is what it
  says. `RateLimiterConformance.TwoStoreClientsShareOneBudget` is the proof, and it is the
  assertion this record exists for.
* **The skip argued in ADR-0025 §2.1 stops being a skip**, and FLOWX1032 narrows again — from
  four kinds to the two the other stages still leave inert.
* **The refusal is observable.** `flowx_ratelimit_rejected_total` is emitted with the `scope`
  and `tenant` labels [10 §9](../10-Policy-Framework.md#9-observing-policies--four-of-seven-metrics-emit)
  froze, which [ADR-0026](ADR-0026-policy-metrics-name-only-what-executes.md) left unnamed
  precisely because "a rate-limit rejection counter describes a decision no code makes". Code
  now makes it.
* **A limiter cannot be half-shipped by accident.** The engine has no in-memory fallback to
  regress into, and the conformance suite has no single-client mode to pass in.

**Negative / accepted trade-offs:**

* **A declared rate limit now costs a network round trip per step.** It is on the policed path
  only — `ExecutionPlan.HasStepPolicies` gates it, exactly as it gates stage 4 — so a flow that
  declares no limit pays nothing and budget B2 is untouched. A flow that declares one pays a
  store call before every dispatch of that step, and there is no local cache in front of it,
  because a cache is the process-local limiter this record refuses, arriving as an
  optimisation.
* **A store outage refuses traffic the dependency could have served.** §2.2 argues that is the
  right direction and it is still a cost: an unreachable Redis takes down every step that
  declares a limit, and the failure is a refusal rather than a degradation. The mitigation is
  the one every store has — the limiter is the same Redis the leases already depend on.
* **The bucket is a token bucket and the catalogue says so, but the refill is uniform.** A
  `RateLimit(20, PT1S)` refills at one token per fifty milliseconds rather than twenty tokens
  each second, so a caller that waits a full window and then bursts twenty is admitted and a
  caller that bursts twenty at the very start of its second window is partly refused. That is
  the standard shape and it is what makes `RetryAfter` computable, but it is a behaviour a
  fixed-window implementation would not share.
* **Two implementations is two arithmetics.** Redis does the arithmetic in Lua against `TIME`;
  PostgreSQL does it in one statement against `now()`. The conformance suite is what makes them
  one contract, and the suite is now the only thing that does.

**Revisit when:** a deployment needs a limit whose scope is not one of the three
`RateLimitScope` offers — a partition, a downstream, or [10 §6](../10-Policy-Framework.md#6-circuit-breaker-scope)'s
composite key, at which point the key becomes a declaration rather than a switch; or a step's
store round trip is measured as the dominant cost of a policed step, which would make the case
for a local pre-check that can only ever refuse and never admit; or `Quota` becomes declarable,
because a long-window budget and a short-window rate are two policies over one store and this
record decided the shape of only one of them.

---

**See also:** [ADR-0009](ADR-0009-plugin-contracts.md) ·
[ADR-0011](ADR-0011-fixed-policy-stage-order.md) ·
[ADR-0025](ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md) ·
[ADR-0026](ADR-0026-policy-metrics-name-only-what-executes.md) ·
[FLOWX1032](../diagnostics/FLOWX1032.md) ·
[10 — Policy Framework](../10-Policy-Framework.md)
