# FLOWX1015 — Capability implements more than one contract

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

## What it means

A capability has exactly one input type and one output type. Multiple `ICapability<,>` implementations make the dispatch ambiguous and the manifest entry meaningless.

## Example that triggers it

```csharp
public sealed class OrderService
    : ICapability<PlaceOrder, Receipt>, ICapability<CancelOrder, Cancellation>
```

## How to fix it

```csharp
// One capability per business operation:
public sealed class PlaceOrder : ICapability<PlaceOrderRequest, Receipt> { }
public sealed class CancelOrder : ICapability<CancelOrderRequest, Cancellation> { }
```

## When to suppress

None. A type implementing two contracts is two capabilities that have not been separated yet.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Quality gates](../21-Quality-Gates.md)
