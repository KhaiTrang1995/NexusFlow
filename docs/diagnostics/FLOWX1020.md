# FLOWX1020 — Step consumes a contract no earlier step produces

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

## What it means

Steps do not pass values positionally. Each step's output goes into the flow's state bag under its own type, and the next step's input is read back out by type — the generated dispatcher writes `ctx.Get<TIn>()` for every step. If nothing before that step ever put a `TIn` into the bag, the lookup throws on the first request the flow serves. The flow compiles, deploys, and then fails for everybody. This rule is quality requirement QR3: adding a step with an incompatible contract fails the *build*.

The bag is keyed on the **exact declared type**. A step that produces a `Derived` does not satisfy a step that consumes its `Base` — the dictionary lookup misses, so the rule reports it.

## Example that triggers it

```csharp
[Flow("order.place")]
public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
{
    protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
        flow.Step<ReserveInventory>()   // consumes ValidatedOrder — nobody has made one yet
            .Step<ValidateOrder>();     // produces ValidatedOrder, one step too late
}
```

```
error FLOWX1020: Step 'ReserveInventory' consumes 'ValidatedOrder', which nothing before
                 it in flow 'PlaceOrderFlow' produces; the context can supply: PlaceOrder
```

## How to fix it

```csharp
// Put the producer first — the bag is never cleared, so every later step can read it:
flow.Step<ValidateOrder>()
    .Step<ReserveInventory>()
    .Step<CapturePayment>()
```

```csharp
// Or, when the shapes genuinely differ, supply the input yourself:
.Step<CapturePayment, CaptureRequest>(ctx => new CaptureRequest(
    ctx.Get<ValidatedOrder>().Sku,
    ctx.Input.PaymentMethod))
```

The mapping runs at the step and its result *is* the step's input. It is not written back
into the state bag — the bag is keyed on `typeof(T)` and a mapping exists precisely because
nothing put a `CaptureRequest` there — so a mapped step supplies only itself, and two
mapped steps of the same contract in one flow do not interfere. A mapping whose result the
capability cannot accept is [FLOWX1029](FLOWX1029.md).

## What it detects

`StepBindingAnalyzer` walks the `Define` chain in declaration order, seeding
the flow's own input contract — which the engine puts into the bag before step 1 — and
adding each capability's declared output as it goes. A step is reported when its declared
input is in neither set.

Deliberate limits, so the rule does not fire on a valid flow:

- **Compensations are not checked.** A compensation does not bind its own input; the
  dispatcher hands it the input of the step it undoes, which is the value it has to
  reverse. Its declared input never reaches a `ctx.Get`. For the same reason it produces
  nothing — it runs only on the failure path, after which no later step runs.
- **A mapped step is not checked as a consumer.** `.Step<TCapability, TStepIn>(ctx => …)`
  supplies its own input from a lambda that the dispatcher runs at the step, so its
  declared input never reaches a `ctx.Get` and there is nothing for this rule to be missing.
  It still counts as a producer, because its output goes into the bag exactly as any other
  step's does. This silence is only defensible while the overload really works — for the
  release in which the mapping was walked and then ignored, this rule was suppressing
  itself for a remedy that did nothing.
- **The walk stops at the first construct it cannot linearise** — `When`, `Parallel`,
  `ForEach`, `SubFlow`, or a DSL method added after this rule. Steps before it are still
  checked; everything after is abandoned, because a branch may have produced the very
  type the next step wants.
- **A step whose capability contract cannot be resolved abandons the whole flow.** An
  unresolved symbol, an open type parameter, or more than one `ICapability<,>`
  ([FLOWX1015](FLOWX1015.md)) makes every later step unknowable too.
- **`AwaitSignal<TSignal>` counts as producing its signal**, which is what a suspension
  means, even though nothing delivers a signal in this release.

## When to suppress

If a value reaches the bag by a route the compiler cannot see — a capability calling
`ctx.Set` for something it cannot return — this rule will report the consumer and be
wrong about it. That is the one legitimate case, and it is worth asking whether the value
should be a step output instead: a bag entry nothing declares is invisible to the
manifest and to `flowx diff` as well as to this rule.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Quality gates](../21-Quality-Gates.md)
