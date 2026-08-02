# FLOWX1048 — Triggers on one flow require different input contracts

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0
> **Applies to:** a flow carrying two or more of `[CronTrigger]`, `[BusTrigger]`,
> `[KafkaTrigger]`, `[ChangeTrigger]` and `[StreamTrigger]` where the kinds do not agree on the
> input contract the flow must declare.

> [!NOTE]
> **This rule exists because of the other rules.** It is the only entry in this catalogue whose
> justification is that four existing rules give unsatisfiable advice when two of them apply at
> once, and none of them can see the other.

## What it means

A trigger that carries a body fixes what that body **is**. Three of the five kinds fix it to a
different type:

| Trigger | Input contract | Why |
|---|---|---|
| `[CronTrigger]` | `FlowX.ScheduledFire` | A firing has only the occurrence to give, and a flow may not read a clock ([FLOWX1007](FLOWX1007.md)) |
| `[BusTrigger]`, `[KafkaTrigger]`, `[ChangeTrigger]` | `FlowX.BusMessage` | A delivery and a change each have only the message, body undeserialised |
| `[StreamTrigger]` | `FlowX.StreamWindowBatch` | A closed window has its interval and its records |

A class has one base type and therefore one `TIn`. Two triggers naming different contracts
cannot both be served by it, whichever of the two the author picks.

## Example that triggers it

Picking is exactly what the four rules below ask for, one after the other:

```csharp
// Reported by FLOWX1038: "declare it as Flow<ScheduledFire, TOut>"
[BusTrigger("invoice.requested", Group = "billing")]
[CronTrigger("0 3 * * *")]
public sealed partial class IssueInvoiceFlow : Flow<BusMessage, Invoice>

// Follow that advice, and FLOWX1039 says "declare it as Flow<BusMessage, TOut>"
public sealed partial class IssueInvoiceFlow : Flow<ScheduledFire, Invoice>
```

Neither message is wrong about its own transport, and neither states the fact that matters. This
rule is reported **instead of** [FLOWX1038](FLOWX1038.md), [FLOWX1039](FLOWX1039.md),
[FLOWX1041](FLOWX1041.md) and [FLOWX1042](FLOWX1042.md) rather than beside them, because a
message that reads as actionable is the one an author will act on.

## Two triggers that agree are not in conflict

`Bus` and `Change` both take `BusMessage`, so a flow declaring both is not reported. That is the
two-subscriber arrangement `samples/event-driven` ships: one flow consumes `invoice.requested`
from a broker and another observes the same event in the outbox, with no broker in the path.

`[HttpTrigger]` and `[AgentTrigger]` are not in the set at all. They bind whatever the flow's own
request contract is, so they conflict with nothing — a `Flow<BusMessage, TOut>` also reachable
over HTTP is unusual, and is a decision its author can defend rather than a build error.

## How to fix it

Declare one flow per input contract and compose the same capabilities in each. The transport
costs one decoding step; everything below that step is unchanged:

```csharp
[BusTrigger("invoice.requested", Group = "billing")]
public sealed partial class IssueInvoiceOverBusFlow : Flow<BusMessage, Invoice>
{
    protected override void Define(IFlowBuilder<BusMessage, Invoice> flow) => flow
        .Step<ReadInvoiceRequest>()          // the transport's whole cost
        .Step<ValidateInvoice>()
        .Step<CalculateTax>()
        .Step<PersistInvoice>().CompensateWith<VoidInvoice>()
        .Emit<InvoiceIssued>(/* … */)
        .Return(ctx => ctx.Get<Invoice>());
}

[CronTrigger("0 3 * * *", TimeZone = "UTC")]
public sealed partial class IssueInvoiceOverScheduleFlow : Flow<ScheduledFire, Invoice>
{
    protected override void Define(IFlowBuilder<ScheduledFire, Invoice> flow) => flow
        .Step<DueInvoice>()                  // the transport's whole cost
        .Step<ValidateInvoice>()
        // … the same four lines, unchanged
}
```

This is what quality goal **Q4** claims, stated precisely:
[ADR-0062](../adr/ADR-0062-transport-portability-is-a-property-of-the-capability-chain.md)
records that portability is a property of the capability chain rather than of a flow class, and
`tests/EventDriven.Tests` runs one billing reference through four transports and requires one
invoice.

## When to suppress

There is no suppression that makes it work. A class cannot have two input contracts, so the
generator emits a registration for at most one of the declarations — what a suppression buys is a
manifest publishing a trigger no host serves, which is the class of defect the manifest exists to
eliminate.

If one of the transports is not wanted, delete its attribute. That also removes it from
`flowx.manifest.json`, which `flowx diff` reports as a removed trigger — correctly.

## Related

- [ADR-0062](../adr/ADR-0062-transport-portability-is-a-property-of-the-capability-chain.md) —
  the decision, and the shape of quality goal Q4's claim
- [ADR-0004](../adr/ADR-0004-universal-trigger-model.md) — why a trigger is an attribute on a
  flow and never on a capability
- [FLOWX1038](FLOWX1038.md), [FLOWX1039](FLOWX1039.md), [FLOWX1041](FLOWX1041.md),
  [FLOWX1042](FLOWX1042.md) — the four rules this one is reported instead of
- [`samples/event-driven`](../../samples/event-driven/) — four transports, one chain
