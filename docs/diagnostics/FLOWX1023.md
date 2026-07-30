# FLOWX1023 — Flow declares no steps

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

## What it means

An empty flow has no observable behaviour. In practice this is almost always a `Define` method that returned early, or a chain whose result was never used.

## Example that triggers it

```csharp
protected override void Define(IFlowBuilder<In, Out> flow)
{
    // chain built but never applied to `flow`
}
```

## How to fix it

```csharp
protected override void Define(IFlowBuilder<In, Out> flow) => flow
    .Step<ValidateOrder>()
    .Return(ctx => new Out(...));
```

## When to suppress

None.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Quality gates](../21-Quality-Gates.md)
