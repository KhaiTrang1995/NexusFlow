# FLOWX1027 — Step is unreachable after Fail

> **Severity:** Warning · **Category:** FlowX · **Since:** 0.1.0

## What it means

`.Fail(error)` is **terminal**. Reaching it ends the flow with the business error
you declared: the engine takes the failure path, the compensable steps that
already completed unwind in strict reverse, and control never returns to the
block. A step written after one in the same block therefore cannot run — not
sometimes, not under some input, ever.

The compiler does not compile those steps. Laying them out would put them in the
`ExecutionPlan`, in `flowx.manifest.json` and in a rendered diagram, where a
reviewer, `flowx diff` or an agent reading the published contract would believe
the flow does work it can never do — and
[a manifest that describes work that does not happen](../adr/ADR-0005-manifest-as-build-artifact.md)
is worse than one that is missing something. Dropping them *silently* would be
worse still, which is what this diagnostic is for.

This is the same rule and the same severity as C#'s own `CS0162`, "unreachable
code detected". The source is not wrong; part of it simply has no effect.

## Example that triggers it

```csharp
protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) => flow
    .Step<ValidateOrder>()
    .Fail(OrderErrors.UnsupportedChannel)
    .Step<CapturePayment>()                       // FLOWX1027 — never runs
    .Return(ctx => new OrderPlacedResult(...));
```

`.Return(...)` is deliberately **not** reported. It declares no step: it is the
flow's output projection, it is read off the chain rather than laid out, and the
engine already does not run it when the flow failed. It is also the only way to
spell a flow that always rejects, so reporting it would fire on the one shape
that has to type-check.

## How to fix it

A `.Fail(...)` at the top level of a chain is almost always meant to be inside a
branch. That is what the construct is for — stating that *one* path is a
rejection:

```csharp
flow.Switch(ctx => ctx.Get<ValidatedOrder>().Channel)
    .Case(Channel.Retail,    b => b.Step<ApplyRetailPricing>())
    .Case(Channel.Wholesale, b => b.Step<ApplyWholesalePricing>())
    .Default(b => b.Fail(OrderErrors.UnsupportedChannel))
    .Step<CapturePayment>();                      // reached by the two cases
```

The `Default` arm ends the flow; the two `Case` arms fall through to
`CapturePayment` as they always did. Nothing is unreachable, so nothing is
reported.

If the steps really do belong before the rejection, move them above it — a
`.Fail(...)` after a compensable step is a supported and useful shape, and what
that step did will be undone.

## When to suppress

Effectively never — an unreachable step is not a trade-off, it is a step that
does nothing. The one case worth the suppression is a flow being edited in place,
where a `.Fail(...)` has been dropped in temporarily to stop a chain short while
the steps below it are being worked on.

```csharp
#pragma warning disable FLOWX1027 // FLOWX-DEBT(orders, 2026-12-31): short-circuited
                                  //   while the pricing rework lands.
    .Fail(OrderErrors.UnsupportedChannel)
    .Step<CapturePayment>()
#pragma warning restore FLOWX1027
```

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build. Note that suppressing the warning does
**not** compile the steps back in: they are still unreachable, and the plan and
the manifest still omit them.

---

**Back to:** [diagnostics index](README.md) · [08 — Flow Definition](../08-Flow-Definition.md)
