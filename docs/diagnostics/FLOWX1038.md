# FLOWX1038 — Scheduled flow cannot be fired

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0
> **Applies to:** a `[CronTrigger]` on a flow whose input contract is not
> `FlowX.ScheduledFire`, or on a flow that does not declare
> `Profile = ExecutionProfile.Durable`.

> [!NOTE]
> **This rule exists because the alternative is the defect the schedule trigger was bound to
> remove.** Until this release, `Schedule` was one of five trigger kinds that were declaration
> only: `TriggerReader`'s remarks said, in those words, that *"a flow declaring one of those
> declares an address nothing serves"*. Now that `[CronTrigger]` becomes a registration, a
> declaration the generator cannot turn into one would be the same hole, one layer down, with
> no message at all — because the generated file simply would not mention the flow.

## What it means

A `[CronTrigger]` is turned into a schedule registration by the same reading of the attribute
that produces the manifest's `triggers` block, so a published schedule and a fired one cannot
disagree ([ADR-0031](../adr/ADR-0031-an-occurrence-names-the-instance-it-starts.md)). Two
things stop a flow being registered, and they fail in opposite directions.

### The input contract is not `ScheduledFire`

A cron firing carries no body. Nobody sent a request, there is no payload, and the only fact a
schedule has to give the flow it starts is **which occurrence this is** — an instant the
expression named, which may be forty minutes in the past by the time a sweep reaches it.

The flow cannot go and look that up, and the reason is another rule.
[FLOWX1007](FLOWX1007.md) makes an ambient `DateTime.UtcNow` an error inside a `Durable`
flow, and [FLOWX1011](FLOWX1011.md) makes it one inside every builder lambda, because a value
taken ambiently is in none of the fields a replay reconstructs. So a scheduled flow that had
to work out its own occurrence could not: reading the clock is forbidden, and if it were not,
a resumed instance would compute a different answer from the one it committed.

The occurrence therefore arrives as the flow's input, is journalled on `flow_instance.input`
like any other trigger's body, and is read back verbatim on a resume
([ADR-0033](../adr/ADR-0033-a-scheduled-flows-input-is-its-occurrence.md)).

### The flow does not declare `Durable`

This one would start perfectly well — and would start on **every node in the fleet, on every
occurrence**.

There is no leader and no election. Every node evaluates the same expression, arrives at the
same occurrence, derives the same instance id from it, and the two stores settle the race:
`ILeaseStore` refuses the losers while the winner is running, and the journal's primary key
refuses them for ever afterwards. An `Ephemeral` flow journals nothing, so the second half of
that does not exist and the first half is never reached — `FlowHost` takes no lease for an
ephemeral plan either. The id is inert, and ten nodes run ten copies.

**Nothing reports it.** No exception, no duplicate-key refusal, no row anywhere to count. A
nightly reconciliation quietly runs once per replica, and the first symptom is in the numbers
it produced.

## Example that triggers it

```csharp
// Wrong input: nothing can construct a LedgerDate from an occurrence.
[Flow("ledger.reconcile", Profile = ExecutionProfile.Durable)]
[CronTrigger("0 2 * * *", TimeZone = "Europe/Berlin")]
public sealed partial class ReconcileLedgerFlow : Flow<LedgerDate, ReconciliationDone>

// Wrong profile: this fires once per node.
[Flow("ledger.reconcile")]
[CronTrigger("0 2 * * *")]
public sealed partial class ReconcileLedgerFlow : Flow<ScheduledFire, ReconciliationDone>
```

## How to fix it

```csharp
[Flow("ledger.reconcile", Version = "1.0.0", Profile = ExecutionProfile.Durable)]
[CronTrigger("0 2 * * *", TimeZone = "Europe/Berlin")]
public sealed partial class ReconcileLedgerFlow : Flow<ScheduledFire, ReconciliationDone>
{
    protected override void Define(IFlowBuilder<ScheduledFire, ReconciliationDone> flow) => flow
        .Step<CloseLedgerDay>()
        .Return(ctx => ctx.Get<ReconciliationDone>());
}
```

Whatever else the flow needs — the ledger to close, the tenant to run for, a configuration
value — comes from a capability, which is the layer allowed to read the outside world. The
occurrence is what the *trigger* knows and nothing else does.

## Why an error rather than a warning

The deleted `FLOWX1032` was a warning because the runtime would eventually execute the
policies it reported: the source was ahead of the platform, not wrong. This rule is
[FLOWX1033](FLOWX1033.md)'s shape instead. Neither case has a deployment, a configuration or a
later release under which it becomes correct — a cron firing will never have a body, and an
ephemeral flow will never have a primary key — and both present as work that silently does not
happen or silently happens *n* times.

## When to suppress

There is no suppression that makes either case work. The generator emits no registration
either way, so what a suppression buys is a manifest that publishes a schedule and a host that
fires nothing at that address — which is precisely the state this rule exists to end.

If the flow is not meant to be scheduled, delete the `[CronTrigger]`. That also removes it from
`flowx.manifest.json`, which `flowx diff` reports as a removed trigger — correctly, because a
schedule nobody can fire is not an address anyone should have been relying on.

## Related

- [ADR-0031](../adr/ADR-0031-an-occurrence-names-the-instance-it-starts.md) — why an occurrence
  names the instance, and why that needs a journal
- [ADR-0033](../adr/ADR-0033-a-scheduled-flows-input-is-its-occurrence.md) — why the input
  contract is fixed by the platform
- [FLOWX1007](FLOWX1007.md), [FLOWX1011](FLOWX1011.md) — the rules that stop a flow reading the
  clock, and therefore make the occurrence an input
- [FLOWX1017](FLOWX1017.md) — the other rule that requires `Durable`, for the other construct
  that cannot work without a journal
- [09 §8](../09-Trigger-Model.md#8-schedule-trigger) — what a schedule declaration binds, and
  what each of its six properties binds to
