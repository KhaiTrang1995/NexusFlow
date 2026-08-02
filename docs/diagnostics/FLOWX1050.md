# FLOWX1050 — Step binds a contract only one of a poll's two endings produces

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0
> **Applies to:** a `.Step<TCapability>()` after a `.PollUntil<T>(…).OrSignal<TSignal>()`.

## What it means

`.OrSignal<TSignal>()` gives one wait a second way to end. The poll leaves it either because
its `until` predicate held over what an attempt produced, or because a delivery arrived and the
engine seeded `TSignal` into the state bag. **Both endings continue at the same index** — that
is what makes it one wait rather than a race — so every step after the poll runs on either.

A step binding `TSignal` therefore binds a value that is present on one of those paths and
absent on the other. On the delivery path it works. On the predicate path nothing produced it,
`ctx.Get<TSignal>()` throws, and the flow fails in the middle with an exception rather than an
error — on the path a poll exists for.

## Example that triggers it

```csharp
.PollUntil<CheckOcrStatus>(                     // produces OcrStatus, once per attempt
    until:    ctx => ctx.Get<OcrStatus>().IsTerminal,
    interval: Waits.OcrPolling,
    timeout:  Waits.OcrBudget)
    .OrSignal<OcrCompleted>()                   // produces OcrCompleted, only on a delivery

.Step<ExtractFromWebhook>()                     // ICapability<OcrCompleted, ExtractedFields>
```

```
error FLOWX1050: Step 'document.extract_webhook' consumes 'OcrCompleted', which flow
'ProcessDocumentFlow' produces only when a delivered signal ends the poll — not when its own
predicate does
```

## How to fix it

**Bind what both endings leave.** A poll only parks after an attempt has committed, so a
delivery to a parked instance arrives with the polled capability's own output already in the
bag — on both paths — and the signal's on one.

```csharp
.PollUntil<CheckOcrStatus>(
    until:    ctx => ctx.Get<OcrStatus>().IsTerminal,
    interval: Waits.OcrPolling,
    timeout:  Waits.OcrBudget)
    .OrSignal<OcrCompleted>()

.Step<ExtractFields>()                          // ICapability<OcrStatus, ExtractedFields>
```

Or supply the input explicitly, which is [`FLOWX1020`](FLOWX1020.md)'s second repair and works
here for the same reason — a mapping reads the context rather than binding a slot, so it can
decide what to do with an ending that produced no signal:

```csharp
.Step<ExtractFields, OcrStatus>(ctx => ctx.Get<OcrStatus>())
```

**What is *not* a repair is deleting the `.OrSignal`** to make the build pass, if the webhook is
genuinely the fast path the flow wants. The rule is about the step after the poll, not about the
second ending.

## The one it does not report

A step binding the **polled capability's** output after an `.OrSignal` compiles and runs on both
paths — but on the delivery path that value is the *last attempt's* answer, which is by
definition the one the predicate said was not terminal yet. That is a semantic question about
what the two endings mean, not a binding this compiler can settle, so it is not reported. Say it
in the contract the signal carries: a delivery that means "the answer is ready" and a poll that
means "the answer is ready" should leave the flow able to read the answer either way.

## When to suppress

None. Every suppression here is a flow that throws on its ordinary path.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A suppression
without one fails the build.

Suppressing it leaves nothing behind that will catch the effect: the plan records that the poll
has two endings and nothing records which contracts each one leaves, because the state bag is a
runtime object.

---

**Back to:** [diagnostics index](README.md) ·
[ADR-0066](../adr/ADR-0066-a-polls-second-ending-is-a-row.md) ·
[ADR-0058](../adr/ADR-0058-a-poll-is-one-wait-not-a-race-between-two.md) ·
[FLOWX1020](FLOWX1020.md)
