# FLOWX1005 — Flow inherits from another flow

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

## What it means

Inheritance hides control flow. A reader of the derived flow cannot see which steps run, and neither can the manifest, `flowx graph` or impact analysis.

## Example that triggers it

```csharp
[Flow("order.place.express")]
public sealed partial class ExpressOrderFlow : PlaceOrderFlow   // inherits a flow
```

## How to fix it

```csharp
// Extract the shared part into a sub-flow and compose it explicitly:
flow.SubFlow<SharedValidationFlow, ValidationInput>(ctx => ...)
    .Step<ExpressRouting>()
```

## When to suppress

None. If two flows share steps, the shared part is a sub-flow, and saying so keeps it in the graph.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Quality gates](../21-Quality-Gates.md)
