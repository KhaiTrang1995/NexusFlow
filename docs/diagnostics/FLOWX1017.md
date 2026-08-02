# FLOWX1017 — AwaitSignal requires the Durable profile

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0

## What it means

A wait must survive a deployment, a crash and a scale-in. Under the `Ephemeral` profile there
is no journal, so a waiting flow simply disappears when its node does — and a timer has
nowhere to record when it is due, which leaves holding the process for the duration as the
only way to honour it.

> [!NOTE]
> **The rule asks "does this journal?", not "is this `Durable`?", from 2026-08-02.** It asked
> the second and meant the first, which cost a `Streaming` flow a wait it could have had: a
> window's flow is journaled for the reason a durable one is
> ([ADR-0055](../adr/ADR-0055-a-window-names-the-instance-it-starts.md)), the engine reads the
> profile in one place and asks `ExecutionProfiles.IsJournaled` there, `FlowTimerScan` and
> `FlowHost.SignalAsync` resume by instance id without reading a profile at all, and
> `FlowStreamScan` already counts a suspended window's flow as started and checkpoints past
> it. The message it got named `Streaming` back at it under a fix it could not take — a
> stream-triggered flow that declares `Durable` instead is
> [`FLOWX1042`](FLOWX1042.md). `ExecutionPlan.Create` was the same literal and made the same
> refusal, and asks the same question now.

> [!NOTE]
> **This rule covers `.PollUntil<T>(...)` as well as `.Delay(duration)` and
> `.AwaitSignal<T>(timeout)`.** The third arrived with the construct: a poll parks between
> attempts and reads which attempt it is on out of the journal that parked it, so outside one it
> has neither anywhere to record when the next call is due nor any way to count the ones already
> made — the same sentence twice over.
>
> **It covered `.Delay(duration)` from 2026-08-02.** It could not before, and not because anybody decided it should not: `.Delay`
> compiled to no step at all, so there was nothing for a rule that reads the compiled step
> model to see. The title still names `AwaitSignal` alone — an id and a title are what an
> `.editorconfig` line and a build log carry, and renaming a shipped rule to cover a second
> construct would break every search saved for the first — and the **message** names whichever
> construct the flow declared.

> [!NOTE]
> **This rule's fix produces a working flow, as of 2026-08-01.** For two phases it did not: a
> since-deleted rule reported `AwaitSignal` as an error under *every* profile, `Durable`
> included, because nothing implemented suspension — so this rule's quick action cleared one
> error and raised another, and `AwaitSignal` had no profile it could legally declare. WP-63
> made a `Durable` flow suspend at its suspension point and resume when the signal is
> delivered. Nothing about *this* rule changed; what changed is that its answer is now worth
> applying.

## Example that triggers it

```csharp
[Flow("order.place")]                       // Ephemeral by default
...
flow.AwaitSignal<PaymentConfirmed>(TimeSpan.FromHours(1))
```

## How to fix it

```csharp
[Flow("order.place", Profile = ExecutionProfile.Durable)]
[FlowDeadline("P30D")]
```

The deadline is not decoration and the quick action deliberately does not write one. A flow
that waits for a person needs longer than thirty seconds. *This paragraph used to go on to say
that `[FlowDeadline]` was the only timeout enforced on a wait, because the declared duration
reached the plan and nothing armed it.* It is armed now — the instance records when it is due
and a sweep brings it back — so the two bounds are different questions again: the wait's own
duration ends the wait, and the flow's deadline ends the flow. An instance whose budget has
gone times out at its next step boundary rather than waiting for a signal it can no longer act
on, which is checked *before* the decision to park.

## When to suppress

None. A durable suspension costs one database row and no thread, no memory and no lease while it waits — the cost is the journal, not the waiting.

That sentence was an argument about a design until WP-63; it is now a description.
`samples/workflow`'s `offer.accept` sends an offer out, suspends, gives its lease back and
leaves one committed row, and a countersignature days later resumes it through the same step
loop a recovery scan uses. A waiting instance is also not in any recovery scan's candidate
set — both shipped indexes list `Pending`, `Running` and `Compensating` only — so it is not
swept up and re-suspended every lease TTL.

Any suppression must carry a `FLOWX-DEBT` marker with an owner and an expiry —
see [21-Quality-Gates §6](../21-Quality-Gates.md#6-technical-debt-policy). A
suppression without one fails the build.

---

**Back to:** [diagnostics index](README.md) · [Quality gates](../21-Quality-Gates.md)
