# FLOWX1039 — Bus-triggered flow cannot be consumed

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0
> **Applies to:** a `[BusTrigger]` or `[KafkaTrigger]` on a flow whose input contract is not
> `FlowX.BusMessage`, or on a flow that does not declare
> `Profile = ExecutionProfile.Durable`.

> [!NOTE]
> **This rule exists because the alternative is the defect the bus trigger was bound to
> remove.** Until this release, `Bus` was one of four trigger kinds that were declaration only:
> `TriggerReader`'s remarks said, in those words, that *"a flow declaring one of those declares
> an address nothing serves"*. Now that a bus trigger becomes a registration, a declaration the
> generator cannot turn into one would be the same hole, one layer down, with no message at all
> — because the generated file simply would not mention the flow.

## What it means

A `[BusTrigger]` or `[KafkaTrigger]` is turned into a subscription registration by the same
reading of the attribute that produces the manifest's `triggers` block, so a published
subscription and a served one cannot disagree
([ADR-0035](../adr/ADR-0035-a-delivery-names-the-instance-it-starts.md)). Two things stop a
flow being registered, and they fail in opposite directions.

### The input contract is not `BusMessage`

A delivery has exactly one thing to give the flow it starts: the message. It arrives as
`BusMessage` — the event's identity, its topic, its type, its schema version, its partition key
and its **undeserialised** body.

Undeserialised is the part worth reading twice. Turning a JSON body into a typed contract needs
a `System.Text.Json.Serialization.JsonTypeInfo` for that contract, and only source-generated
code can name one. `FlowX.Hosting` has no such code — it is a library compiled before any
application's contracts exist — so a host that deserialised would either reflect, or carry a
registry mapping every topic to a type. Reflection is what constraint **C2** forbids, and it is
not a theoretical constraint here: `samples/ecommerce` is the repository's only NativeAOT-published
assembly and CI publishes it. A registry would be a second place the wire format is declared,
one release away from disagreeing with the first.

So the body is handed over verbatim and the flow deserialises it in a **capability**, where a
generated serialiser context is in scope and a malformed body is a `Result` failure rather than
an exception out of a background service.

The message is journalled on `flow_instance.input` like any other trigger's body, so a replay
reconstructs what was delivered — including the `EventId` the instance's own primary key was
derived from.

### The flow does not declare `Durable`

This one would start perfectly well — and would start **again on every redelivery**.

A broker delivers at least once. That is its contract, not its failure mode: the outbox rolls
its mark back with the claim ([ADR-0018](../adr/ADR-0018-outbox-publication-and-ordering.md)
decision 4), a consumer that dies mid-flow has its message reclaimed, and a node that commits
and then dies before acknowledging sees the message again. FlowX answers all three the same
way: the delivery derives the instance id it starts, so `ILeaseStore` refuses the second one
while the first is running and the journal's primary key refuses it for ever afterwards.

An `Ephemeral` flow journals nothing, so the second half of that does not exist and the first
half is never reached — `FlowHost` takes no lease for an ephemeral plan either. The id is
inert, and at-least-once *delivery* becomes at-least-once **execution**.

**Nothing reports it.** No exception, no duplicate-key refusal, no row anywhere to count. An
order is repriced twice, a customer is charged twice, and the first symptom is in the numbers.

## Example that triggers it

```csharp
// Wrong input: nothing can construct an OrderPlaced from a delivery, because the host has no
// JsonTypeInfo for it and may not reflect for one.
[Flow("pricing.reprice", Profile = ExecutionProfile.Durable)]
[BusTrigger("order.placed", Group = "pricing")]
public sealed partial class RepriceOrderFlow : Flow<OrderPlaced, RepricingDone>

// Wrong profile: this runs again on every redelivery.
[Flow("pricing.reprice")]
[BusTrigger("order.placed", Group = "pricing")]
public sealed partial class RepriceOrderFlow : Flow<BusMessage, RepricingDone>
```

## How to fix it

```csharp
[Flow("pricing.reprice", Version = "1.0.0", Profile = ExecutionProfile.Durable)]
[BusTrigger("order.placed", Group = "pricing")]
public sealed partial class RepriceOrderFlow : Flow<BusMessage, RepricingDone>
{
    protected override void Define(IFlowBuilder<BusMessage, RepricingDone> flow) => flow
        // The capability deserialises the body, because that is the layer with a serialiser
        // context in scope and the layer whose failures are Results.
        .Step<ReadPlacedOrder>()
        .Step<RepriceBasket>()
        .Return(ctx => ctx.Get<RepricingDone>());
}
```

Whatever else the flow needs — the catalogue, the tenant's price list, a configuration value —
comes from a capability, which is the layer allowed to read the outside world. The message is
what the *trigger* knows and nothing else does.

## Why an error rather than a warning

[FLOWX1032](FLOWX1032.md) is a warning because the runtime will eventually execute the policies
it reports: the source is ahead of the platform, not wrong. This rule is
[FLOWX1038](FLOWX1038.md)'s shape, and FLOWX1038 took [FLOWX1033](FLOWX1033.md)'s. Neither case
has a deployment, a configuration or a later release under which it becomes correct — a delivery
will never carry a typed contract the host can name, and an ephemeral flow will never have a
primary key — and both present as work that silently does not happen or silently happens *n*
times.

## When to suppress

There is no suppression that makes either case work. The generator emits no registration either
way, so what a suppression buys is a manifest that publishes a subscription and a host that
consumes nothing at that address — which is precisely the state this rule exists to end.

If the flow is not meant to subscribe, delete the trigger attribute. That also removes it from
`flowx.manifest.json`, which `flowx diff` reports as a removed trigger — correctly, because a
subscription nobody serves is not an address anyone should have been relying on.

## Why this is not FLOWX1038 widened

The two rules read the same two facts — an input contract and a profile — and it would be
tempting to make one rule of them. They are separate ids because a suppression is per id, and
the two failures are not the same decision: a team suppressing a schedule finding must not
thereby suppress a subscription one, in a codebase where a flow may declare both. The messages
also name different contracts and different consequences — "every node fires it" against "every
redelivery runs it" — and a merged rule would have to say both and mean one.

## Related

- [ADR-0035](../adr/ADR-0035-a-delivery-names-the-instance-it-starts.md) — why a delivery names
  the instance, and why that needs a journal
- [ADR-0036](../adr/ADR-0036-a-message-is-acknowledged-when-its-flow-is-journalled.md) — when a
  message is acknowledged, and why a business failure is not a redelivery
- [ADR-0038](../adr/ADR-0038-a-poison-message-is-dead-lettered.md) — what happens to a message
  the flow can never consume
- [FLOWX1038](FLOWX1038.md) — the same rule for the schedule trigger
- [FLOWX1017](FLOWX1017.md) — the other rule that requires `Durable`, for the construct in the
  flow's body that cannot work without a journal
- [09 §7](../09-Trigger-Model.md#7-bus-trigger) — what a bus declaration binds, and which of its
  properties still bind nothing
