# FLOWX1004 — Capability invokes another capability

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

> **Not raised yet.** This rule inspects a capability's body — its assembly references
> and its call graph — which the flow generator never looks at. It needs a separate
> `DiagnosticAnalyzer`, which does not exist. The rule below is the intended behaviour
> and a convention worth following; nothing enforces it today. Tracked as **WP-13** in
> [PLAN.md](../../PLAN.md).

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

## When to suppress

A capability may call any number of *infrastructure* services. The rule is about capabilities, not about dependencies.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Quality gates](../21-Quality-Gates.md)
