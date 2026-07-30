# FLOWX1018 — Cache requires a capability with no side effects

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

## What it means

A cache hit returns a success **without performing the effect**. On a capability that writes, that means the caller is told the write happened when it did not.

## Example that triggers it

```csharp
[Capability("inventory.reserve", Version = "1.0.0",
    Authorization = Authorization.Authenticated,
    SideEffects = new[] { "inventory-ledger" })]
...
flow.Step<ReserveInventory>().WithPolicy(Policies.Cached)
```

## How to fix it

```csharp
// Split the read out, and cache only the read:
flow.Step<ReadInventoryLevel>().WithPolicy(Policies.Cached)
    .Step<ReserveInventory>()
```

## When to suppress

None. If a capability genuinely has no side effects, remove them from its declaration and the policy becomes legal.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Quality gates](../21-Quality-Gates.md)
