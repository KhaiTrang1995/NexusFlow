# ADR-0047: `Authorization.Internal` is a composition stance, not a reachability control

**Status:** Accepted
**Date:** 2026-08-02
**Deciders:** Repository owner · Platform architecture
**Amends:** [07-Capability-Model §5](../07-Capability-Model.md), [15-Security §4.1](../15-Security.md#41-what-of-that-diagram-executes), [21-Quality-Gates §3](../21-Quality-Gates.md)
**Relates to:** [ADR-0004](ADR-0004-universal-trigger-model.md), [ADR-0027](ADR-0027-authorisation-runs-in-the-step-loop.md), [ADR-0030](ADR-0030-policy-stance-is-refused-at-build-time.md)

> **The stance promised two controls and shipped neither, and that was the right number.**
> This record says what it means instead, and moves the claim out of a doc comment into a
> gate — because a stance whose meaning lives only in prose is how the two got written.

---

## 1. Context

`Authorization.Internal`'s own summary said:

> Reachable only from another flow. The Trigger Engine rejects it at admission and the
> compiler excludes it from the generated agent tool surface.

Neither control existed. `StepAuthorization.Decide` returned a permit and `CanRefuse` was
false, so a flow stepping through an `Internal` capability ran for an anonymous caller down
HTTP, bus, cron or the MCP agent surface. [21 §3](../21-Quality-Gates.md) recorded this as an
open defect and named the remedy: *"a decision on `Internal`: an ADR, then
`StepAuthorization`."*

The obvious reading — *refuse when an external caller reaches an `Internal` capability* —
cannot be implemented, and the repository's own reference application is the proof.
`samples/workflow/OnboardEmployeeFlow` carries `[HttpTrigger("POST", "/api/v1/onboarding")]`
and steps through `hardware.order`, `equipment.assign` and `welcome.send`, all three
`Internal`, as ordinary forward steps. Any rule refusing that flow refuses the sample written
to demonstrate the whole control-flow surface.

The reason is structural rather than incidental. **A trigger addresses a flow and never a
capability** ([ADR-0004](ADR-0004-universal-trigger-model.md)): `[HttpTrigger]`,
`[BusTrigger]`, `[CronTrigger]` and `[AgentTrigger]` are declared on a `Flow<,>`. So at a step
there is no external caller to refuse — every capability invocation that exists is already
reached from a flow's step loop. The "no" branch [15 §4](../15-Security.md#4-authorisation-model)
draws for this stance has no input that can take it.

The second promised control is not merely unbuilt but category-confused. The agent tool
surface exists now — `plugins/FlowX.Mcp` — and it is keyed on `FlowAgentTool.FlowId`. A tool
**is** a flow. No capability of any stance appears there, so there is nothing stance-specific
for a compiler to exclude.

### 1.1 Rejected options

* **Refuse an `Internal` step when the invocation carries no principal.** Refuses
  `samples/ecommerce`'s `order.reprice` on every broker delivery — a delivery carries no
  principal by construction — and refuses `OnboardEmployeeFlow` for any anonymous caller,
  which is the flow's own business. It also states the rule backwards: `Internal` is chosen
  *because* there is no principal to check, not to require one.

* **Refuse an `Internal` step when the flow's trigger is externally addressable.** This is the
  reading with real content, and it fails on ownership: a capability does not know which flows
  compose it or how they are triggered, so the stance cannot express the rule. Pushed to where
  the information lives, it becomes a property of the flow's trigger — a different declaration
  on a different type, and one this stance would only obscure.

* **Filter `Internal` capabilities out of `tools/list` in `FlowX.Mcp`.** Ruled out by
  [25 §3](../25-Remaining-Platform.md): a filter is a second authorisation path that applies to
  agents and to no other transport, which is the one thing the agent surface is designed not to
  have. It would also be a fiction — the capability remains reachable through any published
  flow that composes it.

* **Delete the stance and fold it into `Public`.** They are not the same claim. `Public`
  asserts that direct exposure is intended and carries `[ApprovedBy]` to prove somebody signed
  for it; `Internal` asserts the opposite, that the capability is never exposed on its own.
  Collapsing them would put every internal step through a review gate designed for a different
  question, and lose the distinction the manifest publishes for access reviews (QR9).

---

## 2. Decision

**`Authorization.Internal` means: the capability is not independently exposed. It is composed
into flows and never addressed on its own, and it therefore admits every caller at the step,
because at the step there is no external caller to refuse.**

The runtime behaviour is unchanged — it was already correct. What changes is that the claim is
now stated once, checked, and no longer resting on prose.

1. **One predicate.** `StepAuthorization.AdmitsEveryCaller` is the single place either half of
   the type says a stance permits. `CanRefuse` is its negation and `Decide` switches on it, so
   the two cannot disagree. They previously spelled the set out independently, which is exactly
   the drift that let the summary and the behaviour diverge unnoticed.

2. **The premise is a gate, not a sentence.** `NoTriggerAttributeAddressesACapability` fails
   the build if any capability carries a trigger attribute, and
   `EveryTriggerAttributeIsNamedForTheConventionTheGateMatches` stops that gate decaying into
   one that checks six names it happens to know. The day something makes a capability directly
   addressable is the day `Internal` would have to start refusing, and that day now turns a
   build red instead of passing in silence.

3. **The summary says what is true**, including the part that most needs saying: `Internal` is
   *not* a way to hide a capability from a caller or from an agent. A published flow's
   `Internal` steps run for whoever called it.

### 2.1 A defect found on the way, and fixed with it

`CanRefuse` read *"decided at run time, and neither `Public` nor `Internal`"*. That conflates
two unrelated properties — *admits everyone* and *cannot be evaluated here* — and
`Authorization.Policy` is the second. Being undecidable, it failed the test, so `From` dropped
the stance, `Decide` was never asked, and **a `Policy` capability permitted every caller.**
`Decide`'s default arm refused it correctly and was unreachable: a refusal written, tested by
nothing, executed never.

[ADR-0030](ADR-0030-policy-stance-is-refused-at-build-time.md) refuses `Policy` at build time
via `FLOWX1037`, which is why this was not reachable from compiled source. It was still a
fail-open on the plan-construction path, and `CapabilityDescriptor.Create` already re-enforces
FLOWX1030 at that boundary for exactly this reason: *a plan built by hand did not go through
the analyzer*. FLOWX1030 refusing late costs a malformed plan; this refusing late costs an
unauthorised execution.

With `CanRefuse` defined as `!AdmitsEveryCaller`, `Policy` survives resolution and reaches the
default arm, which refuses with `authorization.stance_not_enforceable`.

---

## 3. Consequences

**Positive:**

* The stance's meaning and its behaviour are pinned together by
  `EveryStanceTreatsAnAnonymousCallerAsItsPartitionSaysItDoes`, which resolves a descriptor the
  way the plan builder does — the path where the `Policy` defect actually lived — for every
  member of the enum, so a sixth stance is covered on the day it is added.
* `EveryStanceLandsInExactlyOneOutcome` makes the three outcomes a real partition. A stance in
  none of them is one nothing enforces; a stance in two is one whose enforcement depends on
  which predicate the caller happened to ask.
* A `Policy` stance can no longer permit anybody.
* `FlowX.Mcp` needs no special case, so there remains exactly one authorisation path.
* No sample or test changed. The reading was chosen to fit the code that exists, not the other
  way round.

**Negative / accepted trade-offs:**

* `Internal` and `Public` behave identically at run time. They differ in what they claim, in
  the review obligation `[ApprovedBy]` attaches to one of them, and in what the manifest
  publishes for an access review — not in what the step loop does. A reader who expects a
  stance to be a check will find this one is a declaration, which is why the summary now says
  so first rather than last.
* The exposure question `Internal` looks like it answers is genuinely unanswered: nothing
  asserts that an externally triggered flow reaches a refusing step before it does anything
  consequential. That is a flow-level property and belongs to the trigger, not here. This
  record narrows the stance rather than filling that gap, and naming it is the honest cost.
* A `Policy` plan now sets `HasAuthorizedSteps` and pays the step-loop check it did not pay
  before. It pays it to be refused, which is the point; and `FLOWX1037` means such a plan
  cannot be built from source anyway.

**Revisit when:** a transport addresses a capability directly rather than a flow — at which
point `NoTriggerAttributeAddressesACapability` fails, and `AdmitsEveryCaller` must lose
`Internal` in the same change; or an evaluator abstraction ships that lets the engine decide
`Policy` ([ADR-0030](ADR-0030-policy-stance-is-refused-at-build-time.md)'s own revisit
condition), which moves it out of the refusing bucket; or a flow-level exposure declaration is
introduced, which would take over the question §3 records as unanswered and may make this
stance redundant.
