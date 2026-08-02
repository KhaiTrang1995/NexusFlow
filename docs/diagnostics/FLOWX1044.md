# FLOWX1044 — `PollUntil` requires an idempotent capability

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0
> **Applies to:** the capability named by `.PollUntil<TCapability>(...)`.

## What it means

A poll invokes its capability **once per attempt** — with one request's worth of input, one
idempotency key, and no upper bound on the number of attempts short of the budget the author
declared — until a condition holds. That is precisely the repetition `Idempotent = true`
declares to be safe.

It is [`FLOWX1014`](FLOWX1014.md)'s argument reached by a different door, and the stronger of
the two cases. A retry repeats *after a failure*, which is rare and is already an incident. A
poll repeats *after every success*: the call worked, the answer was "not yet", and the flow is
going to ask again in five seconds and fifty more times after that. A capability that starts a
job, reserves stock or charges a card is not a capability to ask that of.

## Example that triggers it

```csharp
[Capability("ocr.submit", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "document.write")]
//  Idempotent defaults to false, and submitting twice is two jobs and two bills.
public sealed class SubmitToOcr : ICapability<Document, OcrJob> { ... }

flow.PollUntil<SubmitToOcr>(
    until:    ctx => ctx.Get<OcrJob>().IsFinished,
    interval: Backoff.Exponential("PT5S", "PT5M"),
    timeout:  TimeSpan.FromHours(4))
```

```
error FLOWX1044: Capability 'ocr.submit' does not declare Idempotent = true, so it cannot be
polled
```

## How to fix it

Two repairs, and which one is right is a question about the capability rather than about the
flow.

```csharp
// Either the capability really is a read, and says so —
[Capability("ocr.status", Version = "1.0.0",
    Authorization = Authorization.Permission, Permission = "document.read",
    Idempotent = true)]
public sealed class CheckOcrStatus : ICapability<OcrJob, OcrStatus> { ... }
```

```csharp
// — or it is not, and the flow needs two capabilities rather than one attribute.
flow.Step<SubmitToOcr>().CompensateWith<CancelOcrJob>()
    .PollUntil<CheckOcrStatus>(
        until:    ctx => ctx.Get<OcrStatus>().IsTerminal,
        interval: Backoff.Exponential("PT5S", "PT5M"),
        timeout:  TimeSpan.FromHours(4))
```

The second is the shape `samples/polling` ships, and the split is not bureaucratic: the thing
that starts the work is the compensable step, and the thing that asks about it is the one that
runs fifty times.

**Adding `Idempotent = true` to make the build pass is the wrong repair**, and it is worth
saying plainly because it is one keystroke. The attribute is a claim about the capability that
`FLOWX1014` also reads, that `PolicyChain` refuses to arm a retry without, and that the runtime
does not verify. Declaring it falsely does not make the second OCR job go away; it removes the
last thing that was going to mention it.

## When to suppress

None. `CapabilityContext.IdempotencyKey` is stable across attempts and replays; pass it
downstream so the provider deduplicates, then declare `Idempotent = true` honestly — which is
[`FLOWX1014`](FLOWX1014.md)'s answer to the same question, for the same reason.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A suppression
without one fails the build.

Suppressing it leaves nothing behind that will catch the effect: the plan carries no claim about
how many times a poll's body may run, because the answer is "as many as the budget allows".

---

**Back to:** [diagnostics index](README.md) ·
[ADR-0058](../adr/ADR-0058-a-poll-is-one-wait-not-a-race-between-two.md) ·
[FLOWX1014](FLOWX1014.md)
