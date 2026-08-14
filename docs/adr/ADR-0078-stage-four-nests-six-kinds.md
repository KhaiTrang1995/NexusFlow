# ADR-0078: Stage four nests six kinds — `Fallback` outside the retry, `Hedge` inside it

**Status:** Accepted
**Date:** 2026-08-14
**Deciders:** Repository owner · Platform architecture
**Amends:** [ADR-0024](ADR-0024-stage-four-is-a-fixed-nesting.md) ·
[ADR-0025](ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md) ·
[10 §3](../10-Policy-Framework.md#3-the-policy-catalogue)

---

## 1. Context

[ADR-0024](ADR-0024-stage-four-is-a-fixed-nesting.md) fixed the nesting of stage 4 as

```
Retry { CircuitBreaker { Bulkhead { Timeout { capability } } } }
```

and named its own revisit condition: *"a fifth stage-4 kind is implemented (`Hedge` and
`Fallback` are catalogued and undeclarable today), because a new kind has to be placed in this
order and the placement may not be obvious"*. Both arrive at once, so the condition has fired
twice over. This record amends 0024 the way
[ADR-0016](ADR-0016-postgres-journal-adapter.md) amended
[ADR-0015](ADR-0015-journal-schema-and-durable-execution.md): the earlier record is untouched
and stays true of the four kinds it placed, and this one places the two it did not have.

The defect is the group's, not either kind's. Stage 4 could bound a call, refuse a failing
dependency, isolate a slow one and ask again — and had no answer at all to the two questions an
operator asks about a dependency that is *up and slow* or *down and optional*. There was no
tail-cutting and no degraded mode: a p99 that is ten times the p50 had to be paid in full, and a
rating service being down had to fail an order.

### 1.1 What ADR-0024's argument actually turned on

Each of its four boundaries was decided by asking what the kind's *unit* is. `Retry` is
outermost because its unit is the whole attempt; `Timeout` is innermost because its unit is one
invocation and it is the only one that needs a token to reach the capability. That test answers
both new placements without a new principle, which is the strongest evidence the original
argument was the right one.

### 1.2 The arithmetic, again

[`FLOWX1019`](../diagnostics/FLOWX1019.md) multiplies `Timeout` by `Retry(attempts)` and checks
the product against `[FlowDeadline]`. ADR-0024 §1.1 showed that the check is only meaningful
under one of the two available nestings. A `Hedge` changes the product, and §2.5 below says how
and what was done about it.

---

## 2. Decision

**Within `PolicyStage.Resilience` the six kinds nest in a fixed order by kind, outermost
first:**

```
Fallback { Retry { Hedge { CircuitBreaker { Bulkhead { Timeout { capability } } } } } }
```

Declaration order within the stage is still ignored by the engine, and `PolicyChain`'s stable
sort still decides only what the manifest publishes. `StepPolicy.From` reads the two new kinds
into named fields beside the other four.

### 2.1 `Fallback` outermost, outside the retry

Its unit is **the step**, and it is the only one of the six whose unit is larger than an
attempt. Everything else in the stage describes a call or a sequence of calls that are still
trying to get the capability's own answer; a fallback is the decision to stop trying and answer
with something else.

Put it anywhere inside the retry and the retry is dead: the first failure would be answered with
the degraded value, and the two, three or five attempts the author declared beside it would
never be made. That is not a subtle interaction — it is a policy silently disabling the policy
next to it, which is the class of thing ADR-0011 exists to make unexpressible.

It also could not sit *inside* anything else and stay implementable. Every other kind wraps the
dispatch and lives in `FlowEngine.DispatchGuardedAsync`; the fallback lives in the step loop,
after the retry loop has stopped asking, because there is no other place that knows the step has
finished failing.

**It is not, however, outside stages 1 and 3.** A rate limit's refusal and an idempotency
window's refusal are not the step's failure — they are the platform declining to run it — and a
degraded value substituted for a rate-limited call would turn a bounded dependency into an
unbounded stream of constants. The fallback is consulted only for a failure that came out of
stage 4 or inward.

### 2.2 `Hedge` inside the retry and outside the breaker

Its unit is **one attempt**, made of several calls. Two boundaries, each decided the same way as
ADR-0024's:

**Inside `Retry`.** A retry's attempt is a hedged race, not the other way round. Hedging a
retry loop would mean two independent retry sequences running concurrently over one step — the
attempt counts multiply, the backoffs decorrelate against each other rather than against other
callers, and the "deadline allows another attempt" question would be asked twice with two
different answers. Inside, the composition is exactly what an author would draw: *ask; if it is
slow, ask again beside it; if the race fails, back off and race again.*

**Outside `CircuitBreaker`, `Bulkhead` and `Timeout`.** A hedge genuinely puts two calls on the
dependency, and each of the three inner kinds exists to count, bound or cut off *a call*. So
each hedged call takes its own permit, arms its own timeout and is counted by the breaker on its
own. The other way round — one permit and one breaker observation for a race — would make a
bulkhead of 64 admit up to 128 concurrent calls to the dependency it was declared to protect,
which is the isolation policy lying about the number it was given. It also makes an open breaker
refuse the hedged call instantly and for free, which is what an open breaker is for.

### 2.3 The two compose, and are not forbidden together

The brief this work came from asked whether `Hedge` and `Retry` should be refused as a pair.
They should not: they answer different signals — a hedge answers silence, a retry answers a
failure — and the nesting above composes them without either changing the other's meaning. What
*is* refused is the composition that cannot be given a meaning, and there is exactly one:
`Fallback` on a capability with side effects ([`FLOWX1053`](../diagnostics/FLOWX1053.md)),
because a degraded success over an effect that may have half happened is not a degraded mode but
a lost write. `Hedge` on a non-idempotent capability is refused for the parallel reason
([`FLOWX1051`](../diagnostics/FLOWX1051.md)), which is FLOWX1014's rule reaching the same
capability from the concurrent side.

### 2.4 What a hedge does to the state bag, and why `Idempotent = true` is the whole answer

Two calls race over **one** `FlowContext`. The generated dispatcher writes the step's result
into the state bag inside `ExecuteAsync`, so both calls can write, and the loser is cancelled
only after it may already have written. Two consequences, and they are answered separately.

**Memory safety** is answered by the plan: `ExecutionPlan.HasParallel` — the flag the context
reads to decide whether the bag needs a lock — now counts a step whose resolved policy declares
a hedge, exactly as it counts a fork or a concurrent `ForEach`. The question that flag asks is
"can two threads reach this context", and for a hedged step the answer is yes; that it arrives
from a policy rather than from the graph does not change it. A flow with no hedge and no fork
keeps the unguarded fast path, so budget B2 is untouched.

**Meaning** is answered by `Idempotent = true`, and this is the part worth stating plainly:
*the value the flow keeps may be the losing call's.* Both calls present the same
`ctx.IdempotencyKey`, so a capability that has declared itself idempotent has declared that they
are one request — which is exactly the claim that makes two answers interchangeable. A
capability for which that is false must not be hedged, which is what FLOWX1051 enforces, and it
is a stronger reading of `Idempotent` than a retry needs: a retry keeps the answer of the
attempt that succeeded and never has to choose between two.

### 2.5 `FLOWX1019`'s product, and why it was left alone

Under this nesting the worst case for one attempt at a hedged step is
`(maxAttempts − 1) × afterDelay + timeout`, and the flow's worst case is that times `attempts`.
FLOWX1019's `timeout × attempts` therefore **understates** a hedged step's worst case by
`attempts × (maxAttempts − 1) × afterDelay`.

The rule is not widened, for two reasons. The understatement is bounded by a number the author
wrote down next to the timeout, and it is bounded *below the timeout* in every declaration that
makes sense: an `afterDelay` at or above the `Timeout` is a hedge that never fires, because the
first call is already dead when the hedge would be issued. And every timeout is clamped to what
is left of the flow's deadline before it is armed, so the deadline remains enforced whatever the
arithmetic of the estimate. What the rule loses is precision in a warning, not a guarantee. It
is recorded here rather than papered over, and it is this record's first revisit condition.

### 2.6 Rejected options

* **`Fallback` inside `Retry`, or per attempt.** Rejected: it disables the retry, above.
* **`Hedge` outside `Retry`.** Rejected: multiplies attempt counts and gives one step two
  independent retry sequences.
* **`Hedge` inside `Bulkhead`/`CircuitBreaker`** (one permit, one observation per race).
  Rejected: the bulkhead would admit `maxConcurrency × maxAttempts` calls to the dependency, and
  the breaker's denominator would stop being calls.
* **Refusing `Hedge` on `Durable` flows.** Rejected, and it was a live option. A hedged race
  commits **one** journal row per attempt — the winner's outcome and the state bag as it then
  stands — because the commit is in the retry loop, outside the race. The loser has no row
  because it has no separate identity: it is the same request under the same key, which is what
  §2.4 requires anyway. There is nothing here that
  [ADR-0042](ADR-0042-a-recorded-result-is-replayed-only-when-recording-lost-nothing.md) does not
  already answer for a retried step, so refusing the profile would have been a refusal without a
  reason.
* **Refusing `Fallback` on `Durable` flows.** Rejected, but it needed a commit of its own: see
  §2.7.
* **Making the fallback a `Func<FlowContext, T>` rather than a constant.** Rejected: a delegate
  is code the compiler cannot check the shape of, cannot publish in the manifest, and cannot
  keep deterministic under replay — the same argument [ADR-0010](ADR-0010-csharp-dsl-over-yaml.md)
  makes for the DSL being read at compile time.

### 2.7 A degraded step is a journal row and is not compensable

Two decisions the durable profile forces, both small and both load-bearing.

**The degraded value is committed.** The failed attempts already have their rows; if the flow
then continued on a fallback with nothing written, the step's frontier would be a failure while
the flow ran past it, and an instance resumed at that point would restore a state bag with no
degraded value in it and bind the next step to something no step produced. So the fallback
commits one more row for the step, a success, on the attempt after the last that failed. The
history keeps both facts: what the dependency said, and what the flow answered.

**Nothing is pushed onto the compensation stack.** FLOWX1053 means the capability had no effect
to produce, so a registered undo would be an undo of nothing — [10 §2](../10-Policy-Framework.md#why-rigidity-is-the-feature)'s
"compensating something that never happened" row, arriving in the one shape the fixed stage
order does not already forbid.

---

## 3. What is not built: fallback to a second capability

[10 §3](../10-Policy-Framework.md#3-the-policy-catalogue) catalogues `Fallback` as *"capability
or constant"*. **The constant half ships complete. The capability half is not built**, and this
is the record of precisely what it is blocked on, so that nobody re-derives it.

A capability-valued fallback would have to run under the same rules as any other invocation, and
each of these is missing rather than merely unwritten:

1. **No dispatch seam.** `IStepDispatcher.ExecuteAsync(stepIndex, …)` invokes *the capability at
   a step index*. A fallback capability is not a step: it has no index, no generated `case`, no
   input mapping and no typed write into the state bag. Only generated code can name a
   `JsonTypeInfo<T>` or call `ctx.Set<T>`, which is the same wall that put `DescribeCacheEntry`,
   `DescribeAudit` and `RestoreState` on the dispatcher — so this needs a new member on the
   dispatcher contract and a new emitter path, not an engine change.
2. **No journal identity.** [ADR-0015](ADR-0015-journal-schema-and-durable-execution.md) keys a
   row on `(instance, scope, step, attempt)`. Two capabilities under one step index would either
   share a key — an append-only table cannot hold that — or need a fifth component, which is a
   schema change and a migration.
3. **No compensation story.** [06 §7](../06-Execution-Engine.md#7-compensation-semantics) makes
   the unwind a stack of *completed steps*, each carrying the compensation its own capability
   declared. A step answered by its fallback capability really did produce an effect, so it must
   be compensable — by the *fallback's* undo, which the step does not declare and the stack has
   nowhere to record. §2.7's "a degraded step is not compensable" is sound precisely because a
   constant produces nothing; it becomes wrong the moment the degraded answer comes from a call.
4. **No manifest surface.** A step publishes one `capability` and one `version`. A second
   capability that a step may invoke is a dependency `flowx diff`, the impact analysis and every
   consumer of the manifest cannot see — and adding it is [ADR-0017](ADR-0017-manifest-v1-freeze-criteria.md)'s
   question, not this one's.

Shipping it half-working would mean a degraded path that is not journalled, not compensable and
not published — the failure mode that is worst exactly when it runs. The builder therefore has
one overload, `Fallback<TValue>(TValue value)`, and there is no syntax for the other half. This
is the third item in the revisit list below.

---

## 4. Consequences

**Positive:**

* **The latency/resilience group is complete against its own catalogue.** Every row §3 lists at
  stage 4 can now be written down, and stage 4 stops being "in full" only in the sense ADR-0025
  meant it.
* **The nesting is still a total order by kind**, so ADR-0024's central property survives
  untouched: two sets with the same kinds in a different order publish the same manifest and run
  the same way.
* **No new plan flag, no new manifest field, no new `flowx diff` rule.** Both kinds are read off
  the chain `StepPolicy.From` already walks, publish `kind` + `stage` like the other seven, and
  are counted by the existing `flowx_policy_invocations_total` through its `policy`/`outcome`
  labels — one new outcome value, `degraded`, and no new instrument
  ([ADR-0026](ADR-0026-policy-metrics-name-only-what-executes.md)'s discipline holds: an
  instrument is created when its policy executes, and these two report through one that already
  exists).
* **B2 is untouched structurally.** Everything here is behind `ExecutionPlan.HasStepPolicies`
  and `StepPolicy.IsActive`; a flow that declares no policy reaches none of it, and
  `EngineAllocationTests` still records zero.

**Negative / accepted trade-offs:**

* **`FLOWX1019` now understates a hedged step's worst case** (§2.5). A warning is less precise
  than it was, on a declaration whose extra term the author wrote down.
* **A hedged step's answer may be the losing call's** (§2.4). Sound under `Idempotent = true`
  and enforced by FLOWX1051, but it is a fact about hedging that no amount of engineering
  removes, and an author who reads `Idempotent` as "safe to repeat" rather than "the same
  request" will be surprised by it once.
* **A hedge multiplies load on the dependency it is protecting the caller from.** That is the
  trade the policy is; `maxAttempts` defaults to 2 and the bulkhead still bounds the total.
* **`Fallback` is refused on any capability with side effects**, which is stricter than
  "capability or constant" reads. A degraded mode for a write is expressible only as a branch in
  the flow, which is where a compensable effect belongs.
* **Half of a catalogued row is still unbuilt** (§3), and §3 is now the second document that has
  to be kept true about it. The alternative was a fourth kind of half-working policy.
* **`ExecutionPlan.HasParallel` now means slightly more than its name.** It counts a hedged step
  as well as a fork, which is right for the question it answers and reads oddly at the property
  name; its remarks say so.

**Revisit when:** `FLOWX1019`'s arithmetic is widened to carry the hedge term, or a deployment
is found whose deadline was met on paper and missed because of it; or three documented cases
need a nesting other than this one — ADR-0011's own bar, and unchanged by there being six kinds
rather than four; or capability-valued fallback is wanted badly enough to pay for §3's four
missing pieces, at which point §2.7's compensation decision re-opens with it; or a seventh
stage-4 kind is implemented, which this record's own §2.1 test should place without a new
argument.

---

**See also:** [ADR-0011](ADR-0011-fixed-policy-stage-order.md) ·
[ADR-0024](ADR-0024-stage-four-is-a-fixed-nesting.md) ·
[ADR-0025](ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md) ·
[ADR-0026](ADR-0026-policy-metrics-name-only-what-executes.md) ·
[FLOWX1051](../diagnostics/FLOWX1051.md) · [FLOWX1052](../diagnostics/FLOWX1052.md) ·
[FLOWX1053](../diagnostics/FLOWX1053.md) · [10 §3](../10-Policy-Framework.md#3-the-policy-catalogue)
