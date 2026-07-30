# FLOWX1004 — Capability invokes another capability

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

## What it means

Capabilities form a **set**, not a graph. That is what lets the architecture be analysed statically, each capability be tested without a host, and the manifest describe the whole application. A capability calling another turns the set back into the call graph FlowX exists to replace.

## Example that triggers it

```csharp
public sealed class PlaceOrder : ICapability<Order, Receipt>
{
    private readonly ReserveInventory _reserve;   // capability calling capability
```

## How to fix it

```csharp
// Compose in a flow, where the composition is visible to the graph:
flow.Step<ReserveInventory>()
    .Step<CapturePayment>()
```

## What it detects

`CapabilityAnalyzer` reports a capability whose constructor parameters, fields or
properties name another type implementing `ICapability<,>`. Generic wrappers are
unwrapped, so a `Lazy<ReserveInventory>` does not slip past.

The generated step dispatcher legitimately holds every capability its flow invokes —
that is its job — and is exempt because generated code is not analysed.

## When to suppress

A capability may call any number of *infrastructure* services. The rule is about capabilities, not about dependencies.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Quality gates](../21-Quality-Gates.md)
