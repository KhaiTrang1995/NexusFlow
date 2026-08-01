# ADR-0037: An idempotency record is keyed by the invocation's own key, narrowed by capability and scope, and holds only a success

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Repository owner · Platform architecture

> **`PLAN` §6a names "the `IIdempotencyStore` contract shape" as one of two decisions P4 must
> invent.** It was left unwritten twice, both times because nothing implemented the policy.
> This is that decision.

---

## 1. Context

`ctx.IdempotencyKey` already exists, is on `FlowInvocation`, is stable across a flow and across
every attempt of a retried step, and is pinned by
`PolicyExecutionTests.EveryAttemptPresentsTheSameIdempotencyKey`. It is the mechanism
[ADR-0025](ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md) §2.2 leans on to
argue that running stage 4 without stage 3 does not re-open ADR-0011's duplicate-charge row.

So there is a key. What there is not is a **record**, and three things about a record had to be
decided.

### 1.1 One key, several policed steps

`ctx.IdempotencyKey` is per invocation. `PolicySet.Idempotency` attaches to a **step**
([10 §4](../10-Policy-Framework.md#4-declaring-policies): there is no flow-level policy surface
and `.WithPolicy(...)` is the whole chain). A flow with two steps each declaring an idempotency
window therefore has one key and two records to keep apart — and if it did not keep them apart,
the second step would find the first step's record and replay it.

### 1.2 What a record holds

`StepOutcome` carries a success or an `Error` and nothing typed. What a later step actually
binds to is the state bag, which the dispatcher fills. So "the recorded result"
[10 §7](../10-Policy-Framework.md#7-idempotency-policy--specification) speaks of is not the
`StepOutcome`: it is the typed values the step contributed, and the only seam that can produce
or consume them is `IStepDispatcher.DescribeStep` / `RestoreState`, for the reason those members
exist at all — `JournalPayload.Of` needs a generated `JsonTypeInfo<T>` and only generated code
can name one.

### 1.3 What a record holds when the step *failed*

[10 §8](../10-Policy-Framework.md#8-cache-safety--specification)'s table decides this for the
cache — *"negative caching: off — stale failures are worse than a retry"* — and the same
question arrives one stage earlier with a sharper edge, because an idempotency record that held
a failure would replay that failure to every repeat of the key for the whole window.

### 1.4 Rejected options

* **Key on `ctx.IdempotencyKey` alone.** Rejected: §1.1. Two policed steps in one flow would
  share a record, and the second would replay the first's state bag.
* **Key on the step index.** Rejected: an index is a position in a graph, so inserting a step
  before a policed one silently invalidates every live record — a deployment would replay the
  wrong step's result for the length of the window. The capability id is what the step is
  *about*, is stable across a graph edit, and is what `CircuitBreakerState` and `BulkheadGate`
  are already keyed by.
* **Mint the key inside the policy from the input's hash.** Rejected: it is a second key
  alongside a stable one that already reaches the capability, and the two would disagree the
  moment a caller retried with a semantically identical but not byte-identical body — which is
  the case an idempotency key exists to handle and a hash cannot.
* **Record the step's own result payload rather than the state bag.** Rejected: it would need a
  new `IStepDispatcher` member to read one payload back into a context, and `RestoreState`
  already reads a composed state-bag document. A second restore seam is a second thing the
  generator emits and a second thing a hand-written dispatcher has to implement.
* **Record failures too, with a shorter window.** Rejected: §1.3, and because "a shorter window"
  is a second duration nobody declared. `PolicySet.Idempotency` takes one `window`.

---

## 2. Decision

**`IIdempotencyStore` is a three-call contract in `FlowX.Abstractions` over a key derived from
`ctx.IdempotencyKey`, the capability id and the declared `IdempotencyScope`. It records the
flow's state bag as of the end of the policed step, and it records only a success.**

### 2.1 The key

```text
idempotency-key · capability-id · scope-discriminant
```

`ctx.IdempotencyKey` rather than anything minted here, which is §1.4's first two rejections
and the instruction the plan gives. The capability id narrows it to the step, per §1.1. The
scope discriminant is the tenant for `IdempotencyScope.Tenant` — the default, because
[10 §3](../10-Policy-Framework.md#3-the-policy-catalogue) says *"two tenants may legitimately
use the same key"* — and nothing for `IdempotencyScope.Global`.

The components are length-prefixed rather than joined by a separator, so no tenant id
containing the separator can be made to collide with another tenant's key. That is the same
class of defect as a SQL injection and it is closed by construction rather than by validating
the tenant.

### 2.2 The three calls

| Call | Answers |
|---|---|
| `BeginAsync(key, window, inFlightFor, ct)` | `Started` — nobody held it and this caller now does; `InFlight` with a `RetryAfter` — somebody else is running it; `Completed` with the recorded document — it ran and here is what it produced |
| `CompleteAsync(key, record, window, ct)` | the record is stored for `window`, replacing this caller's in-flight marker |
| `AbandonAsync(key, ct)` | the in-flight marker is released and the key is free |

**`BeginAsync` is a compare-and-set, not a read followed by a write**, in both implementations,
for `ILeaseStore`'s reason: two callers presenting one key at the same instant is the case the
policy exists for, and [10 §7](../10-Policy-Framework.md#7-idempotency-policy--specification)
names it — *"the in-flight state matters: without it, two concurrent requests with the same key
both execute. This is the most common bug in hand-rolled idempotency."*

**`inFlightFor` is separate from `window` and is not optional.** The in-flight marker is a
lease: a node that takes the key and then dies must not wedge every repeat of that key for the
declared window, which for a `PT24H` idempotency is a day. It is bounded by the flow's own
remaining deadline, so the marker cannot outlive the execution it stands for.

### 2.3 Only a success is recorded

A policed step that fails calls `AbandonAsync`, and the key is free for the next caller.

**This is [10 §8](../10-Policy-Framework.md#8-cache-safety--specification)'s "negative caching:
off" applied one stage earlier, and it matters more here.** A recorded failure would be replayed
for the whole window, so a transient `Unavailable` at the moment a caller first presented a key
would make that key permanently unusable for a day — and the caller's remedy, retrying with the
same key, is exactly the thing that would keep failing. A stale failure is worse than a retry,
and here the retry is what the caller was going to do anyway.

It also keeps the record's meaning exact. `Completed` means *the side effect happened*, which is
the fact a replay is entitled to act on. A record that could mean "it happened" or "it did not"
would need a discriminant, and every consumer would have to branch on it correctly.

### 2.4 A missing store is a refusal

As for the rate limiter ([ADR-0035](ADR-0035-a-rate-limit-is-shared-or-it-is-not-a-rate-limit.md)
§2.2): a step declaring an idempotency window with no `IIdempotencyStore` registered fails with
`policy.idempotency_unavailable` rather than being dispatched, and a store error is a refusal
rather than a dispatch. Dispatching on doubt is the duplicate charge the policy was declared to
prevent.

---

## 3. Consequences

**Positive:**

* **A repeated key does not re-execute, and a concurrent one is told so.** Both are asserted
  against a real engine and against both stores by `IdempotencyStoreConformance`, including the
  concurrent case, which is the half hand-rolled implementations get wrong.
* **The key is the one that already existed.** No second identity, nothing new for a transport
  to supply, and `[HttpTrigger(Idempotent = true)]` already puts the caller's header on it — so
  an endpoint that was already demanding a key now gets a policy that uses it.
* **A graph edit does not invalidate live records**, because the key names the capability rather
  than the position.
* **`flowx_idempotency_replays_total` has a producer**, with the `capability` and `scope` labels
  [10 §9](../10-Policy-Framework.md#9-observing-policies--four-of-seven-metrics-emit) froze and
  [ADR-0026](ADR-0026-policy-metrics-name-only-what-executes.md) declined to name.

**Negative / accepted trade-offs:**

* **Two steps calling one capability in one flow share a record.** The key names the capability,
  so a flow that invokes `ledger.post` twice under one idempotency key has the second invocation
  replay the first's record. That is the same bound `BulkheadGate` and `CircuitBreakerState`
  already impose, and it is wrong for a flow whose two calls to one capability mean different
  things — which is a flow that should be declaring the window on only one of them.
* **A crashed node holds a key for `inFlightFor`.** Repeats inside that window are refused
  rather than executed, so a node dying mid-step converts into refusals for a bounded time
  rather than into a duplicate. The bound is the flow's remaining deadline, which is the
  shortest honest value and is still not zero.
* **The record is the whole state bag, not the step's own output.** It is therefore larger than
  it needs to be, and it carries values earlier steps produced. §1.4 gives the reason — one
  restore seam rather than two — and the cost is a bigger row and a store that holds more than
  the step contributed.
* **Recording only successes means a failed step is dispatched again by the next caller.** That
  is the intent, and it means the policy offers no protection against a caller hammering a key
  that fails every time. `RateLimit` is the policy for that, one stage up, and the fixed order
  puts it there.

**Revisit when:** a flow legitimately needs two records for one capability under one key, at
which point the key needs a component the step carries and the record stops being derivable from
the invocation; or `IdempotencyScope` gains a member, because the discriminant is a switch over
two values and a third would make it a table; or an in-flight repeat needs to *wait* for the
holder rather than be refused, which is a different contract — a rendezvous rather than a
compare-and-set — and would change every method here.

---

**See also:** [ADR-0009](ADR-0009-plugin-contracts.md) ·
[ADR-0025](ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md) ·
[ADR-0035](ADR-0035-a-rate-limit-is-shared-or-it-is-not-a-rate-limit.md) ·
[ADR-0036](ADR-0036-stage-one-and-stage-three-run-outside-the-retry.md) ·
[ADR-0038](ADR-0038-a-recorded-result-is-replayed-only-when-recording-lost-nothing.md) ·
[10 — Policy Framework](../10-Policy-Framework.md)
