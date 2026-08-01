# ADR-0026: The authorisation check runs in the step loop, reached through a plan flag and a resolved node field

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Repository owner · Platform architecture
**Amends:** [ADR-0023](ADR-0023-policy-stages-hook-through-the-plan.md) ·
[15-Security §4](../15-Security.md#4-authorisation-model)

> **This is the first of four decisions P4's authorisation work could not inherit.** The five
> stances are [`Authorization`](../../src/FlowX.Abstractions/Capabilities/Authorization.cs)'s,
> written before the engine existed; that a capability must declare one is `FLOWX1010`; that a
> stance needing a name must have one is `FLOWX1030`; that changing the name is Breaking is
> `FLOWX-DIFF-015`. What no record settled is **where the answer is decided**, and until it was
> decided nothing decided it at all.

---

## 1. Context

A capability declares an authorisation stance. `ManifestWriter` publishes it as
`capability.authorization.mode` and `.value`, `flowx diff` classifies a change to either as
**Breaking**, and two build errors make it impossible to omit. Then:

```
$ grep -rniE 'Authorization|Permission|Authorize' src/FlowX.Runtime src/FlowX.Hosting --include=*.cs | wc -l
0
```

FlowX published an authorisation contract, diffed it, broke builds over it, and never checked
it when a step ran. `samples/ecommerce`'s `payment.capture` declared
`Permission = "payment.write"` and captured payments for anonymous HTTP callers.

### 1.1 The three places the check could go

**At the trigger.** A stance is a property of a capability and a caller's identity arrives at a
trigger, so the trigger is where both are first in the same room. Rejected: a trigger addresses
a *flow*, and a flow's steps are decided at run time by `When`, `Switch` and `ForEach`. A
trigger authorising the union of every stance any step might declare would refuse callers for
steps the flow was never going to take — and one authorising the intersection would authorise
nothing. It also puts the decision in every transport plugin, which is
[ADR-0004](ADR-0004-universal-trigger-model.md)'s "no parallel system to get out of sync" lost
at the first opportunity.

**In `FlowHost`.** Rejected for the same reason one step down, plus a worse one: `FlowHost` is
not on the path of a sub-flow, a compensation or a resumed instance, so a check there would
hold for some invocations of a capability and not others. A stance that means something
different depending on how the flow was composed is not a stance.

**In the step loop.** Chosen. It is the one place that knows *this* step is about to run, has
the resolved node, and is common to every path a capability can be reached by.

### 1.2 Rejected implementations within the step loop

* **Read the `[Capability]` attribute.** Rejected: reflection, per step, on the hot path —
  and constraint **C2** forbids it outright, because `samples/ecommerce` publishes NativeAOT.
* **Decide once per execution, before the first step.** Rejected: it authorises steps the flow
  will not take, which is §1.1's trigger argument arriving inside the engine.
* **A decorator `IStepDispatcher`.** Rejected on [ADR-0023](ADR-0023-policy-stages-hook-through-the-plan.md)'s
  grounds: an allocation per policed step, and it moves a decision out of the engine into a
  wrapper that cannot see the plan.
* **A single `HasAuthorization` flag meaning "some step declared a stance".** Rejected, and
  this is the sharpest of them: `FLOWX1010` is an **error**, so *every* compiled capability
  declares one. A flag of that shape is true for every flow in existence and gates nothing.

---

## 2. Decision

**The authorisation check runs in `FlowEngine`'s step loop, immediately before the step's
retry loop, reached through `ExecutionPlan.HasAuthorizedSteps` and a resolved
`StepNode.StepAuthorization` — never by reading a declaration at run time.**

### 2.1 `StepAuthorization`, resolved on the node

`StepAuthorization.From` reads the stance and its named grant off the step's
`CapabilityDescriptor` when `ExecutionPlan.Create` runs, exactly as `StepPolicy.From` resolves
stage 4. A stance that admits every caller resolves to the shared `StepAuthorization.None`. No
per-step allocation, ever.

### 2.2 `ExecutionPlan.HasAuthorizedSteps`, gating the plan

```csharp
authorized |= step.StepAuthorization.CanRefuse;
```

`CanRefuse` is true for `Authenticated` and `Permission` and false for everything else. **It
counts what can say no, never what was declared** — §1.2's answer to the flag that would gate
nothing. A flow of `Public` and `Internal` steps leaves it false and reads no principal.

### 2.3 Where in the loop, and why exactly there

```csharp
if (plan.HasAuthorizedSteps
    && !context.IsContinuation
    && step.StepAuthorization.Decide(context.Principal, capabilityId) is { } denial)
```

**Before the dispatch**, because a check after the call has authorised nothing — the payment
has already been captured. **Outside the retry loop**, because asking the same question of the
same principal three times gets the same answer three times, while the backoff spends the
flow's deadline and one audit event becomes three. `ErrorCategory.Forbidden` is terminal, so
no `Retry` would act on it in any case; placing the check outside the loop means the flow does
not depend on that remaining true.

`IsContinuation` is [ADR-0027](ADR-0027-identity-arrives-on-the-invocation.md)'s.

### 2.4 A compensation is not authorised

`StepNode.ForCapability` resolves the node's stance from the **forward** capability alone, and
`FlowEmitter` emits no stance on a compensation descriptor.

An undo runs on the failure path to reverse work this principal has already caused. Refusing it
would leave standing exactly the inconsistent state the compensation exists to remove — and the
caller was already authorised for the step that made the mess. It also makes the refusal path
coherent: a step refused by its own stance unwinds the steps before it, which
`AnAuthenticatedCallerWithoutThePermissionIsRefusedAndTheHoldIsReleased` asserts over the real
endpoint.

---

## 3. Consequences

**Positive:**

* **The published contract is now enforced, and from one reading.** `CapabilityReader` fills
  `StepModel` once; `ManifestWriter` and `FlowEmitter` both read it. The stance the engine
  decides and the stance `FLOWX-DIFF-015` compares cannot disagree, because there is no second
  reading for them to disagree between.
* **B2 stays a hard zero for every shape that had it**, asserted rather than argued:
  `EngineAllocationTests` measures unstanced plans, whose flag is false, and
  `APlanWhoseStancesCannotRefuseReportsNoAuthorizedSteps` pins the flag false for a plan of
  `Public` and `Internal` steps.
* **The pattern is the one already in the file.** A reader who understands `HasStepPolicies`
  understands this without being told.
* **A stance holds identically whatever reached the capability** — a trigger, a sub-flow, a
  resumed instance — because the check is at the one point all of them pass through.

**Negative / accepted trade-offs:**

* **`Permission` allocates.** `ClaimsPrincipal.Claims` is an `IEnumerable<Claim>` and there is
  no allocation-free way to walk it, so a step with a `Permission` stance costs one enumerator
  per execution. It is paid only by steps that declared it, and never by the plans B2 measures
  — but it is a real cost and this record does not pretend otherwise. A cache keyed on
  (principal, permission) was rejected as a correctness risk out of proportion to an enumerator.
* **`HasAuthorizedSteps` is coarse**, exactly as `HasStepPolicies` is: one stanced step in a
  two-hundred-step flow sends all two hundred through the flag-true path, where each reads
  `StepAuthorization.None` and branches away. One field read and one comparison, not an
  allocation.
* **A hand-built descriptor carries no stance and is not gated.** `CapabilityDescriptor.Create`
  without one produces `Authorization = null`. That is unreachable from compiled source —
  `FLOWX1010` is an error — but a test or benchmark can do it, and such a step is ungated
  rather than given a default. A default would be a stance nobody wrote, which is what
  `NoPermissiveDefaults` and `FLOWX1010` both exist to refuse. `AHandBuiltDescriptorWithNoStanceIsNotGated`
  pins it so the behaviour is stated rather than discovered.
* **The decision is synchronous.** Anything needing I/O to decide — a permission lookup, a
  policy handler with a dependency — cannot be expressed. That is what makes
  [ADR-0029](ADR-0029-policy-stance-is-refused-at-build-time.md) necessary rather than
  optional, and it is the first thing this record would have to reopen.
* **A compensation runs unauthorised, by construction** (§2.4). An undo with side effects
  heavier than the step it reverses is undone for a caller who was authorised for the step and
  not, separately, for its inverse. The argument for it is strong and the surface is real.

**Revisit when:** a stance needs an asynchronous decision, at which point §2.3's placement and
the whole shape of `Decide` reopen together with ADR-0029; or a stance has to be decided before
the flow starts rather than per step — an admission-time quota, for instance — at which point
§1.1's rejection of the trigger needs re-arguing rather than citing; or `EngineAllocationTests`
records a non-zero figure for an unstanced ephemeral plan, which would mean the gate this
record was written to protect has already been lost; or a fifth flag of this shape is proposed,
at which point [ADR-0023](ADR-0023-policy-stages-hook-through-the-plan.md)'s own "the four that
exist should probably become one bit set" is due.

---

**See also:** [ADR-0023](ADR-0023-policy-stages-hook-through-the-plan.md) ·
[ADR-0027](ADR-0027-identity-arrives-on-the-invocation.md) ·
[ADR-0028](ADR-0028-a-refusal-is-a-result-failure.md) ·
[ADR-0029](ADR-0029-policy-stance-is-refused-at-build-time.md) ·
[15 — Security](../15-Security.md)
