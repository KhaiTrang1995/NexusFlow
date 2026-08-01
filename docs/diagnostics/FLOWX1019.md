# FLOWX1019 — Flow deadline is shorter than the step timeouts it must contain

> **Severity:** Warning · **Category:** FlowX · **Since:** 0.1.0

## What it means

`[FlowDeadline]` is the flow's **absolute** budget. It is set when the flow is triggered
and, as [10 §5](../10-Policy-Framework.md#5-retry-safety) states, "a retry never outlives
the deadline" — the clock is not reset by an attempt, a step or a compensation.

A step's `Timeout` policy bounds **one attempt**; its `Retry` policy multiplies the
attempts. So the worst case a flow can spend is at least

```
Σ over steps ( timeout × attempts )
```

and when that already exceeds the deadline the flow cannot complete on a bad day: the
budget runs out mid-way and every step after that one never runs. What an operator sees is
a cancelled flow several steps away from the policy that spent the budget, which is why
[14 §7](../14-Performance.md#7-application-level-guidance) calls it "arithmetically
incoherent" and why `FlowDeadlineAttribute` names this id for "the classic *3 retries ×
30 s timeout inside a 10 s SLA*".

## The number it reports is a floor

Every case this rule cannot read is counted as **zero**, so the figure it prints is a lower
bound and the true worst case is always larger. That is deliberate: a rule about arithmetic
that fired on a flow which actually fits would be suppressed, and a suppressed rule
protects nothing. The consequence is that **silence is not a statement that a flow fits**.

| Not counted | Why |
|---|---|
| Steps inside a `When`, `Switch`, `ForEach`, `Parallel` or `SubFlow` block | They run on some paths and not others, and a `Parallel` overlaps rather than adds. Only unconditional top-level steps are summed |
| A step with no `.WithPolicy(...)` | There is no platform default step timeout in this release — `CapabilityAttribute` carries no `Timeout` member — so such a step is genuinely unbounded, and this rule will not put a number on unbounded |
| A policy set that is not a field or property with an initialiser in source | The same restriction `PolicySetReader` works under: a set built at run time has no compile-time contents. [FLOWX1036](FLOWX1036.md) reports the case rather than leaving it silent, and `PolicySet`'s own well-known sets — `PolicySet.CompensationDefault` — are read from metadata and are not one of them, though neither declares a `Timeout` for this rule to count |
| A set with a `Retry` and no `Timeout` | Unbounded, not free |
| A timeout not written as `TimeSpan.From…(literal)` | `new TimeSpan(0, 0, 30)`, a `const`, an arithmetic expression and `TimeSpan.Parse` all read as unknown. The duration is read from syntax rather than from a symbol, because the policy set is usually declared in another file and Roslyn's RS1030 forbids an analyzer from binding a second syntax tree |
| Retry backoff delays | Real elapsed time against the same budget, and not counted |
| A second `.WithPolicy(...)` on the same step | Which set wins is a resolution question this rule has no answer to; counting both would be the one way to *overstate* the total. *The answer, since [FLOWX1034](FLOWX1034.md), is that the later call wins outright and the earlier set reaches neither the plan nor the manifest — so this rule declining to count it is right, and the shape is now an error in its own right* |

`Retry(attempts)` is read as the **total** number of attempts — the smaller of the two
readings the parameter name allows. If it turns out to mean retries-after-the-first, every
figure here is short by exactly one timeout per step, and still a floor.

## Why a warning and not an error

Both documents that describe this id say *warns*, and that is also the right answer. The
sum is the **worst** case, not the expected one: a retry only happens when an attempt
fails, so an incoherent budget describes a flow that is correct until the day its
dependency is slow. A deliberately pessimistic timeout under a tight deadline is a
legitimate thing to declare — "give up rather than keep the caller waiting" is a decision,
not a mistake. The [index](README.md#why-these-are-errors-not-warnings)'s bar for an error
is a structural impossibility, and this is not one.

This repository builds with `TreatWarningsAsErrors`, so it does stop the build here.

## Example that triggers it

```csharp
public static class Policies
{
    public static readonly PolicySet PaymentGateway = PolicySet.Named("payment-gateway")
        .Timeout(TimeSpan.FromSeconds(30))
        .Retry(attempts: 3);
}

[Flow("order.place", Version = "1.0.0")]
[FlowDeadline("PT10S")]          // FLOWX1019 is reported here
public sealed partial class PlaceOrderFlow : Flow<PlaceOrder, OrderPlacedResult>
{
    protected override void Define(IFlowBuilder<PlaceOrder, OrderPlacedResult> flow) =>
        flow
            .Step<CapturePayment>().WithPolicy(Policies.PaymentGateway)
            .Return(ctx => new OrderPlacedResult(ctx.Get<Payment>().ReceiptId));
}
```

```
FLOWX1019: Flow 'PlaceOrderFlow' declares a deadline of 10s, but its steps can spend
           at least 90s before it: CapturePayment 3×30s
```

The message writes the arithmetic out step by step, because the useful thing is never that
the sum is too large — it is which step's policy to change.

## How to fix it

Pick whichever of the four is true of your operation:

```csharp
// 1. The deadline was wrong. Three attempts at 30 s needs a minute and a half.
[FlowDeadline("PT120S")]

// 2. The step budget was wrong. A payment gateway that has not answered in 3 s
//    is not going to.
.Timeout(TimeSpan.FromSeconds(3)).Retry(attempts: 3)

// 3. The retries were wrong. Under a tight SLA, one retry is the whole budget.
.Timeout(TimeSpan.FromSeconds(3)).Retry(attempts: 2)

// 4. The work does not belong under this deadline. Split the slow half into its
//    own flow with a budget of its own and compose it detached.
.SubFlow<SettlePaymentFlow, SettleRequest>(ctx => …, SubFlowMode.Detached)
```

Do not silence it by removing the `Timeout` policy: an unbounded step under an absolute
deadline is the same flow with the arithmetic hidden.

## When to suppress

When the pessimistic budget is the point — a flow that should try hard and be cut off by
the deadline rather than keep a caller waiting, where the deadline *is* the intended stop.
Record that where the deadline is declared:

```csharp
#pragma warning disable FLOWX1019 // FLOWX-DEBT: id=… owner=… expires=…
// The retries are for a dependency that recovers in seconds; the 10 s deadline is
// the intended cut-off, and losing the last attempt to it is the accepted outcome.
[FlowDeadline("PT10S")]
#pragma warning restore FLOWX1019
```

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Policy framework](../10-Policy-Framework.md) · [Performance](../14-Performance.md)
