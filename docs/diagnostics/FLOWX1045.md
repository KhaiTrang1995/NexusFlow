# FLOWX1045 — Schedule jitter cannot be read

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0
> **Applies to:** a `[CronTrigger]` whose `Jitter` is not a **positive** ISO-8601 duration —
> unparseable, empty, zero or negative. A `[CronTrigger]` that declares no `Jitter` at all is
> the ordinary declaration and is never reported.

## What it means

`Jitter` is the window one firing of the schedule is released within. It is derived from the
firing rather than drawn at random, so every node in the fleet releases the same firing at the
same instant and the spread survives a fleet of any size
([ADR-0059](../adr/ADR-0059-schedule-jitter-is-derived-from-the-firing.md)).

`FlowSchedule.Create` reads that value at composition time and **throws** on one it cannot use,
for the reason it throws on an unreadable cron expression: a schedule this host cannot read in
full should be a pod that never becomes ready, rather than a job that silently never runs. So
the failure mode without this rule is a green build, a green test run, and every replica of the
deployment refusing to start — over a value that was a string literal on an attribute the whole
way.

### Zero is refused, and an omitted property is not

```csharp
[CronTrigger("0 2 * * *", Jitter = "PT0S")]   // reported
[CronTrigger("0 2 * * *")]                    // fine — fires on its occurrence
```

A declared spread of zero asks for a spread and gets none. It reads as working, which is the
property that gets it through review; the second form says the same thing and says it honestly.

## Example that triggers it

```csharp
[Flow("ledger.reconcile", Version = "1.0.0", Profile = ExecutionProfile.Durable)]
[CronTrigger("0 2 * * *", TimeZone = "Europe/Berlin", Jitter = "120s")]
public sealed partial class ReconcileLedgerFlow : Flow<ScheduledFire, ReconciliationDone>
```

`120s` is not ISO-8601. Neither is `2m`, `00:02:00` or `PT2`.

## How to fix it

```csharp
[CronTrigger("0 2 * * *", TimeZone = "Europe/Berlin", Jitter = "PT120S")]
```

`PT120S`, `PT2M` and `PT1H` are all the same shape: a `P`, a `T` before the time part, and a
unit letter after each number.

## What this rule does not judge

**Whether the spread is wider than the gap between two occurrences.** A jitter of `PT90M` on an
hourly schedule produces firings that can overtake each other, and that is a design decision
rather than a defect — `OverlapPolicy` is what decides what happens when they do. Answering it
would need a cron evaluator inside the analyzer, which is the shape of check that starts
disagreeing with the runtime the moment either changes.

## Why an error rather than a warning

Every reason this fires has a fix that produces a declaration the platform runs today, and none
of them has a deployment or a later release under which the current value becomes correct. It
is [FLOWX1038](FLOWX1038.md)'s stance for the same reason.

## When to suppress

There is nothing a suppression buys. The registration is emitted either way and throws either
way; suppressing this moves the failure from a build that stops to a rollout that does not
start.

## Related

- [ADR-0059](../adr/ADR-0059-schedule-jitter-is-derived-from-the-firing.md) — why the offset is
  derived from the firing, and why a random one is not jitter at all
- [FLOWX1038](FLOWX1038.md) — the other rule a `[CronTrigger]` can break, about the flow rather
  than about the declaration
- [09 §8](../09-Trigger-Model.md#8-schedule-trigger) — what a schedule declaration binds
- [samples/scheduler](../../samples/scheduler/) — a schedule that declares a spread, and the
  test that shows two tenants getting different ones
