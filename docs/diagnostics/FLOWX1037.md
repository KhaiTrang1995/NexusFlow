# FLOWX1037 — Authorisation stance is not enforced by the runtime

> **Severity:** Error · **Category:** FlowX · **Since:** P4
> **Applies to:** the one stance of the five nothing decides — `Authorization.Policy`.
> **Does not apply to:** `Public`, `Authenticated`, `Permission` and `Internal`, which the
> engine decides in the step loop.
> **Scheduled for deletion:** when a policy evaluator the engine may call exists — see
> [When this rule is deleted](#when-this-rule-is-deleted).

## What it means

`[Capability(..., Authorization = Authorization.Policy, Policy = "…")]` declares that a named
authorisation policy must be satisfied before the capability runs.
[15 §4](../15-Security.md#4-authorisation-model) spells out which kind of policy: an
**ASP.NET Core** one, evaluated against the caller's `ClaimsPrincipal`.

Only `IAuthorizationService`, from `Microsoft.AspNetCore.Authorization`, can evaluate one.
`FlowX.Runtime` may not reference it — `RuntimeIsolationTests` in
`tests/FlowX.Architecture.Tests` is the gate, and it exists so that a flow behaves identically
whether a request, a broker record or a cron tick started it ([ADR-0004](../adr/ADR-0004-universal-trigger-model.md)).
So the stance reaches the manifest, reaches `flowx diff`, and reaches a step the engine
dispatches with nothing consulted.

| Stance | What the runtime does |
|---|---|
| `Public` | **Decided.** Permits, and the permit is a branch rather than an absence |
| `Authenticated` | **Decided.** Refuses an invocation whose principal is absent or unauthenticated |
| `Permission` | **Decided.** Refuses a principal not holding the named permission |
| `Internal` | **Decided.** Refuses an invocation the trigger boundary marked externally addressed |
| `Policy` | **Nothing.** This rule |

## Example that triggers it

```csharp
[Capability("report.export", Version = "1.0.0",
    Authorization = Authorization.Policy, Policy = "DataExportApproved")]
public sealed class ExportReport : ICapability<ExportRequest, ExportResult> { … }
```

## How to fix it

**Not by deleting the stance** — that is the edit which silences the rule and removes the
record of what the capability needs, and there is no permissive stance to fall back to that
would not be a downgrade nobody reviewed.

In the order they should be considered:

1. **Say what the policy actually requires, and declare that instead.** The large majority of
   ASP.NET Core policies in practice are a single claim requirement. `Authorization.Permission`
   with that claim's value is the same rule, and it is enforced:

   ```csharp
   [Capability("report.export", Version = "1.0.0",
       Authorization = Authorization.Permission, Permission = "report.export")]
   ```

   This is a **published contract change** — `authorization.mode` and `authorization.value`
   both move — so `flowx diff` reports `FLOWX-DIFF-015` and it is a breaking change. That is
   correct: callers holding the old grant are now judged against a different one.

2. **Keep the policy at the transport**, if it genuinely needs several requirements or a
   handler with a dependency. An ASP.NET Core policy on the endpoint is real enforcement, and
   the capability then declares the stance it is left with — usually `Authenticated`. What is
   lost is the property [15 §4](../15-Security.md#4-authorisation-model) is written for: the
   rule now holds only over HTTP, and a Kafka or agent invocation of the same flow does not
   get it. Record that where the flow is declared.

3. **Do not ship the capability on this release**, if neither is true.

## When to suppress

**There is no honest suppression of this rule**, which is why it is an error and why this
section is shorter than its neighbours'. Suppressing it leaves the manifest publishing a
policy requirement that no code path checks — the exact defect
[FLOWX1010](FLOWX1010.md) and [FLOWX1030](FLOWX1030.md) exist to prevent one and two levels
up, arriving by a third route.

Any suppression must still carry a `FLOWX-DEBT` marker with an owner and an expiry — see
[21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy).

## Why this is an error and not a warning

[FLOWX1032](FLOWX1032.md) reports the same shape of gap — a declaration the runtime does not
honour — as a **warning**, on the argument that "an error erases the inventory the fixing
phase needs" and that "the source is not wrong; it is written correctly for a platform that
has the feature". Neither half transfers, and the reason is what
[ADR-0030](../adr/ADR-0030-policy-stance-is-refused-at-build-time.md) records:

**There is no inventory to preserve.** FLOWX1032's argument turns on the `.WithPolicy(...)`
calls being the list of steps that asked for a timeout, which is what let P4 find them. Here
the inventory is one attribute argument on a capability that has four other stances available,
two of which are enforced and one of which — `Permission` — is what most `Policy` declarations
mean. The declaration is not a record of an unmet need; it is a choice among five, and four of
the five work.

**A rate limit enforced at the gateway is a correct program. An unenforced authorisation
stance is not.** FLOWX1032's honest offer — "confirm the flow is survivable with the policy
unenforced" — has no counterpart here. A capability that publishes a required grant and checks
none is not survivable-with-a-caveat; it is the security control failing open, and
[15 §1](../15-Security.md#1-security-posture)'s first row makes deny-by-default the property
the whole model rests on.

**`SafetyDiagnosticsAreErrorsRatherThanWarnings` holds this line for the rest of the security
set**, and FLOWX1010 and FLOWX1030 are both errors. A warning here would make the third rule
in one family the only one a team may leave on.

## Why this is not FLOWX1030, FLOWX1032 or FLOWX1010

| Rule | Asks |
|---|---|
| [FLOWX1010](FLOWX1010.md) | Was a stance declared **at all**? |
| [FLOWX1030](FLOWX1030.md) | Does a stance that needs a **name** have one? |
| **FLOWX1037** | Can the runtime **decide** the stance that was declared and named? |
| [FLOWX1032](FLOWX1032.md) | Is the **stage** a declared policy runs in implemented? |

The first three are a chain, and each presupposes the previous one passed:
`Authorization.Policy` with no `Policy = "…"` is FLOWX1030 and not this rule, because a stance
naming nothing is a different defect from one naming something nothing reads. FLOWX1032 is a
neighbour rather than a member: it reads a step's `.WithPolicy(...)` chain, and this reads a
capability's `[Capability]` attribute — different declaration, different file, opposite
severity.

## When this rule is deleted

**Deleted, not downgraded**, on [FLOWX1032](FLOWX1032.md)'s precedent: it describes a gap in
the platform, and a rule that outlives what it describes teaches people to suppress a
catalogue.

| Event | Action | Status |
|---|---|---|
| An evaluator abstraction the engine may call — declared in `FlowX.Abstractions`, implemented over `IAuthorizationService` by a host package — ships, and the step loop can reach a decision for this stance | Delete `FLOWX1037`, its descriptor, this page and the catalogue row | Outstanding |

`AuthorizationEnforcementTests` in `tests/FlowX.Runtime.Tests` is the executable half of this
reminder. `EveryStanceIsEitherEnforcedOrDiagnosed` enumerates `Authorization`'s five members
and asserts that each is either decided by the engine or named by this rule — so a sixth
stance, or this one becoming enforceable, turns it red rather than leaving a member silently
unhandled.

---

**Back to:** [diagnostics index](README.md) · [FLOWX1010](FLOWX1010.md) ·
[FLOWX1030](FLOWX1030.md) · [FLOWX1032](FLOWX1032.md) ·
[Security](../15-Security.md) ·
[ADR-0030](../adr/ADR-0030-policy-stance-is-refused-at-build-time.md)
