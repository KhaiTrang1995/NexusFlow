# Technical Debt Register

> **Policy:** [21-Quality-Gates §6](21-Quality-Gates.md#6-technical-debt-policy).
> **Budget:** ≤ 20 open entries · no entry older than 6 months · every entry has an owner and an expiry.
>
> **Open entries: 0**

---

## Why this file exists and is empty

"No technical debt" is not a state a real codebase reaches, and claiming it would
be the first lie in this repository. What *is* reachable: **no debt that is
undeclared, unowned or unexpiring.**

This register is empty because the codebase currently contains no suppressions —
not because debt is forbidden. When the first one is needed, it goes here, with a
date on it.

---

## Entry format

Every `[SuppressMessage]` in the codebase must carry a `FLOWX-DEBT` marker whose
`id` matches a row here. The `SuppressionsAreAccountable` gate in
`.github/workflows/quality.yml` fails the build otherwise, and fails it again the
day an `expires` date passes.

```csharp
// FLOWX-DEBT: id=DEBT-0001 owner=runtime expires=2026-12-31
//   Reason: <what would have to change for this to be removable>
//   Tracked by: #<issue>
[SuppressMessage("Sonar", "S3776:Cognitive Complexity", Justification = "DEBT-0001")]
```

| Field | Rule |
|---|---|
| `id` | `DEBT-####`, allocated sequentially, never reused |
| `owner` | a **team**, never an individual — people change teams, debt does not |
| `expires` | ISO-8601, at most 6 months out; the build fails the day it passes |
| Reason | the condition under which the suppression becomes removable |

---

## Open entries

| ID | Area | Description | Owner | Created | Expires | Removable when |
|---|---|---|---|---|---|---|
| — | — | *none* | — | — | — | — |

---

## Closed entries

| ID | Description | Closed | How |
|---|---|---|---|
| — | *none* | — | — |

---

## What does not belong here

A documented trade-off recorded in an ADR is a **decision**, not debt. Ephemeral
flows losing state on a crash is [ADR-0003](adr/ADR-0003-execution-profiles.md),
not a debt entry. Mixing the two makes the register meaningless and the budget
unenforceable — at which point the whole mechanism is theatre.

If you are unsure which one you have: debt is something you would fix given time;
a decision is something you would make again.

---

**Back to:** [Quality gates](21-Quality-Gates.md) · [Checklist](../CHECKLIST.md)
