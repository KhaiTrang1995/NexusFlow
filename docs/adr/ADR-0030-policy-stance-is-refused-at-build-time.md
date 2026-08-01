# ADR-0030: `Authorization.Policy` is refused at build time rather than skipped at run time

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Repository owner · Platform architecture
**Amends:** [15-Security §4](../15-Security.md#4-authorisation-model)

> **The last of P4's four authorisation decisions, and the only one that says no.** The other
> three describe what the engine now does. This one describes the stance it cannot do, and
> refuses to be quiet about it.

---

## 1. Context

[ADR-0027](ADR-0027-authorisation-runs-in-the-step-loop.md) decides four of the five stances in
the step loop. `Authorization.Policy` is the fifth.

[15 §4](../15-Security.md#4-authorisation-model) is specific about what it means:

```
C -- Policy --> F{"ASP.NET Core policy satisfied?"}
```

An **ASP.NET Core** authorisation policy. Only `IAuthorizationService`, from
`Microsoft.AspNetCore.Authorization`, can evaluate one — a policy is a set of
`IAuthorizationRequirement`s and their handlers, resolved from a container, and evaluating it
is asynchronous.

`FlowX.Runtime` may not reference ASP.NET Core. `RuntimeIsolationTests` is the gate, and it
exists so a flow behaves identically whether a request, a broker record or a cron tick started
it — [ADR-0004](ADR-0004-universal-trigger-model.md). That constraint is not incidental to
FlowX; it is one of the two or three properties the whole design is for.

So the stance reaches `flowx.manifest.json`, reaches `flowx diff`'s `FLOWX-DIFF-015`, satisfies
`FLOWX1010` and `FLOWX1030` — and is checked by nothing.

### 1.1 Rejected options

* **Skip it at run time.** The status quo, and the defect this phase existed to end. A stance
  that permits everyone while publishing a requirement is the control failing open, and
  [15 §1](../15-Security.md#1-security-posture)'s first row makes deny-by-default the property
  the whole model rests on.
* **Refuse it at run time** — every `Policy` step fails with `Forbidden`. Rejected: it turns a
  declaration the author believed in into a `403` in production, discovered by a customer. The
  compiler can see this at build time, and a rule that fires where the mistake is made is worth
  more than a failure where it is felt. (The engine still refuses it, as a fail-closed backstop
  for a hand-built plan — see [ADR-0029 §2.1](ADR-0029-a-refusal-is-a-result-failure.md) — but
  that is a floor, not the mechanism.)
* **Declare an `IAuthorizationPolicyEvaluator` in `FlowX.Abstractions` and have the host
  supply an ASP.NET-backed implementation.** The right long-term answer, and rejected *for
  now* on three counts. It is asynchronous, and [ADR-0027 §2.3](ADR-0027-authorisation-runs-in-the-step-loop.md)'s
  check is synchronous and inside the hot loop. The engine has no container to resolve it from
  — `FlowEngine` takes a clock, and nothing else. And an evaluator that is *not registered*
  leaves the same hole this record is about, one indirection further away, where a
  `FLOWX`-shaped rule can no longer see it. Building it is what closes this record; building it
  badly reopens the defect.
* **Silently treat `Policy` as `Authenticated`.** Rejected outright: it downgrades a security
  declaration to a weaker one without telling anybody, which is worse than not enforcing it,
  because the manifest keeps saying `Policy`.
* **Make it a warning**, as `FLOWX1032` is for an unexecuted policy. Rejected — §2.1.

---

## 2. Decision

**A capability declaring `Authorization = Authorization.Policy` is `FLOWX1037`, an error.**

The set of names the rule reports lives in `FlowAnalyzer.StancesTheRuntimeCannotDecide` and is
pinned against `StepAuthorization.IsRefusedAtBuildTime` by
`AuthorizationStancesMatchTheAbstraction` — the arrangement `PolicyStagesMatchTheAbstraction`
already uses, and for the same reason: `FlowX.Compiler` targets netstandard2.0, loads into the
compiler process, and cannot reference the runtime it compiles for, so the set is a copy and an
unpinned copy of a security decision drifts in silence.

`EveryStanceIsEitherDecidedByTheEngineOrRefusedAtBuildTime` asserts the partition from the
runtime's side and `NoStanceIsBothDecidedAndReported` from the compiler's, so a sixth member
added to `Authorization` cannot fall through both.

### 2.1 Why an error, when `FLOWX1032` is a warning for the same shape of gap

[FLOWX1032](../diagnostics/FLOWX1032.md) reports a declared policy the runtime does not
execute, as a **warning**, on two arguments. Neither transfers.

**"An error erases the inventory the fixing phase needs."** FLOWX1032's argument turns on the
`.WithPolicy(...)` calls being the list of steps that asked for a timeout — which is how P4
found them, and that argument has now been paid off once. Here there is no inventory to
preserve: it is one attribute argument on a capability with four other stances available, two
of which are enforced, and one of which — `Permission` — is what most `Policy` declarations
actually mean. The declaration is not the record of an unmet need; it is a choice among five,
and four of the five work.

**"The source is not wrong; it is written correctly for a platform that has the feature."**
A `RateLimit` enforced at the gateway instead of in-process is a *correct program*, which is
why FLOWX1032 can honestly offer "confirm the flow is survivable and record that". There is no
counterpart here. A capability that publishes a required grant and checks none is not
survivable-with-a-caveat; it is the security control failing open.

And `SafetyDiagnosticsAreErrorsRatherThanWarnings` holds this line for the rest of the security
set. `FLOWX1010` and `FLOWX1030` are both errors; a warning here would make the third rule in
one family the only one a team may leave on.

### 2.2 The remedy is usually one word

The large majority of ASP.NET Core policies in practice are a single claim requirement, and
that is `Authorization.Permission`, which the engine enforces. The diagnostic's page leads with
that, and says plainly that it is a **published contract change** — `authorization.mode` and
`.value` both move, `FLOWX-DIFF-015` reports Breaking — which is correct, because callers
holding the old grant are now judged against a different one.

---

## 3. Consequences

**Positive:**

* **No stance is silently unenforced.** Four are decided and one is reported, the partition is
  asserted from both sides, and a member added to the enum turns a gate red rather than falling
  through a `switch`.
* **The failure arrives where the mistake is made.** An author writing
  `Authorization.Policy` is told at build time, with the remedy, rather than a customer meeting
  a `403`.
* **The manifest cannot publish an unenforceable claim.** `Policy` no longer reaches
  `flowx.manifest.json` from any compilable source, so `FLOWX-DIFF-015` has nothing to compare
  that nothing enforces.
* **Nothing in the repository is affected.** No sample, test or template declares `Policy` — a
  fact worth stating, because it means the rule shipped with zero suppressions and its first
  finding will be a real one.

**Negative / accepted trade-offs:**

* **A documented feature is now a build error.** [15 §4](../15-Security.md#4-authorisation-model)
  lists five stances and offers four. `Authorization.Policy` remains on the enum — removing it
  would be a breaking change to `FlowX.Abstractions` under constraint C7 — so the contract
  surface advertises something no build accepts. That is an honest state and an ugly one.
* **It costs real expressiveness.** A genuine multi-requirement policy — "in the EU **and**
  over 18 **and** the account is not frozen" — has no FlowX expression. The remedy is to keep
  it on the transport endpoint, and then the rule holds over HTTP and **not** for a bus or
  agent invocation of the same flow, which is precisely the transport-attached authorisation
  [15 §4](../15-Security.md#4-authorisation-model) exists to replace. This record does not
  pretend that is equivalent; it is a downgrade, and the page says so.
* **`ManifestWriter` keeps a mapping nothing exercises**, and a manifest test had to be
  rewritten to assert the diagnostic instead of the field. The writer's `Policy` handling and
  the schema's `authorization.value` are now dead paths kept against this record being
  reopened.
* **The rule has to be deleted rather than fixed**, and a rule scheduled for deletion is a rule
  somebody must remember to delete. [FLOWX1032](../diagnostics/FLOWX1032.md) carries the same
  debt and the same table.

**Revisit when:** an evaluator abstraction the engine may call ships — declared in
`FlowX.Abstractions`, implemented over `IAuthorizationService` by a host package, and reachable
from a step loop that by then must tolerate an asynchronous decision — at which point
`FLOWX1037`, its descriptor, its page and its catalogue row are **deleted**, not downgraded, on
FLOWX1032's precedent; or `Authorization.Policy` is removed from the enum at a major version,
which would close this record from the other end and is the only thing that resolves §3's first
negative; or a second stance becomes undecidable, at which point
`StancesTheRuntimeCannotDecide` stops being a one-element array and the rule's message needs to
name which of them it means.

---

**See also:** [FLOWX1037](../diagnostics/FLOWX1037.md) ·
[FLOWX1032](../diagnostics/FLOWX1032.md) ·
[ADR-0027](ADR-0027-authorisation-runs-in-the-step-loop.md) ·
[ADR-0004](ADR-0004-universal-trigger-model.md) ·
[15 — Security](../15-Security.md)
