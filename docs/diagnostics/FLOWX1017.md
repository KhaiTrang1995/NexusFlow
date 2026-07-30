# FLOWX1017 — AwaitSignal requires the Durable profile

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

## What it means

A suspension point must survive a deployment, a crash and a scale-in. Under the `Ephemeral` profile there is no journal, so a waiting flow simply disappears when its node does.

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

## When to suppress

None. A durable suspension costs one database row and no thread, no memory and no lease while it waits — the cost is the journal, not the waiting.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Quality gates](../21-Quality-Gates.md)
