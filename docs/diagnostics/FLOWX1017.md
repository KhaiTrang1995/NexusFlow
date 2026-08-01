# FLOWX1017 — AwaitSignal requires the Durable profile

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

## What it means

A suspension point must survive a deployment, a crash and a scale-in. Under the `Ephemeral` profile there is no journal, so a waiting flow simply disappears when its node does.

> [!NOTE]
> **This rule's fix produces a working flow, as of 2026-08-01.** For two phases it did not:
> [`FLOWX1031`](FLOWX1031.md) was an error on `AwaitSignal` under *every* profile, `Durable`
> included, because nothing implemented suspension — so this rule's quick action cleared one
> error and raised another, and `AwaitSignal` had no profile it could legally declare. WP-63
> made a `Durable` flow suspend at its suspension point and resume when the signal is
> delivered, and narrowed `FLOWX1031` off `AwaitSignal`. Nothing about *this* rule changed;
> what changed is that its answer is now worth applying.

## Example that triggers it

```csharp
[Flow("order.place")]                       // Ephemeral by default
...
flow.AwaitSignal<PaymentConfirmed>(TimeSpan.FromHours(1))
```

## How to fix it

```csharp
[Flow("order.place", Profile = ExecutionProfile.Durable)]
[FlowDeadline("P30D")]
```

The deadline is not decoration and the quick action deliberately does not write one. A flow
that waits for a person needs longer than thirty seconds, and `[FlowDeadline]` is the **only**
timeout this release enforces on a wait: the duration declared on `.AwaitSignal<T>(timeout)`
reaches the plan and nothing arms it, because there is no timer
([FLOWX1031](FLOWX1031.md#what-is-still-owed)). An instance whose budget has gone times out at
its next step boundary rather than waiting for a signal it can no longer act on.

## When to suppress

None. A durable suspension costs one database row and no thread, no memory and no lease while it waits — the cost is the journal, not the waiting.

That sentence was an argument about a design until WP-63; it is now a description.
`samples/workflow`'s `offer.accept` sends an offer out, suspends, gives its lease back and
leaves one committed row, and a countersignature days later resumes it through the same step
loop a recovery scan uses. A waiting instance is also not in any recovery scan's candidate
set — both shipped indexes list `Pending`, `Running` and `Compensating` only — so it is not
swept up and re-suspended every lease TTL.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Quality gates](../21-Quality-Gates.md)
