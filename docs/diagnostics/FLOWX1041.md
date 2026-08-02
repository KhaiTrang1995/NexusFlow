# FLOWX1041 — Change-triggered flow cannot be observed

> **Severity:** Error · **Category:** FlowX · **Since:** 0.1.0
> **Applies to:** a `[ChangeTrigger]` on a flow whose input contract is not `FlowX.BusMessage`,
> or on a flow that does not declare `Profile = ExecutionProfile.Durable`.

> [!NOTE]
> **This is [FLOWX1039](FLOWX1039.md)'s rule one transport over**, and it is a separate id for
> the reason FLOWX1039 is separate from [FLOWX1038](FLOWX1038.md): a suppression is per id, a
> flow may declare both a bus trigger and a change trigger, and silencing one must not silence
> the other.

## What it means

A `[ChangeTrigger]` is turned into a subscription registration by the same reading of the
attribute that produces the manifest's `triggers` block, so a published change subscription and
a served one cannot disagree
([ADR-0047](../adr/ADR-0050-a-change-trigger-observes-the-outbox.md)). Two things stop a flow
being registered.

### The input contract is not `BusMessage`

A change is an outbox row: an event id, a type, a schema version, a partition key and an
**undeserialised** payload — which is exactly `BusMessage`, and is why a change trigger and a
bus trigger hand the flow the same thing. The body stays undeserialised for FLOWX1039's reason:
turning it into a typed contract needs a `JsonTypeInfo` only source-generated code can name, and
`FlowX.Hosting` is compiled before any application's contracts exist. Deserialise it in a
capability, where a generated serialiser context is in scope and a malformed payload is a
`Result` failure rather than an exception out of a background service.

That the two transports share the contract is the point rather than a coincidence: swapping
`[BusTrigger]` for `[ChangeTrigger]` is a one-line edit, and quality goal Q4 is that claim
stated about two files instead of about a design.

### The flow does not declare `Durable`

A change feed is at-least-once for a reason of its own, and it is not the broker's. The cursor
is committed *after* the flows in a batch have run
([ADR-0048](../adr/ADR-0048-a-change-feed-advances-a-cursor.md)) — the only order that cannot
lose work — so a crash in between re-offers every change since the last committed position. The
answer is that the change *derives* the instance id it starts
([ADR-0049](../adr/ADR-0049-a-change-names-the-instance-it-starts.md)), and the journal's
primary key refuses the second one.

An `Ephemeral` flow journals nothing, so the derived id is inert, `FlowHost` takes no lease and
writes no row, and every re-read of the window runs the flow again. **Nothing reports it.** No
exception, no duplicate-key refusal, no row anywhere to count.

## Example that triggers it

```csharp
// Wrong input: a change is an outbox row, and nothing can construct an OrderPlaced from one
// without a JsonTypeInfo the host may not reflect for.
[Flow("orders.project", Profile = ExecutionProfile.Durable)]
[ChangeTrigger("order.placed", Group = "projection")]
public sealed partial class ProjectOrderFlow : Flow<OrderPlaced, OrderProjected>

// Wrong profile: this runs again every time the cursor is re-read from an uncommitted position.
[Flow("orders.project")]
[ChangeTrigger("order.placed", Group = "projection")]
public sealed partial class ProjectOrderFlow : Flow<BusMessage, OrderProjected>
```

## How to fix it

```csharp
[Flow("orders.project", Version = "1.0.0", Profile = ExecutionProfile.Durable)]
[ChangeTrigger("order.placed", Group = "projection")]
public sealed partial class ProjectOrderFlow : Flow<BusMessage, OrderProjected>
{
    protected override void Define(IFlowBuilder<BusMessage, OrderProjected> flow) => flow
        .Step<ProjectOrder>()
        .Return(ctx => ctx.Get<OrderProjected>());
}
```

## Why an error rather than a warning

FLOWX1039's reasons unchanged: neither case has a deployment, a configuration or a later release
under which it becomes correct — a change will never carry a typed contract the host can name,
and an ephemeral flow will never have a primary key — and both present as work that silently
does not happen or silently happens *n* times.

## When to suppress

There is no suppression that makes either case work. The generator emits no registration either
way, so what a suppression buys is a manifest publishing a change subscription with nothing
observing that address.

If the flow is not meant to observe a change, delete the attribute. That also removes it from
`flowx.manifest.json`, which `flowx diff` reports as a removed trigger — correctly.

## The rule this is *not*

A `[ChangeTrigger]` whose source is an event type the same flow **emits** is a different defect
and is refused elsewhere: `FlowChangeCatalog.Add` throws at registration, because the emitted
types are in the `ExecutionPlan` the registration is handed and reading them there is reading
the artifact that will actually run
([ADR-0047](../adr/ADR-0050-a-change-trigger-observes-the-outbox.md) decision 3). It is not this
rule, and the pod does not become ready rather than the build failing.

## Related

- [ADR-0047](../adr/ADR-0050-a-change-trigger-observes-the-outbox.md) — what a change trigger
  observes, and why it refuses to observe itself
- [ADR-0048](../adr/ADR-0048-a-change-feed-advances-a-cursor.md) — why there is a cursor and no
  acknowledgement, and why it is not over `staged_seq`
- [ADR-0049](../adr/ADR-0049-a-change-names-the-instance-it-starts.md) — why a change names the
  instance it starts, and why that needs a journal
- [FLOWX1039](FLOWX1039.md) — the same rule for the bus trigger
- [FLOWX1038](FLOWX1038.md) — the same rule for the schedule trigger
- [09 §4](../09-Trigger-Model.md#4-trigger-kinds-and-their-semantics) — `Change`'s declared
  delivery, ordering and failure semantics
