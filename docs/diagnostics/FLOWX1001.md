# FLOWX1001 — Flow must be partial

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

## What it means

The generator emits your flow's execution plan and step dispatcher as a **second part** of the same class. Without `partial` there is nowhere to put them.

## Example that triggers it

```csharp
[Flow("order.place")]
public sealed class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>   // no 'partial'
```

## How to fix it

```csharp
[Flow("order.place")]
public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderResult>
```

## When to suppress

None. The generated code has to live somewhere, and a second file for a type you already own is the least surprising place.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Quality gates](../21-Quality-Gates.md)
