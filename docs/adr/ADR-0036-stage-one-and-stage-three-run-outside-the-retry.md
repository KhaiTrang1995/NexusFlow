# ADR-0036: Stage 1 and stage 3 run in the step loop outside the retry, off the same resolved `StepPolicy`, and add no plan flag

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Repository owner · Platform architecture
**Amends:** [ADR-0023](ADR-0023-policy-stages-hook-through-the-plan.md) ·
[ADR-0025](ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md) §2.5

> **[ADR-0023](ADR-0023-policy-stages-hook-through-the-plan.md)'s "Revisit when" says a *third
> flag of this shape* should make the four that exist into one bit set.** This record is the
> third stage arriving and the argument that it needs **no** flag, so that trigger does not
> fire.

---

## 1. Context

[ADR-0025](ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md) §2.5 wrote down what
must be true when a skipped stage lands: *"A stage added later must not be able to run after
stage 4… adding stage 3 means adding it outside the retry loop, and adding stage 5 means adding
it outside the dispatch and inside stage 4."* It named the position and did not decide the
mechanism, because nothing was implementing one.

Two mechanisms were available, and the repository has one instance of each.

### 1.1 The flag question, and why the answer is "no new flag"

[ADR-0023](ADR-0023-policy-stages-hook-through-the-plan.md) established a shape:
a resolved field on the node, gated by a flag on the plan.
[ADR-0027](ADR-0027-authorisation-runs-in-the-step-loop.md) struck that shape a second time,
with `StepNode.StepAuthorization` and `ExecutionPlan.HasAuthorizedSteps`. A reader arriving at
stage 1 and stage 3 would reasonably expect a third pair.

It is not needed, and ADR-0023 says why in its own Consequences: *"Widening is mechanical.
Implementing stage 5 means adding fields to `StepPolicy`, widening `IsActive`, and nothing
else: no new flag, no new read site, no change to the step loop's shape."*

`HasAuthorizedSteps` is a separate flag for a reason that does not transfer. Authorisation is
not a policy: it is resolved from a **capability attribute** rather than from a `PolicySet`, so
there is no `PolicyChain` for `StepPolicy.From` to read it out of, and a step with no
`.WithPolicy(...)` at all can still refuse a caller. A rate limit and an idempotency window are
`PolicySet` builder methods, arrive on the same `PolicyChain` as the four stage-4 kinds, and are
read by the same `StepPolicy.From` that reads past them today.

So the change is exactly the mechanical one ADR-0023 predicted: fields on `StepPolicy`, a wider
`IsActive`, and no new flag. `ExecutionPlan.HasStepPolicies` continues to mean *"some step will
actually be wrapped"* rather than *"some step declared something"*, which is the property that
keeps budget B2 a hard zero — and it now means it about six kinds rather than four.

### 1.2 The position question

The retry is a `while (true)` loop in `RunRangeAsync` around the dispatch and the commit
([ADR-0024](ADR-0024-stage-four-is-a-fixed-nesting.md) makes it the outermost of the four). So
"outside the retry" is a statement about where the code goes relative to that loop, and it is
not a detail:

* **A rate limit inside the retry** would charge a permit per attempt. A `RateLimit(20, PT1S)`
  with a `Retry(3)` would then admit between seven and twenty callers per second depending on
  how many of them failed, which is a limit whose effective value is a function of the
  dependency's health.
* **An idempotency window inside the retry** would record the key, dispatch, fail, and then find
  its own in-flight marker on attempt two — deadlocking the step against itself.

Both are ADR-0011's ordering incidents in miniature, and both are unexpressible once the two
stages sit where the fixed order puts them.

### 1.3 Rejected options

* **A third and fourth plan flag, `HasRateLimitedSteps` and `HasIdempotentSteps`.** Rejected:
  §1.1. It would also fire ADR-0023's own revisit trigger for no behavioural gain, and a bit set
  is a refactor that should be motivated by a fifth flag rather than by two that were not
  needed.
* **A separate resolved node field, `StepNode.StepAdmission`.** Rejected: it is a second thing
  read off `PolicyChain.Ordered` at plan-build time, so it is a second copy of the walk that
  `StepPolicy.From` already performs, and `IsActive` would then have to consult two objects to
  answer one question.
* **Wrap `DispatchPolicedAsync` so the whole policy chain is one call.** Rejected for
  ADR-0023's reason and one more: the retry loop is the caller of `DispatchPolicedAsync`, so
  anything inside that method is inside the retry, which is precisely the position §1.2
  forbids.

---

## 2. Decision

**`StepPolicy` gains the stage-1 and stage-3 parameters, `IsActive` widens to count them, and
`FlowEngine`'s step loop applies them in fixed order before the retry loop opens and after it
closes. No new flag is added to `ExecutionPlan` and no new field is added to `StepNode`.**

The step loop's shape, for a step whose `StepPolicy.IsActive` is true:

```text
stage 1 · admission   TryAcquireAsync → refuse, or continue           ← once per step
stage 3 · integrity   BeginAsync      → replay, refuse, or continue   ← once per step
  ┌ retry ────────────────────────────────────────────────────────────┐
  │ stage 4 · resilience  CircuitBreaker { Bulkhead { Timeout { … } } }│ ← once per attempt
  └───────────────────────────────────────────────────────────────────┘
stage 3 · integrity   CompleteAsync or AbandonAsync                   ← once per step
```

Stage 1 before stage 3, because ADR-0011 puts `Admission` before `Integrity` and the reason
survives the implementation: an unbounded flood of repeated keys would otherwise reach the
idempotency store, which is the *"rate limit after authentication → unauthenticated flood
exhausts the token validator"* row of
[10 §2](../10-Policy-Framework.md#2-fixed-stage-order--the-core-decision)'s table with a
different validator.

The closing half of stage 3 is after the retry rather than after each attempt for §1.2's
reason, and it is where the "a failed step frees its key" decision lives — see
[ADR-0037](ADR-0037-an-idempotency-record-is-keyed-by-the-invocations-key.md) §2.3.

---

## 3. Consequences

**Positive:**

* **B2 stays a hard zero and the argument is unchanged rather than re-made.** The gate is still
  `plan.HasStepPolicies ? step.StepPolicy : StepPolicy.None`, still one comparison against a
  field the plan already holds, and an unpoliced ephemeral plan still reaches nothing.
  `EngineAllocationTests` measures the same plan it always did.
* **ADR-0023's revisit trigger does not fire**, and the reason is recorded rather than the
  trigger being quietly ignored. There are still four flags of that shape, and the fifth stage
  to land will not add one either.
* **A step declaring only a `Cache` or an `Audit` still leaves `HasStepPolicies` false**, so the
  two stages that remain inert still cost their declarers nothing — which is the property
  ADR-0023 was written to protect and the reason it is worth checking that widening `IsActive`
  did not quietly make it true for everything.
* **The order is checkable from one file.** What runs before what is decided in
  `RunRangeAsync`, in the order the stages are numbered, with no dispatch table and no
  registration.

**Negative / accepted trade-offs:**

* **`RunRangeAsync` grows, and it was already the longest method in the runtime.** Two stages
  are two more blocks in a loop that is read by everybody who touches the engine. The
  alternative — a method per stage — would move the ordering out of the one place a reader can
  see it whole, which ADR-0011 makes the thing that must not happen.
* **`IsActive` is now the predicate for six kinds rather than four**, and ADR-0023's recorded
  trade-off — *"a stage added to `StepPolicy.From` and forgotten in `IsActive` would resolve
  parameters the engine then never applies, silently"* — is correspondingly larger. It is still
  one predicate in one file, and `PolicyStageFitnessTests` pins the executed set against it
  from the compiler's side.
* **A step can now make two store calls before its capability is dispatched**, and neither is
  visible in the flow's source. A `.WithPolicy(...)` naming a set with both kinds is two round
  trips per step; the manifest publishes both kinds, so the fact is discoverable, but the cost
  is not written anywhere the author is looking.

**Revisit when:** a stage has to run *between* two steps rather than around one, which is
ADR-0023's own first trigger and is still open; or `StepPolicy` acquires a parameter that is not
a value the plan can resolve at build time — an expression, a callback — at which point the
resolved-field shape stops being available and the whole of ADR-0023 re-opens; or stage 5
lands and needs a position ADR-0025 §2.5 did not name.

---

**See also:** [ADR-0011](ADR-0011-fixed-policy-stage-order.md) ·
[ADR-0023](ADR-0023-policy-stages-hook-through-the-plan.md) ·
[ADR-0024](ADR-0024-stage-four-is-a-fixed-nesting.md) ·
[ADR-0025](ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md) ·
[ADR-0027](ADR-0027-authorisation-runs-in-the-step-loop.md) ·
[10 — Policy Framework](../10-Policy-Framework.md)
