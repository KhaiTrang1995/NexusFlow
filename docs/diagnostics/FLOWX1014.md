# FLOWX1014 — Retry requires an idempotent capability

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

## What it means

Retrying a non-idempotent operation duplicates its effect. For a payment capture that is a duplicate charge — the single most expensive bug this platform can prevent structurally.

## Example that triggers it

```csharp
[Capability("payment.capture", Version = "2.1.0",
    Authorization = Authorization.Permission)]   // Idempotent defaults to false
...
flow.Step<CapturePayment>().WithPolicy(Policies.WithRetry)
```

## How to fix it

```csharp
// Either make the capability idempotent and declare it —
[Capability("payment.capture", Version = "2.1.0",
    Authorization = Authorization.Permission, Idempotent = true)]

// — or handle the failure in the flow rather than retrying it.
```

## When to suppress

None. `CapabilityContext.IdempotencyKey` is stable across retries and replays; pass it downstream so the provider can deduplicate, then declare `Idempotent = true` honestly.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Quality gates](../21-Quality-Gates.md)
