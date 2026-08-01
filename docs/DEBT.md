# Technical Debt Register

> **Policy:** [21-Quality-Gates §6](21-Quality-Gates.md#6-technical-debt-policy).
> **Budget:** ≤ 20 open entries · no entry older than 6 months · every entry has an owner and an expiry.
>
> **Open entries: 1**

---

## Why this file exists

"No technical debt" is not a state a real codebase reaches, and claiming it would
be the first lie in this repository. What *is* reachable: **no debt that is
undeclared, unowned or unexpiring.**

The register was empty until the reference sample needed the first suppression. It
now has one entry, with a date on it.

---

## Entry format

Every `[SuppressMessage]` in the codebase must carry a `FLOWX-DEBT` marker whose
`id` matches a row here. A `#pragma warning disable` must carry either the same
marker or a same-line reason — it silences a rule just as completely, and was not
covered by the gate until the sample used one. The `SuppressionsAreAccountable` gate fails
the build otherwise, and fails it again the day an `expires` date passes.

It runs in exactly one place: the `SuppressionsAreAccountable` fitness function in
`tests/FlowX.Architecture.Tests`. It fails on `dotnet test`, before the commit, and again
in CI, where the architecture gates run before the rest of the suite.

It used to run in two places. A shell step in `.github/workflows/quality.yml` asked a
weaker question — whether the *file* contained a `FLOWX-DEBT` marker anywhere — so one
accountable suppression at the top of a file licensed every unaccountable one below it,
and it never checked that the id cited had a row in the table below. A marker citing
`DEBT-0099` when no such row exists is accountable to nobody and reads as accountable to
everybody. The step was deleted rather than repaired: two implementations of one rule
disagree eventually, and the weaker one is what a developer meets first.

```csharp
// FLOWX-DEBT: id=DEBT-0042 owner=runtime expires=2026-12-31
//   Reason: <what would have to change for this to be removable>
//   Tracked by: #<issue>
[SuppressMessage("Sonar", "S3776:Cognitive Complexity", Justification = "DEBT-0042")]
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
| DEBT-0001 | `samples/ecommerce` | `FLOWX1024` suppressed on `PlaceOrderFlow`'s `.Emit<OrderPlaced>()`. The step is compiled into the plan and recorded in the manifest, so a consumer reading the manifest expects the event — and this flow does not publish it. *The reason changed again when `.Emit<T>()` reached the outbox, and this entry is now about a **choice rather than a gap**: the engine stages an emitted event in the step's own transaction and `PostgresOutboxPublisher` drains it, and `PlaceOrderFlow` is deliberately `Ephemeral`, so it keeps no transaction to stage into. The argument for that profile is on [`FLOWX1012`](diagnostics/FLOWX1012.md); the cost of it is this row.* | orders | 2026-07-30 | 2026-12-31 | `PlaceOrderFlow` declares `Profile = Durable` and `OrderPlaced` is added to `EcommerceJsonContext` — at which point the event is staged and published ([P8](03-Design-Principles.md#p8--event-native)). Then delete the pragma; the warning disappears on its own. Deciding **not** to make the sample durable is also a resolution, and closes this row by moving the argument into the sample's README rather than leaving a dated suppression. |

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
