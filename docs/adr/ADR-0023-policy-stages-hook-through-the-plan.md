# ADR-0023: A policy stage hooks into the step loop through a plan-level flag and a resolved node field, never through the declared chain

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Repository owner · Platform architecture
**Amends:** [ADR-0011](ADR-0011-fixed-policy-stage-order.md) ·
[14-Performance §B2](../14-Performance.md)

> **This is the third of the three decisions P4 could not inherit.** The stage order is
> ADR-0011's, the catalogue is [10 §3](../10-Policy-Framework.md#3-the-policy-catalogue), the
> retry arithmetic is [10 §5](../10-Policy-Framework.md#5-retry-safety) — all recorded before
> a line of the engine existed. What no record settled is **where the engine reads them**, and
> that is the one question with a measured budget attached to its answer.

---

## 1. Context

Budget **B2 is a hard zero**: zero allocations per step on the success path of an ephemeral
flow, asserted by `EngineAllocationTests` in Release and gated by the unit-test job. It is not
a benchmark — it is a `GC.GetAllocatedBytesForCurrentThread()` delta around one warm
execution, and it has held for the linear, conditional and switch shapes since WP-4.

A policy engine is the first feature that wants to do work *around every step*. The obvious
implementation reads the step's declared chain at the point of use:

```csharp
foreach (var policy in step.Policies.Ordered)   // ← an enumerator, per step, per execution
{
    if (policy.Kind == "Timeout") { … }         // ← a string comparison, per step
}
```

That costs an `ImmutableArray<PolicyDescriptor>` enumerator and a dictionary lookup per
parameter, on **every** step of **every** flow — including the flows that declare nothing,
because the loop has to look before it can know there is nothing to find. `docs/10`'s stage
order would then be a tax on the flows that never asked for it, which is the exact bargain
[`HasParallel`](../../src/FlowX.Core/ExecutionPlan.cs), `HasCompensationPolicies`, `HasEmit`
and `HasTimers` were each introduced to refuse.

### 1.1 The established pattern, and why it is four flags rather than one

Every one of those four answers a question of the form *"can any step of this plan reach the
code behind this branch"*, is computed once when the plan is built, and is read before the
per-step field it guards. `HasCompensationPolicies` is the closest relative: it gates the
compensation retry so that "the unwind of a plain saga stays the single dispatch per entry it
always was — one predictable always-false comparison, no retry bookkeeping, no clock read".

A single `HasPolicies` flag would be the wrong shape, and the reason is specific rather than
aesthetic: **most declared policies are still not executed.** `samples/banking` declares seven
policy sets containing `RateLimit`, `Idempotency`, `Cache` and `Audit`; none of those four is
implemented ([ADR-0025](ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md)). A
flag that were true because a step declared a `Cache` would send that step down the policy
path to discover there is nothing to do — charging the flow for a policy nothing applies,
which is precisely the cost this decision exists to avoid.

### 1.2 Rejected options

* **Read `StepNode.Policies` at the point of use.** Rejected: an enumerator and a string
  comparison per step, on flows that declare nothing. This is the option that costs B2.
* **A `HasPolicies` flag meaning "some step declared something".** Rejected: true for every
  step of `samples/banking` today and false for none of them, so it gates nothing and the
  cost above returns in full.
* **Wrap the dispatcher.** A decorator `IStepDispatcher` per policed step. Rejected: the
  wrapper is an allocation per step, it cannot see the flow's deadline (which the timeout
  must be clamped to), and it moves ordering out of `PolicyChain` — which ADR-0011 makes the
  single place ordering may live.
* **Emit the policy pipeline from the generator.** Rejected: it would put resilience
  behaviour into generated code, where it cannot be fixed without recompiling every consuming
  assembly, and would make two flows with the same policy set carry two copies of the engine.

---

## 2. Decision

**A policy stage is reached through two precomputed values and nothing else: a resolved field
on the node, gated by a flag on the plan. The declared chain is read once, when the plan is
built, and never again.**

### 2.1 `StepPolicy`, resolved on the node

`StepNode.StepPolicy` holds the stage-4 parameters as fields, resolved by `StepPolicy.From`
from the declared `PolicyChain` at construction time — exactly as `StepNode.CompensationRetry`
already resolves the compensation retry, and for the reason its remarks give: reading it "on
the failure path, where an incident is already in progress" is the wrong moment to walk an
array. The forward path's version of that argument is volume rather than urgency: it is read
once per step of every policed execution.

A chain that declares no stage-4 kind resolves to the shared `StepPolicy.None` instance. No
per-step allocation, ever — resolution happens when `ExecutionPlan.Create` runs.

### 2.2 `ExecutionPlan.HasStepPolicies`, gating the plan

```csharp
stepPolicies |= step.StepPolicy.IsActive;
```

`IsActive` is true when a timeout, a retry of more than one attempt, a breaker or a bulkhead
was declared. **It counts what executes, not what was declared** — a step whose chain holds
only a `RateLimit`, an `Idempotency` window, a `Cache` or an `Audit` leaves it false.

The step loop reads the plan's flag first:

```csharp
var policy = plan.HasStepPolicies ? step.StepPolicy : StepPolicy.None;
```

An unpoliced flow therefore pays one comparison against a field the plan already holds, and
the branch is perfectly predicted. Nothing else on the success path changes.

### 2.3 What this fixes about the DSL that nothing else could

`PolicyChain` stays the only place ordering lives, which is ADR-0011's requirement. This
record adds no second ordering mechanism: `StepPolicy.From` walks `PolicyChain.Ordered`,
which is already sorted by stage, and reads parameters off it. What runs before what is still
decided in exactly one file.

---

## 3. Consequences

**Positive:**

* **B2 stays a hard zero for every shape that had it**, and this is asserted rather than
  argued: `EngineAllocationTests` measures the same ephemeral plan it always did, and
  `PolicyExecutionTests.APlanDeclaringOnlyInertKindsReportsNoStepPolicies` pins the flag
  false for a plan carrying three declared-but-unexecuted kinds.
* **A declaration whose stage is not implemented costs nothing.** `samples/banking`'s
  `Admission` set — a `RateLimit` and an `Idempotency` window — leaves `HasStepPolicies`
  false on the step that carries it, so the sample's honesty about what does not run is now a
  property of the plan rather than a paragraph.
* **The pattern is the one already in the file.** A reader who understands
  `HasCompensationPolicies` understands this without being told, and there are now two
  instances of the shape rather than one plus an exception.
* **Widening is mechanical.** Implementing stage 5 means adding fields to `StepPolicy`,
  widening `IsActive`, and nothing else: no new flag, no new read site, no change to the step
  loop's shape.

**Negative / accepted trade-offs:**

* **`IsActive` is a second thing that has to be kept in step with the engine.** A stage added
  to `StepPolicy.From` and forgotten in `IsActive` would resolve parameters the engine then
  never applies, silently. It is one predicate in one file rather than a table, which is the
  smallest surface this could have, but it is a surface.
* **`HasStepPolicies` is coarse.** One policed step in a two-hundred-step flow sends all two
  hundred through the flag-true path, where each reads `step.StepPolicy` and finds
  `StepPolicy.None`. That is one field read and one comparison per step, not an allocation,
  and a per-step flag would be a second copy of a fact the node already holds.
* **The plan grows a field, and the plan is compared.** `flowx diff` reads the manifest and
  not the plan, so nothing published changes — but a second consumer of `ExecutionPlan` would
  now see a property whose meaning is "and the engine does something about it", which is a
  narrower claim than its name makes.
* **A hand-built plan can disagree with a compiled one.** `StepPolicy.From` runs in
  `StepNode.ForCapability`, so any plan built any way gets it; but a caller who constructs
  `PolicyChain` themselves and expects declaration order to decide nesting will be wrong. That
  is [ADR-0024](ADR-0024-stage-four-is-a-fixed-nesting.md)'s subject, not this one's.

**Revisit when:** a stage needs to run *between* two steps rather than around one — an
outbox or a batch coalescing across several — at which point the node is the wrong place to
hang it and the flag is the wrong question to ask; or `EngineAllocationTests` records a
non-zero figure for an unpoliced ephemeral plan, which would mean the gate this record was
written to protect has already been lost; or a third flag of this shape is proposed, at which
point the four that exist should probably become one bit set.

---

**See also:** [ADR-0011](ADR-0011-fixed-policy-stage-order.md) ·
[ADR-0024](ADR-0024-stage-four-is-a-fixed-nesting.md) ·
[ADR-0025](ADR-0025-a-partial-policy-engine-executes-stage-four-alone.md) ·
[10 — Policy Framework](../10-Policy-Framework.md)
