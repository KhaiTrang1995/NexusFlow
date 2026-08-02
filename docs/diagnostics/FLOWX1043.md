# FLOWX1043 — Poll interval outlasts the poll's own timeout

> **Severity:** Warning · **Category:** FlowX · **Since:** 0.1.0
> **Applies to:** the `interval:` and `timeout:` arguments of `.PollUntil<TCapability>(...)`.

## What it means

A poll runs its first attempt **immediately** and then parks for the interval before the second.
If that first gap is longer than the budget, the instance wakes after the budget has already
gone and takes the `.OnTimeout` block — so a declaration that reads as *"check every ten minutes
for five minutes"* checks **once** and gives up.

What makes it worth a rule is that nothing about the instance says so. One committed attempt
followed by an escalation is exactly what a journal shows for a dependency that genuinely never
answered, and an operator reading the rows has no way to tell the two apart. The declaration is
the only place the difference is visible, which is where this reports.

## Example that triggers it

```csharp
flow.PollUntil<CheckOcrStatus>(
    until:    ctx => ctx.Get<OcrStatus>().IsTerminal,
    interval: Backoff.Exponential("PT10M", "PT30M"),
    timeout:  TimeSpan.FromMinutes(5))
```

```
warning FLOWX1043: Polling 'ocr.status' waits PT10M between attempts and gives up after PT5M,
so it makes one attempt and then escalates
```

## How to fix it

Decide which of the two numbers is the one you meant.

```csharp
// Either the budget is the real number and the schedule was too coarse —
interval: Backoff.Exponential("PT30S", "PT2M"),
timeout:  TimeSpan.FromMinutes(5)

// — or the schedule is right and the budget has to fit the attempts you intended.
interval: Backoff.Exponential("PT10M", "PT30M"),
timeout:  TimeSpan.FromHours(4)
```

If one attempt and a fallback really is what you want, write it as one: a `.Step<T>()`, a
`.When(...)` on what it produced, and the escalation in the `Otherwise` block. That flow says
what it does, and it costs no suspension at all.

## When it stays silent

Whenever either duration is an expression the compiler cannot evaluate — a schedule held in a
variable, built by a helper, or read from configuration. That is the stance every folding rule
in this compiler takes and the one [`FLOWX1019`](FLOWX1019.md) takes on the same kind of
arithmetic: a guess would fire on flows that are correct at run time, and a build gate that
fires on correct code is a build gate somebody suppresses at the top of the file.

The consequence is worth stating rather than leaving to be discovered: moving a schedule into a
constant turns this rule off for that call site. The rule protects the common form and does not
claim to protect every form.

## Why a warning

The flow runs. Both durations are legal, the poll makes its attempt, the escalation runs, and
the instance reaches a recorded outcome — so refusing the build would refuse a flow that works.
That is [`FLOWX1019`](FLOWX1019.md)'s argument about a deadline its own steps cannot fit inside,
and this is the same shape of finding: two declared durations that cannot both mean what they
appear to.

[`FLOWX1044`](FLOWX1044.md) is an error beside it, and the difference is what the two produce. A
schedule that disagrees with a budget produces a flow that runs and reads oddly; polling
something that is not repeatable produces a flow that runs correctly the first time and creates
a second job on the second attempt.

## When to suppress

When one attempt and a fallback is genuinely the intent and the poll is being kept for a budget
that is about to change. Any suppression must carry a `FLOWX-DEBT` marker with an owner and an
expiry — see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) ·
[ADR-0058](../adr/ADR-0058-a-poll-is-one-wait-not-a-race-between-two.md) ·
[FLOWX1019](FLOWX1019.md)
