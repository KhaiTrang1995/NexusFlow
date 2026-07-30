# FLOWX1024 — Emit step is recorded but not published

> **Severity:** Warning · **Category:** FlowX · **Since:** 0.1.0

## What it means

`.Emit<TEvent>(...)` compiles into a real step: it appears in the flow's
`ExecutionPlan`, and it appears in `flowx.manifest.json` as a published event.
Anyone reading that manifest — a consumer team, `flowx diff`, a generated
contract — will reasonably conclude the event is published.

In this release it is not. Transactional outbox publication
([design principle P8](../03-Design-Principles.md#p8--event-native)) is not
implemented, so the generated dispatcher records the step and does nothing else.

This is the only FlowX diagnostic with `Warning` severity. The gap it reports is
between what the manifest promises and what the process does, and that is a
class of defect that is otherwise found in production, by the consumer who was
waiting.

## Example that triggers it

```csharp
protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) => flow
    .Step<ValidateOrder>()
    .Emit<OrderPlaced>(ctx => new OrderPlaced(...))   // FLOWX1024
    .Return(ctx => new OrderPlacedResult(...));
```

## How to fix it

There is no fix yet — the feature it waits on does not exist. Your options are:

- **Remove the `.Emit`** until the outbox ships, if a consumer would otherwise be
  built against an event that never arrives. This is the honest default.
- **Publish the event from a capability** in the meantime, accepting that it is
  outside the flow's transaction and can be lost.
- **Suppress it**, if nothing downstream depends on delivery yet and you want the
  declaration in place for when the outbox arrives.

## When to suppress

When the event has no consumer, and the `.Emit` is there to declare intent.
Scope the suppression to the single step, never the file or the project:

```csharp
#pragma warning disable FLOWX1024 // FLOWX-DEBT(orders, 2026-12-31): no consumer yet;
                                  //   remove once the outbox lands.
    .Emit<OrderPlaced>(ctx => new OrderPlaced(...))
#pragma warning restore FLOWX1024
```

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Quality gates](../21-Quality-Gates.md)
