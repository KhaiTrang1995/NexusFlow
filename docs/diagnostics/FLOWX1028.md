# FLOWX1028 — Step input mapping produces the wrong contract

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

## What it means

`.Step<TCapability, TStepIn>(map)` supplies a step's input from a lambda instead of
binding it out of the flow's state bag. The generated dispatcher hands the lambda's result
straight to the capability:

```csharp
var result = await _capturePayment
    .ExecuteAsync(StepInputs.Step1(Typed(ctx)), ctx, ct)
    .ConfigureAwait(false);
```

So `TStepIn` has to be the capability's declared input contract, or something implicitly
convertible to it. **C# does not require that.** `TStepIn` is inferred from the lambda and
constrained to nothing, so `.Step<CapturePayment, string>(ctx => "x")` is a legal call even
though `CapturePayment` implements `ICapability<CaptureRequest, Payment>`. Without this
rule the mistake surfaces as a `CS1503` inside generated source — a compile error in a file
the developer did not write, about a call they cannot see.

This is checked with **assignability**, not exact type identity — the opposite of
[FLOWX1020](FLOWX1020.md), deliberately. That rule asks what a `Dictionary<Type, object>`
lookup finds, and a lookup is exact. This one asks what a C# argument accepts, and an
argument takes anything implicitly convertible to it, so mapping to a derived type is fine.

## Example that triggers it

```csharp
[Capability("payment.capture", Version = "2.1.0", Authorization = Authorization.Permission)]
public sealed class CapturePayment : ICapability<CaptureRequest, Payment> { /* … */ }

[Flow("order.place")]
public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
{
    protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
        flow.Step<ValidateOrder>()
            // The mapping produces a ValidatedOrder; the capability consumes a CaptureRequest.
            .Step<CapturePayment, ValidatedOrder>(ctx => ctx.Get<ValidatedOrder>())
            .Return(ctx => new OrderPlacedResult("id", "receipt"));
}
```

```
error FLOWX1028: Step 'payment.capture' in flow 'PlaceOrderFlow' maps its input to
                 'Ecommerce.ValidatedOrder', which the capability cannot accept — it
                 consumes 'Ecommerce.CaptureRequest'
```

## How to fix it

Build the contract the capability actually declares:

```csharp
.Step<CapturePayment, CaptureRequest>(ctx => new CaptureRequest(
    ctx.Get<ValidatedOrder>().Sku,
    ctx.Get<ValidatedOrder>().Total,
    ctx.Input.PaymentToken))
```

Naming the second type argument explicitly — rather than letting it be inferred from
whatever the lambda happens to return — is worth doing for its own sake. With it written
down, a mismatch between the lambda body and the contract is an ordinary C# error on the
author's own line, which is a better message than this one.

If the shapes are the same and the mapping was only there to work around
[FLOWX1020](FLOWX1020.md), the answer may be to reorder the steps instead: the producer
goes first and the mapping disappears.

## What it detects

`FlowAnalyzer` reads the `TStepIn` that C# inferred — the resolved method's second type
argument, not a second opinion formed from the lambda body — and classifies the conversion
to the `TIn` of the capability's `ICapability<TIn, TOut>`. The mapping is refused when the
conversion does not exist or is not implicit.

Deliberate limits:

- **A capability with no readable contract is not checked.** Zero `ICapability<,>` is
  [FLOWX1002](FLOWX1002.md)'s business and more than one is [FLOWX1015](FLOWX1015.md)'s;
  in both cases this rule has no defensible answer and gives none.
- **A call that does not bind is not checked.** An unresolved type argument means the file
  does not compile, and the C# compiler is already saying something more useful.
- **A refused mapping is not emitted.** The step is dropped rather than compiled with an
  input it cannot accept, so a flow that cannot build does not also produce a dispatcher
  that cannot build. One message about the author's line beats that message plus a
  `CS1503` in generated code.

## When to suppress

There is no legitimate case. The rule reports exactly the situation in which the generated
call would not compile, so suppressing it converts one build error into a different, worse
build error. If it fires on something you believe is valid, that is a bug in the rule and
worth reporting.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Quality gates](../21-Quality-Gates.md)
